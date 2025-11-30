using Infidex.Api;
using Infidex.Core;
using Infidex.Coverage;
using Infidex.Indexing;
using Infidex.Tokenization;
using Infidex.Filtering;
using Infidex.Scoring;
using Infidex.Synonyms;
using System.Collections.Concurrent;

namespace Infidex;

public enum SearchEngineStatus { Ready, Indexing, Loading }

/// <summary>
/// Main search engine combining TF-IDF relevancy ranking with Coverage lexical matching.
/// Thread-safe for concurrent searching and indexing.
/// </summary>
public class SearchEngine : IDisposable
{
    private readonly VectorModel _vectorModel;
    private readonly CoverageEngine? _coverageEngine;
    private readonly CoverageSetup? _coverageSetup;
    private readonly WordMatcher.WordMatcher? _wordMatcher;
    private readonly SearchPipeline _searchPipeline;
    private readonly SynonymMap? _synonymMap;
    private bool _isIndexed;
    private DocumentFields? _documentFieldSchema;

    private readonly ThreadLocal<FilterCompiler> _filterCompiler = new ThreadLocal<FilterCompiler>(() => new FilterCompiler());
    private readonly ThreadLocal<FilterVM> _filterVM = new ThreadLocal<FilterVM>(() => new FilterVM());
    private readonly ConcurrentDictionary<Filter, CompiledFilter> _compiledFilterCache = new ConcurrentDictionary<Filter, CompiledFilter>();
    private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();
    private volatile SearchEngineStatus _status = SearchEngineStatus.Ready;

    public event EventHandler<int>? ProgressChanged;
    public SearchEngineStatus Status { get => _status; private set => _status = value; }
    
    /// <summary>
    /// Gets the synonym map for this search engine. Null if synonyms are not enabled.
    /// </summary>
    public SynonymMap? SynonymMap => _synonymMap;

    public SearchEngine(
        int[] indexSizes,
        int startPadSize = 2,
        int stopPadSize = 0,
        bool enableCoverage = true,
        TextNormalizer? textNormalizer = null,
        TokenizerSetup? tokenizerSetup = null,
        CoverageSetup? coverageSetup = null,
        int stopTermLimit = 1_250_000,
        WordMatcherSetup? wordMatcherSetup = null,
        float[]? fieldWeights = null,
        SynonymMap? synonymMap = null)
    {
        textNormalizer ??= TextNormalizer.CreateDefault();
        tokenizerSetup ??= TokenizerSetup.CreateDefault();

        Tokenizer tokenizer = new Tokenizer(indexSizes, startPadSize, stopPadSize, textNormalizer, tokenizerSetup);
        _vectorModel = new VectorModel(tokenizer, stopTermLimit, fieldWeights, synonymMap);
        _vectorModel.ProgressChanged += (s, p) => ProgressChanged?.Invoke(this, 50 + p / 2);

        if (enableCoverage)
        {
            _coverageSetup = coverageSetup ?? CoverageSetup.CreateDefault();
            _coverageEngine = new CoverageEngine(tokenizer, _coverageSetup);
        }

        if (wordMatcherSetup != null && tokenizerSetup != null)
            _wordMatcher = new WordMatcher.WordMatcher(wordMatcherSetup, tokenizerSetup.Delimiters, textNormalizer);

        _synonymMap = synonymMap;
        _searchPipeline = new SearchPipeline(_vectorModel, _coverageEngine, _coverageSetup, _wordMatcher, _synonymMap);
        _isIndexed = false;
    }

    public static SearchEngine CreateDefault()
    {
        ConfigurationParameters config = ConfigurationParameters.GetConfig(400);
        return new SearchEngine(
            indexSizes: config.IndexSizes,
            startPadSize: config.StartPadSize,
            stopPadSize: config.StopPadSize,
            enableCoverage: true,
            textNormalizer: config.TextNormalizer,
            tokenizerSetup: config.TokenizerSetup,
            coverageSetup: null,
            stopTermLimit: config.StopTermLimit,
            wordMatcherSetup: config.WordMatcherSetup,
            fieldWeights: config.FieldWeights);
    }

    public static SearchEngine CreateMinimal() => new SearchEngine(indexSizes: [3], startPadSize: 2, stopPadSize: 0, enableCoverage: false);

