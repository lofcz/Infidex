using Infidex.Core;
using Infidex.Coverage;
using Infidex.Indexing;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Tokenization;
using Infidex.Utilities;
using System.Diagnostics;

namespace Infidex.Scoring;

/// <summary>
/// Orchestrates the multi-stage search pipeline: TF-IDF → Coverage → Fusion.
/// </summary>
internal sealed class SearchPipeline
{
    private readonly VectorModel _vectorModel;
    private readonly CoverageEngine? _coverageEngine;
    private readonly CoverageSetup? _coverageSetup;
    private readonly WordMatcher.WordMatcher? _wordMatcher;
        
        // Short-query specialization thresholds
        private const int ShortQueryMaxLength = 3;
        
        /// <summary>
        /// If a short query (length <= ShortQueryMaxLength) matches more than this many
        /// documents in the positional prefix index, we skip coverage and rely on the
        /// short-query fast path / BM25 backbone instead.
        /// </summary>
        private const int ShortQueryCoverageDocCap = 500;

    public bool EnableDebugLogging { get; set; }

    public SearchPipeline(
        VectorModel vectorModel,
        CoverageEngine? coverageEngine,
        CoverageSetup? coverageSetup,
        WordMatcher.WordMatcher? wordMatcher)
    {
        _vectorModel = vectorModel;
        _coverageEngine = coverageEngine;
        _coverageSetup = coverageSetup;
        _wordMatcher = wordMatcher;
        
        // Wire corpus statistics into coverage engine for IDF computation
        if (_coverageEngine != null)
        {
            _coverageEngine.SetCorpusStatistics(_vectorModel.TermCollection, _vectorModel.Documents.Count);
            _coverageEngine.SetDocumentMetadataCache(_vectorModel.DocumentMetadataCache);
        }
    }

