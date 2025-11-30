using Infidex.Core;
using Infidex.Indexing.Segments;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Utilities;
using System.Buffers;
using System.Diagnostics;
using Infidex.Scoring; // For FusionScorer

namespace Infidex.Indexing;

/// <summary>
/// BM25+ scoring with MaxScore early termination algorithm.
/// </summary>
internal static class Bm25Scorer
{
    private const float K1 = 1.2f;
    private const float B = 0.75f;
    private const float Delta = 1.0f;

    internal readonly struct TermScoreInfo(Term term, float idf, float maxScore)
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
        float avgdl = avgDocLength > 0f ? avgDocLength : 1f;
        TermScoreInfo[] termInfos = ComputeTermScores(queryTerms, totalDocs, avgdl);
        Array.Sort(termInfos, (a, b) => b.MaxScore.CompareTo(a.MaxScore));

        return Search(termInfos, topK, totalDocs, docLengths, avgdl, stopTermLimit, documents, bestSegmentsMap, queryIndex, prefixIndex, originalQuery);
    }

    public static TopKHeap Search(
        TermScoreInfo[] termInfos,
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
        bool enableLogging = FusionScorer.EnableDebugLogging;
        Stopwatch? sw = enableLogging ? Stopwatch.StartNew() : null;
        TopKHeap resultHeap = new TopKHeap(topK);

        if (termInfos.Length == 0 || totalDocs == 0)
            return resultHeap;

        float avgdl = avgDocLength > 0f ? avgDocLength : 1f;

        Stopwatch? candSw = enableLogging ? Stopwatch.StartNew() : null;
        HashSet<int> candidates = TieredCandidateSelector.SelectCandidates(
            termInfos, topK, totalDocs, avgdl, prefixIndex, originalQuery, out Dictionary<int, float> upperBounds);
        candSw?.Stop();
        
        bool useTieredSelection = candidates.Count > 0;

        if (enableLogging)
        {
            Console.WriteLine($"[TF-IDF-INST] Candidate Selection: {candSw?.Elapsed.TotalMilliseconds:F3}ms, Candidates: {candidates.Count}");
        }

        float[] suffixMaxScore = ComputeSuffixSums(termInfos);
        
        // Internal heap for WAND pruning
        PriorityQueue<int, float> pruningHeap = new PriorityQueue<int, float>();
        float threshold = 0f;

        Stopwatch? scoringSw = enableLogging ? Stopwatch.StartNew() : null;
        long totalAdvance = 0;
        long totalNextDoc = 0;

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
                    documents, docScores, candidates, pruningHeap, ref threshold, bestSegmentsMap, queryIndex, ref totalAdvance, ref totalNextDoc);
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
                        documents, docScores, pruningHeap, ref threshold, bestSegmentsMap, queryIndex, ref totalAdvance, ref totalNextDoc);
                }

                PopulateResultHeapDense(resultHeap, docScores, documents, topK, pruningHeap);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(docScores);
            }
        }
        
        scoringSw?.Stop();
        if (enableLogging)
        {
            Console.WriteLine($"[TF-IDF-INST] Scoring Loop: {scoringSw?.Elapsed.TotalMilliseconds:F3}ms");
            Console.WriteLine($"[TF-IDF-INST] Stats: Advance={totalAdvance}, NextDoc={totalNextDoc}");
            if (sw != null) Console.WriteLine($"[TF-IDF-INST] Total Search: {sw.Elapsed.TotalMilliseconds:F3}ms");
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
        int queryIndex,
        ref long totalAdvance,
        ref long totalNextDoc)
    {
        IPostingsEnum? postings = info.Term.GetPostingsEnum();
        if (postings == null)
            return;

        // Devirtualization Dispatch
        if (postings is MMapBlockPostingsEnum mmap)
        {
            ProcessTermWithCandidatesStruct(ref mmap, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, candidates, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
            totalAdvance += mmap.AdvanceCount;
            totalNextDoc += mmap.NextDocCount;
            return;
        }

        ProcessTermWithCandidatesGeneric(postings, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, candidates, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
    }
    
    private static void ProcessTermWithCandidatesGeneric(
        IPostingsEnum postings,
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
         ProcessTermWithCandidatesStruct(ref postings, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, candidates, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
    }

    private static void ProcessTermWithCandidatesStruct<TEnum>(
        ref TEnum postings,
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
        int queryIndex) where TEnum : IPostingsEnum
    {
        // Optimization: Choose the iteration strategy based on set sizes.
        if (postings.Cost() < candidates.Count)
        {
            // Posting list is small: iterate postings and check candidates
            while (true)
            {
                int docId = postings.NextDoc();
                if (docId == PostingsEnumConstants.NO_MORE_DOCS)
                    break;

                if ((uint)docId >= (uint)totalDocs) 
                    continue;

                if (!candidates.Contains(docId))
                    continue;

                ProcessDocScore(docId, postings.Freq, info, remainingMaxScore, topK, totalDocs,
                    docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
            }
        }
        else
        {
            // Candidates set is small: iterate sorted candidates and advance postings
            int[] sortedCandidates = ArrayPool<int>.Shared.Rent(candidates.Count);
            try
            {
                candidates.CopyTo(sortedCandidates);
                Array.Sort(sortedCandidates, 0, candidates.Count);

                int count = candidates.Count;
                for (int i = 0; i < count; i++)
                {
                    int target = sortedCandidates[i];
                    if ((uint)target >= (uint)totalDocs)
                        continue;

                    int docId = postings.Advance(target);
                    if (docId == PostingsEnumConstants.NO_MORE_DOCS)
                        break;

                    if (docId == target)
                    {
                        ProcessDocScore(docId, postings.Freq, info, remainingMaxScore, topK, totalDocs,
                            docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(sortedCandidates);
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
        int queryIndex,
        ref long totalAdvance,
        ref long totalNextDoc)
    {
        IPostingsEnum? postings = info.Term.GetPostingsEnum();
        if (postings == null)
            return;

        // Devirtualization Dispatch
        if (postings is MMapBlockPostingsEnum mmap)
        {
            ProcessTermFullScanStruct(ref mmap, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
            totalAdvance += mmap.AdvanceCount;
            totalNextDoc += mmap.NextDocCount;
            return;
        }

        ProcessTermFullScanGeneric(postings, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
    }
    
    private static void ProcessTermFullScanGeneric(
        IPostingsEnum postings,
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
        ProcessTermFullScanStruct(ref postings, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
    }

    private static void ProcessTermFullScanStruct<TEnum>(
        ref TEnum postings,
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
        int queryIndex) where TEnum : IPostingsEnum
    {
        while (true)
        {
            int internalId = postings.NextDoc();
            if (internalId == PostingsEnumConstants.NO_MORE_DOCS)
                break;

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

            float tf = postings.Freq;
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
