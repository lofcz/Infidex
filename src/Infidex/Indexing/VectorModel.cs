using Infidex.Core;
using Infidex.Tokenization;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;
using Infidex.Coverage;
using Infidex.Synonyms;
using Infidex.Indexing.Segments;

namespace Infidex.Indexing;

/// <summary>
/// TF-IDF vector space model for relevancy ranking (Stage 1).
/// Uses FST-based term index and O(1) short query resolution.
/// Thread-safe for concurrent searching.
/// </summary>
public class VectorModel : IDisposable
{
    private readonly Tokenizer _tokenizer;
    private readonly TermCollection _termCollection;
    private readonly DocumentCollection _documents;
    private readonly int _stopTermLimit;
    private readonly float[] _fieldWeights;
    private readonly SynonymMap? _synonymMap;

    private float[]? _docLengths;
    private float _avgDocLength;
    
    // FST-based indexes
    private FstIndex? _fstIndex;
    private FstBuilder? _fstBuilder;
    private PositionalPrefixIndex? _shortQueryIndex;
    private ShortQueryResolver? _shortQueryResolver;
    private DocumentMetadataCache? _documentMetadataCache;
    
    // Segments
    private readonly List<SegmentReader> _segments = new List<SegmentReader>();
    private readonly List<int> _segmentDocBases = new List<int>(); 
    private int _flushedDocCount = 0; 

    // Word-level IDF cache
    private Dictionary<string, float>? _wordIdfCache;

    public Tokenizer Tokenizer => _tokenizer;
    public DocumentCollection Documents => _documents;
    public TermCollection TermCollection => _termCollection;
    
    internal FstIndex? FstIndex => _fstIndex;
    internal PositionalPrefixIndex? ShortQueryIndex => _shortQueryIndex;
    internal ShortQueryResolver? ShortQueryResolver => _shortQueryResolver;
    internal DocumentMetadataCache? DocumentMetadataCache => _documentMetadataCache;
    internal Dictionary<string, float>? WordIdfCache => _wordIdfCache;

    public event EventHandler<int>? ProgressChanged;
    public bool EnableDebugLogging { get; set; }

    public VectorModel(Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null, SynonymMap? synonymMap = null)
    {
        _tokenizer = tokenizer;
        _stopTermLimit = stopTermLimit;
        _termCollection = new TermCollection();
        _documents = new DocumentCollection();
        _fieldWeights = fieldWeights ?? ConfigurationParameters.DefaultFieldWeights;
        _synonymMap = synonymMap;
        _fstBuilder = new FstBuilder();
        char[] delimiters = tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        _shortQueryIndex = new PositionalPrefixIndex(delimiters: delimiters);
    }

    public Document IndexDocument(Document document)
    {
        Document doc = _documents.AddDocument(document);
        bool isSegmentContinuation = doc.SegmentNumber > 0;

        (ushort Position, byte WeightIndex)[] fieldBoundaries =
            document.Fields.GetSearchableTexts('§', out string concatenatedText);

        doc.IndexedText = concatenatedText;

        string indexText = concatenatedText;
        if (_tokenizer.TextNormalizer != null)
        {
            indexText = _tokenizer.TextNormalizer.Normalize(indexText);
        }
        indexText = indexText.ToLowerInvariant();

        if (_synonymMap != null && _synonymMap.HasCanonicalMappings && _tokenizer.TokenizerSetup != null)
        {
            indexText = _synonymMap.CanonicalizeText(indexText, _tokenizer.TokenizerSetup.Delimiters);
        }

        List<Shingle> shingles = _tokenizer.TokenizeForIndexing(indexText, isSegmentContinuation);

        foreach (Shingle shingle in shingles)
        {
            float fieldWeight = DetermineFieldWeight(shingle.Position, fieldBoundaries);
            Term term = _termCollection.CountTermUsage(shingle.Text, _stopTermLimit, forFastInsert: false, out bool isNewTerm);
            
            if (isNewTerm)
            {
                _fstBuilder?.AddForwardOnly(shingle.Text, _termCollection.Count - 1);
            }
            
            term.FirstCycleAdd(doc.Id, _stopTermLimit, removeDuplicates: isSegmentContinuation, fieldWeight);
        }
        
        _shortQueryIndex?.IndexDocument(indexText, doc.Id);

        return doc;
    }