    public ScoreEntry[] Execute(string searchText, CoverageSetup? coverageSetup, int coverageDepth, int maxResults = int.MaxValue)
    {
        // Always enable timing for performance analysis
        Stopwatch perfStopwatch = Stopwatch.StartNew();
        long normMs = 0, tfidfMs = 0, consolidateMs = 0, topKMs = 0, prescreenMs = 0, 
             wordMatcherCoverageMs = 0, tfidfCoverageMs = 0, truncationMs = 0;
        long allocMs = 0; // No expensive allocation for segmented docs anymore!

        if (string.IsNullOrWhiteSpace(searchText))
            return [];

        long normStart = perfStopwatch.ElapsedMilliseconds;
        if (_vectorModel.Tokenizer.TextNormalizer != null)
        {
            searchText = _vectorModel.Tokenizer.TextNormalizer.Normalize(searchText);
        }
        normMs = perfStopwatch.ElapsedMilliseconds - normStart;

        if (EnableDebugLogging)
        {
            Console.WriteLine($"[DEBUG] Search start: normalized=\"{searchText}\", coverageDepth={coverageDepth}");
        }

        // Lazy sparse storage: only allocate if we encounter segmented documents (rare)
        // For non-segmented documents (99.9% of cases), this stays null - zero overhead!
        Dictionary<int, byte>? bestSegmentsMap = null;

        long stage1StartMs = perfStopwatch.ElapsedMilliseconds;
        
        ScoreArray relevancyScores = ExecuteRelevancyStage(searchText, ref bestSegmentsMap, coverageDepth, maxResults, ref tfidfMs, perfStopwatch);

        long consolidateStart = perfStopwatch.ElapsedMilliseconds;

        // Always compute consolidated Stage 1 candidates so we can fall back if coverage
        // decides there are no good lexical matches (e.g. typo-heavy queries like "battamam").
        ScoreArray consolidatedStage1 = SegmentProcessor.ConsolidateSegments(relevancyScores, bestSegmentsMap);
        ScoreEntry[] stage1Results = consolidatedStage1.GetAll();
        consolidateMs = perfStopwatch.ElapsedMilliseconds - consolidateStart;
        
        long stage1TotalMs = perfStopwatch.ElapsedMilliseconds - stage1StartMs;

        // Decide whether coverage should run.
        // For general queries we use QueryAnalyzer.CanUseNGrams.
        // For very short queries (1-3 chars) we additionally consult the positional
        // prefix index and skip coverage if the prefix matches too many documents.
        bool isShortQuery = searchText.Length > 0 &&
                            searchText.Length <= ShortQueryMaxLength &&
                            (_vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? [' ']).All(d => !searchText.Contains(d));

        // Two-step strategy for short queries: if the fast path already fills the
        // requested number of results, skip the expensive coverage stage entirely.
        if (isShortQuery && stage1Results.Length >= maxResults && maxResults < int.MaxValue)
        {
            if (EnableDebugLogging)
            {
                Console.WriteLine($"[DEBUG] Short-query fast path satisfied maxResults={maxResults}; skipping coverage.");
            }

            if (stage1Results.Length > maxResults)
                stage1Results = stage1Results[..maxResults];

            return stage1Results;
        }

        int shortQueryDocCount = 0;
        bool shortQueryDocCountKnown = false;

        if (isShortQuery && _vectorModel.ShortQueryIndex != null)
        {
            shortQueryDocCount = _vectorModel.ShortQueryIndex.CountDocuments(searchText.AsSpan());
            shortQueryDocCountKnown = true;
        }

        bool canUseNGrams = QueryAnalyzer.CanUseNGrams(searchText, _vectorModel.Tokenizer);

        bool allowShortQueryCoverage =
            isShortQuery &&
            shortQueryDocCountKnown &&
            shortQueryDocCount > 0 &&
            shortQueryDocCount <= ShortQueryCoverageDocCap;

        bool skipCoverageDueToShortQueryDocCap =
            isShortQuery &&
            shortQueryDocCountKnown &&
            shortQueryDocCount > ShortQueryCoverageDocCap;

        if (_coverageEngine == null || coverageSetup == null ||
            (!canUseNGrams && !allowShortQueryCoverage) ||
            skipCoverageDueToShortQueryDocCap)
        {
            long overhead = stage1TotalMs - (tfidfMs + consolidateMs);
            Console.WriteLine($"[TIMING] total={perfStopwatch.ElapsedMilliseconds}ms (norm={normMs}ms, stage1={stage1TotalMs}ms [tfidf={tfidfMs}ms, consolidate={consolidateMs}ms, overhead={overhead}ms]) - NO COVERAGE");
            return stage1Results;
        }

        long coverageStartMs = perfStopwatch.ElapsedMilliseconds;
            
        ScoreEntry[] coverageResults = ExecuteCoverageStage(
            searchText, coverageSetup, coverageDepth, maxResults,
            relevancyScores, ref bestSegmentsMap,
            ref topKMs, ref prescreenMs, ref wordMatcherCoverageMs, ref tfidfCoverageMs, ref truncationMs,
            perfStopwatch, tfidfMs);
                
        long coverageTotalMs = perfStopwatch.ElapsedMilliseconds - coverageStartMs;

        // Safety net: if coverage returns no results but TF-IDF produced candidates,
        // fall back to the TF-IDF backbone instead of returning an empty result set.
        if (coverageResults.Length == 0 && stage1Results.Length > 0)
        {
            if (EnableDebugLogging)
            {
                Console.WriteLine("[DEBUG] Coverage produced 0 results; falling back to TF-IDF backbone results.");
            }
            long s1Overhead = stage1TotalMs - (tfidfMs + consolidateMs);
            long covOverhead = coverageTotalMs - (topKMs + prescreenMs + wordMatcherCoverageMs + tfidfCoverageMs + truncationMs);
            Console.WriteLine($"[TIMING] total={perfStopwatch.ElapsedMilliseconds}ms (norm={normMs}ms, stage1={stage1TotalMs}ms [tfidf={tfidfMs}ms, consolidate={consolidateMs}ms, oh={s1Overhead}ms], coverage={coverageTotalMs}ms [topK={topKMs}ms, prescreen={prescreenMs}ms, wmCov={wordMatcherCoverageMs}ms, tfidfCov={tfidfCoverageMs}ms, trunc={truncationMs}ms, oh={covOverhead}ms]) - FALLBACK");
            return stage1Results;
        }

        long overhead1 = stage1TotalMs - (tfidfMs + consolidateMs);
        long coverageOverhead = coverageTotalMs - (topKMs + prescreenMs + wordMatcherCoverageMs + tfidfCoverageMs + truncationMs);
        long totalOverhead = perfStopwatch.ElapsedMilliseconds - (stage1TotalMs + coverageTotalMs);
        Console.WriteLine($"[TIMING] total={perfStopwatch.ElapsedMilliseconds}ms (norm={normMs}ms, stage1={stage1TotalMs}ms [tfidf={tfidfMs}ms, consolidate={consolidateMs}ms, oh={overhead1}ms], coverage={coverageTotalMs}ms [topK={topKMs}ms, prescreen={prescreenMs}ms, wmCov={wordMatcherCoverageMs}ms, tfidfCov={tfidfCoverageMs}ms, trunc={truncationMs}ms, oh={coverageOverhead}ms], finalOH={totalOverhead}ms)");
        return coverageResults;
    }