    public void IndexDocuments(IEnumerable<Document> documents, IProgress<int>? progress = null)
    {
        _rwLock.EnterWriteLock();
        try
        {
            Status = SearchEngineStatus.Indexing;
            IndexDocumentsInternal(documents, progress);
            Status = SearchEngineStatus.Ready;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public async Task IndexDocumentsAsync(IEnumerable<Document> documents, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            _rwLock.EnterWriteLock();
            try
            {
                Status = SearchEngineStatus.Indexing;
                ct.ThrowIfCancellationRequested();
                IndexDocumentsInternal(documents, progress, ct);
                Status = SearchEngineStatus.Ready;
            }
            finally { _rwLock.ExitWriteLock(); }
        }, ct);
    }

    private void IndexDocumentsInternal(IEnumerable<Document> documents, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        List<Document> docList = documents.ToList();
        int total = docList.Count;
        int current = 0;

        // Explicitly invalidate index to ensure exception safety (if loop crashes)
        // and to signal that we are in a dirty state even if holding the lock.
        _isIndexed = false;

        foreach (Document doc in docList)
        {
            if (current % 100 == 0 && ct.IsCancellationRequested)
                ct.ThrowIfCancellationRequested();

            if (_documentFieldSchema == null && doc.Fields != null)
                _documentFieldSchema = doc.Fields;

            Document stored = _vectorModel.IndexDocument(doc);
            _wordMatcher?.Load(stored.IndexedText, stored.Id);

            current++;
            if (total > 0)
            {
                int percent = (int)(current * 50.0 / total);
                ProgressChanged?.Invoke(this, percent);
                progress?.Report(percent);
            }
        }

        EventHandler<int>? progressForwarder = null;
        if (progress != null)
        {
            progressForwarder = (sender, p) => progress.Report(50 + p / 2);
            _vectorModel.ProgressChanged += progressForwarder;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            
            // Always rebuild inverted lists after batch indexing to account for new documents.
            // Previously we only built if (!_isIndexed), which caused stale indexes during
            // incremental batch updates where _isIndexed was already true.
            _vectorModel.BuildInvertedLists(cancellationToken: ct);

            // After we have full corpus statistics (terms, documents, doc lengths,
            // word-level IDF cache), re-wire them into the coverage engine so that
            // coverage scoring for this in-memory index uses the same metadata as
            // a persisted+loaded index. This fixes subtle parity issues where
            // CoverageEngine was originally constructed before any documents were
            // indexed (totalDocs==0), causing fallback IDF computations.
            if (_coverageEngine != null)
            {
                _coverageEngine.SetCorpusStatistics(_vectorModel.TermCollection, _vectorModel.Documents.Count);
                _coverageEngine.SetDocumentMetadataCache(_vectorModel.DocumentMetadataCache);
                _coverageEngine.SetWordIdfCache(_vectorModel.WordIdfCache);
            }

            _isIndexed = true;
            _vectorModel.BuildOptimizedIndexes();
        }
        finally
        {
            if (progressForwarder != null)
                _vectorModel.ProgressChanged -= progressForwarder;
        }
    }