    private float DetermineFieldWeight(int tokenPosition, (ushort Position, byte WeightIndex)[] fieldBoundaries)
    {
        if (fieldBoundaries.Length == 0) return 1.0f;

        byte weightIndex = 0;
        for (int i = 0; i < fieldBoundaries.Length; i++)
        {
            if (fieldBoundaries[i].Position <= tokenPosition)
                weightIndex = fieldBoundaries[i].WeightIndex;
            else
                break;
        }

        return weightIndex < _fieldWeights.Length ? _fieldWeights[weightIndex] : 1.0f;
    }

    public void BuildInvertedLists(int batchDelayMs = -1, int batchSize = 0, CancellationToken cancellationToken = default)
    {
        int totalDocs = _documents.Count;
        _docLengths = new float[totalDocs];
        _avgDocLength = 0f;

        int totalTerms = _termCollection.Count;
        if (totalTerms == 0 || totalDocs == 0)
        {
            ProgressChanged?.Invoke(this, 100);
            return;
        }

        int workerCount = Math.Max(1, Environment.ProcessorCount);
        int chunkSize = (totalTerms + workerCount - 1) / workerCount;

        float[][] localDocLengths = new float[workerCount][];
        int processedTerms = 0;

        Parallel.For(0, workerCount, new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = cancellationToken
        }, workerIndex =>
        {
            int start = workerIndex * chunkSize;
            int end = Math.Min(start + chunkSize, totalTerms);
            if (start >= end)
            {
                localDocLengths[workerIndex] = new float[totalDocs];
                return;
            }

            float[] local = new float[totalDocs];

            for (int termIndex = start; termIndex < end; termIndex++)
            {
                if (batchDelayMs >= 0 && batchSize > 0 &&
                    processedTerms > 0 && processedTerms % batchSize == 0)
                {
                    Thread.Sleep(batchDelayMs);
                }

                Term? term = _termCollection.GetTermByIndex(termIndex);
                if (term == null) continue;

                List<int>? docIds = term.GetDocumentIds();
                List<byte>? weights = term.GetWeights();

                if (docIds == null || weights == null) continue;

                int postings = docIds.Count;
                for (int i = 0; i < postings; i++)
                {
                    int internalId = docIds[i];
                    if ((uint)internalId < (uint)totalDocs)
                        local[internalId] += weights[i];
                }

                int done = Interlocked.Increment(ref processedTerms);
                if (done % 100 == 0)
                {
                    int percent = done * 100 / Math.Max(totalTerms, 1);
                    ProgressChanged?.Invoke(this, percent);
                }
            }

            localDocLengths[workerIndex] = local;
        });

        for (int d = 0; d < totalDocs; d++)
        {
            float sum = 0f;
            for (int w = 0; w < workerCount; w++)
            {
                float[] local = localDocLengths[w];
                if (local != null && d < local.Length)
                    sum += local[d];
            }
            _docLengths[d] = sum;
        }

        float totalLength = 0f;
        for (int i = 0; i < _docLengths.Length; i++)
            totalLength += _docLengths[i];

        _avgDocLength = totalDocs > 0 ? totalLength / totalDocs : 0f;
        
