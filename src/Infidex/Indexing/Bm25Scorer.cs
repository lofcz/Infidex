using Infidex.Core;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Utilities;
using System.Buffers;

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
    /// Returns TopKHeap with float scores.
    /// </summary>
    public static TopKHeap Search(
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
        TopKHeap resultHeap = new TopKHeap(topK);

        if (queryTerms.Count == 0 || totalDocs == 0)
            return resultHeap;

        HashSet<int> candidates = Scoring.TieredCandidateSelector.SelectCandidates(
            queryTerms, topK, totalDocs, avgDocLength, prefixIndex, originalQuery, out Dictionary<int, float> upperBounds);
        bool useTieredSelection = candidates.Count > 0;
        if (!useTieredSelection)
        {
            candidates = new HashSet<int>();
        }

        float avgdl = avgDocLength > 0f ? avgDocLength : 1f;

        TermScoreInfo[] termInfos = ComputeTermScores(queryTerms, totalDocs, avgdl);
        Array.Sort(termInfos, (a, b) => b.MaxScore.CompareTo(a.MaxScore));

        float[] suffixMaxScore = ComputeSuffixSums(termInfos);
        
        // Internal heap for WAND pruning
        PriorityQueue<int, float> pruningHeap = new PriorityQueue<int, float>();
        float threshold = 0f;

        if (useTieredSelection)
        {
            Dictionary<int, float> docScores = new Dictionary<int, float>(candidates.Count);

            for (int i = 0; i < termInfos.Length; i++)
            {
                TermScoreInfo info = termInfos[i];
                if (info.Idf <= 0f)
                    continue;

                float remainingMaxScore = suffixMaxScore[i + 1];
                ProcessTermWithCandidates(info, remainingMaxScore, topK, totalDocs, docLengths, avgdl,
                    documents, docScores, candidates, pruningHeap, ref threshold, bestSegmentsMap, queryIndex);
            }

            PopulateResultHeap(resultHeap, docScores, documents, topK, pruningHeap);
        }
        else
        {
            // Rent a buffer from the shared ArrayPool to avoid LOH allocations for large document sets
            float[] docScores = ArrayPool<float>.Shared.Rent(totalDocs);
            Array.Clear(docScores, 0, totalDocs);

            try
            {
                for (int i = 0; i < termInfos.Length; i++)
                {
                    TermScoreInfo info = termInfos[i];
                    if (info.Idf <= 0f)
                        continue;

                    float remainingMaxScore = suffixMaxScore[i + 1];
                    ProcessTermFullScan(info, remainingMaxScore, topK, totalDocs, docLengths, avgdl,
                        documents, docScores, pruningHeap, ref threshold, bestSegmentsMap, queryIndex);
                }

                PopulateResultHeapDense(resultHeap, docScores, documents, topK, pruningHeap);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(docScores);
            }
        }

        return resultHeap;
    }

    private static void PopulateResultHeap(
        TopKHeap resultHeap, 
        Dictionary<int, float> docScores, 
        DocumentCollection documents, 
        int topK,
        PriorityQueue<int, float> pruningHeap)
    {
        if (topK < int.MaxValue && pruningHeap.Count > 0)
        {
             while (pruningHeap.TryDequeue(out int internalId, out float score))
             {
                 Document? doc = documents.GetDocument(internalId);
                 if (doc != null && !doc.Deleted)
                 {
                     resultHeap.Add(doc.DocumentKey, score);
                 }
             }
        }
        else
        {
            foreach (var kvp in docScores)
            {
                if (kvp.Value > 0f)
                {
                    Document? doc = documents.GetDocument(kvp.Key);
                    if (doc != null && !doc.Deleted)
                    {
                        resultHeap.Add(doc.DocumentKey, kvp.Value);
                    }
                }
            }
        }
    }

    private static void PopulateResultHeapDense(
        TopKHeap resultHeap, 
        float[] docScores, 
        DocumentCollection documents, 
        int topK,
        PriorityQueue<int, float> pruningHeap)
    {
        if (topK < int.MaxValue && pruningHeap.Count > 0)
        {
             while (pruningHeap.TryDequeue(out int internalId, out float score))
             {
                 Document? doc = documents.GetDocument(internalId);
                 if (doc != null && !doc.Deleted)
                 {
                     resultHeap.Add(doc.DocumentKey, score);
                 }
             }
        }
        else
        {
            for (int i = 0; i < docScores.Length; i++)
            {
                if (docScores[i] > 0f)
                {
                    Document? doc = documents.GetDocument(i);
                    if (doc != null && !doc.Deleted)
                    {
                        resultHeap.Add(doc.DocumentKey, docScores[i]);
                    }
                }
            }
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

        // Optimization: Choose the iteration strategy based on set sizes.
        // If the posting list is significantly smaller than the candidate set,
        // it is faster to iterate the postings and check existence in candidates.
        // This avoids O(N) binary searches where N=candidates.Count.
        if (docIds.Count < candidates.Count)
        {
            for (int j = 0; j < docIds.Count; j++)
            {
                int internalId = docIds[j];
                if ((uint)internalId >= (uint)totalDocs) continue;

                if (!candidates.Contains(internalId))
                    continue;

                ProcessDocScore(internalId, docWeights[j], info, remainingMaxScore, topK, totalDocs,
                    docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
            }
        }
        else
        {
            // iterate candidates and binary search postings
            foreach (int internalId in candidates)
            {
                if ((uint)internalId >= (uint)totalDocs)
                    continue;

                int postingIndex = docIds.BinarySearch(internalId);
                if (postingIndex < 0)
                    continue;

                ProcessDocScore(internalId, docWeights[postingIndex], info, remainingMaxScore, topK, totalDocs,
                    docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
            }
        }
    }

    private static void ProcessDocScore(
        int internalId,
        float tf,
        TermScoreInfo info,
        float remainingMaxScore,
        int topK,
        int totalDocs,
        float[] docLengths,
        float avgdl,
        DocumentCollection documents,
        Dictionary<int, float> docScores,
        PriorityQueue<int, float> topKHeap,
        ref float threshold,
        Dictionary<int, byte>? bestSegmentsMap,
        int queryIndex)
    {
        docScores.TryGetValue(internalId, out float currentScore);
        if (topK < int.MaxValue && topKHeap.Count >= topK)
        {
            float upperBound = currentScore + info.MaxScore + remainingMaxScore;
            if (upperBound <= threshold)
                return;
        }

        Document? doc = documents.GetDocument(internalId);
        if (doc == null || doc.Deleted)
            return;

        if (tf <= 0f)
            return;

        float dl = docLengths[internalId];
        if (dl <= 0f)
            dl = 1f;

        float termScore = ComputeTermScore(tf, dl, avgdl, info.Idf);
        float newScore = currentScore + termScore;
        docScores[internalId] = newScore;

        UpdateTopK(internalId, newScore, topK, topKHeap, ref threshold);
        TrackBestSegment(doc, internalId, bestSegmentsMap, queryIndex);
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
