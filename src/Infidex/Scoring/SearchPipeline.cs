using Infidex.Core;
using Infidex.Coverage;
using Infidex.Indexing;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Utilities;
using Infidex.Synonyms;
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
    private readonly SynonymMap? _synonymMap;

    private const int ShortQueryMaxLength = 3;
    private const int ShortQueryCoverageDocCap = 500;

    public static bool EnableDebugLogging => FusionScorer.EnableDebugLogging;

    public SearchPipeline(
        VectorModel vectorModel,
        CoverageEngine? coverageEngine,
        CoverageSetup? coverageSetup,
        WordMatcher.WordMatcher? wordMatcher,
        SynonymMap? synonymMap = null)
    {
        _vectorModel = vectorModel;
        _coverageEngine = coverageEngine;
        _coverageSetup = coverageSetup;
        _wordMatcher = wordMatcher;
        _synonymMap = synonymMap;

        if (_coverageEngine != null)
        {
            _coverageEngine.SetCorpusStatistics(_vectorModel.TermCollection, _vectorModel.Documents.Count);
            _coverageEngine.SetDocumentMetadataCache(_vectorModel.DocumentMetadataCache);
            _coverageEngine.SetWordIdfCache(_vectorModel.WordIdfCache);
        }
    }

    public ScoreEntry[] Execute(string searchText, CoverageSetup? coverageSetup, int coverageDepth, int maxResults = int.MaxValue)
    {
        Stopwatch perfStopwatch = Stopwatch.StartNew();
        long normMs = 0,
            tfidfMs = 0,
            consolidateMs = 0,
            topKMs = 0,
            prescreenMs = 0,
            wmLookupMs = 0,
            docIdxMs = 0,
            prepMs = 0,
            wordMatcherCoverageMs = 0,
            tfidfCoverageMs = 0,
            truncationMs = 0;

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

        Dictionary<int, byte>? bestSegmentsMap = null;

        long stage1StartMs = perfStopwatch.ElapsedMilliseconds;

        // Execute Stage 1 (TF-IDF / Short Query) - Returns TopKHeap
        TopKHeap relevancyScores = ExecuteRelevancyStage(searchText, ref bestSegmentsMap, coverageDepth, maxResults, ref tfidfMs, perfStopwatch);

        long consolidateStart = perfStopwatch.ElapsedMilliseconds;
        ScoreEntry[] stage1Candidates = relevancyScores.GetTopK();
        ScoreEntry[] stage1Results = SegmentProcessor.ConsolidateSegments(stage1Candidates, bestSegmentsMap);

        consolidateMs = perfStopwatch.ElapsedMilliseconds - consolidateStart;

        if (EnableDebugLogging)
        {
            Console.WriteLine($"[PIPE] Stage1 completed for query=\"{searchText}\" candidates={stage1Results.Length}");
            int logCount = Math.Min(5, stage1Results.Length);
            for (int i = 0; i < logCount; i++)
            {
                ScoreEntry e = stage1Results[i];
                Document? d = _vectorModel.Documents.GetDocumentByPublicKey(e.DocumentId);
                string title = d?.IndexedText ?? "<null>";
                if (title.Length > 120)
                    title = title[..117] + "...";
                Console.WriteLine($"[PIPE]   S1[{i}] id={e.DocumentId} score={e.Score:F4} tie={e.Tiebreaker} doc=\"{title}\"");
            }
        }

        long stage1TotalMs = perfStopwatch.ElapsedMilliseconds - stage1StartMs;

        bool isShortQuery = searchText.Length > 0 &&
                            searchText.Length <= ShortQueryMaxLength &&
                            (_vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? [' ']).All(d => !searchText.Contains(d));

        if (isShortQuery && stage1Results.Length >= maxResults && maxResults < int.MaxValue)
        {
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

        if (EnableDebugLogging)
        {
            Console.WriteLine(
                $"[PIPE] Coverage decision for \"{searchText}\": " +
                $"canUseNGrams={canUseNGrams}, isShortQuery={isShortQuery}, " +
                $"allowShortQueryCoverage={allowShortQueryCoverage}, " +
                $"shortQueryDocCountKnown={shortQueryDocCountKnown}, " +
                $"shortQueryDocCount={shortQueryDocCount}, " +
                $"skipDueToDocCap={skipCoverageDueToShortQueryDocCap}, " +
                $"coverageEngineNull={_coverageEngine == null}, " +
                $"coverageSetupNull={coverageSetup == null}");
        }

        if (_coverageEngine == null || coverageSetup == null ||
            (!canUseNGrams && !allowShortQueryCoverage) ||
            skipCoverageDueToShortQueryDocCap)
        {
#if DEBUG
            long overhead = stage1TotalMs - (tfidfMs + consolidateMs);
            Console.WriteLine($"[TIMING] total={perfStopwatch.ElapsedMilliseconds}ms (norm={normMs}ms, stage1={stage1TotalMs}ms [tfidf={tfidfMs}ms, consolidate={consolidateMs}ms, overhead={overhead}ms]) - NO COVERAGE");
            if (EnableDebugLogging)
            {
                Console.WriteLine("[PIPE] Returning Stage1 results without coverage.");
            }
#endif
            return stage1Results;
        }

        long coverageStartMs = perfStopwatch.ElapsedMilliseconds;

        ScoreEntry[] coverageResults = ExecuteCoverageStage(
            searchText, coverageSetup, coverageDepth, maxResults,
            stage1Results, ref bestSegmentsMap,
            ref topKMs, ref prescreenMs, ref wmLookupMs, ref docIdxMs, ref prepMs, ref wordMatcherCoverageMs, ref tfidfCoverageMs, ref truncationMs,
            perfStopwatch, tfidfMs);

        long coverageTotalMs = perfStopwatch.ElapsedMilliseconds - coverageStartMs;

        // Safety net: if coverage returns no results but TF-IDF produced candidates,
        // fall back to the TF-IDF backbone instead of returning an empty result set.
        if (coverageResults.Length == 0 && stage1Results.Length > 0)
        {
#if DEBUG
            if (EnableDebugLogging)
            {
                Console.WriteLine("[DEBUG] Coverage produced 0 results; falling back to TF-IDF backbone results.");
            }

            long s1Overhead = stage1TotalMs - (tfidfMs + consolidateMs);
            long covOverhead = coverageTotalMs - (topKMs + prescreenMs + wmLookupMs + docIdxMs + prepMs + wordMatcherCoverageMs + tfidfCoverageMs + truncationMs);
            Console.WriteLine($"[TIMING] total={perfStopwatch.ElapsedMilliseconds}ms (norm={normMs}ms, stage1={stage1TotalMs}ms [tfidf={tfidfMs}ms, consolidate={consolidateMs}ms, oh={s1Overhead}ms], coverage={coverageTotalMs}ms [topK={topKMs}ms, prescreen={prescreenMs}ms, wmLup={wmLookupMs}ms, docIdx={docIdxMs}ms, prep={prepMs}ms, wmCov={wordMatcherCoverageMs}ms, tfidfCov={tfidfCoverageMs}ms, trunc={truncationMs}ms, oh={covOverhead}ms]) - FALLBACK");
#endif
            return stage1Results;
        }

#if DEBUG
        long overhead1 = stage1TotalMs - (tfidfMs + consolidateMs);
        long coverageOverhead = coverageTotalMs - (topKMs + prescreenMs + wmLookupMs + docIdxMs + prepMs + wordMatcherCoverageMs + tfidfCoverageMs + truncationMs);
        long totalOverhead = perfStopwatch.ElapsedMilliseconds - (stage1TotalMs + coverageTotalMs);
        Console.WriteLine($"[TIMING] total={perfStopwatch.ElapsedMilliseconds}ms (norm={normMs}ms, stage1={stage1TotalMs}ms [tfidf={tfidfMs}ms, consolidate={consolidateMs}ms, oh={overhead1}ms], coverage={coverageTotalMs}ms [topK={topKMs}ms, prescreen={prescreenMs}ms, wmLup={wmLookupMs}ms, docIdx={docIdxMs}ms, prep={prepMs}ms, wmCov={wordMatcherCoverageMs}ms, tfidfCov={tfidfCoverageMs}ms, trunc={truncationMs}ms, oh={coverageOverhead}ms], finalOH={totalOverhead}ms)");
#endif
        return coverageResults;
    }

    private TopKHeap ExecuteRelevancyStage(
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

        (bool canUseNGrams, bool hasMixedTerms, string longWordsSearchText) = QueryAnalyzer.Analyze(searchText, _vectorModel.Tokenizer);
        TopKHeap relevancyScores;

        if (!canUseNGrams)
        {
            if (searchText.Length == 1)
            {
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
                        relevancyScores = new TopKHeap(maxResults);
                        foreach (ScoreEntry entry in prefixResults)
                        {
                            relevancyScores.Add(entry);
                        }

                        goto DoneRelevancy;
                    }
                }

                ScoreEntry[] singleCharResults = ShortQueryProcessor.SearchSingleCharacter(
                    ch, bestSegmentsMap, queryIndex: 0, maxResults: maxResults,
                    _vectorModel.Documents.GetAllDocuments(), delimiters);

                relevancyScores = new TopKHeap(maxResults);
                foreach (ScoreEntry entry in singleCharResults)
                {
                    relevancyScores.Add(entry);
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
            Console.WriteLine($"[DEBUG] Stage1 TF-IDF: {relevancyScores.Count} candidates in {tfidfMs}ms");
        }

        return relevancyScores;
    }

    private ScoreEntry[] ExecuteCoverageStage(
        string searchText,
        CoverageSetup coverageSetup,
        int coverageDepth,
        int maxResults,
        ScoreEntry[] topCandidates, // Passed as array now
        ref Dictionary<int, byte>? bestSegmentsMap,
        ref long topKMs,
        ref long prescreenMs,
        ref long wmLookupMs,
        ref long docIdxMs,
        ref long prepMs,
        ref long wordMatcherCoverageMs,
        ref long tfidfCoverageMs,
        ref long truncationMs,
        Stopwatch? perfStopwatch,
        long tfidfMs)
    {
        if (topCandidates.Length > coverageDepth)
            topCandidates = topCandidates.Take(coverageDepth).ToArray();

        if (EnableDebugLogging)
        {
            Console.WriteLine($"[PIPE] Coverage stage for \"{searchText}\": topK={topCandidates.Length}, depth={coverageDepth}, maxResults={maxResults}");
            int logCount = Math.Min(5, topCandidates.Length);
        }

        long prescreenStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
        if (coverageSetup.EnableLexicalPrescreen && topCandidates.Length > 0)
        {
            topCandidates = LexicalPrescreen.Apply(
                searchText, topCandidates, _vectorModel.Tokenizer,
                _vectorModel.TermCollection, _vectorModel.Documents, coverageSetup);
        }

        if (perfStopwatch != null) prescreenMs = perfStopwatch.ElapsedMilliseconds - prescreenStart;

        long wmLookupStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
        HashSet<int> wordMatcherInternalIds = WordMatcherLookup.Execute(
            searchText, _wordMatcher, _coverageSetup,
            _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? [' '],
            EnableDebugLogging);
        if (perfStopwatch != null) wmLookupMs = perfStopwatch.ElapsedMilliseconds - wmLookupStart;

        long docIdxStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
        (HashSet<long> uniqueDocKeys, Dictionary<long, int> documentKeyToIndex) = BuildDocumentKeyIndex(topCandidates, wordMatcherInternalIds);
        if (perfStopwatch != null) docIdxMs = perfStopwatch.ElapsedMilliseconds - docIdxStart;

        long lcsSpanPointer = 0;
        Span2D<byte> lcsAndWordHitsSpan = default;

        if (uniqueDocKeys.Count > 0)
        {
            lcsAndWordHitsSpan = SpanAlloc.Alloc2D(2, documentKeyToIndex.Count, out lcsSpanPointer);
        }

        try
        {
            TopKHeap finalScores = new TopKHeap(coverageDepth);
            int maxWordHits = 0;
            char[] delimiters = _vectorModel.Tokenizer.TokenizerSetup?.Delimiters ?? [' '];
            int minStemLength = _vectorModel.Tokenizer.IndexSizes.Min();

            long prepStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
            string[] queryTokens = searchText.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);

            HashSet<int> tfidfInternalIds = BuildTfidfInternalIdSet(topCandidates);
            (List<int> wmOverlapping, List<int> wmUnique) = PartitionWordMatcherCandidates(wordMatcherInternalIds, tfidfInternalIds);

            int wmLimit = Math.Max(0, coverageDepth - wmOverlapping.Count);
            List<int> wmToProcess = wmOverlapping.Concat(wmUnique.Take(wmLimit)).ToList();
            if (perfStopwatch != null) prepMs = perfStopwatch.ElapsedMilliseconds - prepStart;

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

                float maxTfidf = topCandidates.Length > 0 ? topCandidates[0].Score : 1f;
                float normBm25 = maxTfidf > 0 ? candidate.Score / maxTfidf : 0f;

                ProcessCandidate(doc.Id, searchText, queryTokens, coverageSetup, normBm25,
                    ref bestSegmentsMap, lcsAndWordHitsSpan, documentKeyToIndex,
                    finalScores, ref maxWordHits, delimiters, minStemLength);
            }

            if (perfStopwatch != null) tfidfCoverageMs = perfStopwatch.ElapsedMilliseconds - tfidfCoverageStart;

            if (maxWordHits == 0 && wordMatcherInternalIds.Count == 0)
                return [];

            ScoreEntry[] finalCandidates = finalScores.GetTopK();
            ScoreEntry[] finalResults = SegmentProcessor.ConsolidateSegments(finalCandidates, bestSegmentsMap);

            int truncationIndex = -1;
            if (coverageSetup.Truncate && finalResults.Length > 0)
            {
                long truncStart = perfStopwatch?.ElapsedMilliseconds ?? 0;
                truncationIndex = ResultProcessor.CalculateTruncationIndex(
                    finalResults, maxWordHits, lcsAndWordHitsSpan, documentKeyToIndex, coverageSetup);
                if (perfStopwatch != null) truncationMs = perfStopwatch.ElapsedMilliseconds - truncStart;
            }

            int resultCount = (truncationIndex == -1 || !coverageSetup.Truncate)
                ? maxResults
                : Math.Min(Math.Max(0, truncationIndex) + 1, maxResults);

            if (finalResults.Length > resultCount)
                finalResults = finalResults.Take(resultCount).ToArray();

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
        TopKHeap finalScores,
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

        string coverageDocText = docText;
        string coverageQueryText = searchText;
        if (_synonymMap != null &&
            _synonymMap.HasCanonicalMappings &&
            _vectorModel.Tokenizer.TokenizerSetup != null)
        {
            char[] delims = _vectorModel.Tokenizer.TokenizerSetup.Delimiters;
            coverageDocText = _synonymMap.CanonicalizeText(coverageDocText, delims);
            coverageQueryText = _synonymMap.CanonicalizeText(coverageQueryText, delims);
        }

        int lcsFromSpan = 0;
        if (docIndex < lcsAndWordHitsSpan.Height)
        {
            lcsFromSpan = lcsAndWordHitsSpan[0, docIndex];
            if (lcsFromSpan == 0)
            {
                int errorTolerance = 0;
                if (coverageQueryText.Length >= coverageSetup.CoverageQLimitForErrorTolerance)
                    errorTolerance = (int)(coverageQueryText.Length * coverageSetup.CoverageLcsErrorToleranceRelativeq);

                lcsFromSpan = SegmentProcessor.CalculateLcs(coverageQueryText, coverageDocText, errorTolerance);
                lcsAndWordHitsSpan[0, docIndex] = (byte)Math.Min(lcsFromSpan, 255);
            }
        }

        CoverageFeatures features = _coverageEngine!.CalculateFeatures(coverageQueryText, coverageDocText, lcsFromSpan, internalId);
        (float finalScore, byte tiebreaker) = FusionScorer.Calculate(
            coverageQueryText,
            coverageDocText,
            features,
            baseScore,
            minStemLength,
            delimiters);

        if (docIndex < lcsAndWordHitsSpan.Height && lcsAndWordHitsSpan[1, docIndex] == 0)
            lcsAndWordHitsSpan[1, docIndex] = (byte)Math.Min(features.WordHits, 255);

        maxWordHits = Math.Max(maxWordHits, features.WordHits);
        finalScores.Add(new ScoreEntry(finalScore, doc.DocumentKey, tiebreaker));
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