        BuildWordIdfCache();
        ProgressChanged?.Invoke(this, 100);
    }

    public void CalculateWeights() => BuildInvertedLists();

    public void FastInsert(string text, int documentIndex)
    {
        Shingle[] shingles = _tokenizer.TokenizeForSearch(text, out _, false);

        List<Term> terms = [];
        foreach (Shingle shingle in shingles)
        {
            Term term = _termCollection.CountTermUsage(shingle.Text, _stopTermLimit, forFastInsert: true);
            term.QueryOccurrences = (byte)shingle.Occurrences;
            terms.Add(term);
        }

        byte[] weights = Bm25Scorer.CalculateQueryWeights(terms, _documents.Count);
        for (int i = 0; i < terms.Count; i++)
            terms[i].AddForFastInsert(weights[i], documentIndex);
    }
    
    public void BuildOptimizedIndexes()
    {
        if (_fstIndex == null && _termCollection.Count > 0)
        {
            FstBuilder builder = new FstBuilder();
            for (int i = 0; i < _termCollection.Count; i++)
            {
                Term? term = _termCollection.GetTermByIndex(i);
                if (term?.Text != null)
                {
                    builder.Add(term.Text, i);
                }
            }
            _fstIndex = builder.Build();
        }
        
        if (_shortQueryIndex != null)
        {
            _shortQueryIndex.Freeze();
            char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            _shortQueryResolver = new ShortQueryResolver(_shortQueryIndex, _documents, delimiters);
        }
        
        BuildDocumentMetadataCache();
    }
    
    private void BuildDocumentMetadataCache()
    {
        _documentMetadataCache = new DocumentMetadataCache(_documents.Count);
        char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        HashSet<char> delimiterSet = new HashSet<char>(delimiters);
        
        Parallel.For(0, _documents.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        }, docId =>
        {
            Document? doc = _documents.GetDocument(docId);
            if (doc == null || doc.Deleted || string.IsNullOrEmpty(doc.IndexedText))
            {
                _documentMetadataCache.Set(docId, DocumentMetadata.Empty);
                return;
            }
            
            string text = doc.IndexedText.ToLowerInvariant();
            ReadOnlySpan<char> textSpan = text.AsSpan();

            if (_tokenizer.TextNormalizer != null)
            {
                text = _tokenizer.TextNormalizer.Normalize(text);
                textSpan = text.AsSpan();
            }

            if (_synonymMap != null && _synonymMap.HasCanonicalMappings)
            {
                text = _synonymMap.CanonicalizeText(text, delimiters);
                textSpan = text.AsSpan();
            }
            
            string firstToken = string.Empty;
            ushort tokenCount = 0;
            int i = 0;
            
            while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
                i++;
            
            while (i < textSpan.Length && tokenCount < ushort.MaxValue)
            {
                int tokenStart = i;
                while (i < textSpan.Length && !delimiterSet.Contains(textSpan[i]))
                    i++;
                
                int tokenLength = i - tokenStart;
                
                if (tokenLength > 0)
                {
                    if (tokenCount == 0)
                    {
                        firstToken = textSpan.Slice(tokenStart, tokenLength).ToString();
                    }
                    tokenCount++;
                }
                
                while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
                    i++;
            }
            
            _documentMetadataCache.Set(docId, new DocumentMetadata(firstToken, tokenCount));
        });
    }
    
    public void GetTermsByPrefix(string prefix, List<int> termIds)
    {
        _fstIndex?.GetByPrefix(prefix.AsSpan(), termIds);
    }
    
    public bool HasTermPrefix(string prefix)
    {
        return _fstIndex?.HasPrefix(prefix.AsSpan()) ?? false;
    }
    
    internal TopKHeap Search(string queryText, Dictionary<int, byte>? bestSegmentsMap = null, int queryIndex = 0)
        => SearchWithMaxScore(queryText, int.MaxValue, bestSegmentsMap, queryIndex);

    internal TopKHeap SearchWithMaxScore(string queryText, int topK, Dictionary<int, byte>? bestSegmentsMap = null, int queryIndex = 0)
    {
        Shingle[] queryShingles = _tokenizer.TokenizeForSearch(queryText, out _, false);
        int totalDocs = _documents.Count;

        if (_docLengths == null || _docLengths.Length != totalDocs || _avgDocLength <= 0f)
            BuildInvertedLists(cancellationToken: CancellationToken.None);

        var termInfos = new Dictionary<string, (int GlobalDf, byte QueryOccurrences)>();
        
        foreach (Shingle shingle in queryShingles)
        {
            GatherTermInfo(shingle.Text, (byte)shingle.Occurrences, termInfos);
            if (termInfos[shingle.Text].GlobalDf == 0 && shingle.Text.Length >= 4)
            {
                ExpandMissingTerm(shingle, termInfos);
            }
        }

        List<Bm25Scorer.TermScoreInfo> activeTermInfos = new List<Bm25Scorer.TermScoreInfo>();
        List<List<Bm25Scorer.TermScoreInfo>> segmentTermInfos = new List<List<Bm25Scorer.TermScoreInfo>>(_segments.Count);
        for(int i=0; i<_segments.Count; i++) segmentTermInfos.Add(new List<Bm25Scorer.TermScoreInfo>());

        float avgdl = _avgDocLength > 0f ? _avgDocLength : 1f;

        foreach (var kvp in termInfos)
        {
            string text = kvp.Key;
            int df = kvp.Value.GlobalDf;
            byte queryOcc = kvp.Value.QueryOccurrences;

            if (df <= 0 || df > _stopTermLimit) continue;

            float idf = Bm25Scorer.ComputeIdf(totalDocs, df);
            
            float maxTf = 255f;
            float k1 = 1.2f;
            float b = 0.75f;
            float delta = 1.0f;
            float minDlNorm = 1f - b + b * (1f / avgdl);
            float maxBm25Core = (maxTf * (k1 + 1f)) / (maxTf + k1 * minDlNorm);
            float maxScore = idf * (maxBm25Core + delta);

            Term? activeTerm = _termCollection.GetTerm(text);
            if (activeTerm != null)
            {
                activeTerm.QueryOccurrences = queryOcc;
                activeTermInfos.Add(new Bm25Scorer.TermScoreInfo(activeTerm, idf, maxScore));
            }

            for (int i = 0; i < _segments.Count; i++)
            {
                var segReader = _segments[i];
                int docBase = _segmentDocBases[i];
                
                var postingsEnum = segReader.GetPostingsEnum(text, docBase);
                if (postingsEnum != null)
                {
                    var segTerm = new Term(text);
                    int segDf = (int)postingsEnum.Cost();
                    segTerm.SetSegmentSource(segReader, docBase, segDf);
                    segmentTermInfos[i].Add(new Bm25Scorer.TermScoreInfo(segTerm, idf, maxScore));
                }
            }
        }

        TopKHeap resultHeap = Bm25Scorer.Search(
            activeTermInfos.ToArray(), 
            topK, totalDocs, _docLengths!, _avgDocLength, _stopTermLimit, _documents, bestSegmentsMap, queryIndex, _shortQueryIndex, queryText);

        for (int i = 0; i < _segments.Count; i++)
        {
            if (segmentTermInfos[i].Count == 0) continue;

            TopKHeap segHeap = Bm25Scorer.Search(
                segmentTermInfos[i].ToArray(),
                topK, totalDocs, _docLengths!, _avgDocLength, _stopTermLimit, _documents, bestSegmentsMap, queryIndex, _shortQueryIndex, queryText);
            
            foreach (var entry in segHeap.GetTopK())
            {
                resultHeap.Add(entry);
            }
        }

        return resultHeap;
    }

    private void GatherTermInfo(string text, byte occurrences, Dictionary<string, (int GlobalDf, byte QueryOccurrences)> termInfos)
    {
        bool isNew = !termInfos.TryGetValue(text, out var info);
        if (isNew)
        {
            info = (0, 0);
        }

        int newOcc = info.QueryOccurrences + occurrences;
        info.QueryOccurrences = (byte)Math.Min(newOcc, 255);

        if (isNew)
        {
            Term? activeTerm = _termCollection.GetTerm(text);
            if (activeTerm != null)
            {
                info.GlobalDf += activeTerm.DocumentFrequency;
            }

            foreach (var segment in _segments)
            {
                var postings = segment.GetPostings(text);
                if (postings != null)
                {
                    info.GlobalDf += postings.Value.DocIds.Length;
                }
            }
        }

        termInfos[text] = info;
    }

    private void ExpandMissingTerm(Shingle shingle, Dictionary<string, (int GlobalDf, byte QueryOccurrences)> termInfos)
    {
        List<(string Term, int Output)> fuzzyMatches = new List<(string Term, int Output)>();
        
        if (_fstIndex != null)
        {
            _fstIndex.GetWithinEditDistance1WithTerms(shingle.Text.AsSpan(), fuzzyMatches);
        }
        
        foreach (var segment in _segments)
        {
            if (segment.FstIndex != null)
            {
                segment.FstIndex.GetWithinEditDistance1WithTerms(shingle.Text.AsSpan(), fuzzyMatches);
            }
        }
        
        foreach (var match in fuzzyMatches)
        {
            if (!termInfos.ContainsKey(match.Term))
            {
                GatherTermInfo(match.Term, (byte)shingle.Occurrences, termInfos);
            }
        }
    }

    public void Flush(string segmentPath)
    {
        if (_termCollection.Count == 0) return;

        var writer = new SegmentWriter();
        writer.WriteSegment(_termCollection, _documents.Count - _flushedDocCount, _flushedDocCount, segmentPath);

        var reader = new SegmentReader(segmentPath);
        _segments.Add(reader);
        _segmentDocBases.Add(_flushedDocCount);

        _flushedDocCount = _documents.Count;

        _termCollection.ClearAllData();
        _fstBuilder = new FstBuilder();
        _fstIndex = null;
    }

    public void Dispose()
    {
        foreach(var seg in _segments) seg.Dispose();
        _segments.Clear();
    }

    public void Save(string filePath)
    {
        using FileStream stream = File.Create(filePath);
        using BinaryWriter writer = new BinaryWriter(stream);
        SaveToStream(writer);
    }

    public async Task SaveAsync(string filePath) => await Task.Run(() => Save(filePath));

    internal void SaveToStream(BinaryWriter writer)
        => VectorModelPersistence.SaveToStream(writer, _documents, _termCollection, _fstIndex, _shortQueryIndex, _documentMetadataCache);

    public static VectorModel Load(string filePath, Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
    {
        VectorModel model = new VectorModel(tokenizer, stopTermLimit, fieldWeights);
        using FileStream stream = File.OpenRead(filePath);
        using BinaryReader reader = new BinaryReader(stream);
        model.LoadFromStream(reader);
        return model;
    }

    public static async Task<VectorModel> LoadAsync(string filePath, Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
        => await Task.Run(() => Load(filePath, tokenizer, stopTermLimit, fieldWeights));

    internal void LoadFromStream(BinaryReader reader)
    {
        VectorModelPersistence.LoadFromStream(reader, _documents, _termCollection, _stopTermLimit, 
            out _fstIndex, out _shortQueryIndex, out _documentMetadataCache);
        
        if (_shortQueryIndex != null)
        {
            char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            _shortQueryResolver = new ShortQueryResolver(_shortQueryIndex, _documents, delimiters);
        }
    }
    
    private void BuildWordIdfCache()
    {
        int totalDocs = _documents.Count;
        if (totalDocs == 0)
        {
            _wordIdfCache = new Dictionary<string, float>();
            return;
        }
        
        char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        Dictionary<string, int> wordDocFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        for (int docId = 0; docId < totalDocs; docId++)
        {
            Document? doc = _documents.GetDocument(docId);
            if (doc == null || doc.Deleted || string.IsNullOrEmpty(doc.IndexedText))
                continue;
            
            string normalized = doc.IndexedText.ToLowerInvariant();
            if (_tokenizer.TextNormalizer != null)
            {
                normalized = _tokenizer.TextNormalizer.Normalize(normalized);
            }
            
            string[] words = normalized.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
            HashSet<string> uniqueWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);
            
            foreach (string word in uniqueWords)
            {
                if (word.Length > 0)
                {
                    wordDocFreq[word] = wordDocFreq.GetValueOrDefault(word, 0) + 1;
                }
            }
        }
        
        _wordIdfCache = new Dictionary<string, float>(wordDocFreq.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (word, df) in wordDocFreq)
        {
            if (df > 0 && df <= totalDocs)
            {
                _wordIdfCache[word] = Bm25Scorer.ComputeIdf(totalDocs, df);
            }
        }
    }
}
