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
/// Indicates the type of match found for short query searches.
/// </summary>
internal enum MatchType
{
    Partial,      // Query characters found but not as prefix
    ExactPrefix   // Query found as exact prefix match
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
    private DocumentFields? _documentFieldSchema;
    
    // Bytecode VM for filter execution
    // Thread-local to avoid state corruption between concurrent threads
    private readonly ThreadLocal<FilterCompiler> _filterCompiler = new ThreadLocal<FilterCompiler>(() => new FilterCompiler());
    private readonly ThreadLocal<FilterVM> _filterVM = new ThreadLocal<FilterVM>(() => new FilterVM());
    // Thread-safe cache for compiled filters
    private readonly ConcurrentDictionary<Filter, CompiledFilter> _compiledFilterCache = new ConcurrentDictionary<Filter, CompiledFilter>();
    
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
            _wordMatcher = new WordMatcher.WordMatcher(wordMatcherSetup, tokenizerSetup.Delimiters, textNormalizer);
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
            
            // Update term prefix trie for O(|prefix|) short query lookups.
            // Called after every batch but is O(new terms) not O(all terms) for incremental updates.
            _vectorModel.BuildTermPrefixTrie();
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
    public Result Search(Query query)
    {
        _rwLock.EnterReadLock();
        try
        {
            // If the index is not yet fully built, treat as an empty index rather than throwing.
            // This makes concurrent search+indexing scenarios safe: searches will either block
            // until indexing completes or see an empty, consistent snapshot.
            if (!_isIndexed)
            {
                return Result.MakeEmptyResult();
            }
            
            // Validate and normalize query
            Query normalizedQuery = new Query(query);
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
                
                return new Result(topResultsArray, facetsAll,
                    topResultsArray.Length > 0 ? topResultsArray.Length - 1 : 0,
                    topResultsArray.Length > 0 ? topResultsArray[^1].Score : (byte)0,
                    false);
            }
            
            if (string.IsNullOrWhiteSpace(normalizedQuery.Text))
            {
                return Result.MakeEmptyResult();
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
            
            return new Result(topResults, facets,
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

        // Normalize searchText for accent-insensitive matching (same normalization as indexing)
        if (_vectorModel.Tokenizer.TextNormalizer != null)
        {
            searchText = _vectorModel.Tokenizer.TextNormalizer.Normalize(searchText);
        }

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
            
            // SHORT QUERY HANDLING
            // Check if ANY word in the query can use n-grams. If at least one word can, use normal search.
            // Only use short query path if ALL words are too short.
            ScoreArray relevancyScores;
            int minIndexSize = _vectorModel.Tokenizer.IndexSizes.Min();
            
            // Analyze query words to determine which can use n-grams (long words) and which are short.
            // This allows us to run TF-IDF only on long words (e.g. "san") while handling short words
            // (e.g. "a") via precedence logic, without any arbitrary thresholds.
            bool canUseNGrams = false;
            bool hasMixedTerms = false;
            string[] shortWords = [];
            string longWordsSearchText = searchText; // default: whole query
            
            if (_vectorModel.Tokenizer.TokenizerSetup != null)
            {
                string[] words = searchText.Split(_vectorModel.Tokenizer.TokenizerSetup.Delimiters,
                    StringSplitOptions.RemoveEmptyEntries);
                
                if (words.Length == 0)
                {
                    // No words after split - use entire query length check
                    canUseNGrams = searchText.Length >= minIndexSize;
                }
                else
                {
                    List<string> longWords = new List<string>();
                    List<string> shortWordsList = new List<string>();
                    
                    foreach (string word in words)
                    {
                        if (word.Length >= minIndexSize)
                        {
                            longWords.Add(word);
                        }
                        else
                        {
                            shortWordsList.Add(word.ToLowerInvariant());
                        }
                    }
                    
                    if (longWords.Count > 0)
                    {
                        canUseNGrams = true;
                        longWordsSearchText = string.Join(' ', longWords);
                    }
                    
                    if (shortWordsList.Count > 0 && longWords.Count > 0)
                    {
                        hasMixedTerms = true;
                        shortWords = shortWordsList.ToArray();
                    }
                }
            }
            else
            {
                // No tokenizer setup - fall back to checking entire query
                canUseNGrams = searchText.Length >= minIndexSize;
            }
            
            // SPECIAL CASE: Single-character queries (e.g., "a", "s").
            // For these, n-gram indexing is not applicable and naive inverted index
            // scoring tends to over-favor very long titles with many occurrences.
            // Instead, we run a direct lexical scan over titles and rank by:
            //   - whether any word starts with the character,
            //   - whether the title itself starts with that character,
            //   - and simple density/position heuristics.
            if (!canUseNGrams && searchText.Length == 1)
            {
                ScoreEntry[] singleCharResults = SearchSingleCharacterQuery(
                    searchText[0],
                    bestSegments,
                    queryIndex: 0,
                    maxResults: maxResults);
                return singleCharResults;
            }
            
            if (!canUseNGrams)
            {
                // SHORT QUERY FAST PATH
                // Query is too short for standard n-gram search - use prefix matching with inverted index.
                // Complexity: O(terms) instead of O(documents), leveraging existing inverted index.

                if (EnableDebugLogging)
                {
                    Console.WriteLine($"[DEBUG] Short query fast path: query='{searchText}', len={searchText.Length}, minIndexSize={minIndexSize}");
                }
                
                relevancyScores = new ScoreArray();
                string searchLower = searchText.ToLowerInvariant();
                HashSet<long> matchedDocs = [];

                // Track whether a document has a word whose FIRST token starts
                // with the short query prefix. This lets us enforce a clear,
                // data-agnostic precedence rule:
                //   Docs whose first token starts with the query prefix
                //   strictly outrank docs where the prefix only appears later.
                // Example for "de":
                //   - "Dear Dead Delilah"  -> first token "dear"  -> high tier
                //   - "Intent to Destroy"  -> first token "intent" -> lower tier
                HashSet<long> firstTokenPrefixDocs = [];
                
                // Build prefix patterns to match
                List<string> prefixPatterns = [];
                
                // Add padded prefixes (documents starting with the query)
                string padPrefix = new string(Tokenizer.START_PAD_CHAR, _vectorModel.Tokenizer.StartPadSize);
                for (int i = 0; i < minIndexSize && i < padPrefix.Length + searchLower.Length; i++)
                {
                    int padCount = Math.Max(0, padPrefix.Length - i);
                    int queryCount = Math.Min(searchLower.Length, minIndexSize - padCount);
                    
                    if (queryCount > 0)
                    {
                        string prefix = string.Concat(new string(Tokenizer.START_PAD_CHAR, padCount), searchLower.AsSpan(0, queryCount));
                        prefixPatterns.Add(prefix);
                    }
                }
                
                // Add word-boundary prefix (documents containing the query as a word)
                prefixPatterns.Add(" " + searchLower);
                
                if (EnableDebugLogging)
                {
                    Console.WriteLine($"[DEBUG] Generated {prefixPatterns.Count} prefix patterns: {string.Join(", ", prefixPatterns.Select(p => $"'{p}'"))}");
                }
                
                // TIERED STRATEGY (Fast to Slow):
                // 1. EXACT PREFIX MATCH: Look up terms starting with prefix patterns using trie (O(|prefix| + k))
                // 2. FUZZY FALLBACK: If not enough results, add terms containing query chars (SLOWER, LOWER QUALITY)
                
                int termsScanned = 0, exactMatched = 0, fuzzyMatched = 0;
                Dictionary<long, int> docScores = new Dictionary<long, int>();
                
                // PHASE 1: Exact prefix matching using trie (HIGH PRIORITY - score * 10)
                // Complexity: O(|prefix| + k) where k is matching terms, vs O(n) linear scan
                var trie = _vectorModel.TermPrefixTrie;
                
                foreach (string pattern in prefixPatterns)
                {
                    IEnumerable<Term> matchingTerms = trie != null 
                        ? trie.FindByPrefix(pattern) 
                        : _vectorModel.TermCollection.GetAllTerms().Where(t => t.Text?.StartsWith(pattern) == true);
                    
                    foreach (var term in matchingTerms)
                    {
                        termsScanned++;
                        exactMatched++;
                        
                        var docIds = term.GetDocumentIds();
                        var weights = term.GetWeights();
                        
                        if (docIds != null && weights != null)
                        {
                            for (int i = 0; i < docIds.Count; i++)
                            {
                                int internalId = docIds[i];
                                byte weight = weights[i];
                                
                                Document? doc = _vectorModel.Documents.GetDocument(internalId);
                                if (doc != null && !doc.Deleted)
                                {
                                    // Exact matches get 10x score boost
                                    int score = weight * 10;
                                    
                                    if (docScores.ContainsKey(doc.DocumentKey))
                                    {
                                        docScores[doc.DocumentKey] += score;
                                    }
                                    else
                                    {
                                        docScores[doc.DocumentKey] = score;
                                        matchedDocs.Add(doc.DocumentKey);
                                    }

                                    // Record whether this document's first token
                                    // starts with the short query prefix.
                                    // We evaluate this once per document and
                                    // reuse it at normalization time.
                                    if (!firstTokenPrefixDocs.Contains(doc.DocumentKey))
                                    {
                                        string titleLower = doc.IndexedText.ToLowerInvariant();
                                        // First token starts with the query prefix
                                        if (titleLower.StartsWith(searchLower, StringComparison.Ordinal))
                                        {
                                            firstTokenPrefixDocs.Add(doc.DocumentKey);
                                        }
                                    }
                                    
                                    if (bestSegments.Height > 0 && internalId < bestSegments.Height)
                                    {
                                        int baseId = internalId - doc.SegmentNumber;
                                        if (baseId >= 0 && baseId < bestSegments.Height)
                                        {
                                            bestSegments[baseId, 0] = (byte)doc.SegmentNumber;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                // PHASE 2: Fuzzy fallback if we don't have enough results
                // For fuzzy matching, prioritize n-grams where query chars appear near word boundaries
                if (matchedDocs.Count < maxResults)
                {
                    foreach (var term in _vectorModel.TermCollection.GetAllTerms())
                    {
                        if (term.Text == null)
                            continue;
                        
                        // Skip if already matched as exact prefix
                        bool alreadyMatched = false;
                        foreach (string pattern in prefixPatterns)
                        {
                            if (term.Text.StartsWith(pattern))
                            {
                                alreadyMatched = true;
                                break;
                            }
                        }
                        
                        if (alreadyMatched)
                            continue;
                        
                        // FUZZY MATCH: Look for query chars at word boundaries or early positions
                        // Examples for query "a":
                        //   - " a*" (word starting with 'a') - GOOD
                        //   - "^a*" (document starting with 'a') - GOOD  
                        //   - "*a*" (contains 'a' anywhere) - FALLBACK only if needed
                        
                        bool hasWordBoundaryMatch = false;
                        int charMatchCount = 0;
                        
                        foreach (char qChar in searchLower)
                        {
                            // Check if char appears at word boundary
                            string wordBoundaryPattern = " " + qChar;
                            if (term.Text.Contains(wordBoundaryPattern))
                            {
                                hasWordBoundaryMatch = true;
                                charMatchCount++;
                            }
                            else if (term.Text.Contains(qChar))
                            {
                                charMatchCount++;
                            }
                        }
                        
                        // Only add fuzzy matches that have word boundary matches OR contain any query chars
                        bool shouldAdd = hasWordBoundaryMatch || (charMatchCount > 0);
                        
                        if (shouldAdd)
                        {
                            fuzzyMatched++;
                            var docIds = term.GetDocumentIds();
                            var weights = term.GetWeights();
                            
                            if (docIds != null && weights != null)
                            {
                                for (int i = 0; i < docIds.Count; i++)
                                {
                                    int internalId = docIds[i];
                                    byte weight = weights[i];
                                    
                                    Document? doc = _vectorModel.Documents.GetDocument(internalId);
                                    if (doc != null && !doc.Deleted)
                                    {
                                        // Word boundary matches get small boost
                                        int score = hasWordBoundaryMatch ? weight * 2 : weight;
                                        
                                        if (docScores.ContainsKey(doc.DocumentKey))
                                        {
                                            docScores[doc.DocumentKey] += score;
                                        }
                                        else
                                        {
                                            docScores[doc.DocumentKey] = score;
                                            matchedDocs.Add(doc.DocumentKey);
                                        }

                                        // Also propagate the first-token-prefix
                                        // flag for docs discovered via fuzzy
                                        // fallback, to keep precedence rules
                                        // consistent across both phases.
                                        if (!firstTokenPrefixDocs.Contains(doc.DocumentKey))
                                        {
                                            string titleLower = doc.IndexedText.ToLowerInvariant();
                                            if (titleLower.StartsWith(searchLower, StringComparison.Ordinal))
                                            {
                                                firstTokenPrefixDocs.Add(doc.DocumentKey);
                                            }
                                        }
                                        
                                        if (bestSegments.Height > 0 && internalId < bestSegments.Height)
                                        {
                                            int baseId = internalId - doc.SegmentNumber;
                                            if (baseId >= 0 && baseId < bestSegments.Height)
                                            {
                                                bestSegments[baseId, 0] = (byte)doc.SegmentNumber;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                // Build final score array with proper normalization and a clear
                // precedence rule for short queries driven purely by lexical
                // structure (no data-tuned weights).
                //
                // For single-token short queries, precedence is:
                //   1) Title equals query (e.g., "IO" for "io")
                //   2) First token equals query
                //   3) First token starts with query prefix
                //   4) Any token equals query
                //   5) Others, ranked by normalized docScores
                //
                // For multi-token short queries (all tokens too short for n‑grams),
                // precedence is instead based on *token coverage*:
                //   1) Documents containing all query tokens as full words
                //   2) Documents containing at least one query token as a full word
                //   3) Others, ranked by normalized docScores
                // Find max score to normalize
                int maxScore = 0;
                foreach (var score in docScores.Values)
                {
                    if (score > maxScore)
                        maxScore = score;
                }
                
                // Normalize scores to 0-255 range, preserving relative differences,
                // then add lexical precedence bits in the high byte.
                char[] shortDelimiters = _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? new[] { ' ' };
                // Tokenize the query text once for multi-token coverage logic.
                string[] queryTokensShort = searchLower.Split(shortDelimiters, StringSplitOptions.RemoveEmptyEntries);
                
                if (maxScore > 0)
                {
                    foreach (var kvp in docScores)
                    {
                        // Scale score to fit in byte range while preserving ranking
                        byte normalizedScore = (byte)Math.Min(255, (kvp.Value * 255L) / maxScore);

                        Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(kvp.Key);
                        if (doc == null || doc.Deleted)
                            continue;
                        
                        string titleLower = doc.IndexedText.ToLowerInvariant();
                        string trimmedTitle = titleLower.Trim();
                        string[] words = titleLower.Split(shortDelimiters, StringSplitOptions.RemoveEmptyEntries);
                        
                        // Multi-token short query: prefer documents that cover more
                        // of the query tokens as full words, independently of any
                        // dataset specifics.
                        int precedence = 0;
                        if (queryTokensShort.Length >= 2)
                        {
                            int tokenMatches = 0;
                            foreach (string qt in queryTokensShort)
                            {
                                if (words.Any(w => string.Equals(w, qt, StringComparison.Ordinal)))
                                {
                                    tokenMatches++;
                                }
                            }

                            bool allTokensPresent = queryTokensShort.Length > 0 && tokenMatches == queryTokensShort.Length;
                            if (allTokensPresent)
                            {
                                // All short query tokens present as words – strongest signal.
                                precedence |= 8;

                                // Within the full-coverage tier, prefer titles that are
                                // lexically compact with respect to the query: the number
                                // of tokens in the title is close to the number of query
                                // tokens. This is a general, data-agnostic rule that
                                // favors focused matches over noisy ones.
                                if (words.Length <= queryTokensShort.Length + 1)
                                {
                                    precedence |= 2;
                                }
                            }
                            else if (tokenMatches > 0)
                            {
                                // Partial coverage of query tokens.
                                precedence |= 4;
                            }
                        }
                        else
                        {
                            // Single-token short query behavior (IO, X, etc.)
                            bool anyTokenExact = false;
                            bool firstTokenExact = false;
                            if (words.Length > 0)
                            {
                                firstTokenExact = string.Equals(words[0], searchLower, StringComparison.Ordinal);
                                if (firstTokenExact)
                                {
                                    anyTokenExact = true;
                                }
                                else
                                {
                                    for (int i = 0; i < words.Length; i++)
                                    {
                                        if (string.Equals(words[i], searchLower, StringComparison.Ordinal))
                                        {
                                            anyTokenExact = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            
                            bool titleEqualsQuery = string.Equals(trimmedTitle, searchLower, StringComparison.Ordinal);
                            bool firstTokenStartsWithPrefix = firstTokenPrefixDocs.Contains(kvp.Key);
                            
                            // Bit layout (high to low importance) for single-token case:
                            //  3: titleEqualsQuery
                            //  2: firstTokenExact
                            //  1: firstTokenStartsWithPrefix
                            //  0: anyTokenExact (elsewhere in title)
                            if (anyTokenExact) precedence |= 1;
                            if (firstTokenStartsWithPrefix) precedence |= 2;
                            if (firstTokenExact) precedence |= 4;
                            if (titleEqualsQuery) precedence |= 8;
                        }
                        
                        ushort finalScore = (ushort)((precedence << 8) | normalizedScore);

                        relevancyScores.Add(kvp.Key, finalScore);
                    }
                }
                else
                {
                    foreach (var kvp in docScores)
                    {
                        byte baseScore = (byte)Math.Min(255, kvp.Value);
                        
                        Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(kvp.Key);
                        if (doc == null || doc.Deleted)
                            continue;
                        
                        string titleLower = doc.IndexedText.ToLowerInvariant();
                        string trimmedTitle = titleLower.Trim();
                        string[] words = titleLower.Split(shortDelimiters, StringSplitOptions.RemoveEmptyEntries);
                        
                        bool anyTokenExact = false;
                        bool firstTokenExact = false;
                        if (words.Length > 0)
                        {
                            firstTokenExact = string.Equals(words[0], searchLower, StringComparison.Ordinal);
                            if (firstTokenExact)
                            {
                                anyTokenExact = true;
                            }
                            else
                            {
                                for (int i = 0; i < words.Length; i++)
                                {
                                    if (string.Equals(words[i], searchLower, StringComparison.Ordinal))
                                    {
                                        anyTokenExact = true;
                                        break;
                                    }
                                }
                            }
                        }
                        
                        bool titleEqualsQuery = string.Equals(trimmedTitle, searchLower, StringComparison.Ordinal);
                        bool firstTokenStartsWithPrefix = firstTokenPrefixDocs.Contains(kvp.Key);
                        
                        int precedence = 0;
                        if (anyTokenExact) precedence |= 1;
                        if (firstTokenStartsWithPrefix) precedence |= 2;
                        if (firstTokenExact) precedence |= 4;
                        if (titleEqualsQuery) precedence |= 8;
                        
                        ushort finalScore = (ushort)((precedence << 8) | baseScore);

                        relevancyScores.Add(kvp.Key, finalScore);
                    }
                }
                
                if (EnableDebugLogging)
                {
                    Console.WriteLine($"[DEBUG] Short query results: scanned={termsScanned} terms, exactMatched={exactMatched}, fuzzyMatched={fuzzyMatched}, results={matchedDocs.Count} docs");
                }
            }
            else
            {
                // Use normal n-gram search. For mixed queries we only pass the strong terms
                // (e.g., \"san\"), because short terms are handled via precedence bits.
                string tfidfQuery = hasMixedTerms ? longWordsSearchText : searchText;
                if (string.IsNullOrWhiteSpace(tfidfQuery))
                {
                    tfidfQuery = searchText;
                }
                // Use MaxScore algorithm with coverageDepth as K for early termination
                relevancyScores = _vectorModel.SearchWithMaxScore(tfidfQuery, coverageDepth, bestSegments, queryIndex: 0);
            }

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
                    // disabled - long outputs
                    // Console.WriteLine($"  [DEBUG]   TF-IDF docKey={e.DocumentId}, score={e.Score}");
                }
            }
            
            // Skip coverage stage if disabled OR query is too short for n-grams.
            // - Short queries use prefix matching which is already lexical
            if (_coverageEngine == null || coverageSetup == null || !canUseNGrams)
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
                    // disabled - long outputs
                    //Console.WriteLine($"  [DEBUG]   top TF-IDF docKey={c.DocumentId}, score={c.Score}");
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
                    // OPTIMIZATION: Limit WordMatcher processing to avoid O(n) coverage on thousands of docs.
                    // Prioritize WordMatcher candidates that ALSO appear in TF-IDF top-K (highest relevance).
                    // For candidates only in WordMatcher, process up to coverageDepth.
                    HashSet<int> tfidfInternalIds = new HashSet<int>();
                    foreach (ScoreEntry candidate in topCandidates)
                    {
                        Document? tDoc = _vectorModel.Documents.GetDocumentByPublicKey(candidate.DocumentId);
                        if (tDoc != null) tfidfInternalIds.Add(tDoc.Id);
                    }
                    
                    // Split WordMatcher candidates into overlapping (high priority) and unique
                    List<int> wmOverlapping = new List<int>();
                    List<int> wmUnique = new List<int>();
                    foreach (int id in wordMatcherInternalIds)
                    {
                        if (tfidfInternalIds.Contains(id))
                            wmOverlapping.Add(id);
                        else
                            wmUnique.Add(id);
                    }
                    
                    // Process all overlapping, then up to (coverageDepth - overlap) unique
                    int wmLimit = Math.Max(0, coverageDepth - wmOverlapping.Count);
                    var wmToProcess = wmOverlapping.Concat(wmUnique.Take(wmLimit)).ToList();
                    
                    long wmCoverageStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
                    foreach (int internalId in wmToProcess)
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

                        // Calculate features for principled scoring
                        var features = _coverageEngine.CalculateFeatures(searchText, docText, lcsFromSpan);
                        
                        // WordMatcher hits have base score 0 (or we could assume they are good matches, but we rely on features)
                        ushort finalScore = CalculateFusionScore(searchText, docText, features, 0f);

                        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
                        {
                            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(features.WordHits, 255);
                        }

                        maxWordHits = Math.Max(maxWordHits, features.WordHits);

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

                        // Calculate features for principled scoring
                        var features = _coverageEngine.CalculateFeatures(searchText, docText, lcsFromSpan);
                        
                        ushort finalScore = CalculateFusionScore(searchText, docText, features, (float)candidate.Score / 255f);

                        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
                        {
                            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(features.WordHits, 255);
                        }

                        maxWordHits = Math.Max(maxWordHits, features.WordHits);
                        
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
    /// Calculates a fused score from coverage features and (optionally) BM25.
    /// 
    /// Design principles:
    /// - The ordering is driven by discrete lexical structure, not tuned weights.
    /// - Stage 2 ranking is purely lexical/information-theoretic; BM25 is used
    ///   only to generate candidates in Stage 1.
    /// - Precedence is lexicographic:
    ///   1) All query terms have some evidence (completeness)
    ///   2) Matches are clean (no fuzzy contamination)
    ///   3) For single-term queries: exact/prefix at title start
    ///   4) For multi-term queries: \"perfect\" title-level coverage and phrase quality
    ///   5) Within a precedence bucket, CoverageScore is used as a smooth tie-breaker.
    /// </summary>
    private ushort CalculateFusionScore(
        string queryText,
        string documentText,
        CoverageEngine.CoverageFeatures features,
        float bm25Score)
    {
        // Tokenization for lexical reasoning (independent of coverage MinWordSize).
        char[] delimiters = _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? new[] { ' ' };
        string[] queryTokensLex = queryText
            .Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
        string[] docTokensLex = documentText
            .Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
        
        int rawQueryTerms = queryTokensLex.Length;
        bool isSingleTermQuery = rawQueryTerms <= 1;
        
        // Tier 1: Completeness (all query terms that are large enough for coverage have some match)
        bool allTermsFound = features.TermsCount > 0 &&
                             features.TermsWithAnyMatch == features.TermsCount;
        
        // Tier 2: Cleanliness – all terms matched via Exact/Prefix (no fuzzy-only terms)
        bool isCleanMatch = features.TermsCount > 0 &&
                            features.TermsPrefixMatched == features.TermsCount;
        
        // Exact whole-word match for all (coverage Strict)
        bool isExactMatch = features.TermsCount > 0 &&
                            features.TermsStrictMatched == features.TermsCount;
        
        // Lexical "perfect document":
        // Every document token is explained by some query token via
        // whole-word or prefix containment in either direction.
        bool isLexicalPerfectDoc = false;
        if (rawQueryTerms > 0 && docTokensLex.Length > 0)
        {
            bool allDocWordsCovered = true;
            for (int i = 0; i < docTokensLex.Length; i++)
            {
                string d = docTokensLex[i];
                bool covered = false;
                
                for (int j = 0; j < rawQueryTerms; j++)
                {
                    string q = queryTokensLex[j];
                    if (d.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                        q.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                    {
                        covered = true;
                        break;
                    }
                }
                
                if (!covered)
                {
                    allDocWordsCovered = false;
                    break;
                }
            }
            
            isLexicalPerfectDoc = allDocWordsCovered;
        }
        
        bool startsAtBeginning = features.FirstMatchIndex == 0;
        
        int precedence = 0;
        
        // Bit 7: all (coverage-eligible) terms have some match
        if (allTermsFound)
        {
            precedence |= 128;
        }
        
        // Bit 6: clean vs noisy (no fuzzy-only terms)
        if (isCleanMatch && features.TermsCount > 0)
        {
            precedence |= 64;
        }
        
        // Special handling for two-term queries with a very short second token
        // (e.g., "san a"). For these, we distinguish between:
        //  - strict bigram prefixes like "San Andreas" (first word == q0, second starts with q1)
        //  - looser matches like "Sandeep Aur ..." (both words just start with "san"/"a")
        bool isTwoTermShortSecond = rawQueryTerms == 2 &&
                                    queryTokensLex[1].Length < (_coverageSetup?.MinWordSize ?? 2);
        bool hasBigramPrefix = false;
        bool strictShortBigram = false;
        // First lexical token as an \"intent\" anchor for multi-term queries.
        // If this token is reasonably specific (len >= 4) and appears as a
        // substring in the document text, we treat that as a strong signal
        // of user intent (e.g. \"scio\" in \"ScioŠkola Zlín\") even when
        // common tail phrases like \"škola ve Zlíně\" match other documents
        // better. This is purely lexical and does not use any rarity/IDF.
        bool firstAnchorTokenHit = false;
        if (!isSingleTermQuery && rawQueryTerms > 0 && queryTokensLex[0].Length >= 4)
        {
            string firstToken = queryTokensLex[0];
            if (documentText.IndexOf(firstToken, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                firstAnchorTokenHit = true;
            }
        }
        if (rawQueryTerms >= 2 && docTokensLex.Length >= 2)
        {
            string q0 = queryTokensLex[0];
            string q1 = queryTokensLex[1];
            string d0 = docTokensLex[0];
            string d1 = docTokensLex[1];
            
            bool firstMatches = d0.StartsWith(q0, StringComparison.OrdinalIgnoreCase) ||
                                q0.StartsWith(d0, StringComparison.OrdinalIgnoreCase);
            bool secondMatches = d1.StartsWith(q1, StringComparison.OrdinalIgnoreCase) ||
                                 q1.StartsWith(d1, StringComparison.OrdinalIgnoreCase);
            
            hasBigramPrefix = firstMatches && secondMatches;
            
            if (isTwoTermShortSecond)
            {
                // Strict version: first token must be exactly the first query term
                // (e.g., "San"), second must start with the short trailing term.
                strictShortBigram =
                    d0.Equals(q0, StringComparison.OrdinalIgnoreCase) &&
                    d1.StartsWith(q1, StringComparison.OrdinalIgnoreCase);
            }
        }
        
        if (isSingleTermQuery)
        {
            // Single-term behavior ("star", "sap", "matrix", etc.):
            //  - Prefer titles whose FIRST token has an exact or prefix match
            //    for the query term.
            //  - Within those, ordering is:
            //      1) Exact whole-word AND starts at beginning ("Star Kid", "SAP")
            //      2) Prefix AND starts at beginning ("Sapphire", "Sapoot")
            //      3) Exact but not at beginning ("Lone Star", "Mae Martin SAP")
            //      4) Other clean matches (substring, prefix in later tokens)
            if (startsAtBeginning && allTermsFound)
            {
                if (isExactMatch)
                {
                    // Exact + starts-with: strongest single-term signal.
                    precedence |= 32;
                }
                else if (isCleanMatch)
                {
                    // Prefix (or equivalent clean) at the beginning of title.
                    // This should outrank later-position exact matches.
                    precedence |= 24; // > 16 (non-start exact)
                }
            }
            else if (allTermsFound)
            {
                if (isExactMatch)
                {
                    // Exact but not at beginning: strong, but below any
                    // "starts with" titles.
                    precedence |= 16;
                }
            }
        }
        else
        {
            // Multi-term behavior:
            // Within (allTermsFound, clean/noisy) tiers, we prefer:
            //  1) Lexically perfect documents (every doc token supported by query)
            //  2) Strong suffix phrase (longest clean run at end of query)
            //  3) Overall phrase quality (longest clean run anywhere)
            //  4) Compactness (smaller phraseSpan -> tighter phrase)
            
            int suffixRun = features.SuffixPrefixRun;
            int longestRun = features.LongestPrefixRun;
            int span = features.PhraseSpan;
            
            // For short-second two-term queries we treat a strict bigram prefix
            // (q0, q1) -> (d0, d1) as the strongest signal, and we *do not*
            // treat bag-of-words coverage as perfect in that regime.
            bool useLexicalPerfectDoc = !isTwoTermShortSecond && isLexicalPerfectDoc;
            
            if (isTwoTermShortSecond)
            {
                // Bit 5: Exact bigram at title start, with no extra leading words.
                if (strictShortBigram)
                {
                    if (docTokensLex.Length == rawQueryTerms)
                    {
                        // Pure "San Andreas" beats "San Andreas Quake" and others.
                        precedence |= 32;
                    }
                    else
                    {
                        // Still very strong, but slightly below the minimal title.
                        precedence |= 16;
                    }
                }
            }
            else
            {
                // General multi-term behavior:
                // Bit 5: Strong title-level intent signal.
                //   - Prefer lexically \"perfect\" documents where every title
                //     token is explained by some query token.
                //   - Additionally, if the first query token is reasonably
                //     specific (len >= 4) and appears as a substring in the
                //     document text (e.g. \"scio\" in \"ScioŠkola Zlín\"), we
                //     treat that as an equally strong signal of user intent,
                //     but only when there is evidence of a phrase with at
                //     least two matched tokens (LongestPrefixRun >= 2).
                //     This prevents documents that match only the first word
                //     (e.g. \"tyršovka\" without \"Česká Lípa\") from outranking
                //     more complete matches.
                if (useLexicalPerfectDoc || (firstAnchorTokenHit && longestRun >= 2))
                {
                    precedence |= 32;
                }
                // Bit 4: Strong suffix phrase (covers all or all-but-one tokens)
                if (suffixRun >= Math.Max(2, Math.Min(features.TermsCount, rawQueryTerms) - 1))
                {
                    precedence |= 16;
                }
                // Bit 3: Moderate suffix phrase (at least 2 clean suffix tokens)
                else if (suffixRun >= 2)
                {
                    precedence |= 8;
                }
                
                // Bit 2: Strong overall phrase run (longest clean run >= 3)
                if (longestRun >= 3)
                {
                    precedence |= 4;
                }
                else if (longestRun >= 2)
                {
                    // Weaker phrase, but still better than isolated tokens
                    precedence |= 2;
                }
                
                // Bit 1: Compact phrase span (matched terms are adjacent in the document).
                // For at least two matched terms, span == 2 means the phrase is tight.
                if (features.TermsWithAnyMatch >= 2 && span == 2)
                {
                    precedence |= 1;
                }
            }
        }
        
        // Semantic tie-breaker within precedence tiers:
        // use an information-theoretic style signal:
        //   - avgCi = average per-term coverage (how well each query token is covered)
        //   - docCoverage = WordHits / DocTokenCount (how concentrated coverage is in the title)
        // For multi-term queries we use avgCi * docCoverage, which naturally prefers
        // shorter, cleaner titles (e.g., "The Matrix") over longer noisy variants
        // (e.g., "The Matrix Resurrections") when they explain the same query.
        float avgCi = (features.TermsCount > 0)
            ? features.SumCi / features.TermsCount
            : 0f;
        float semantic;
        if (isSingleTermQuery)
        {
            // For single-term queries we augment the per-term coverage signal
            // with a lexical similarity measure that captures how well the
            // (possibly joined) query suffix aligns with individual document
            // tokens. This is crucial for cases like:
            //   - "sciozlí"  vs  "ScioŠkola Zlín" / "ScioŠkola Kolín"
            //   - "sciozlínskáškola" vs the same titles
            //
            // The idea:
            //   1) If a document token occurs as a full substring inside the
            //      query, score it by how much of the query it covers and how
            //      early it appears (earlier substrings are more intentful).
            //   2) Otherwise, look for the longest prefix of the token that
            //      matches the *suffix* of the query (captures cases like
            //      "sciozlí" ending with "zlí" vs "Zlín").
            //
            // We combine coverage and lexical similarity so that documents
            // with identical coverage can still be ordered by how well their
            // tokens align with the query string.
            // The blend is purely lexical (no document-frequency/rarity):
            //   semantic = 0.5 * avgCi + 0.5 * lexicalSim
            float lexicalSim = ComputeSingleTermLexicalSimilarity(queryText, docTokensLex);
            semantic = 0.5f * avgCi + 0.5f * lexicalSim;
        }
        else if (features.DocTokenCount == 0)
        {
            semantic = avgCi;
        }
        else
        {
            float docCoverage = features.DocTokenCount > 0
                ? (float)features.WordHits / features.DocTokenCount
                : 0f;
            semantic = avgCi * docCoverage;

            // For longer, multi-term queries (3+ tokens), apply a small,
            // purely lexical intent bonus based on whether the document
            // simultaneously matches the first query token (as a stem prefix)
            // prefix of some title token) and the trailing phrase (captured
            // by a strong suffix run). This helps cases like
            //   - "tyršovka česká lípa"
            //     where the intended school has both a Tyrš-* token and a
            //     clean "Česká Lípa" suffix, without using any rarity/IDF.
            if (rawQueryTerms >= 3 && docTokensLex.Length > 0)
            {
                bool hasSuffixPair = features.SuffixPrefixRun >= 2;
                bool hasFirstStem = false;

                string firstToken = queryTokensLex[0];
                const int AnchorStemLength = 3;
                if (firstToken.Length >= AnchorStemLength)
                {
                    string stem = firstToken[..AnchorStemLength];
                    for (int i = 0; i < docTokensLex.Length; i++)
                    {
                        string t = docTokensLex[i];
                        if (!string.IsNullOrEmpty(t) &&
                            t.Length >= stem.Length &&
                            t.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                        {
                            hasFirstStem = true;
                            break;
                        }
                    }
                }

                int intentTier = 0;
                if (hasFirstStem && hasSuffixPair)
                    intentTier = 2;
                else if (hasFirstStem || hasSuffixPair)
                    intentTier = 1;

                if (intentTier > 0)
                {
                    float bonus = 0.15f * intentTier;
                    semantic = MathF.Min(1f, semantic + bonus);
                }
            }
        }
        
        byte semanticScore = (byte)Math.Clamp(semantic * 255f, 0, 255);
        
        return (ushort)((precedence << 8) | semanticScore);
    }
    
    /// <summary>
    /// Computes a lexical similarity signal for single-term queries by
    /// comparing the query string against individual document tokens.
    /// 
    /// Design:
    /// - If a document token T appears as a full substring of the query Q,
    ///   we score it by (|T| / |Q|) * (1 - startIndex(Q,T)/|Q|). This favors
    ///   longer substrings that appear earlier in the query.
    /// - Otherwise we look for:
    ///     (a) the longest prefix of T that matches the suffix of Q
    ///     (b) a small-edit-distance alignment between Q and T (LD ≤ 2)
    ///   and score the best of these as len / |Q|. This captures cases
    ///   like "sciozli" vs "Zlín" and "shwashan" vs "Shawshank" while
    ///   remaining purely lexical (no collection statistics).
    /// </summary>
    private static float ComputeSingleTermLexicalSimilarity(string queryText, string[] docTokensLex)
    {
        if (string.IsNullOrEmpty(queryText) || docTokensLex.Length == 0)
            return 0f;

        // Ignore very short single-term queries ("a", "th", etc.) – these are
        // already handled by the dedicated short-query pipeline.
        string q = queryText.ToLowerInvariant();
        int qLen = q.Length;
        if (qLen < 3)
            return 0f;

        float best = 0f;

        foreach (string token in docTokensLex)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            string t = token.ToLowerInvariant();
            if (t.Length < 2)
                continue;

            // 1) Full substring case: token appears inside the query.
            int idx = q.IndexOf(t, StringComparison.Ordinal);
            if (idx >= 0)
            {
                float lenFrac = (float)t.Length / qLen;
                float positionFactor = 1f - (float)idx / qLen;
                float score = lenFrac * positionFactor;
                if (score > best)
                    best = score;
                continue;
            }

            // 2a) Longest prefix of token that matches suffix of query.
            int maxK = Math.Min(qLen, t.Length);
            int bestK = 0;
            for (int len = maxK; len >= 2; len--)
            {
                if (q.AsSpan(qLen - len).Equals(t.AsSpan(0, len), StringComparison.Ordinal))
                {
                    bestK = len;
                    break;
                }
            }

            float prefixSuffixScore = bestK > 0 ? (float)bestK / qLen : 0f;

            // 2b) Small-edit-distance alignment between full query and token
            //     using Damerau-Levenshtein (transpositions allowed).
            //     We clamp maxEdits to 2 to keep this cheap and focused.
            float fuzzyScore = 0f;
            int maxEdits = 2;
            int dist = LevenshteinDistance.CalculateDamerau(q, t, maxEdits, ignoreCase: true);
            if (dist <= maxEdits)
            {
                fuzzyScore = (float)(qLen - dist) / qLen;
            }

            float combined = MathF.Max(prefixSuffixScore, fuzzyScore);
            if (combined > best)
                best = combined;
        }

        // Two-segment coverage: detect documents that simultaneously explain
        // both a prefix and a suffix fragment of the query (e.g. two words
        // joined together). We require room for at least two disjoint
        // fragments of minimum length L >= 3, which implies |Q| >= 2L.
        const int MinSegmentLength = 3;
        if (qLen >= 2 * MinSegmentLength)
        {
            int segLen = Math.Min(2 * MinSegmentLength, qLen / 2);
            string prefixFrag = q[..segLen];
            string suffixFrag = q.Substring(qLen - segLen, segLen);

            int prefixIndex = -1;
            int suffixIndex = -1;

            for (int i = 0; i < docTokensLex.Length; i++)
            {
                string token = docTokensLex[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                string t = token.ToLowerInvariant();
                if (t.Length < 3)
                    continue;

                if (prefixIndex == -1 &&
                    (t.StartsWith(prefixFrag, StringComparison.Ordinal) ||
                     prefixFrag.StartsWith(t, StringComparison.Ordinal)))
                {
                    prefixIndex = i;
                }

                if (suffixIndex == -1 &&
                    (t.EndsWith(suffixFrag, StringComparison.Ordinal) ||
                     suffixFrag.EndsWith(t, StringComparison.Ordinal)))
                {
                    suffixIndex = i;
                }

                if (prefixIndex != -1 && suffixIndex != -1)
                    break;
            }

            // Require that prefix and suffix evidence come from (possibly)
            // different tokens, so that we truly capture two-part coverage
            // like "scio" + "zlín" rather than a single word.
            if (prefixIndex != -1 && suffixIndex != -1 && prefixIndex != suffixIndex)
            {
                float twoSegScore = MathF.Min(1f, (prefixFrag.Length + suffixFrag.Length) / (float)qLen);
                if (twoSegScore > best)
                    best = twoSegScore;
            }
        }

        return best;
    }
    
    
    /// <summary>
    /// Looks up fuzzy word matches (exact, LD1, affix) using the WordMatcher.
    /// Returns internal document indices.
    /// </summary>
    private HashSet<int> LookupFuzzyWords(string queryText)
    {
        HashSet<int> result = [];

        if (_wordMatcher == null)
        {
            if (EnableDebugLogging)
                Console.WriteLine("[DEBUG] WordMatcher is null!");
            return result;
        }

        // Exact + LD1 matches
        HashSet<int> ids = _wordMatcher.Lookup(queryText, filter: null);
        if (EnableDebugLogging)
            Console.WriteLine($"[DEBUG] WordMatcher Lookup('{queryText}'): {ids.Count} exact/LD1 matches");
        foreach (int id in ids)
        {
            result.Add(id);
        }

        // Affix matches if enabled in coverage setup
        if (_coverageSetup != null && _coverageSetup.CoverPrefixSuffix)
        {
            HashSet<int> affixIds = _wordMatcher.LookupAffix(queryText, filter: null);
            if (EnableDebugLogging)
                Console.WriteLine($"[DEBUG] WordMatcher LookupAffix('{queryText}'): {affixIds.Count} affix matches");
            foreach (int id in affixIds)
            {
                result.Add(id);
            }
        }
        else if (EnableDebugLogging)
        {
            Console.WriteLine($"[DEBUG] Affix lookup disabled (CoverPrefixSuffix={_coverageSetup?.CoverPrefixSuffix})");
        }

        if (EnableDebugLogging)
            Console.WriteLine($"[DEBUG] WordMatcher total: {result.Count} matches");
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

            // Normalize text for accent-insensitive comparison
            string text = doc.IndexedText;
            if (_vectorModel.Tokenizer.TextNormalizer != null)
            {
                text = _vectorModel.Tokenizer.TextNormalizer.Normalize(text);
            }
            
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

        // Normalize for accent-insensitive coverage comparisons
        if (_vectorModel.Tokenizer.TextNormalizer != null)
        {
            docText = _vectorModel.Tokenizer.TextNormalizer.Normalize(docText);
        }

        return docText;
    }
    
    /// <summary>
    /// Specialized ranking for single-character queries (e.g., "a", "s").
    /// Uses a direct lexical scan over all titles to avoid pathologies where
    /// long documents with many occurrences of the character dominate purely
    /// by raw term frequency in the n-gram index.
    /// </summary>
    private ScoreEntry[] SearchSingleCharacterQuery(char ch, Span2D<byte> bestSegments, int queryIndex, int maxResults)
    {
        ch = char.ToLowerInvariant(ch);
        
        var docs = _vectorModel.Documents.GetAllDocuments();
        ScoreArray scores = new ScoreArray();
        
        char[] delimiters = _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? new[] { ' ' };
        
        foreach (Document doc in docs)
        {
            if (doc.Deleted)
                continue;
            
            string text = doc.IndexedText ?? string.Empty;
            if (text.Length == 0)
                continue;
            
            string lower = text.ToLowerInvariant();
            
            // Count occurrences and find earliest character position
            int charCount = 0;
            int firstCharIndex = -1;
            for (int i = 0; i < lower.Length; i++)
            {
                if (lower[i] == ch)
                {
                    charCount++;
                    if (firstCharIndex == -1)
                        firstCharIndex = i;
                }
            }
            
            if (charCount == 0)
                continue;
            
            // Tokenize into words to detect word starts
            string[] words = lower.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
            bool hasWordStart = false;
            int firstWordIndex = int.MaxValue;
            int wordStartCount = 0;
            
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length > 0 && w[0] == ch)
                {
                    hasWordStart = true;
                    wordStartCount++;
                    if (i < firstWordIndex)
                        firstWordIndex = i;
                }
            }
            
            bool anyExactToken = false;
            bool firstTokenExact = false;
            if (words.Length > 0)
            {
                firstTokenExact = words[0].Length == 1 && words[0][0] == ch;
                if (firstTokenExact)
                {
                    anyExactToken = true;
                }
                else
                {
                    for (int i = 0; i < words.Length; i++)
                    {
                        if (words[i].Length == 1 && words[i][0] == ch)
                        {
                            anyExactToken = true;
                            break;
                        }
                    }
                }
            }
            
            bool titleEqualsChar = lower.Length == 1 && lower[0] == ch;
            
            int precedence = 0;
            if (hasWordStart)
            {
                precedence |= 128; // any word starts with the character
                if (firstWordIndex == 0)
                {
                    precedence |= 64; // title starts with that character
                }
            }
            if (anyExactToken)
            {
                precedence |= 32; // has a single-character token 'x'
            }
            if (firstTokenExact)
            {
                precedence |= 16; // first token is exactly 'x'
            }
            if (titleEqualsChar)
            {
                precedence |= 8; // whole title is exactly 'x' (strongest)
            }
            
            // Prefer shorter titles when everything else is equal
            int wordCount = words.Length;
            if (wordCount <= 3)
            {
                precedence |= 32;
            }
            
            byte baseScore;
            if (hasWordStart)
            {
                // Earlier word index and more word-start matches are better.
                int posComponent = 255 - Math.Min(firstWordIndex * 16, 240); // 0 -> 255, 1 -> 239, ...
                int densityComponent = Math.Min(wordStartCount * 8, 32);
                int raw = Math.Clamp(posComponent + densityComponent, 0, 255);
                baseScore = (byte)raw;
            }
            else
            {
                // Fallback: contains the character but not at word start.
                int posComponent = 200 - Math.Min(Math.Max(firstCharIndex, 0) * 4, 180);
                int densityComponent = Math.Min(charCount * 4, 40);
                int raw = Math.Clamp(posComponent + densityComponent, 0, 200);
                baseScore = (byte)Math.Max(1, raw);
            }
            
            ushort finalScore = (ushort)((precedence << 8) | baseScore);
            scores.Add(doc.DocumentKey, finalScore);
            
            // Track best segment: simply mark the current segment as best for its base.
            if (bestSegments.Height > 0 && bestSegments.Width > 0)
            {
                int internalId = doc.Id;
                int segmentNumber = doc.SegmentNumber;
                int baseId = internalId - segmentNumber;
                
                if (baseId >= 0 && baseId < bestSegments.Height &&
                    queryIndex >= 0 && queryIndex < bestSegments.Width)
                {
                    bestSegments[baseId, queryIndex] = (byte)segmentNumber;
                }
            }
        }
        
        ScoreArray consolidated = ConsolidateSegments(scores, bestSegments);
        ScoreEntry[] all = consolidated.GetAll();
        
        if (maxResults < int.MaxValue && all.Length > maxResults)
        {
            all = all.Take(maxResults).ToArray();
        }
        
        return all;
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
        
        SearchEngine engine = new SearchEngine(vectorModel, enableCoverage, coverageSetup, tokenizerSetup, wordMatcherSetup, textNormalizer);
        
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
        WordMatcherSetup? wordMatcherSetup,
        TextNormalizer? textNormalizer = null)
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
            _wordMatcher = new WordMatcher.WordMatcher(wordMatcherSetup, tokenizerSetup.Delimiters, textNormalizer);
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
    private ScoreEntry[] ApplyFilter(ScoreEntry[] results, Filter filter)
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
    private ScoreEntry[] ApplyBoosts(ScoreEntry[] results, Boost[] boosts)
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
    private ScoreEntry[] ApplySort(ScoreEntry[] results, Field sortByField, bool ascending)
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