    public void IndexDocument(Document document)
    {
        _rwLock.EnterWriteLock();
        try
        {
            Status = SearchEngineStatus.Indexing;
            Document stored = _vectorModel.IndexDocument(document);
            _wordMatcher?.Load(stored.IndexedText, stored.Id);
            _isIndexed = false;
            Status = SearchEngineStatus.Ready;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    /// <summary>
    /// Flushes the active in-memory segment to a disk segment.
    /// </summary>
    public void Flush(string segmentPath)
    {
        _rwLock.EnterWriteLock();
        try
        {
            Status = SearchEngineStatus.Indexing;
            _vectorModel.Flush(segmentPath);
            _isIndexed = true; // Flushed state is indexed
            Status = SearchEngineStatus.Ready;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public void CalculateWeights()
    {
        _rwLock.EnterWriteLock();
        try
        {
            Status = SearchEngineStatus.Indexing;
            _vectorModel.CalculateWeights();
            _isIndexed = true;
            Status = SearchEngineStatus.Ready;
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public async Task CalculateWeightsAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            _rwLock.EnterWriteLock();
            try
            {
                Status = SearchEngineStatus.Indexing;
                ct.ThrowIfCancellationRequested();
                _vectorModel.BuildInvertedLists(cancellationToken: ct);
                _isIndexed = true;
                Status = SearchEngineStatus.Ready;
            }
            finally { _rwLock.ExitWriteLock(); }
        }, ct);
    }
    
    public Result Search(Query query)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (!_isIndexed)
                return Result.MakeEmptyResult();

            Query q = new Query(query);
            string qText = q.Text.Trim();

            // Apply the same normalization (diacritics/whitespace) as indexing
            // before canonicalization so that synonyms operate in the normalized
            // token space
            if (_vectorModel.Tokenizer.TextNormalizer != null)
            {
                qText = _vectorModel.Tokenizer.TextNormalizer.Normalize(qText);
            }
            qText = qText.ToLowerInvariant();

            // Canonicalize query text using the same synonym map and delimiters
            // as indexing so that equivalent surface forms are treated
            // identically throughout the pipeline.
            if (_synonymMap != null &&
                _synonymMap.HasCanonicalMappings &&
                _vectorModel.Tokenizer.TokenizerSetup != null)
            {
                qText = _synonymMap.CanonicalizeText(
                    qText,
                    _vectorModel.Tokenizer.TokenizerSetup.Delimiters);
            }

            q.Text = qText;

            q.TimeOutLimitMilliseconds = Math.Clamp(q.TimeOutLimitMilliseconds, 0, 10000);

            if (string.IsNullOrWhiteSpace(q.Text) && q.EnableFacets)
                return HandleEmptyQueryWithFacets(q);

            if (string.IsNullOrWhiteSpace(q.Text))
                return Result.MakeEmptyResult();

            ScoreEntry[] results = _searchPipeline.Execute(
                q.Text,
                q.EnableCoverage ? (q.CoverageSetup ?? _coverageSetup) : null,
                q.CoverageDepth,
                q.MaxNumberOfRecordsToReturn);

            results = ApplyPostProcessing(results, q);

            Dictionary<string, KeyValuePair<string, int>[]>? facets = null;
            if (q.EnableFacets)
                facets = FacetBuilder.BuildFacets(results, _vectorModel.Documents, _documentFieldSchema);

            ScoreEntry[] topResults = results.Take(q.MaxNumberOfRecordsToReturn).ToArray();

            return new Result(topResults, facets,
                topResults.Length > 0 ? topResults.Length - 1 : 0,
                topResults.Length > 0 ? topResults[^1].Score : 0f,
                false)
            { TotalCandidates = results.Length };
        }
        finally { _rwLock.ExitReadLock(); }
    }

    private Result HandleEmptyQueryWithFacets(Query query)
    {
        List<ScoreEntry> allResults = [];
        for (int i = 0; i < _vectorModel.Documents.Count; i++)
        {
            Document? doc = _vectorModel.Documents.GetDocument(i);
            if (doc != null && !doc.Deleted)
                allResults.Add(new ScoreEntry(ushort.MaxValue, doc.DocumentKey));
        }

        ScoreEntry[] arr = allResults.ToArray();

        if (query.Filter != null)
        {
            ResultProcessor processor = new ResultProcessor(_vectorModel.Documents, _filterCompiler, _filterVM, _compiledFilterCache);
            arr = processor.ApplyFilter(arr, query.Filter);
        }

        ScoreEntry[] top = arr.Take(query.MaxNumberOfRecordsToReturn).ToArray();
        var facets = FacetBuilder.BuildFacets(top, _vectorModel.Documents, _documentFieldSchema);

        return new Result(top, facets,
            top.Length > 0 ? top.Length - 1 : 0,
            top.Length > 0 ? top[^1].Score : 0f,
            false);
    }

    private ScoreEntry[] ApplyPostProcessing(ScoreEntry[] results, Query query)
    {
        ResultProcessor processor = new ResultProcessor(_vectorModel.Documents, _filterCompiler, _filterVM, _compiledFilterCache);

        if (query.Filter != null)
            results = processor.ApplyFilter(results, query.Filter);

        if (query.EnableBoost && query.Boosts != null && query.Boosts.Length > 0)
            results = processor.ApplyBoosts(results, query.Boosts);

        if (query.SortBy != null)
            results = processor.ApplySort(results, query.SortBy, query.SortAscending);

        return results;
    }
    
    public Document? GetDocument(long documentKey)
    {
        _rwLock.EnterReadLock();
        try { return _vectorModel.Documents.GetDocumentByPublicKey(documentKey); }
        finally { _rwLock.ExitReadLock(); }
    }

    public List<Document> GetDocuments(long documentKey)
    {
        _rwLock.EnterReadLock();
        try { return _vectorModel.Documents.GetDocumentsByKey(documentKey); }
        finally { _rwLock.ExitReadLock(); }
    }

    public IndexStatistics GetStatistics()
    {
        _rwLock.EnterReadLock();
        try { return new IndexStatistics(_vectorModel.Documents.Count, _vectorModel.TermCollection.Count); }
        finally { _rwLock.ExitReadLock(); }
    }
    
    public void Save(string filePath)
    {
        _rwLock.EnterWriteLock();
        try
        {
            using FileStream stream = File.Create(filePath);
            using BinaryWriter writer = new BinaryWriter(stream);
            _vectorModel.SaveToStream(writer);
            writer.Write(_wordMatcher != null);
            _wordMatcher?.Save(writer);
        }
        finally { _rwLock.ExitWriteLock(); }
    }

    public static SearchEngine Load(
        string filePath,
        int[] indexSizes,
        int startPadSize = 2,
        int stopPadSize = 0,
        bool enableCoverage = true,
        TextNormalizer? textNormalizer = null,
        TokenizerSetup? tokenizerSetup = null,
        CoverageSetup? coverageSetup = null,
        int stopTermLimit = 1_250_000,
        WordMatcherSetup? wordMatcherSetup = null,
        float[]? fieldWeights = null)
    {
        textNormalizer ??= TextNormalizer.CreateDefault();
        tokenizerSetup ??= TokenizerSetup.CreateDefault();

        Tokenizer tokenizer = new Tokenizer(indexSizes, startPadSize, stopPadSize, textNormalizer, tokenizerSetup);
        VectorModel vectorModel = new VectorModel(tokenizer, stopTermLimit, fieldWeights);

        using FileStream stream = File.OpenRead(filePath);
        using BinaryReader reader = new BinaryReader(stream);
        vectorModel.LoadFromStream(reader);

        // Rebuild all derived statistics (document lengths, word-level IDF cache, etc.)
        // from the loaded index. This mirrors the work done after IndexDocuments and
        // ensures that coverage and fusion scoring see the same metadata for both
        // in-memory and persisted indexes.
        vectorModel.CalculateWeights();

        // Now that the vector model is fully initialized, create the search engine
        // and its coverage pipeline. The pipeline constructor will wire the freshly
        // built WordIdfCache into the coverage engine.
        SearchEngine engine = new SearchEngine(vectorModel, enableCoverage, coverageSetup, tokenizerSetup, wordMatcherSetup, textNormalizer);

        bool hasWordMatcher = reader.ReadBoolean();
        if (hasWordMatcher && engine._wordMatcher != null)
            engine._wordMatcher.Load(reader);
        else if (hasWordMatcher && engine._wordMatcher == null)
            throw new InvalidOperationException("Index contains WordMatcher data but engine is configured without it.");
        else if (!hasWordMatcher && engine._wordMatcher != null)
            throw new InvalidDataException("Index file is missing required WordMatcher data.");

        return engine;
    }

    public static async Task<SearchEngine> LoadAsync(
        string filePath, int[] indexSizes, int startPadSize = 2, int stopPadSize = 0,
        bool enableCoverage = true, TextNormalizer? textNormalizer = null, TokenizerSetup? tokenizerSetup = null,
        CoverageSetup? coverageSetup = null, int stopTermLimit = 1_250_000,
        WordMatcherSetup? wordMatcherSetup = null, float[]? fieldWeights = null, CancellationToken ct = default)
    {
        return await Task.Run(() => Load(filePath, indexSizes, startPadSize, stopPadSize, enableCoverage,
            textNormalizer, tokenizerSetup, coverageSetup, stopTermLimit, wordMatcherSetup, fieldWeights));
    }

    private SearchEngine(
        VectorModel vectorModel,
        bool enableCoverage,
        CoverageSetup? coverageSetup,
        TokenizerSetup? tokenizerSetup,
        WordMatcherSetup? wordMatcherSetup,
        TextNormalizer? textNormalizer = null)
    {
        _vectorModel = vectorModel;
        _isIndexed = true;

        if (enableCoverage)
        {
            _coverageSetup = coverageSetup ?? CoverageSetup.CreateDefault();
            _coverageEngine = new CoverageEngine(_vectorModel.Tokenizer, _coverageSetup);
        }

        if (wordMatcherSetup != null && tokenizerSetup != null)
            _wordMatcher = new WordMatcher.WordMatcher(wordMatcherSetup, tokenizerSetup.Delimiters, textNormalizer);

        _searchPipeline = new SearchPipeline(_vectorModel, _coverageEngine, _coverageSetup, _wordMatcher);
    }

    public void Dispose()
    {
        _rwLock?.Dispose();
        _wordMatcher?.Dispose();
        _filterCompiler?.Dispose();
        _filterVM?.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class IndexStatistics(int documentCount, int vocabularySize)
{
    public int DocumentCount { get; } = documentCount;
    public int VocabularySize { get; } = vocabularySize;
    public override string ToString() => $"{DocumentCount} documents, {VocabularySize} terms";
}
