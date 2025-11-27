using Infidex.Core;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Utilities;

namespace Infidex.Indexing;

/// <summary>
/// BM25+ scoring with MaxScore early termination algorithm.
/// </summary>
internal static class Bm25Scorer
{
    private const float K1 = 1.2f;
    private const float B = 0.75f;
    private const float Delta = 1.0f;

    private readonly struct TermScoreInfo(Term term, float idf, float maxScore)
    {
        public readonly Term Term = term;
        public readonly float Idf = idf;
        public readonly float MaxScore = maxScore;
    }

    /// <summary>
    /// Searches using tiered candidate selection with MaxScore algorithm for exact early termination.
    /// Uses WAND-style posting list intersection to reduce candidates from millions to thousands.
    /// </summary>
    public static ScoreArray Search(
        List<Term> queryTerms,
        int topK,
        int totalDocs,
        float[] docLengths,
        float avgDocLength,
        int stopTermLimit,
        DocumentCollection documents,
        Dictionary<int, byte>? bestSegmentsMap,
        int queryIndex,
        ShortQuery.PositionalPrefixIndex? prefixIndex = null,
        string? originalQuery = null)
    {
        ScoreArray scoreArray = new ScoreArray();

        if (queryTerms.Count == 0 || totalDocs == 0)
            return scoreArray;

        // OPTIMIZATION: Always use tiered candidate selection with PREFIX PRECEDENCE
        // PRECEDENCE RULES (provable quality tiers):
        // Tier 0: Prefix match at position 0 (document start) - HIGHEST
        // Tier 1: Prefix match at word boundaries - HIGH  
        // Tier 2: Contains all rarest terms (AND logic) - MEDIUM
        // Tier 3: Contains any query term (OR logic) - LOW (fallback for typos)
        // Coverage/fuzzy matching CAN'T outrank higher precedence tiers
        
        HashSet<int> candidates = Scoring.TieredCandidateSelector.SelectCandidates(
            queryTerms, topK, totalDocs, avgDocLength, prefixIndex, originalQuery, out Dictionary<int, float> upperBounds);
        bool useTieredSelection = candidates.Count > 0;
        if (!useTieredSelection)
        {
            // Create a dummy "all candidates" set - we'll iterate postings instead
            candidates = new HashSet<int>();
        }

        float avgdl = avgDocLength > 0f ? avgDocLength : 1f;

        TermScoreInfo[] termInfos = ComputeTermScores(queryTerms, totalDocs, avgdl);
        Array.Sort(termInfos, (a, b) => b.MaxScore.CompareTo(a.MaxScore));

        float[] suffixMaxScore = ComputeSuffixSums(termInfos);
        
        PriorityQueue<int, float> topKHeap = new PriorityQueue<int, float>();
        float threshold = 0f;

        if (useTieredSelection)
        {
            // FAST PATH: Sparse dictionary for exact queries
            Dictionary<int, float> docScores = new Dictionary<int, float>(candidates.Count);

            for (int i = 0; i < termInfos.Length; i++)
            {
                TermScoreInfo info = termInfos[i];
                if (info.Idf <= 0f)
                    continue;

                float remainingMaxScore = suffixMaxScore[i + 1];
                ProcessTermWithCandidates(info, remainingMaxScore, topK, totalDocs, docLengths, avgdl,
                    documents, docScores, candidates, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
            }

            return NormalizeScoresSparse(docScores, documents, scoreArray, totalDocs);
        }
        else
        {
            // FALLBACK PATH: Dense array for typo queries (old behavior)
            float[] docScores = new float[totalDocs];

            for (int i = 0; i < termInfos.Length; i++)
            {
                TermScoreInfo info = termInfos[i];
                if (info.Idf <= 0f)
                    continue;

                float remainingMaxScore = suffixMaxScore[i + 1];
                ProcessTermFullScan(info, remainingMaxScore, topK, totalDocs, docLengths, avgdl,
                    documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
            }

            return NormalizeScoresDense(docScores, documents, scoreArray);
        }
    }

    private static TermScoreInfo[] ComputeTermScores(List<Term> queryTerms, int totalDocs, float avgdl)
    {
        TermScoreInfo[] termInfos = new TermScoreInfo[queryTerms.Count];

        for (int i = 0; i < queryTerms.Count; i++)
        {
            Term term = queryTerms[i];
            int df = term.DocumentFrequency;

            if (df <= 0)
            {
                termInfos[i] = new TermScoreInfo(term, 0f, 0f);
                continue;
            }

            float idf = ComputeIdf(totalDocs, df);
            float maxTf = 255f;
            float minDlNorm = 1f - B + B * (1f / avgdl);
            float maxBm25Core = (maxTf * (K1 + 1f)) / (maxTf + K1 * minDlNorm);
            float maxTermScore = idf * (maxBm25Core + Delta);

            termInfos[i] = new TermScoreInfo(term, idf, maxTermScore);
        }

        return termInfos;
    }

    private static float[] ComputeSuffixSums(TermScoreInfo[] termInfos)
    {
        float[] suffixMaxScore = new float[termInfos.Length + 1];
        suffixMaxScore[termInfos.Length] = 0f;

        for (int i = termInfos.Length - 1; i >= 0; i--)
            suffixMaxScore[i] = suffixMaxScore[i + 1] + termInfos[i].MaxScore;

        return suffixMaxScore;
    }


    private static void ProcessTermWithCandidates(
        TermScoreInfo info,
        float remainingMaxScore,
        int topK,
        int totalDocs,
        float[] docLengths,
        float avgdl,
        DocumentCollection documents,
        Dictionary<int, float> docScores,
        HashSet<int> candidates,
        PriorityQueue<int, float> topKHeap,
        ref float threshold,
        Dictionary<int, byte>? bestSegmentsMap,
        int queryIndex)
    {
        List<int>? docIds = info.Term.GetDocumentIds();
        List<byte>? docWeights = info.Term.GetWeights();

        if (docIds == null || docWeights == null)
            return;

        // CRITICAL OPTIMIZATION: Iterate candidates (1k), NOT postings (500k)!
        foreach (int internalId in candidates)
        {
            if ((uint)internalId >= (uint)totalDocs)
                continue;

            // Binary search to find position in sorted posting list (O(log n))
            int postingIndex = docIds.BinarySearch(internalId);
            if (postingIndex < 0)
                continue; // This candidate doesn't contain this term

            docScores.TryGetValue(internalId, out float currentScore);
            if (topK < int.MaxValue && topKHeap.Count >= topK)
            {
                float upperBound = currentScore + info.MaxScore + remainingMaxScore;
                if (upperBound <= threshold)
                    continue;
            }

            Document? doc = documents.GetDocument(internalId);
            if (doc == null || doc.Deleted)
                continue;

            float tf = docWeights[postingIndex];
            if (tf <= 0f)
                continue;

            float dl = docLengths[internalId];
            if (dl <= 0f)
                dl = 1f;

            float termScore = ComputeTermScore(tf, dl, avgdl, info.Idf);
            float newScore = currentScore + termScore;
            docScores[internalId] = newScore;

            UpdateTopK(internalId, newScore, topK, topKHeap, ref threshold);
            TrackBestSegment(doc, internalId, bestSegmentsMap, queryIndex);
        }
    }

    private static void ProcessTermFullScan(
        TermScoreInfo info,
        float remainingMaxScore,
        int topK,
        int totalDocs,
        float[] docLengths,
        float avgdl,
        DocumentCollection documents,
        float[] docScores,
        PriorityQueue<int, float> topKHeap,
        ref float threshold,
        Dictionary<int, byte>? bestSegmentsMap,
        int queryIndex)
    {
        List<int>? docIds = info.Term.GetDocumentIds();
        List<byte>? docWeights = info.Term.GetWeights();

        if (docIds == null || docWeights == null)
            return;

        // Old behavior: iterate all postings for typo/fuzzy queries
        for (int j = 0; j < docIds.Count; j++)
        {
            int internalId = docIds[j];
            if ((uint)internalId >= (uint)totalDocs)
                continue;

            float currentScore = docScores[internalId];

            if (topK < int.MaxValue && topKHeap.Count >= topK)
            {
                float upperBound = currentScore + info.MaxScore + remainingMaxScore;
                if (upperBound <= threshold)
                    continue;
            }

            Document? doc = documents.GetDocument(internalId);
            if (doc == null || doc.Deleted)
                continue;

            float tf = docWeights[j];
            if (tf <= 0f)
                continue;

            float dl = docLengths[internalId];
            if (dl <= 0f)
                dl = 1f;

            float termScore = ComputeTermScore(tf, dl, avgdl, info.Idf);
            float newScore = currentScore + termScore;
            docScores[internalId] = newScore;

            UpdateTopK(internalId, newScore, topK, topKHeap, ref threshold);
            TrackBestSegment(doc, internalId, bestSegmentsMap, queryIndex);
        }
    }

    private static float ComputeTermScore(float tf, float dl, float avgdl, float idf)
    {
        float normFactor = K1 * (1f - B + B * (dl / avgdl));
        float denom = tf + normFactor;
        if (denom <= 0f)
            return 0f;

        float bm25Core = (tf * (K1 + 1f)) / denom;
        return idf * (bm25Core + Delta);
    }

    private static void UpdateTopK(int internalId, float newScore, int topK, PriorityQueue<int, float> topKHeap, ref float threshold)
    {
        if (topK >= int.MaxValue)
            return;

        if (topKHeap.Count < topK)
        {
            topKHeap.Enqueue(internalId, newScore);
            if (topKHeap.Count == topK)
                topKHeap.TryPeek(out _, out threshold);
        }
        else if (newScore > threshold)
        {
            topKHeap.EnqueueDequeue(internalId, newScore);
            topKHeap.TryPeek(out _, out threshold);
        }
    }

    private static void TrackBestSegment(Document doc, int internalId, Dictionary<int, byte>? bestSegmentsMap, int queryIndex)
    {
        if (bestSegmentsMap == null)
            return;

        int segmentNumber = doc.SegmentNumber;
        int baseId = internalId - segmentNumber;

        if (baseId >= 0)
        {
            bestSegmentsMap[baseId] = (byte)segmentNumber;
        }
    }

    private static ScoreArray NormalizeScoresSparse(Dictionary<int, float> docScores, DocumentCollection documents, ScoreArray scoreArray, int totalDocs)
    {
        if (docScores.Count == 0)
            return scoreArray;

        float maxScore = 0f;
        foreach (float score in docScores.Values)
        {
            if (score > maxScore)
                maxScore = score;
        }

        if (maxScore <= 0f)
            return scoreArray;

        foreach (KeyValuePair<int, float> kvp in docScores)
        {
            int internalId = kvp.Key;
            float raw = kvp.Value;

            if (raw <= 0f || (uint)internalId >= (uint)totalDocs)
                continue;

            Document? doc = documents.GetDocument(internalId);
            if (doc == null || doc.Deleted)
                continue;

            byte scaled = (byte)MathF.Min(255f, (raw / maxScore) * 255f);
            scoreArray.Add(doc.DocumentKey, scaled);
        }

        return scoreArray;
    }

    private static ScoreArray NormalizeScoresDense(float[] docScores, DocumentCollection documents, ScoreArray scoreArray)
    {
        float maxScore = 0f;
        for (int i = 0; i < docScores.Length; i++)
        {
            if (docScores[i] > maxScore)
                maxScore = docScores[i];
        }

        if (maxScore <= 0f)
            return scoreArray;

        for (int internalId = 0; internalId < docScores.Length; internalId++)
        {
            float raw = docScores[internalId];
            if (raw <= 0f)
                continue;

            Document? doc = documents.GetDocument(internalId);
            if (doc == null || doc.Deleted)
                continue;

            byte scaled = (byte)MathF.Min(255f, (raw / maxScore) * 255f);
            scoreArray.Add(doc.DocumentKey, scaled);
        }

        return scoreArray;
    }

    public static float ComputeIdf(int totalDocuments, int documentFrequency)
    {
        if (documentFrequency <= 0 || totalDocuments <= 0)
            return 0f;

        float df = documentFrequency;
        float N = totalDocuments;
        float ratio = (N - df + 0.5f) / (df + 0.5f);
        return ratio <= 0f ? 0f : MathF.Log(ratio + 1f);
    }

    public static byte[] CalculateQueryWeights(List<Term> queryTerms, int totalDocs)
    {
        float[] rawWeights = new float[queryTerms.Count];
        float sumSquares = 0f;

        for (int i = 0; i < queryTerms.Count; i++)
        {
            float tf = queryTerms[i].QueryOccurrences;
            float idf = queryTerms[i].InverseDocFrequency(totalDocs, tf);
            rawWeights[i] = idf;
            sumSquares += idf * idf;
        }

        float norm = MathF.Sqrt(sumSquares);
        byte[] quantizedWeights = new byte[queryTerms.Count];

        for (int i = 0; i < rawWeights.Length; i++)
        {
            float normalized = norm > 0 ? rawWeights[i] / norm : 0f;
            quantizedWeights[i] = ByteAsFloat.F2B(normalized);
        }

        return quantizedWeights;
    }
}