        private ScoreArray ExecuteRelevancyStage(
        string searchText,
        ref Dictionary<int, byte>? bestSegmentsMap,
        int coverageDepth,
        int maxResults,
        ref long tfidfMs,
        Stopwatch? perfStopwatch)
    {
        long tfidfStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
        int minIndexSize = _vectorModel.Tokenizer.IndexSizes.Min();
        char[] delimiters = _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? [' '];

        var (canUseNGrams, hasMixedTerms, longWordsSearchText) = QueryAnalyzer.Analyze(searchText, _vectorModel.Tokenizer);
        ScoreArray relevancyScores;

        if (!canUseNGrams)
        {
            if (searchText.Length == 1)
            {
                // Single-character query: principled two-step strategy.
                // 1) Try the positional prefix index fast path via ShortQueryResolver.
                //    If it can already supply at least maxResults candidates, we use it.
                // 2) Otherwise, fall back to the original full scan so semantics are preserved.
                char ch = char.ToLowerInvariant(searchText[0]);

                if (_vectorModel.ShortQueryResolver != null &&
                    _vectorModel.ShortQueryIndex != null &&
                    maxResults < int.MaxValue)
                {
                    Span<char> prefixSpan = stackalloc char[1];
                    prefixSpan[0] = ch;

                    if (_vectorModel.ShortQueryResolver.TryGetChampions(prefixSpan, maxResults, out ScoreEntry[] prefixResults))
                    {
                        relevancyScores = new ScoreArray();
                        foreach (ScoreEntry entry in prefixResults)
                        {
                            relevancyScores.Add(entry.DocumentId, entry.Score, entry.Tiebreaker);
                        }

                        goto DoneRelevancy;
                    }
                }

                // Fallback: original single-character scan (contains-char-anywhere semantics).
                ScoreEntry[] singleCharResults = ShortQueryProcessor.SearchSingleCharacter(
                    ch, bestSegmentsMap, queryIndex: 0, maxResults: maxResults,
                    _vectorModel.Documents.GetAllDocuments(), delimiters);

                relevancyScores = new ScoreArray();
                foreach (var entry in singleCharResults)
                {
                    relevancyScores.Add(entry.DocumentId, entry.Score, entry.Tiebreaker);
                }
            }
            else
            {
                if (EnableDebugLogging)
                {
                    Console.WriteLine($"[DEBUG] Short query fast path: query='{searchText}', len={searchText.Length}, minIndexSize={minIndexSize}");
                }

                relevancyScores = ShortQueryProcessor.SearchShortQuery(
                    searchText, searchText.ToLowerInvariant(), minIndexSize,
                    _vectorModel.Tokenizer.StartPadSize, _vectorModel.FstIndex,
                    _vectorModel.TermCollection, _vectorModel.Documents,
                    bestSegmentsMap, delimiters, EnableDebugLogging);
            }
        }
        else
        {
            string tfidfQuery = hasMixedTerms ? longWordsSearchText : searchText;
            if (string.IsNullOrWhiteSpace(tfidfQuery))
                tfidfQuery = searchText;

            _vectorModel.EnableDebugLogging = EnableDebugLogging;
            relevancyScores = _vectorModel.SearchWithMaxScore(tfidfQuery, coverageDepth, bestSegmentsMap, queryIndex: 0);
        }

    DoneRelevancy:

        if (perfStopwatch != null)
            tfidfMs = perfStopwatch.ElapsedMilliseconds - tfidfStart;

        if (EnableDebugLogging)
        {
            Console.WriteLine($"[DEBUG] Stage1 TF-IDF: {relevancyScores.GetAll().Length} candidates");
        }

        return relevancyScores;
    }

