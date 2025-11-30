using Infidex.Core;
using Infidex.Indexing.Segments;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Utilities;
using Infidex.Internalized.Roaring;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Numerics;
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

        // Rent buffer for upper bounds
        float[] upperBounds = ArrayPool<float>.Shared.Rent(totalDocs);
        Array.Clear(upperBounds, 0, totalDocs);

        Stopwatch? candSw = enableLogging ? Stopwatch.StartNew() : null;
        RoaringBitmap candidates;
        try
        {
            candidates = TieredCandidateSelector.SelectCandidates(
                termInfos, topK, totalDocs, avgdl, prefixIndex, originalQuery, upperBounds);
        }
        catch
        {
            ArrayPool<float>.Shared.Return(upperBounds);
            throw;
        }
        candSw?.Stop();

        long candidateCount = candidates.Cardinality;
        bool useTieredSelection = candidateCount > 0;

        if (enableLogging)
        {
            Console.WriteLine($"[TF-IDF-INST] Candidate Selection: {candSw?.Elapsed.TotalMilliseconds:F3}ms, Candidates: {candidateCount}");
        }

        float[] suffixMaxScore = ComputeSuffixSums(termInfos);
        
        // Internal heap for WAND pruning
        PriorityQueue<int, float> pruningHeap = new PriorityQueue<int, float>();
        float threshold = 0f;

        Stopwatch? scoringSw = enableLogging ? Stopwatch.StartNew() : null;
        long totalAdvance = 0;
        long totalNextDoc = 0;

        try
        {
            if (useTieredSelection)
            {
                // Initialize iterators
                IPostingsEnum?[] postings = new IPostingsEnum?[termInfos.Length];
                bool allMMap = true;
                for (int i = 0; i < termInfos.Length; i++)
                {
                    postings[i] = termInfos[i].Term.GetPostingsEnum();
                    if (!(postings[i] is MMapBlockPostingsEnum))
                    {
                        allMMap = false;
                    }
                }

                if (allMMap)
                {
                    // Fast Path: Specialized for MMapBlockPostingsEnum struct (avoid vtable)
                    MMapBlockPostingsEnum[] mmaps = new MMapBlockPostingsEnum[termInfos.Length];
                    for(int i=0; i<termInfos.Length; i++) 
                    {
                        if (postings[i] != null)
                            mmaps[i] = (MMapBlockPostingsEnum)postings[i]!;
                    }
                    
                    ProcessBlockedCandidates(mmaps.AsSpan(), termInfos, suffixMaxScore, candidates, documents, topK, docLengths, avgdl, pruningHeap, bestSegmentsMap, queryIndex, ref threshold);
                }
                else
                {
                    // Slow Path: Interface virtual calls
                    ProcessBlockedCandidates(postings.AsSpan(), termInfos, suffixMaxScore, candidates, documents, topK, docLengths, avgdl, pruningHeap, bestSegmentsMap, queryIndex, ref threshold);
                }

                // Transfer from pruningHeap to resultHeap
                PopulateResultHeapFromPruning(resultHeap, documents, topK, pruningHeap);
            }
            else
            {
                // Full Scan
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
        }
        finally
        {
            ArrayPool<float>.Shared.Return(upperBounds);
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

    private static void ProcessBlockedCandidates<TEnum>(
        Span<TEnum> postings, 
        TermScoreInfo[] termInfos,
        float[] suffixMaxScore,
        RoaringBitmap candidates,
        DocumentCollection documents,
        int topK,
        float[] docLengths,
        float avgdl,
        PriorityQueue<int, float> pruningHeap,
        Dictionary<int, byte>? bestSegmentsMap,
        int queryIndex,
        ref float threshold) where TEnum : IPostingsEnum
    {
        int blockSize = 4096;
        int[] docBlock = ArrayPool<int>.Shared.Rent(65536); // Max container size
        float[] scoreBlock = ArrayPool<float>.Shared.Rent(65536);
        float[] tfBlock = ArrayPool<float>.Shared.Rent(65536); // For vectorization
        int[] indexBlock = ArrayPool<int>.Shared.Rent(65536); // For vectorization

        try
        {
            RoaringArray ra = candidates.HighLowContainer;
            int size = ra.Size;
            ushort[] keys = ra.Keys;
            Container[] values = ra.Values;

            for (int i = 0; i < size; i++)
            {
                ushort keyHigh = keys[i];
                int highBits = keyHigh << 16;
                Container container = values[i];

                int count = 0;

                if (container is ArrayContainer ac)
                {
                    ushort[] content = ac.Content;
                    int card = ac.Cardinality;
                    for (int j = 0; j < card; j++)
                    {
                        docBlock[count++] = highBits | content[j];
                    }
                }
                else if (container is BitmapContainer bc)
                {
                    // Iterate bitmap efficiently
                    ulong[] bitmap = bc.Bitmap;
                    for (int k = 0; k < 1024; k++)
                    {
                        ulong bitset = bitmap[k];
                        if (bitset == 0) continue;
                        
                        int baseVal = (k << 6) | highBits;
                        while (bitset != 0)
                        {
                            int tz = BitOperations.TrailingZeroCount(bitset);
                            docBlock[count++] = baseVal | tz;
                            bitset ^= (1UL << tz);
                        }
                    }
                }

                if (count == 0) continue;
                
                int processed = 0;
                while (processed < count)
                {
                    int chunk = Math.Min(blockSize, count - processed);
                    Array.Clear(scoreBlock, 0, chunk);
                    
                    ProcessChunk(postings, termInfos, suffixMaxScore, topK, docLengths, avgdl, 
                        docBlock, processed, chunk, scoreBlock, tfBlock, indexBlock, pruningHeap, documents, bestSegmentsMap, queryIndex, ref threshold);
                        
                    processed += chunk;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(docBlock);
            ArrayPool<float>.Shared.Return(scoreBlock);
            ArrayPool<float>.Shared.Return(tfBlock);
            ArrayPool<int>.Shared.Return(indexBlock);
        }
    }

    private static void ProcessChunk<TEnum>(
        Span<TEnum> postings, 
        TermScoreInfo[] termInfos,
        float[] suffixMaxScore,
        int topK,
        float[] docLengths,
        float avgdl,
        int[] docBlock,
        int offset,
        int count,
        float[] scoreBlock,
        float[] tfBlock,
        int[] indexBlock,
        PriorityQueue<int, float> pruningHeap,
        DocumentCollection documents,
        Dictionary<int, byte>? bestSegmentsMap,
        int queryIndex,
        ref float threshold) where TEnum : IPostingsEnum
    {
        for (int t = 0; t < termInfos.Length; t++)
        {
            ref TEnum p = ref postings[t];
            if ((object?)p == null) continue;

            TermScoreInfo info = termInfos[t];
            float remainingMaxScore = suffixMaxScore[t + 1];

            if (info.Idf <= 0f) continue;

            ScoreBlockStruct(ref p, info, remainingMaxScore, topK, docLengths, avgdl, 
                scoreBlock, docBlock, offset, count, tfBlock, indexBlock, ref threshold);
        }

        // Flush results
        for (int j = 0; j < count; j++)
        {
            float score = scoreBlock[j];
            if (score > 0f)
            {
                int docId = docBlock[offset + j];
                Document? doc = documents.GetDocument(docId);
                if (doc != null && !doc.Deleted)
                {
                    UpdateTopK(docId, score, topK, pruningHeap, ref threshold);
                    TrackBestSegment(doc, docId, bestSegmentsMap, queryIndex);
                }
            }
        }
    }

    private static void ScoreBlockStruct<TEnum>(
        ref TEnum postings,
        TermScoreInfo info,
        float remainingMaxScore,
        int topK,
        float[] docLengths,
        float avgdl,
        float[] scoreBlock,
        int[] docBlock,
        int offset,
        int count,
        float[] tfBlock,
        int[] indexBlock,
        ref float threshold) where TEnum : IPostingsEnum
    {
        // Collect matches
        int matchCount = 0;
        
        for (int j = 0; j < count; j++)
        {
            float currentScore = scoreBlock[j];
            // WAND pruning
            if (topK < int.MaxValue && currentScore + info.MaxScore + remainingMaxScore <= threshold)
                continue;

            int target = docBlock[offset + j];
            int docId = postings.Advance(target);

            if (docId == target)
            {
                float tf = postings.Freq;
                // Store match for vectorization
                indexBlock[matchCount] = j;
                tfBlock[matchCount] = tf;
                matchCount++;
            }
        }
        
        if (matchCount == 0) return;

        // Vectorized scoring
        float k1 = K1;
        float b = B;
        float idf = info.Idf;
        float delta = Delta;
        
        // Constants for vector calc
        Vector256<float> vK1 = Vector256.Create(k1);
        Vector256<float> vOne = Vector256.Create(1.0f);
        Vector256<float> vB = Vector256.Create(b);
        Vector256<float> vDelta = Vector256.Create(delta);
        Vector256<float> vIdf = Vector256.Create(idf);
        Vector256<float> vAvgDl = Vector256.Create(avgdl);
        Vector256<float> vK1Plus1 = Vector256.Create(k1 + 1.0f);
        Vector256<float> vMinDlNorm = Vector256.Create(1f - b);
        Vector256<float> vBDivAvgDl = Vector256.Create(b / avgdl); // Optimization: precompute b/avgdl
        
        // Note: original code: 1 - B + B * (dl / avgdl) = (1-B) + (B/avgdl)*dl
        
        int i = 0;
        int vectorSize = Vector256<float>.Count;
        
        // Loop over matches
        for (; i <= matchCount - vectorSize; i += vectorSize)
        {
            Vector256<float> tf = Vector256.LoadUnsafe(ref tfBlock[i]);
            
            // Gather DLs
            // Just load manually.
            float d0 = docLengths[docBlock[offset + indexBlock[i + 0]]];
            float d1 = docLengths[docBlock[offset + indexBlock[i + 1]]];
            float d2 = docLengths[docBlock[offset + indexBlock[i + 2]]];
            float d3 = docLengths[docBlock[offset + indexBlock[i + 3]]];
            float d4 = docLengths[docBlock[offset + indexBlock[i + 4]]];
            float d5 = docLengths[docBlock[offset + indexBlock[i + 5]]];
            float d6 = docLengths[docBlock[offset + indexBlock[i + 6]]];
            float d7 = docLengths[docBlock[offset + indexBlock[i + 7]]];
            
            Vector256<float> dl = Vector256.Create(d0, d1, d2, d3, d4, d5, d6, d7);
            
            // Calc BM25
            // float normFactor = K1 * (1f - B + B * (dl / avgdl));
            // Optimized: K1 * ((1-B) + (B/avgdl)*dl)
            Vector256<float> normFactor = vK1 * (vMinDlNorm + vBDivAvgDl * dl);
            Vector256<float> denom = tf + normFactor;
            
            // float bm25Core = (tf * (K1 + 1f)) / denom;
            Vector256<float> bm25Core = (tf * vK1Plus1) / denom;
            
            // return idf * (bm25Core + Delta);
            Vector256<float> score = vIdf * (bm25Core + vDelta);
            
            // Scatter add to scoreBlock
            scoreBlock[indexBlock[i + 0]] += score[0];
            scoreBlock[indexBlock[i + 1]] += score[1];
            scoreBlock[indexBlock[i + 2]] += score[2];
            scoreBlock[indexBlock[i + 3]] += score[3];
            scoreBlock[indexBlock[i + 4]] += score[4];
            scoreBlock[indexBlock[i + 5]] += score[5];
            scoreBlock[indexBlock[i + 6]] += score[6];
            scoreBlock[indexBlock[i + 7]] += score[7];
        }
        
        // Scalar remainder
        for (; i < matchCount; i++)
        {
            float tf = tfBlock[i];
            int idx = indexBlock[i];
            int docId = docBlock[offset + idx];
            float dl = docLengths[docId];
            if (dl <= 0f) dl = 1f;
            scoreBlock[idx] += ComputeTermScore(tf, dl, avgdl, info.Idf);
        }
    }

    private static void PopulateResultHeapFromPruning(
        TopKHeap resultHeap, 
        DocumentCollection documents, 
        int topK,
        PriorityQueue<int, float> pruningHeap)
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
        switch (postings)
        {
            case null:
                return;
            // Devirtualization Dispatch
            case MMapBlockPostingsEnum mmap:
                ProcessTermFullScanStruct(ref mmap, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
                totalAdvance += mmap.AdvanceCount;
                totalNextDoc += mmap.NextDocCount;
                return;
            case RoaringPostingsEnum roaring:
                ProcessTermFullScanStruct(ref roaring, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
                return;
            default:
                ProcessTermFullScanGeneric(postings, info, remainingMaxScore, topK, totalDocs, docLengths, avgdl, documents, docScores, topKHeap, ref threshold, bestSegmentsMap, queryIndex);
                break;
        }
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
