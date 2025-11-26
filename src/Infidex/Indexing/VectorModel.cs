using Infidex.Core;
using Infidex.Tokenization;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;

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

    private float[]? _docLengths;
    private float _avgDocLength;
    
    // FST-based indexes
    private FstIndex? _fstIndex;
    private FstBuilder? _fstBuilder;
    private PositionalPrefixIndex? _shortQueryIndex;
    private ShortQueryResolver? _shortQueryResolver;

    public Tokenizer Tokenizer => _tokenizer;
    public DocumentCollection Documents => _documents;
    public TermCollection TermCollection => _termCollection;
    
    // Index accessors
    internal FstIndex? FstIndex => _fstIndex;
    internal PositionalPrefixIndex? ShortQueryIndex => _shortQueryIndex;
    internal ShortQueryResolver? ShortQueryResolver => _shortQueryResolver;

    public event EventHandler<int>? ProgressChanged;
    public bool EnableDebugLogging { get; set; }

    public VectorModel(Tokenizer tokenizer, int stopTermLimit = 1_250_000, float[]? fieldWeights = null)
    {
        _tokenizer = tokenizer;
        _stopTermLimit = stopTermLimit;
        _termCollection = new TermCollection();
        _documents = new DocumentCollection();
        _fieldWeights = fieldWeights ?? ConfigurationParameters.DefaultFieldWeights;
        
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
        doc.IndexedText = concatenatedText;

        string text = concatenatedText.ToLowerInvariant();
        List<Shingle> shingles = _tokenizer.TokenizeForIndexing(text, isSegmentContinuation);

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
        
        // Index for short query resolution
        _shortQueryIndex?.IndexDocument(text, doc.Id);

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
    /// Builds all optimized indexes (FST, short query).
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
    
    internal ScoreArray Search(string queryText, Span2D<byte> bestSegments = default, int queryIndex = 0)
        => SearchWithMaxScore(queryText, int.MaxValue, bestSegments, queryIndex);

    internal ScoreArray SearchWithMaxScore(string queryText, int topK, Span2D<byte> bestSegments = default, int queryIndex = 0)
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

        return Bm25Scorer.Search(queryTerms, topK, totalDocs, _docLengths!, _avgDocLength, _stopTermLimit, _documents, bestSegments, queryIndex);
    }

    public void Save(string filePath)
    {
        using FileStream stream = File.Create(filePath);
        using BinaryWriter writer = new BinaryWriter(stream);
        SaveToStream(writer);
    }

    public async Task SaveAsync(string filePath) => await Task.Run(() => Save(filePath));

    internal void SaveToStream(BinaryWriter writer)
        => VectorModelPersistence.SaveToStream(writer, _documents, _termCollection, _fstIndex, _shortQueryIndex);

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
            out _fstIndex, out _shortQueryIndex);
        
        // Create resolver if short query index was loaded
        if (_shortQueryIndex != null)
        {
            char[] delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            _shortQueryResolver = new ShortQueryResolver(_shortQueryIndex, _documents, delimiters);
        }
    }
}
