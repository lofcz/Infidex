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
    }

    public ScoreEntry[] Execute(string searchText, CoverageSetup? coverageSetup, int coverageDepth, int maxResults = int.MaxValue)
    {
        Stopwatch? perfStopwatch = EnableDebugLogging ? Stopwatch.StartNew() : null;
        long tfidfMs = 0, topKMs = 0, wordMatcherCoverageMs = 0, tfidfCoverageMs = 0, truncationMs = 0;

        if (string.IsNullOrWhiteSpace(searchText))
            return [];

        if (_vectorModel.Tokenizer.TextNormalizer != null)
        {
            searchText = _vectorModel.Tokenizer.TextNormalizer.Normalize(searchText);
        }

        if (EnableDebugLogging)
        {
            Console.WriteLine($"[DEBUG] Search start: normalized=\"{searchText}\", coverageDepth={coverageDepth}");
        }

        long bestSegmentsPointer = 0;
        Span2D<byte> bestSegments = default;

        if (_vectorModel.Documents.Count > 0)
        {
            bestSegments = SpanAlloc.Alloc2D(_vectorModel.Documents.Count, 1, out bestSegmentsPointer);
        }

        try
        {
            ScoreArray relevancyScores = ExecuteRelevancyStage(searchText, bestSegments, coverageDepth, maxResults, ref tfidfMs, perfStopwatch);

            // Always compute consolidated TF-IDF candidates so we can fall back if coverage
            // decides there are no good lexical matches (e.g. typo-heavy queries like "battamam").
            ScoreArray consolidatedStage1 = SegmentProcessor.ConsolidateSegments(relevancyScores, bestSegments);
            ScoreEntry[] stage1Results = consolidatedStage1.GetAll();

            if (_coverageEngine == null || coverageSetup == null || !QueryAnalyzer.CanUseNGrams(searchText, _vectorModel.Tokenizer))
            {
                return stage1Results;
            }

            ScoreEntry[] coverageResults = ExecuteCoverageStage(
                searchText, coverageSetup, coverageDepth, maxResults,
                relevancyScores, bestSegments,
                ref topKMs, ref wordMatcherCoverageMs, ref tfidfCoverageMs, ref truncationMs,
                perfStopwatch, tfidfMs);

            // Safety net: if coverage returns no results but TF-IDF produced candidates,
            // fall back to the TF-IDF backbone instead of returning an empty result set.
            if (coverageResults.Length == 0 && stage1Results.Length > 0)
            {
                if (EnableDebugLogging)
                {
                    Console.WriteLine("[DEBUG] Coverage produced 0 results; falling back to TF-IDF backbone results.");
                }
                return stage1Results;
            }

            return coverageResults;
        }
        finally
        {
            if (bestSegmentsPointer != 0)
                SpanAlloc.Free(bestSegmentsPointer);
        }
    }

    private ScoreArray ExecuteRelevancyStage(
        string searchText,
        Span2D<byte> bestSegments,
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
                ScoreEntry[] singleCharResults = ShortQueryProcessor.SearchSingleCharacter(
                    searchText[0], bestSegments, queryIndex: 0, maxResults: maxResults,
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
                    bestSegments, delimiters, EnableDebugLogging);
            }
        }
        else
        {
            string tfidfQuery = hasMixedTerms ? longWordsSearchText : searchText;
            if (string.IsNullOrWhiteSpace(tfidfQuery))
                tfidfQuery = searchText;

            _vectorModel.EnableDebugLogging = EnableDebugLogging;
            relevancyScores = _vectorModel.SearchWithMaxScore(tfidfQuery, coverageDepth, bestSegments, queryIndex: 0);
        }

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
        Span2D<byte> bestSegments,
        ref long topKMs,
        ref long wordMatcherCoverageMs,
        ref long tfidfCoverageMs,
        ref long truncationMs,
        Stopwatch? perfStopwatch,
        long tfidfMs)
    {
        long topKStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
        ScoreEntry[] topCandidates = relevancyScores.GetTopK(coverageDepth);
        if (perfStopwatch != null) topKMs = perfStopwatch.ElapsedMilliseconds - topKStart;

        if (coverageSetup.EnableLexicalPrescreen && topCandidates.Length > 0)
        {
            topCandidates = LexicalPrescreen.Apply(
                searchText, topCandidates, _vectorModel.Tokenizer,
                _vectorModel.TermCollection, _vectorModel.Documents, coverageSetup);
        }

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

            HashSet<int> tfidfInternalIds = BuildTfidfInternalIdSet(topCandidates);
            var (wmOverlapping, wmUnique) = PartitionWordMatcherCandidates(wordMatcherInternalIds, tfidfInternalIds);

            int wmLimit = Math.Max(0, coverageDepth - wmOverlapping.Count);
            List<int> wmToProcess = wmOverlapping.Concat(wmUnique.Take(wmLimit)).ToList();

            long wmCoverageStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
            foreach (int internalId in wmToProcess)
            {
                ProcessCandidate(internalId, searchText, coverageSetup, 0f,
                    bestSegments, lcsAndWordHitsSpan, documentKeyToIndex,
                    finalScores, ref maxWordHits, delimiters, minStemLength);
            }
            if (perfStopwatch != null) wordMatcherCoverageMs = perfStopwatch.ElapsedMilliseconds - wmCoverageStart;

            long tfidfCoverageStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
            foreach (ScoreEntry candidate in topCandidates)
            {
                Document? doc = _vectorModel.Documents.GetDocumentByPublicKey(candidate.DocumentId);
                if (doc == null || doc.Deleted)
                    continue;

                ProcessCandidate(doc.Id, searchText, coverageSetup, (float)candidate.Score / 255f,
                    bestSegments, lcsAndWordHitsSpan, documentKeyToIndex,
                    finalScores, ref maxWordHits, delimiters, minStemLength);
            }
            if (perfStopwatch != null) tfidfCoverageMs = perfStopwatch.ElapsedMilliseconds - tfidfCoverageStart;

            if (maxWordHits == 0 && wordMatcherInternalIds.Count == 0)
                return [];

            ScoreArray consolidatedFinalScores = SegmentProcessor.ConsolidateSegments(finalScores, bestSegments);
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
        CoverageSetup coverageSetup,
        float baseScore,
        Span2D<byte> bestSegments,
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
            doc, bestSegments, _vectorModel.Documents, _vectorModel.Tokenizer.TextNormalizer);

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

        CoverageFeatures features = _coverageEngine!.CalculateFeatures(searchText, docText, lcsFromSpan);
        var (finalScore, tiebreaker) = FusionScorer.Calculate(searchText, docText, features, baseScore, minStemLength, delimiters);

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

