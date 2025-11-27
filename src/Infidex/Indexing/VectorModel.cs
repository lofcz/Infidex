using Infidex.Core;
using Infidex.Tokenization;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;
using Infidex.Coverage;
using Infidex.Synonyms;

namespace Infidex.Indexing;

/// <summary>
/// TF-IDF vector space model for relevancy ranking (Stage 1).
/// Uses FST-based term index and O(1) short query resolution.
/// Thread-safe for concurrent searching.
/// </summary>
public class VectorModel
{
    private readonly Tokenizer _tokenizer;
    private readonly TermCollection _termCollection;
    private readonly DocumentCollection _documents;
    private readonly int _stopTermLimit;
    private readonly float[] _fieldWeights;
    private readonly SynonymMap? _synonymMap;

    private float[]? _docLengths;
    private float _avgDocLength;
    private bool _impactOrderingApplied;
    private int[]? _oldToNewDocId;
    private int[]? _newToOldDocId;
    
    // FST-based indexes
    private FstIndex? _fstIndex;
    private FstBuilder? _fstBuilder;
    private PositionalPrefixIndex? _shortQueryIndex;
    private ShortQueryResolver? _shortQueryResolver;
    private DocumentMetadataCache? _documentMetadataCache;
    
    // Word-level IDF cache: maps normalized word tokens to their IDF values.
    // Built once during indexing to provide clean, token-level discriminative
    // power measurements for coverage scoring without n-gram approximation.
    private Dictionary<string, float>? _wordIdfCache;

    public Tokenizer Tokenizer => _tokenizer;
    public DocumentCollection Documents => _documents;
    public TermCollection TermCollection => _termCollection;
    
    // Index accessors
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
        
        // Initialize FST builder
        _fstBuilder = new FstBuilder();
        
