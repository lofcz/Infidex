using Infidex.Api;
using Infidex.Core;
using Infidex.Coverage;
using Infidex.Indexing;
using Infidex.Tokenization;
using Infidex.Utilities;
using Infidex.Metrics;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Filtering;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Infidex;

/// <summary>
/// Indicates the current operational state of the search engine.
/// </summary>
public enum SearchEngineStatus
{
    Ready,
    Indexing,
    Loading
}

/// <summary>
/// Main search engine that combines TF-IDF relevancy ranking (Stage 1) 
/// with Coverage lexical matching (Stage 2) using score fusion.
/// Provides thread-safe concurrency for searching and indexing.
/// </summary>
public class SearchEngine : IDisposable
{
    private readonly VectorModel _vectorModel;
    private readonly CoverageEngine? _coverageEngine;
    private readonly CoverageSetup? _coverageSetup;
    private readonly WordMatcher.WordMatcher? _wordMatcher;
    private bool _isIndexed;
    private Api.DocumentFields? _documentFieldSchema;
    
    // Bytecode VM for filter execution
    // Thread-local to avoid state corruption between concurrent threads
    private readonly ThreadLocal<FilterCompiler> _filterCompiler = new ThreadLocal<FilterCompiler>(() => new FilterCompiler());
    private readonly ThreadLocal<FilterVM> _filterVM = new ThreadLocal<FilterVM>(() => new FilterVM());
    // Thread-safe cache for compiled filters
    private readonly ConcurrentDictionary<Api.Filter, CompiledFilter> _compiledFilterCache = new ConcurrentDictionary<Api.Filter, CompiledFilter>();
    
    // Concurrency management
    // Reader/writer lock: multiple concurrent searches, writers block readers.
    private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();
    private volatile SearchEngineStatus _status = SearchEngineStatus.Ready;
    
    /// <summary>
    /// When enabled, Search will emit detailed debug information about
    /// TF-IDF candidates, coverage scores, and final fused scores to the console.
    /// Intended for analysis/parity work, not for production use.
    /// </summary>
    public bool EnableDebugLogging { get; set; }

    /// <summary>
    /// Event fired when indexing progress changes (0-100%)
    /// </summary>
    public event EventHandler<int>? ProgressChanged;
    
    /// <summary>
    /// Gets the current operational status of the engine.
    /// </summary>
    public SearchEngineStatus Status
    {
        get => _status;
        private set => _status = value;
    }

