using Infidex.Core;
using Infidex.Coverage;
using Infidex.Indexing;
using Infidex.Tokenization;
using Infidex.Utilities;
using Infidex.Metrics;
using Infidex.Internalized.CommunityToolkit;

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
    
    // Concurrency management
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
        WordMatcherSetup? wordMatcherSetup = null)
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
        
        // Setup vector model (Stage 1)
        _vectorModel = new VectorModel(tokenizer, stopTermLimit);
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
            wordMatcherSetup: config.WordMatcherSetup);
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
        }
        finally
        {
            Status = SearchEngineStatus.Ready;
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
            }
            finally
            {
                Status = SearchEngineStatus.Ready;
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
            // Note: VectorModel.CalculateWeights doesn't take a token in the current API, 
            // but BuildInvertedLists does.
            _vectorModel.BuildInvertedLists(cancellationToken: cancellationToken);
            _isIndexed = true;
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
        }
        finally
        {
            Status = SearchEngineStatus.Ready;
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
        }
        finally
        {
            Status = SearchEngineStatus.Ready;
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
            }
            finally
            {
                Status = SearchEngineStatus.Ready;
                _rwLock.ExitWriteLock();
            }
        }, cancellationToken);
    }
    
    /// <summary>
    /// Searches for documents matching the query.
    /// Thread-safe: Allows concurrent searches but blocks during indexing.
    /// Combines Stage 1 (relevancy ranking) and Stage 2 (coverage).
    /// Returns matching documents with their metadata intact, consolidating segments.
    /// </summary>
    public SearchResult Search(string query, int maxResults = 10)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (!_isIndexed)
                throw new InvalidOperationException("Index weights not calculated. Call CalculateWeights() first.");
            
            if (string.IsNullOrWhiteSpace(query))
                return new SearchResult([], 0);

            // Match original behavior: case-insensitive search by normalizing query text.
            string searchText = query.ToLowerInvariant();

            if (EnableDebugLogging)
            {
                Console.WriteLine($"[DEBUG] Search start: query=\"{query}\", normalized=\"{searchText}\", maxResults={maxResults}");
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
                ScoreArray relevancyScores = _vectorModel.Search(searchText, bestSegments, queryIndex: 0);

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
                if (_coverageEngine == null || _coverageSetup == null)
                {
                    ScoreArray consolidated = ConsolidateSegments(relevancyScores, bestSegments);
                    ScoreEntry[] results = consolidated.GetTopK(maxResults);
                    return new SearchResult(results, results.Length);
                }
                
                // STAGE 2: Coverage (Lexical Matching) - matches original exactly
                int coverageDepth = _coverageSetup.CoverageDepth;
                ScoreEntry[] topCandidates = relevancyScores.GetTopK(coverageDepth);

                if (EnableDebugLogging)
                {
                    Console.WriteLine($"[DEBUG] Stage2 Coverage: depth={coverageDepth}, top TF-IDF candidates:");
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
                            lcsFromSpan = (int)lcsAndWordHitsSpan[0, docIndex];
                            if (lcsFromSpan == 0)
                            {
                                int errorTolerance = 0;
                                if (_coverageSetup != null && query.Length >= _coverageSetup.CoverageQLimitForErrorTolerance)
                                {
                                    errorTolerance = (int)(query.Length * _coverageSetup.CoverageLcsErrorToleranceRelativeq);
                                }
                                
                                lcsFromSpan = CalculateLcs(searchText, docText, errorTolerance);
                                lcsAndWordHitsSpan[0, docIndex] = (byte)Math.Min(lcsFromSpan, 255);
                            }
                        }

                        byte coverageScore = _coverageEngine.CalculateCoverageScore(
                            searchText,
                            docText,
                            lcsFromSpan,
                            out int wordHits);

                        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
                        {
                            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(wordHits, 255);
                        }

                        maxWordHits = Math.Max(maxWordHits, wordHits);
                        
                        // Score fusion: from word matcher we always use coverage score
                        byte finalScore = coverageScore;

                        if (EnableDebugLogging)
                        {
                            Console.WriteLine(
                                $"[DEBUG] Coverage (WordMatcher): docKey={doc.DocumentKey}, " +
                                $"coverage={coverageScore}, wordHits={wordHits}, lcsFromSpan={lcsFromSpan}, " +
                                $"finalScore={finalScore}");
                        }

                        finalScores.Add(doc.DocumentKey, finalScore);
                    }

                    // --- Coverage for TF-IDF candidates (isFromWordMatcher = false) ---
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
                            lcsFromSpan = (int)lcsAndWordHitsSpan[0, docIndex];
                            if (lcsFromSpan == 0)
                            {
                                int errorTolerance = 0;
                                if (_coverageSetup != null && query.Length >= _coverageSetup.CoverageQLimitForErrorTolerance)
                                {
                                    errorTolerance = (int)(query.Length * _coverageSetup.CoverageLcsErrorToleranceRelativeq);
                                }
                                
                                lcsFromSpan = CalculateLcs(searchText, docText, errorTolerance);
                                lcsAndWordHitsSpan[0, docIndex] = (byte)Math.Min(lcsFromSpan, 255);
                            }
                        }

                        byte coverageScore = _coverageEngine.CalculateCoverageScore(
                            searchText,
                            docText,
                            lcsFromSpan,
                            out int wordHits);

                        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
                        {
                            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(wordHits, 255);
                        }

                        maxWordHits = Math.Max(maxWordHits, wordHits);
                        
                        // Score fusion: (!isFromWordMatcher && coverage <= relevancy) ? relevancy : coverage
                        byte finalScore = (!false && coverageScore <= candidate.Score)
                            ? candidate.Score
                            : coverageScore;

                        if (EnableDebugLogging)
                        {
                            Console.WriteLine(
                                $"[DEBUG] Coverage (TF-IDF): docKey={doc.DocumentKey}, relevancy={candidate.Score}, " +
                                $"coverage={coverageScore}, wordHits={wordHits}, lcsFromSpan={lcsFromSpan}, " +
                                $"finalScore={finalScore}");
                        }

                        finalScores.Add(doc.DocumentKey, finalScore);
                    }

                    // If there is no lexical evidence at all (no word hits and no WordMatcher hits),
                    // do not return results purely based on TF-IDF scores. This matches the
                    // behavior observed in the reference engine for non-matching queries.
                    if (maxWordHits == 0 && wordMatcherInternalIds.Count == 0)
                    {
                        return new SearchResult([], topCandidates.Length);
                    }

                    // STAGE 3: Consolidate segments - keep only best segment per DocumentKey
                    ScoreArray consolidatedFinalScores = ConsolidateSegments(finalScores, bestSegments);

                    // Get final top-K results (matches FilteredGetKTop: CoverageDepth + exactWordHits, where exactWordHits=0)
                    int exactWordHits = 0; // Always 0 in our simplified implementation
                    ScoreEntry[] finalResults = consolidatedFinalScores.GetTopK(coverageDepth + exactWordHits);

                    // Apply truncation if enabled (matches FindTruncationPoint shape)
                    if (_coverageSetup.Truncate && finalResults.Length > 0)
                    {
                        int truncationIndex = CalculateTruncationIndex(
                            finalResults,
                            maxWordHits,
                            lcsAndWordHitsSpan,
                            documentKeyToIndex);

                        if (truncationIndex >= 0 && truncationIndex < finalResults.Length - 1)
                        {
                            finalResults = finalResults.Take(truncationIndex + 1).ToArray();
                        }
                        // If truncationIndex == -1, we don't truncate (return all up to maxResults below)
                    }

                    // Limit to maxResults (matches MaxNumberOfRecordsToReturn in original)
                    if (finalResults.Length > maxResults)
                    {
                        finalResults = finalResults.Take(maxResults).ToArray();
                    }

                    if (EnableDebugLogging)
                    {
                        Console.WriteLine("[DEBUG] Final fused results:");
                        foreach (ScoreEntry r in finalResults)
                        {
                            Console.WriteLine($"  [DEBUG]   docKey={r.DocumentId}, score={r.Score}");
                        }
                    }
                    
                    return new SearchResult(finalResults, topCandidates.Length);
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
        finally
        {
            _rwLock.ExitReadLock();
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
    /// Consolidates segment scores to return only the best-scoring segment per DocumentKey
    /// </summary>
    private ScoreArray ConsolidateSegments(ScoreArray scores, Span2D<byte> bestSegments)
    {
        ScoreArray consolidated = new ScoreArray();
        
        // Group all scores by DocumentKey and keep only the highest score per key
        Dictionary<long, byte> scoresByKey = new Dictionary<long, byte>();
        
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
        foreach (KeyValuePair<long, byte> kvp in scoresByKey)
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
    private int CalculateLcs(string q, string r, int errorTolerance)
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
        
        int minWordHits = Math.Max(
            _coverageSetup.CoverageMinWordHitsAbs,
            maxWordHits - _coverageSetup.CoverageMinWordHitsRelative);
        
        // First pass: detect whether there is any lexical evidence at all (word hits or LCS).
        // If there is none, we should not keep documents purely based on high TF-IDF scores.
        bool hasAnyLexicalEvidence = false;
        for (int i = results.Length - 1; i >= 0; i--)
        {
            if (!documentKeyToIndex.TryGetValue(results[i].DocumentId, out int docIndex))
                continue;
            if (docIndex >= lcsAndWordHitsSpan.Height)
                continue;

            byte wordHitsByte = lcsAndWordHitsSpan[1, docIndex];
            byte lcsByte = lcsAndWordHitsSpan[0, docIndex];
            if (wordHitsByte >= minWordHits || lcsByte > 0)
            {
                hasAnyLexicalEvidence = true;
                break;
            }
        }
        
        // Second pass: find last result from tail that meets criteria (matches IfMuagPWrF),
        // but only allow the pure score-based override when there is some lexical evidence present.
        for (int i = results.Length - 1; i >= 0; i--)
        {
            if (!documentKeyToIndex.TryGetValue(results[i].DocumentId, out int docIndex))
                continue;
            
            if (docIndex >= lcsAndWordHitsSpan.Height)
                continue;
            
            byte wordHitsByte = lcsAndWordHitsSpan[1, docIndex];
            byte lcsByte = lcsAndWordHitsSpan[0, docIndex];
            
            bool passesWordHits = wordHitsByte >= minWordHits;
            bool passesLcs = lcsByte > 0;
            bool passesScore = hasAnyLexicalEvidence && results[i].Score >= _coverageSetup.TruncationScore;
            
            if (passesWordHits || passesLcs || passesScore)
                return i;
        }
        
        return -1; // No truncation
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
            _vectorModel.Save(filePath);
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
        WordMatcherSetup? wordMatcherSetup = null)
    {
        textNormalizer ??= TextNormalizer.CreateDefault();
        tokenizerSetup ??= TokenizerSetup.CreateDefault();
        
        Tokenizer tokenizer = new Tokenizer(
            indexSizes,
            startPadSize,
            stopPadSize,
            textNormalizer,
            tokenizerSetup);
            
        VectorModel vectorModel = VectorModel.Load(filePath, tokenizer, stopTermLimit);
        
        return new SearchEngine(vectorModel, enableCoverage, coverageSetup, tokenizerSetup, wordMatcherSetup);
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
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () => 
        {
            textNormalizer ??= TextNormalizer.CreateDefault();
            tokenizerSetup ??= TokenizerSetup.CreateDefault();
            
            Tokenizer tokenizer = new Tokenizer(
                indexSizes,
                startPadSize,
                stopPadSize,
                textNormalizer,
                tokenizerSetup);
                
            VectorModel vectorModel = await VectorModel.LoadAsync(filePath, tokenizer, stopTermLimit);
            
            return new SearchEngine(vectorModel, enableCoverage, coverageSetup, tokenizerSetup, wordMatcherSetup);
        }, cancellationToken);
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
        }
        
        if (wordMatcherSetup != null && tokenizerSetup != null)
        {
            _wordMatcher = new WordMatcher.WordMatcher(wordMatcherSetup, tokenizerSetup.Delimiters);
            // Populate WordMatcher from loaded documents
            foreach (Document doc in _vectorModel.Documents.GetAllDocuments())
            {
                _wordMatcher.Load(doc.IndexedText, doc.Id);
            }
        }
    }

    public void Dispose()
    {
        _rwLock?.Dispose();
        _wordMatcher?.Dispose();
    }
}

/// <summary>
/// Represents search results
/// </summary>
public class SearchResult
{
    public ScoreEntry[] Results { get; }
    public int CandidatesProcessed { get; }
    
    public SearchResult(ScoreEntry[] results, int candidatesProcessed)
    {
        Results = results;
        CandidatesProcessed = candidatesProcessed;
    }
    
    public override string ToString() => 
        $"{Results.Length} results (from {CandidatesProcessed} candidates)";
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