    private ScoreEntry[] ExecuteCoverageStage(
        string searchText,
        CoverageSetup coverageSetup,
        int coverageDepth,
        int maxResults,
        ScoreArray relevancyScores,
        ref Dictionary<int, byte>? bestSegmentsMap,
        ref long topKMs,
        ref long prescreenMs,
        ref long wordMatcherCoverageMs,
        ref long tfidfCoverageMs,
        ref long truncationMs,
        Stopwatch? perfStopwatch,
        long tfidfMs)
    {
        long topKStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
        ScoreEntry[] topCandidates = relevancyScores.GetTopK(coverageDepth);
        if (perfStopwatch != null) topKMs = perfStopwatch.ElapsedMilliseconds - topKStart;

        long prescreenStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
        if (coverageSetup.EnableLexicalPrescreen && topCandidates.Length > 0)
        {
            topCandidates = LexicalPrescreen.Apply(
                searchText, topCandidates, _vectorModel.Tokenizer,
                _vectorModel.TermCollection, _vectorModel.Documents, coverageSetup);
        }
        if (perfStopwatch != null) prescreenMs = perfStopwatch.ElapsedMilliseconds - prescreenStart;

        HashSet<int> wordMatcherInternalIds = WordMatcherLookup.Execute(
            searchText, _wordMatcher, _coverageSetup,
            _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? [' '],
            EnableDebugLogging);

        if (EnableDebugLogging)
        {
            Console.WriteLine($"[DEBUG] WordMatcher returned {wordMatcherInternalIds.Count} candidates for '{searchText}'");
            Console.WriteLine($"[DEBUG] TF-IDF returned {topCandidates.Length} candidates");
        }

        var (uniqueDocKeys, documentKeyToIndex) = BuildDocumentKeyIndex(topCandidates, wordMatcherInternalIds);

        long lcsSpanPointer = 0;
        Span2D<byte> lcsAndWordHitsSpan = default;

        if (uniqueDocKeys.Count > 0)
        {
            lcsAndWordHitsSpan = SpanAlloc.Alloc2D(2, documentKeyToIndex.Count, out lcsSpanPointer);
        }

        try
        {
            ScoreArray finalScores = new ScoreArray();
            int maxWordHits = 0;
            char[] delimiters = _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            int minStemLength = _vectorModel.Tokenizer.IndexSizes.Min();

            // Tokenize the query once per search and reuse across all candidate documents
            string[] queryTokens = searchText.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);

            HashSet<int> tfidfInternalIds = BuildTfidfInternalIdSet(topCandidates);
            var (wmOverlapping, wmUnique) = PartitionWordMatcherCandidates(wordMatcherInternalIds, tfidfInternalIds);

            int wmLimit = Math.Max(0, coverageDepth - wmOverlapping.Count);
            List<int> wmToProcess = wmOverlapping.Concat(wmUnique.Take(wmLimit)).ToList();

            long wmCoverageStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
            foreach (int internalId in wmToProcess)
            {
                ProcessCandidate(internalId, searchText, queryTokens, coverageSetup, 0f,
                    ref bestSegmentsMap, lcsAndWordHitsSpan, documentKeyToIndex,
                    finalScores, ref maxWordHits, delimiters, minStemLength);
            }
            if (perfStopwatch != null) wordMatcherCoverageMs = perfStopwatch.ElapsedMilliseconds - wmCoverageStart;

            long tfidfCoverageStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
            foreach (ScoreEntry candidate in topCandidates)
            {
                Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(candidate.DocumentId);
                if (doc == null || doc.Deleted)
                    continue;

                ProcessCandidate(doc.Id, searchText, queryTokens, coverageSetup, (float)candidate.Score / 255f,
                    ref bestSegmentsMap, lcsAndWordHitsSpan, documentKeyToIndex,
                    finalScores, ref maxWordHits, delimiters, minStemLength);
            }
            if (perfStopwatch != null) tfidfCoverageMs = perfStopwatch.ElapsedMilliseconds - tfidfCoverageStart;

            if (maxWordHits == 0 && wordMatcherInternalIds.Count == 0)
                return [];

            ScoreArray consolidatedFinalScores = SegmentProcessor.ConsolidateSegments(finalScores, bestSegmentsMap);
            ScoreEntry[] finalResults = consolidatedFinalScores.GetTopK(coverageDepth);

            int truncationIndex = -1;
            if (coverageSetup.Truncate && finalResults.Length > 0)
            {
                long truncStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
                truncationIndex = ResultProcessor.CalculateTruncationIndex(
                    finalResults, maxWordHits, lcsAndWordHitsSpan, documentKeyToIndex, coverageSetup);
                if (perfStopwatch != null) truncationMs = perfStopwatch.ElapsedMilliseconds - truncStart;

                if (EnableDebugLogging)
                    Console.WriteLine($"[TRUNC] finalResults.Length={finalResults.Length}, truncationIndex={truncationIndex}, maxWordHits={maxWordHits}");
            }

            int resultCount = (truncationIndex == -1 || !coverageSetup.Truncate)
                ? maxResults
                : Math.Min(Math.Max(0, truncationIndex) + 1, maxResults);

            if (EnableDebugLogging)
                Console.WriteLine($"[TRUNC] resultCount={resultCount} (truncIdx={truncationIndex}, maxResults={maxResults})");

            if (finalResults.Length > resultCount)
                finalResults = finalResults.Take(resultCount).ToArray();

            if (EnableDebugLogging)
            {
                Console.WriteLine("[DEBUG] Final fused results:");
                foreach (ScoreEntry r in finalResults)
                    Console.WriteLine($"  [DEBUG]   docKey={r.DocumentId}, score={r.Score}");

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

    private void ProcessCandidate(
        int internalId,
        string searchText,
        string[] queryTokens,
        CoverageSetup coverageSetup,
        float baseScore,
        ref Dictionary<int, byte>? bestSegmentsMap,
        Span2D<byte> lcsAndWordHitsSpan,
        Dictionary<long, int> documentKeyToIndex,
        ScoreArray finalScores,
        ref int maxWordHits,
        char[] delimiters,
        int minStemLength)
    {
        Document? doc = _vectorModel.Documents.GetDocument(internalId);
        if (doc == null || doc.Deleted)
            return;

        if (!documentKeyToIndex.TryGetValue(doc.DocumentKey, out int docIndex))
            return;

        string docText = SegmentProcessor.GetBestSegmentText(
            doc, bestSegmentsMap, _vectorModel.Documents, _vectorModel.Tokenizer.TextNormalizer);

        int lcsFromSpan = 0;
        if (docIndex < lcsAndWordHitsSpan.Height)
        {
            lcsFromSpan = lcsAndWordHitsSpan[0, docIndex];
            if (lcsFromSpan == 0)
            {
                int errorTolerance = 0;
                if (searchText.Length >= coverageSetup.CoverageQLimitForErrorTolerance)
                    errorTolerance = (int)(searchText.Length * coverageSetup.CoverageLcsErrorToleranceRelativeq);

                lcsFromSpan = SegmentProcessor.CalculateLcs(searchText, docText, errorTolerance);
                lcsAndWordHitsSpan[0, docIndex] = (byte)Math.Min(lcsFromSpan, 255);
            }
        }

        CoverageFeatures features = _coverageEngine!.CalculateFeatures(searchText, docText, lcsFromSpan, internalId);
        var (finalScore, tiebreaker) = FusionScorer.Calculate(
            searchText,
            docText,
            features,
            baseScore,
            minStemLength,
            delimiters);

        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(features.WordHits, 255);

        maxWordHits = Math.Max(maxWordHits, features.WordHits);

        if (EnableDebugLogging)
            Console.WriteLine($"[DEBUG] Coverage: docKey={doc.DocumentKey}, final={finalScore}, tie={tiebreaker}");

        finalScores.Add(doc.DocumentKey, finalScore, tiebreaker);
    }

    private (HashSet<long> uniqueDocKeys, Dictionary<long, int> documentKeyToIndex) BuildDocumentKeyIndex(
        ScoreEntry[] topCandidates,
        HashSet<int> wordMatcherInternalIds)
    {
        HashSet<long> uniqueDocKeys = [];
        foreach (ScoreEntry candidate in topCandidates)
            uniqueDocKeys.Add(candidate.DocumentId);

        foreach (int internalId in wordMatcherInternalIds)
        {
            Document? doc = _vectorModel.Documents.GetDocument(internalId);
            if (doc != null && !doc.Deleted)
                uniqueDocKeys.Add(doc.DocumentKey);
        }

        Dictionary<long, int> documentKeyToIndex = new Dictionary<long, int>();
        int nextIndex = 0;
        foreach (long key in uniqueDocKeys)
            documentKeyToIndex[key] = nextIndex++;

        return (uniqueDocKeys, documentKeyToIndex);
    }

    private HashSet<int> BuildTfidfInternalIdSet(ScoreEntry[] topCandidates)
    {
        HashSet<int> result = [];
        foreach (ScoreEntry candidate in topCandidates)
        {
            Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(candidate.DocumentId);
            if (doc != null)
                result.Add(doc.Id);
        }
        return result;
    }

    private static (List<int> overlapping, List<int> unique) PartitionWordMatcherCandidates(
        HashSet<int> wordMatcherInternalIds,
        HashSet<int> tfidfInternalIds)
    {
        List<int> overlapping = [];
        List<int> unique = [];

        foreach (int id in wordMatcherInternalIds)
        {
            if (tfidfInternalIds.Contains(id))
                overlapping.Add(id);
            else
                unique.Add(id);
        }

        return (overlapping, unique);
    }
}