        // Initialize short query index
        char[] delimiters = tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        _shortQueryIndex = new PositionalPrefixIndex(delimiters: delimiters);
    }

    public Document IndexDocument(Document document)
    {
        Document doc = _documents.AddDocument(document);
        bool isSegmentContinuation = doc.SegmentNumber > 0;

        (ushort Position, byte WeightIndex)[] fieldBoundaries =
            document.Fields.GetSearchableTexts('§', out string concatenatedText);

        // Preserve the original concatenated text (including casing and formatting)
        // for external consumption and parity tests.
        doc.IndexedText = concatenatedText;

        // Internal indexing text is normalized, lowercased and optionally
        // canonicalized for synonym equivalence. This text is used only for
        // n-gram indexing and short-query structures, not for user-visible fields.
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
                // Add to FST builder
                _fstBuilder?.AddForwardOnly(shingle.Text, _termCollection.Count - 1);
            }
            
            term.FirstCycleAdd(doc.Id, _stopTermLimit, removeDuplicates: isSegmentContinuation, fieldWeight);
        }
        
        // Index for short query resolution (also using canonicalized text)
        _shortQueryIndex?.IndexDocument(indexText, doc.Id);

        return doc;
    }

    private float DetermineFieldWeight(int tokenPosition, (ushort Position, byte WeightIndex)[] fieldBoundaries)
    {
        if (fieldBoundaries.Length == 0)
            return 1.0f;

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

        // Each worker gets its own local vector of document lengths to avoid contention.
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
                if (term == null)
                    continue;

                List<int>? docIds = term.GetDocumentIds();
                List<byte>? weights = term.GetWeights();

                if (docIds == null || weights == null)
                    continue;

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

        // Aggregate local vectors into the final document length array.
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
        
        // Build word-level IDF cache for clean discriminative power measurements
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
    
    /// <summary>
    /// Builds all optimized indexes (FST, short query, document metadata cache).
    /// Call after all documents have been indexed.
    /// </summary>
    public void BuildOptimizedIndexes()
    {
        // Build FST from the current term collection if it is not already available
        // (e.g., after a load from persistence).
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
        
        // Finalize the short-query positional prefix index and create resolver.
        if (_shortQueryIndex != null)
        {
            _shortQueryIndex.Finalize();
            
            char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            _shortQueryResolver = new ShortQueryResolver(_shortQueryIndex, _documents, delimiters);
        }
        
        // Build document metadata cache for fusion signal optimization
        BuildDocumentMetadataCache();
    }
    
    /// <summary>
    /// Builds the document metadata cache by tokenizing each document and extracting
    /// first token and token count. This is done once at index time to avoid repeated
    /// tokenization during scoring.
    /// </summary>
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
            
            // Use the same normalization pipeline as search: lowercase, optional
            // text normalization, and synonym canonicalization when configured.
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
            
            // Tokenize to find first token and count tokens
            string firstToken = string.Empty;
            ushort tokenCount = 0;
            int i = 0;
            
            // Skip leading delimiters
            while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
                i++;
            
            while (i < textSpan.Length && tokenCount < ushort.MaxValue)
            {
                // Find end of current token
                int tokenStart = i;
                while (i < textSpan.Length && !delimiterSet.Contains(textSpan[i]))
                    i++;
                
                int tokenLength = i - tokenStart;
                
                if (tokenLength > 0)
                {
                    if (tokenCount == 0)
                    {
                        // Capture first token
                        firstToken = textSpan.Slice(tokenStart, tokenLength).ToString();
                    }
                    tokenCount++;
                }
                
                // Skip delimiters to next token
                while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
                    i++;
            }
            
            _documentMetadataCache.Set(docId, new DocumentMetadata(firstToken, tokenCount));
        });
    }
    
    /// <summary>
    /// Gets terms by prefix using the FST index.
    /// Returns term IDs (outputs) that can be used to look up Term objects.
    /// </summary>
    public void GetTermsByPrefix(string prefix, List<int> termIds)
    {
        _fstIndex?.GetByPrefix(prefix.AsSpan(), termIds);
    }
    
    /// <summary>
    /// Checks if any term starts with the given prefix.
    /// </summary>
    public bool HasTermPrefix(string prefix)
    {
        return _fstIndex?.HasPrefix(prefix.AsSpan()) ?? false;
    }
    
    internal ScoreArray Search(string queryText, Dictionary<int, byte>? bestSegmentsMap = null, int queryIndex = 0)
        => SearchWithMaxScore(queryText, int.MaxValue, bestSegmentsMap, queryIndex);

    internal ScoreArray SearchWithMaxScore(string queryText, int topK, Dictionary<int, byte>? bestSegmentsMap = null, int queryIndex = 0)
    {
        Shingle[] queryShingles = _tokenizer.TokenizeForSearch(queryText, out _, false);

        List<Term> queryTerms = [];
        foreach (Shingle shingle in queryShingles)
        {
            Term? term = _termCollection.GetTerm(shingle.Text);
            if (term != null && term.DocumentFrequency <= _stopTermLimit)
            {
                term.QueryOccurrences = (byte)shingle.Occurrences;
                queryTerms.Add(term);
            }
        }

        if (queryTerms.Count == 0 || _documents.Count == 0)
            return new ScoreArray();

        int totalDocs = _documents.Count;
        if (_docLengths == null || _docLengths.Length != totalDocs || _avgDocLength <= 0f)
            BuildInvertedLists(cancellationToken: CancellationToken.None);

        return Bm25Scorer.Search(queryTerms, topK, totalDocs, _docLengths!, _avgDocLength, _stopTermLimit, _documents, bestSegmentsMap, queryIndex, _shortQueryIndex, queryText);
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
        
        // Create resolver if short query index was loaded
        if (_shortQueryIndex != null)
        {
            char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            _shortQueryResolver = new ShortQueryResolver(_shortQueryIndex, _documents, delimiters);
        }
        
    }
    
    /// <summary>
    /// Builds a word-level IDF cache by tokenizing all documents and computing
    /// document frequency per word token. This provides clean, token-level
    /// discriminative power for coverage scoring without n-gram approximations.
    /// </summary>
    private void BuildWordIdfCache()
    {
        int totalDocs = _documents.Count;
        if (totalDocs == 0)
        {
            _wordIdfCache = new Dictionary<string, float>();
            return;
        }
        
        char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        
        // Count document frequency for each word
        Dictionary<string, int> wordDocFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        for (int docId = 0; docId < totalDocs; docId++)
        {
            Document? doc = _documents.GetDocument(docId);
            if (doc == null || doc.Deleted || string.IsNullOrEmpty(doc.IndexedText))
                continue;
            
            // Tokenize and normalize (same as coverage will do at query time)
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
        
        // Compute IDF for each word
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