    /// <summary>
    /// Creates a new search engine with the specified configuration
    /// </summary>
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
        float[]? fieldWeights = null)
    {
        // Setup tokenizer
        textNormalizer ??= TextNormalizer.CreateDefault();
        tokenizerSetup ??= TokenizerSetup.CreateDefault();
        
        Tokenizer tokenizer = new Tokenizer(
            indexSizes,
            startPadSize,
            stopPadSize,
            textNormalizer,
            tokenizerSetup);
        
        // Setup vector model (Stage 1) with field weights
        _vectorModel = new VectorModel(tokenizer, stopTermLimit, fieldWeights);
        _vectorModel.ProgressChanged += (s, p) => 
        {
            // Map vector model progress (0-100) to second half of total progress (50-100)
            ProgressChanged?.Invoke(this, 50 + p / 2);
        };
        
        // Setup coverage (Stage 2)
        if (enableCoverage)
        {
            _coverageSetup = coverageSetup ?? CoverageSetup.CreateDefault();
            _coverageEngine = new CoverageEngine(tokenizer, _coverageSetup);
        }

        // Setup word matcher (exact + LD1 + affix), mirroring original config 400 behavior
        if (wordMatcherSetup != null && tokenizerSetup != null)
        {
            _wordMatcher = new WordMatcher.WordMatcher(wordMatcherSetup, tokenizerSetup.Delimiters);
        }
        
        _isIndexed = false;
    }
    
    /// <summary>
    /// Creates a search engine with default configuration.
    /// - 3-gram indexing only
    /// - StartPadSize = 2, StopPadSize = 0
    /// - Case-insensitive search
    /// - Text normalization and tokenizer delimiters as in config 400
    /// - WordMatcher enabled (LD1 + affix) via Coverage/WordMatcher pipeline (wired elsewhere)
    /// - Field weights for multi-field support
    /// </summary>
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
    
    /// <summary>
    /// Creates a minimal search engine (relevancy ranking only, no coverage)
    /// </summary>
    public static SearchEngine CreateMinimal()
    {
        return new SearchEngine(
            indexSizes: [3],
            startPadSize: 2,
            stopPadSize: 0,
            enableCoverage: false);
    }
    
    /// <summary>
    /// Indexes a collection of documents with progress reporting.
    /// Thread-safe: Blocks other indexing or search operations.
    /// </summary>
    public void IndexDocuments(IEnumerable<Document> documents, IProgress<int>? progress = null)
    {
        _rwLock.EnterWriteLock();
        try
        {
            Status = SearchEngineStatus.Indexing;
            IndexDocumentsInternal(documents, progress);
            Status = SearchEngineStatus.Ready;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Indexes a collection of documents asynchronously with cancellation support.
    /// </summary>
    public async Task IndexDocumentsAsync(IEnumerable<Document> documents, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        // Use Task.Run to offload the blocking lock acquisition and indexing to a background thread
        await Task.Run(() =>
        {
            _rwLock.EnterWriteLock();
            try
            {
                Status = SearchEngineStatus.Indexing;
                
                // Note: We check token before starting, but the internal loop isn't fully granularly cancelled 
                // in this simplified version unless we rewrite IndexDocumentsInternal to take a token.
                // For now, we check at the start.
                cancellationToken.ThrowIfCancellationRequested();
                
                IndexDocumentsInternal(documents, progress, cancellationToken);
                Status = SearchEngineStatus.Ready;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }, cancellationToken);
    }

    private void IndexDocumentsInternal(IEnumerable<Document> documents, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        // Convert to list to know total count for progress
        List<Document> docList = documents.ToList();
        int total = docList.Count;
        int current = 0;
        
        foreach (Document doc in docList)
        {
            // Check for cancellation periodically
            if (current % 100 == 0 && cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            // Capture field schema from first document (for faceting support)
            if (_documentFieldSchema == null && doc.Fields != null)
            {
                _documentFieldSchema = doc.Fields;
            }

            Document stored = _vectorModel.IndexDocument(doc);
            // Load into WordMatcher using internal Id (mirrors JsonIndex / CoreDocuments index)
            _wordMatcher?.Load(stored.IndexedText, stored.Id);
            
            current++;
            // Report progress for first phase (0-50%)
            if (total > 0)
            {
                int percent = (int)(current * 50.0 / total);
                ProgressChanged?.Invoke(this, percent);
                progress?.Report(percent);
            }
        }
        
        // Setup progress forwarding for Phase 2
        EventHandler<int>? progressForwarder = null;
        if (progress != null)
        {
            progressForwarder = (sender, p) => progress.Report(50 + p / 2);
            _vectorModel.ProgressChanged += progressForwarder;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            // Calculate TF-IDF weights after all documents are indexed (Phase 2: 50-100%)
            // We only perform the expensive global rebuild on the first full indexing pass.
            // Subsequent IndexDocuments calls are treated as incremental updates: they
            // update the inverted index structures but avoid re-normalizing the entire corpus.
            if (!_isIndexed)
            {
                _vectorModel.BuildInvertedLists(cancellationToken: cancellationToken);
                _isIndexed = true;
            }
        }
        finally
        {
            if (progressForwarder != null)
            {
                _vectorModel.ProgressChanged -= progressForwarder;
            }
        }
    }
    
    /// <summary>
    /// Indexes a single document (must call CalculateWeights after batch indexing).
    /// Thread-safe: Blocks other indexing or search operations.
    /// </summary>
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
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Recalculates TF-IDF weights (call after batch indexing).
    /// Thread-safe: Blocks other indexing or search operations.
    /// </summary>
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
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Recalculates TF-IDF weights asynchronously.
    /// </summary>
    public async Task CalculateWeightsAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            _rwLock.EnterWriteLock();
            try
            {
                Status = SearchEngineStatus.Indexing;
                cancellationToken.ThrowIfCancellationRequested();
                _vectorModel.BuildInvertedLists(cancellationToken: cancellationToken);
                _isIndexed = true;
                Status = SearchEngineStatus.Ready;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }, cancellationToken);
    }
    
    /// <summary>
    /// Searches for documents using a comprehensive Query object.
    /// Thread-safe: Allows concurrent searches but blocks during indexing.
    /// Supports faceting, filtering, sorting, boosting, and advanced options.
    /// </summary>
    /// <param name="query">Query with search parameters</param>
    /// <returns>Result with records, facets, and metadata</returns>
    public Api.Result Search(Api.Query query)
    {
        _rwLock.EnterReadLock();
        try
        {
            // If the index is not yet fully built, treat as an empty index rather than throwing.
            // This makes concurrent search+indexing scenarios safe: searches will either block
            // until indexing completes or see an empty, consistent snapshot.
            if (!_isIndexed)
            {
                return Api.Result.MakeEmptyResult();
            }
            
            // Validate and normalize query
            Query normalizedQuery = new Api.Query(query);
            normalizedQuery.Text = normalizedQuery.Text.Trim().ToLowerInvariant();
            normalizedQuery.TimeOutLimitMilliseconds = Math.Clamp(normalizedQuery.TimeOutLimitMilliseconds, 0, 10000);
            
            // Handle empty query with facets
            if (string.IsNullOrWhiteSpace(normalizedQuery.Text) && normalizedQuery.EnableFacets)
            {
                // Return all documents with facets
                List<ScoreEntry> allResults = [];
                for (int i = 0; i < _vectorModel.Documents.Count; i++)
                {
                    Document? doc = _vectorModel.Documents.GetDocument(i);
                    if (doc != null && !doc.Deleted)
                    {
                        allResults.Add(new ScoreEntry(ushort.MaxValue, doc.DocumentKey)); // Max score for all
                    }
                }
                
                ScoreEntry[] allResultsArray = allResults.ToArray();
                
                // Apply filter if specified
                if (normalizedQuery.Filter != null)
                {
                    allResultsArray = ApplyFilter(allResultsArray, normalizedQuery.Filter);
                }
                
                ScoreEntry[] topResultsArray = allResultsArray.Take(normalizedQuery.MaxNumberOfRecordsToReturn).ToArray();
                Dictionary<string, KeyValuePair<string, int>[]> facetsAll = FacetBuilder.BuildFacets(topResultsArray, _vectorModel.Documents, _documentFieldSchema);
                
                return new Api.Result(topResultsArray, facetsAll,
                    topResultsArray.Length > 0 ? topResultsArray.Length - 1 : 0,
                    topResultsArray.Length > 0 ? topResultsArray[^1].Score : (byte)0,
                    false);
            }
            
            if (string.IsNullOrWhiteSpace(normalizedQuery.Text))
            {
                return Api.Result.MakeEmptyResult();
            }
            
            // Perform the search using existing logic
            // Pass MaxNumberOfRecordsToReturn to ensure truncation doesn't cut below requested amount
            ScoreEntry[] results = PerformSearchInternal(normalizedQuery.Text, 
                normalizedQuery.EnableCoverage ? (normalizedQuery.CoverageSetup ?? _coverageSetup) : null,
                normalizedQuery.CoverageDepth,
                normalizedQuery.MaxNumberOfRecordsToReturn);
            
            // Apply filter if specified
            if (normalizedQuery.Filter != null)
            {
                results = ApplyFilter(results, normalizedQuery.Filter);
            }
            
            // Apply boosts if enabled
            if (normalizedQuery.EnableBoost && normalizedQuery.Boosts != null && normalizedQuery.Boosts.Length > 0)
            {
                results = ApplyBoosts(results, normalizedQuery.Boosts);
            }
            
            // Apply sorting if specified
            if (normalizedQuery.SortBy != null)
            {
                results = ApplySort(results, normalizedQuery.SortBy, normalizedQuery.SortAscending);
            }
            
            // Build facets if enabled
            Dictionary<string, KeyValuePair<string, int>[]>? facets = null;
            if (normalizedQuery.EnableFacets)
            {
                // Build facets from all results before taking top N
                facets = FacetBuilder.BuildFacets(results, _vectorModel.Documents, _documentFieldSchema);
            }
            
            // Take top N results
            ScoreEntry[] topResults = results.Take(normalizedQuery.MaxNumberOfRecordsToReturn).ToArray();
            
            return new Api.Result(topResults, facets,
                topResults.Length > 0 ? topResults.Length - 1 : 0,
                topResults.Length > 0 ? topResults[^1].Score : (byte)0,
                false)
            {
                TotalCandidates = results.Length
            };
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Internal method that performs the core search logic with coverage and WordMatcher
    /// </summary>
    private ScoreEntry[] PerformSearchInternal(string searchText, CoverageSetup? coverageSetup, int coverageDepth, int maxResults = int.MaxValue)
    {
        // Optional performance instrumentation (enabled via EnableDebugLogging)
        Stopwatch? perfStopwatch = null;
        long tfidfMs = 0;
        long topKMs = 0;
        long wordMatcherCoverageMs = 0;
        long tfidfCoverageMs = 0;
        long truncationMs = 0;

        if (EnableDebugLogging)
        {
            perfStopwatch = Stopwatch.StartNew();
        }

        if (string.IsNullOrWhiteSpace(searchText))
            return [];

        if (EnableDebugLogging)
        {
            Console.WriteLine($"[DEBUG] Search start: normalized=\"{searchText}\", coverageDepth={coverageDepth}");
        }
        
        // Allocate bestSegments tracking array
        long bestSegmentsPointer = 0;
        Span2D<byte> bestSegments = default;
        
        if (_vectorModel.Documents.Count > 0)
        {
            bestSegments = SpanAlloc.Alloc2D(_vectorModel.Documents.Count, 1, out bestSegmentsPointer);
        }
        
        try
        {
            // STAGE 1: Relevancy Ranking (TF-IDF Vector Space) with segment tracking
            _vectorModel.EnableDebugLogging = EnableDebugLogging;
            long tfidfStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
            ScoreArray relevancyScores = _vectorModel.Search(searchText, bestSegments, queryIndex: 0);
            if (perfStopwatch != null)
            {
                tfidfMs = perfStopwatch.ElapsedMilliseconds - tfidfStart;
            }

            if (EnableDebugLogging)
            {
                ScoreEntry[] tfidfAll = relevancyScores.GetAll();
                Console.WriteLine($"[DEBUG] Stage1 TF-IDF: {tfidfAll.Length} candidates");
                foreach (ScoreEntry e in tfidfAll)
                {
                    Console.WriteLine($"  [DEBUG]   TF-IDF docKey={e.DocumentId}, score={e.Score}");
                }
            }
            
            // If coverage is disabled, consolidate segments and return
            if (_coverageEngine == null || coverageSetup == null)
            {
                ScoreArray consolidated = ConsolidateSegments(relevancyScores, bestSegments);
                return consolidated.GetAll();
            }
            
            // STAGE 2: Coverage (Lexical Matching) - matches original exactly
            long topKStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
            ScoreEntry[] topCandidates = relevancyScores.GetTopK(coverageDepth);
            if (perfStopwatch != null)
            {
                topKMs = perfStopwatch.ElapsedMilliseconds - topKStart;
            }

            // Optional lexical pre-screen before full coverage
            if (coverageSetup != null && coverageSetup.EnableLexicalPrescreen && topCandidates.Length > 0)
            {
                topCandidates = ApplyLexicalPrescreen(searchText, topCandidates);
            }

            if (EnableDebugLogging)
            {
                Console.WriteLine($"[DEBUG] Stage2 Coverage: depth={coverageDepth}, top TF-IDF candidates (after prescreen={coverageSetup?.EnableLexicalPrescreen == true}):");
                foreach (ScoreEntry c in topCandidates)
                {
                    Console.WriteLine($"  [DEBUG]   top TF-IDF docKey={c.DocumentId}, score={c.Score}");
                }
            }

            // --- Build WordMatcher candidate set (internal indices) ---
            HashSet<int> wordMatcherInternalIds = LookupFuzzyWords(searchText);

                if (EnableDebugLogging && wordMatcherInternalIds.Count > 0)
                {
                    Console.WriteLine("[DEBUG] WordMatcher hits (internal IDs): " +
                                      string.Join(", ", wordMatcherInternalIds));
                }

                // --- Build unified document key set for coverage / truncation ---
                HashSet<long> uniqueDocKeys = [];
                foreach (ScoreEntry candidate in topCandidates)
                {
                    uniqueDocKeys.Add(candidate.DocumentId);
                }

                foreach (int internalId in wordMatcherInternalIds)
                {
                    Document? doc = _vectorModel.Documents.GetDocument(internalId);
                    if (doc != null && !doc.Deleted)
                    {
                        uniqueDocKeys.Add(doc.DocumentKey);
                    }
                }

                // Allocate LCS and word hits tracking (matches lcsAndWordHitsSpan semantics)
                long lcsSpanPointer = 0;
                Span2D<byte> lcsAndWordHitsSpan = default;
                Dictionary<long, int> documentKeyToIndex = new Dictionary<long, int>();
                int nextIndex = 0;

                if (uniqueDocKeys.Count > 0)
                {
                    foreach (long key in uniqueDocKeys)
                    {
                        documentKeyToIndex[key] = nextIndex++;
                    }

                    lcsAndWordHitsSpan = SpanAlloc.Alloc2D(2, nextIndex, out lcsSpanPointer);
                }

                try
                {
                    ScoreArray finalScores = new ScoreArray();
                    int maxWordHits = 0;

                    // --- Coverage for WordMatcher hits (isFromWordMatcher = true) ---
                    long wmCoverageStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
                    foreach (int internalId in wordMatcherInternalIds)
                    {
                        Document? doc = _vectorModel.Documents.GetDocument(internalId);
                        if (doc == null || doc.Deleted)
                            continue;

                        if (!documentKeyToIndex.TryGetValue(doc.DocumentKey, out int docIndex))
                            continue;

                        string docText = GetBestSegmentText(doc, bestSegments);

                        // Calculate LCS if not already cached in span
                        int lcsFromSpan = 0;
                        if (docIndex < lcsAndWordHitsSpan.Height)
                        {
                            lcsFromSpan = lcsAndWordHitsSpan[0, docIndex];
                            if (lcsFromSpan == 0)
                            {
                                int errorTolerance = 0;
                                if (coverageSetup != null && searchText.Length >= coverageSetup.CoverageQLimitForErrorTolerance)
                                {
                                    errorTolerance = (int)(searchText.Length * coverageSetup.CoverageLcsErrorToleranceRelativeq);
                                }
                                
                                lcsFromSpan = CalculateLcs(searchText, docText, errorTolerance);
                                lcsAndWordHitsSpan[0, docIndex] = (byte)Math.Min(lcsFromSpan, 255);
                            }
                        }

                        // FAST PATH: Use optimized CalculateRankedScore
                        ushort finalScore = _coverageEngine.CalculateRankedScore(
                            searchText,
                            docText,
                            lcsFromSpan,
                            (byte)0, // WordMatcher results have no base TF-IDF score (use 0)
                            out int wordHits
                        );

                        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
                        {
                            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(wordHits, 255);
                        }

                        maxWordHits = Math.Max(maxWordHits, wordHits);

                        if (EnableDebugLogging)
                        {
                            Console.WriteLine($"[DEBUG] Coverage (WordMatcher Fast): docKey={doc.DocumentKey}, final={finalScore}");
                        }

                        finalScores.Add(doc.DocumentKey, finalScore);
                    }
                    if (perfStopwatch != null)
                    {
                        wordMatcherCoverageMs = perfStopwatch.ElapsedMilliseconds - wmCoverageStart;
                    }

                    // --- Coverage for TF-IDF candidates (isFromWordMatcher = false) ---
                    long tfidfCoverageStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
                    foreach (ScoreEntry candidate in topCandidates)
                    {
                        Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(candidate.DocumentId);
                        if (doc == null || doc.Deleted)
                            continue;

                        if (!documentKeyToIndex.TryGetValue(doc.DocumentKey, out int docIndex))
                            continue;

                        string docText = GetBestSegmentText(doc, bestSegments);

                        // Calculate LCS if not already cached in span
                        int lcsFromSpan = 0;
                        if (docIndex < lcsAndWordHitsSpan.Height)
                        {
                            lcsFromSpan = lcsAndWordHitsSpan[0, docIndex];
                            if (lcsFromSpan == 0)
                            {
                                int errorTolerance = 0;
                                if (coverageSetup != null && searchText.Length >= coverageSetup.CoverageQLimitForErrorTolerance)
                                {
                                    errorTolerance = (int)(searchText.Length * coverageSetup.CoverageLcsErrorToleranceRelativeq);
                                }
                                
                                lcsFromSpan = CalculateLcs(searchText, docText, errorTolerance);
                                lcsAndWordHitsSpan[0, docIndex] = (byte)Math.Min(lcsFromSpan, 255);
                            }
                        }

                        // FAST PATH: Use optimized CalculateRankedScore
                        ushort finalScore = _coverageEngine.CalculateRankedScore(
                            searchText,
                            docText,
                            lcsFromSpan,
                            (byte)candidate.Score, // Base TF-IDF score
                            out int wordHits
                        );

                        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
                        {
                            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(wordHits, 255);
                        }

                        maxWordHits = Math.Max(maxWordHits, wordHits);
                        
                        if (EnableDebugLogging)
                        {
                            Console.WriteLine($"[DEBUG] Coverage (TF-IDF Fast): docKey={doc.DocumentKey}, final={finalScore}");
                        }

                        finalScores.Add(doc.DocumentKey, finalScore);
                    }
                    if (perfStopwatch != null)
                    {
                        tfidfCoverageMs = perfStopwatch.ElapsedMilliseconds - tfidfCoverageStart;
                    }

                    // If there is no lexical evidence at all (no word hits and no WordMatcher hits),
                    // do not return results purely based on TF-IDF scores. This matches the
                    // behavior observed in the reference engine for non-matching queries.
                    if (maxWordHits == 0 && wordMatcherInternalIds.Count == 0)
                    {
                        return [];
                    }

                    // STAGE 3: Consolidate segments - keep only best segment per DocumentKey
                    ScoreArray consolidatedFinalScores = ConsolidateSegments(finalScores, bestSegments);

                    // Get final top-K results (matches FilteredGetKTop: CoverageDepth + exactWordHits, where exactWordHits=0)
                    int exactWordHits = 0; // Always 0 in our simplified implementation
                    ScoreEntry[] finalResults = consolidatedFinalScores.GetTopK(coverageDepth + exactWordHits);

                    // Calculate final result count based on truncation and maxResults
                    // This matches the reference library logic (SearchEngine.cs:840)
                    int truncationIndex = -1;
                    if (coverageSetup != null && coverageSetup.Truncate && finalResults.Length > 0)
                    {
                        long truncStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
                        truncationIndex = CalculateTruncationIndex(
                            finalResults,
                            maxWordHits,
                            lcsAndWordHitsSpan,
                            documentKeyToIndex);
                        if (perfStopwatch != null)
                        {
                            truncationMs = perfStopwatch.ElapsedMilliseconds - truncStart;
                        }

                        if (EnableDebugLogging)
                        {
                            Console.WriteLine($"[TRUNC] finalResults.Length={finalResults.Length}, truncationIndex={truncationIndex}, maxWordHits={maxWordHits}");
                            Console.WriteLine($"[TRUNC] First 5 scores: {string.Join(", ", finalResults.Take(5).Select(r => $"{r.DocumentId}={r.Score}"))}");
                        }
                    }

                    // Reference logic: if truncation disabled or no truncation point found, use maxResults
                    // Otherwise, use the MINIMUM of (truncationIndex + 1) and maxResults
                    int resultCount = ((truncationIndex == -1 || coverageSetup == null || !coverageSetup.Truncate)
                        ? maxResults
                        : Math.Min(Math.Max(0, truncationIndex) + 1, maxResults));

                    if (EnableDebugLogging)
                    {
                        Console.WriteLine($"[TRUNC] resultCount={resultCount} (truncIdx={truncationIndex}, maxResults={maxResults})");
                    }

                    if (finalResults.Length > resultCount)
                    {
                        finalResults = finalResults.Take(resultCount).ToArray();
                    }

                    if (EnableDebugLogging)
                    {
                        Console.WriteLine("[DEBUG] Final fused results:");
                        foreach (ScoreEntry r in finalResults)
                        {
                            Console.WriteLine($"  [DEBUG]   docKey={r.DocumentId}, score={r.Score}");
                        }

                        if (perfStopwatch != null)
                        {
                            Console.WriteLine(
                                $"[PERF] total={perfStopwatch.ElapsedMilliseconds}ms, " +
                                $"tfidf={tfidfMs}ms, topK={topKMs}ms, " +
                                $"wmCoverage={wordMatcherCoverageMs}ms, " +
                                $"tfidfCoverage={tfidfCoverageMs}ms, truncation={truncationMs}ms");
                        }
                    }
                    
                    return finalResults;
                }
                finally
                {
                    if (lcsSpanPointer != 0)
                        SpanAlloc.Free(lcsSpanPointer);
                }
                
        }
        finally
        {
            if (bestSegmentsPointer != 0)
                SpanAlloc.Free(bestSegmentsPointer);
        }
    }
    
    /// <summary>
    /// Looks up fuzzy word matches (exact, LD1, affix) using the WordMatcher.
    /// Returns internal document indices.
    /// </summary>
    private HashSet<int> LookupFuzzyWords(string queryText)
    {
        HashSet<int> result = [];

        if (_wordMatcher == null)
            return result;

        // Exact + LD1 matches
        HashSet<int> ids = _wordMatcher.Lookup(queryText, filter: null);
        foreach (int id in ids)
        {
            result.Add(id);
        }

        // Affix matches if enabled in coverage setup
        if (_coverageSetup != null && _coverageSetup.CoverPrefixSuffix)
        {
            HashSet<int> affixIds = _wordMatcher.LookupAffix(queryText, filter: null);
            foreach (int id in affixIds)
            {
                result.Add(id);
            }
        }

        return result;
    }
    
    /// <summary>
    /// Lightweight lexical pre-screen on TF-IDF candidates, used to drop
    /// documents that only match extremely common query terms before running
    /// the full coverage pipeline. This implementation is intentionally
    /// conservative to avoid impacting fuzzy/typo behavior: it only acts when
    /// all query tokens are known in the index (i.e. no obvious typos) and
    /// requires that candidates contain at least one query token as a plain
    /// substring in the indexed text.
    /// </summary>
    private ScoreEntry[] ApplyLexicalPrescreen(string searchText, ScoreEntry[] candidates)
    {
        // Get query tokens using the same tokenizer configuration as coverage
        string[] queryTokens = _vectorModel.Tokenizer
            .GetWordTokensForCoverage(searchText, _coverageSetup?.MinWordSize ?? 2)
            .ToArray();

        if (queryTokens.Length == 0)
            return candidates;

        // If any token is not present in the index at all (df == 0) we treat
        // this as a potential typo/fuzzy case and completely disable
        // pre-screening to avoid dropping fuzzy matches.
        foreach (string token in queryTokens)
        {
            Term? term = _vectorModel.TermCollection.GetTerm(token);
            if (term == null || term.DocumentFrequency == 0)
            {
                return candidates;
            }
        }

        List<ScoreEntry> filtered = new List<ScoreEntry>(candidates.Length);

        foreach (ScoreEntry candidate in candidates)
        {
            Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(candidate.DocumentId);
            if (doc == null || doc.Deleted)
                continue;

            string text = doc.IndexedText;
            bool hasAnyToken = false;

            foreach (string token in queryTokens)
            {
                if (token.Length == 0)
                    continue;

                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasAnyToken = true;
                    break;
                }
            }

            if (hasAnyToken)
            {
                filtered.Add(candidate);
            }
        }

        return filtered.Count == 0 ? candidates : filtered.ToArray();
    }
    
    /// <summary>
    /// Consolidates segment scores to return only the best-scoring segment per DocumentKey
    /// </summary>
    private static ScoreArray ConsolidateSegments(ScoreArray scores, Span2D<byte> bestSegments)
    {
        ScoreArray consolidated = new ScoreArray();
        
        // Group all scores by DocumentKey and keep only the highest score per key
        Dictionary<long, ushort> scoresByKey = new Dictionary<long, ushort>();
        
        foreach (ScoreEntry entry in scores.GetAll())
        {
            long docKey = entry.DocumentId;
            
            if (!scoresByKey.ContainsKey(docKey))
            {
                scoresByKey[docKey] = entry.Score;
            }
            else
            {
                // Keep the highest score
                if (entry.Score > scoresByKey[docKey])
                {
                    scoresByKey[docKey] = entry.Score;
                }
            }
        }
        
        // Add deduplicated results to consolidated array
        foreach (KeyValuePair<long, ushort> kvp in scoresByKey)
        {
            consolidated.Add(kvp.Key, kvp.Value);
        }
        
        return consolidated;
    }

    /// <summary>
    /// Returns the best segment text for a document, using the bestSegments span when available.
    /// Mirrors the logic of lS5uwnZk2w in the original implementation.
    /// </summary>
    private string GetBestSegmentText(Document doc, Span2D<byte> bestSegments)
    {
        string docText = doc.IndexedText;

        if (bestSegments.Height > 0 && bestSegments.Width > 0)
        {
            List<Document> allSegments = _vectorModel.Documents.GetDocumentsForPublicKey(doc.DocumentKey);
            if (allSegments.Count > 0)
            {
                Document firstSeg = allSegments[0];
                int baseId = firstSeg.Id - firstSeg.SegmentNumber;

                if (baseId >= 0 && baseId < bestSegments.Height)
                {
                    byte bestSegmentNum = bestSegments[baseId, 0];
                    Document? bestSegmentDoc = _vectorModel.Documents.GetDocumentOfSegment(doc.DocumentKey, bestSegmentNum);
                    if (bestSegmentDoc != null)
                    {
                        docText = bestSegmentDoc.IndexedText;
                    }
                }
            }
        }

        return docText;
    }
    
    /// <summary>
    /// Gets a document by its DocumentKey. Thread-safe.
    /// </summary>
    public Document? GetDocument(long documentKey)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _vectorModel.Documents.GetDocumentByPublicKey(documentKey);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets all documents with a specific DocumentKey (supports aliases). Thread-safe.
    /// </summary>
    public List<Document> GetDocuments(long documentKey)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _vectorModel.Documents.GetDocumentsByKey(documentKey);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Calculates the LCS sum value used by coverage.
    /// </summary>
    private static int CalculateLcs(string q, string r, int errorTolerance)
    {
        // Normalize like the original pipeline (TextNormalizer / ToLowerInvariant)
        string qNorm = q.ToLowerInvariant();
        string rNorm = r.ToLowerInvariant();

        return StringMetrics.Lcs(qNorm, rNorm, errorTolerance);
    }
    
    /// <summary>
    /// Calculates where to truncate results based on word hits and scores
    /// Matches IfMuagPWrF exactly
    /// </summary>
    private int CalculateTruncationIndex(
        ScoreEntry[] results, 
        int maxWordHits,
        Span2D<byte> lcsAndWordHitsSpan,
        Dictionary<long, int> documentKeyToIndex)
    {
        if (_coverageSetup == null || results == null || results.Length == 0)
            return -1;
        
        // Calculate minimum word hits threshold
        // Reference: line 1837 of SearchEngine.cs
        int minWordHits = Math.Max(
            _coverageSetup.CoverageMinWordHitsAbs,
            maxWordHits - _coverageSetup.CoverageMinWordHitsRelative);
        
        // Iterate backwards from end to start, find LAST document that meets ANY of these criteria:
        // 1. wordHits >= minWordHits
        // 2. lcs > 0
        // 3. score >= TruncationScore
        // Reference: lines 1838-1848 of SearchEngine.cs (IfMuagPWrF method)
        for (int i = results.Length - 1; i >= 0; i--)
        {
            if (!documentKeyToIndex.TryGetValue(results[i].DocumentId, out int docIndex))
            {
                continue;
            }
            
            if (docIndex >= lcsAndWordHitsSpan.Width)
            {
                continue;
            }
            
            byte wordHitsByte = lcsAndWordHitsSpan[1, docIndex];
            byte lcsByte = lcsAndWordHitsSpan[0, docIndex];
            
            // Match reference logic exactly: no extra conditions on score check
            if (wordHitsByte >= minWordHits || lcsByte > 0 || results[i].Score >= _coverageSetup.TruncationScore)
            {
                return i;
            }
        }
        
        return -1; // No valid truncation point found
    }
    
    /// <summary>
    /// Gets statistics about the indexed data
    /// </summary>
    public IndexStatistics GetStatistics()
    {
        _rwLock.EnterReadLock();
        try
        {
            return new IndexStatistics(
                _vectorModel.Documents.Count,
                _vectorModel.TermCollection.Count);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Saves the current search index to a file.
    /// Thread-safe: Blocks during save.
    /// </summary>
    public void Save(string filePath)
    {
        _rwLock.EnterWriteLock();
        try
        {
            using FileStream stream = File.Create(filePath);
            using BinaryWriter writer = new BinaryWriter(stream);
            
            _vectorModel.SaveToStream(writer);
            
            // Save WordMatcher if present
            writer.Write(_wordMatcher != null);
            if (_wordMatcher != null)
            {
                _wordMatcher.Save(writer);
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Loads a search index from a file.
    /// </summary>
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
        
        Tokenizer tokenizer = new Tokenizer(
            indexSizes,
            startPadSize,
            stopPadSize,
            textNormalizer,
            tokenizerSetup);
            
        VectorModel vectorModel = new VectorModel(tokenizer, stopTermLimit, fieldWeights);
        
        SearchEngine engine = new SearchEngine(vectorModel, enableCoverage, coverageSetup, tokenizerSetup, wordMatcherSetup);
        
        using (FileStream stream = File.OpenRead(filePath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            vectorModel.LoadFromStream(reader);
            
            bool hasWordMatcher = reader.ReadBoolean();
            if (hasWordMatcher && engine._wordMatcher != null)
            {
                engine._wordMatcher.Load(reader);
            }
            else if (hasWordMatcher && engine._wordMatcher == null)
            {
                // File has WordMatcher data but engine configured without it.
                // We should skip the data to avoid stream corruption or errors,
                // but since we can't easily skip without loading, we'll just
                // throw or warn. For internal tool, let's just throw.
                throw new InvalidOperationException("Index contains WordMatcher data but engine is configured without it.");
            }
            else if (!hasWordMatcher && engine._wordMatcher != null)
            {
                 // File missing WordMatcher data but engine expects it.
                 throw new InvalidDataException("Index file is missing required WordMatcher data (legacy format not supported).");
            }
        }
        
        return engine;
    }

    /// <summary>
    /// Asynchronously loads a search index from a file.
    /// </summary>
    public static async Task<SearchEngine> LoadAsync(
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
        float[]? fieldWeights = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => Load(filePath, indexSizes, startPadSize, stopPadSize, enableCoverage, textNormalizer, tokenizerSetup, coverageSetup, stopTermLimit, wordMatcherSetup, fieldWeights));
    }

    private SearchEngine(
        VectorModel vectorModel,
        bool enableCoverage,
        CoverageSetup? coverageSetup,
        TokenizerSetup? tokenizerSetup,
        WordMatcherSetup? wordMatcherSetup)
    {
        _vectorModel = vectorModel;
        _isIndexed = true; // Loaded index is assumed to be calculated
        
        if (enableCoverage)
        {
            _coverageSetup = coverageSetup ?? CoverageSetup.CreateDefault();
            // Fix: Initialize CoverageEngine using the tokenizer from VectorModel
            _coverageEngine = new CoverageEngine(_vectorModel.Tokenizer, _coverageSetup);
        }
        
        if (wordMatcherSetup != null && tokenizerSetup != null)
        {
            _wordMatcher = new WordMatcher.WordMatcher(wordMatcherSetup, tokenizerSetup.Delimiters);
            // WordMatcher population is now handled by the Load method (either loading from disk or rebuilding)
        }
    }

    public void Dispose()
    {
        _rwLock?.Dispose();
        _wordMatcher?.Dispose();
        _filterCompiler?.Dispose();
        _filterVM?.Dispose();
    }
    
    /// <summary>
    /// Apply filter to search results using bytecode VM.
    /// Also computes NumberOfDocumentsInFilter by checking against all documents.
    /// </summary>
    private ScoreEntry[] ApplyFilter(ScoreEntry[] results, Api.Filter filter)
    {
        // Get or compile bytecode (with thread-safe caching)
        var compiled = _compiledFilterCache.GetOrAdd(filter, f => _filterCompiler.Value!.Compile(f));
        
        // Count total documents matching the filter
        // This needs to be done against ALL documents, not just search results
        if (filter.NumberOfDocumentsInFilter == 0)
        {
            int matchCount = 0;
            IReadOnlyList<Document> allDocuments = _vectorModel.Documents.GetAllDocuments();
            
            foreach (Document doc in allDocuments)
            {
                if (_filterVM.Value!.Execute(compiled, doc.Fields))
                {
                    matchCount++;
                }
            }
            
            filter.NumberOfDocumentsInFilter = matchCount;
        }
        
        // Now filter the search results
        List<ScoreEntry> filteredResults = [];
        
        foreach (ScoreEntry result in results)
        {
            // Get document by public key
            Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(result.DocumentId);
            if (doc == null)
                continue;
            
            // Execute bytecode against document
            if (_filterVM.Value!.Execute(compiled, doc.Fields))
            {
                filteredResults.Add(result);
            }
        }
        
        return filteredResults.ToArray();
    }
    
    /// <summary>
    /// Apply boosts to search results.
    /// compile each boost filter and check if documents match,
    /// then add boost strength to the score.
    /// </summary>
    private ScoreEntry[] ApplyBoosts(ScoreEntry[] results, Api.Boost[] boosts)
    {
        if (boosts == null || boosts.Length == 0)
            return results;
        
        // Compile all boost filters and cache them
        List<(CompiledFilter compiled, int strength)> compiledBoosts = [];
        
        foreach (Boost boost in boosts)
        {
            if (boost.Filter == null)
                continue;
            
            // Get or compile the boost filter (thread-safe)
            var compiled = _compiledFilterCache.GetOrAdd(boost.Filter, f => _filterCompiler.Value!.Compile(f));
            compiledBoosts.Add((compiled, (int)boost.BoostStrength));
        }
        
        if (compiledBoosts.Count == 0)
            return results;
        
        // Apply boosts to each result
        for (int i = 0; i < results.Length; i++)
        {
            ScoreEntry result = results[i];
            Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(result.DocumentId);
            
            if (doc == null)
                continue;
            
            int totalBoost = 0;
            
            // Check each boost filter
            foreach ((CompiledFilter compiled, int strength) in compiledBoosts)
            {
                if (_filterVM.Value!.Execute(compiled, doc.Fields))
                {
                    totalBoost += strength;
                }
            }
            
            // Apply boost to score (clamped to ushort range)
            if (totalBoost > 0)
            {
                ushort newScore = (ushort)Math.Min(65535, result.Score + totalBoost);
                results[i] = new ScoreEntry(newScore, result.DocumentId);
            }
        }
        
        // Re-sort by score descending
        Array.Sort(results, (a, b) => b.Score.CompareTo(a.Score));
        
        return results;
    }
    
    /// <summary>
    /// Apply sorting to search results
    /// </summary>
    private ScoreEntry[] ApplySort(ScoreEntry[] results, Api.Field sortByField, bool ascending)
    {
        // Extract sort values from documents
        (ScoreEntry Entry, object? SortValue)[] withSortKeys = results.Select(r =>
        {
            // DocumentId in ScoreEntry is the public document key
            Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(r.DocumentId);
            Field? field = doc?.Fields.GetField(sortByField.Name);
            return (Entry: r, SortValue: field?.Value);
        }).ToArray();
        
        // Sort by sort value
        if (ascending)
            Array.Sort(withSortKeys, (a, b) => CompareValues(a.SortValue, b.SortValue));
        else
            Array.Sort(withSortKeys, (a, b) => CompareValues(b.SortValue, a.SortValue));
        
        return withSortKeys.Select(x => x.Entry).ToArray();
    }
    
    /// <summary>
    /// Compare two values for sorting
    /// </summary>
    private static int CompareValues(object? a, object? b)
    {
        switch (a)
        {
            case null when b == null:
                return 0;
            case null:
                return -1;
        }

        if (b == null) return 1;
        
        // Handle numeric types
        if (a is IComparable ca && b is IComparable cb && a.GetType() == b.GetType())
        {
            return ca.CompareTo(cb);
        }
        
        // Fallback to string comparison
        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// Index statistics
/// </summary>
public class IndexStatistics
{
    public int DocumentCount { get; }
    public int VocabularySize { get; }
    
    public IndexStatistics(int documentCount, int vocabularySize)
    {
        DocumentCount = documentCount;
        VocabularySize = vocabularySize;
    }
    
    public override string ToString() => 
        $"{DocumentCount} documents, {VocabularySize} terms";
}
