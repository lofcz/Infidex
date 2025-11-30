using System.Buffers;
using Infidex.Core;
using Infidex.Indexing;
using Infidex.Indexing.ShortQuery;
using Infidex.Internalized.Roaring;

namespace Infidex.Scoring;

/// <summary>
/// Implements WAND/MaxScore algorithm for efficient candidate selection.
/// </summary>
internal class TieredCandidateSelector
{
    private readonly struct TermInfo
    {
        public readonly Term Term;
        public readonly float Idf;
        public readonly float MaxScore;
        public readonly int QueryOccurrences; // currently not used

        public TermInfo(Term term, float idf, float maxScore, int queryOccurrences)
        {
            Term = term;
            Idf = idf;
            MaxScore = maxScore;
            QueryOccurrences = queryOccurrences;
        }
    }

    /// <summary>
    /// Selects candidates using tiered approach with PREFIX PRECEDENCE.
    /// </summary>
    public static RoaringBitmap SelectCandidates(
        List<Term> queryTerms,
        int topK,
        int totalDocs,
        float avgDocLength,
        PositionalPrefixIndex? prefixIndex,
        string? originalQuery,
        float[] upperBounds)
    {
        var termInfos = new Indexing.Bm25Scorer.TermScoreInfo[queryTerms.Count];
        for (int i = 0; i < queryTerms.Count; i++)
        {
            Term t = queryTerms[i];
            float idf = t.DocumentFrequency > 0 ? MathF.Log10((float)totalDocs / t.DocumentFrequency) : 0;
            float maxScore = idf * 2.2f;
            termInfos[i] = new Indexing.Bm25Scorer.TermScoreInfo(t, idf, maxScore);
        }
        return SelectCandidates(termInfos, topK, totalDocs, avgDocLength, prefixIndex, originalQuery, upperBounds);
    }

    public static RoaringBitmap SelectCandidates(
        Indexing.Bm25Scorer.TermScoreInfo[] queryTerms,
        int topK,
        int totalDocs,
        float avgDocLength,
        PositionalPrefixIndex? prefixIndex,
        string? originalQuery,
        float[] upperBounds)
    {
        if (queryTerms.Length == 0)
            return RoaringBitmap.Create();

        // PREFIX PRECEDENCE (short queries / strong prefixes)
        if (prefixIndex != null && !string.IsNullOrEmpty(originalQuery))
        {
            RoaringBitmap prefixCandidates = TrySelectPrefixCandidates(
                prefixIndex, originalQuery, topK, queryTerms.Length, totalDocs, out float prefixUpperBound);

            long prefixCount = prefixCandidates.Cardinality;
            if (prefixCount > 0)
            {
                foreach (int docId in prefixCandidates)
                {
                    upperBounds[docId] = prefixUpperBound;
                }

                if (prefixCount >= Math.Min(topK * 2, 100))
                    return prefixCandidates;
            }
        }

        // Build internal term infos enriched with query occurrences.
        List<TermInfo> terms = new List<TermInfo>(queryTerms.Length);
        int missingTerms = 0;
        
        foreach (var info in queryTerms)
        {
            if (info.Term.DocumentFrequency <= 0)
            {
                missingTerms++;
                continue;
            }

            int qOcc = 1;
            if (info.Term is { } t) qOcc = t.QueryOccurrences;
            
            terms.Add(new TermInfo(info.Term, info.Idf, info.MaxScore, qOcc));
        }
        
        if (terms.Count == 0)
        {
            // upperBounds logic handled by caller clearing
            return RoaringBitmap.Create();
        }

        bool hasPotentialTypo = false;
        float maxIdf = 0f;

        foreach (TermInfo termInfo in terms)
        {
            if (termInfo.Term.DocumentFrequency < 10)
                hasPotentialTypo = true;

            if (termInfo.Idf > maxIdf)
                maxIdf = termInfo.Idf;
        }

        // Disjunctive logic for typo-like / missing term scenarios
        if (hasPotentialTypo || missingTerms > 0 || queryTerms.Length == 1)
        {
            RoaringBitmap disjunctiveCandidates = SelectCandidatesDisjunctive(terms, topK, totalDocs, upperBounds);
            return disjunctiveCandidates;
        }

        // Sort by IDF descending (most selective first)
        terms.Sort((a, b) => b.Idf.CompareTo(a.Idf));

        RoaringBitmap globalCandidates = RoaringBitmap.Create();

        // Tier 0: Full AND (all terms required)
        if (terms.Count >= 2)
        {
            float tier0UpperBound = terms.Sum(t => t.MaxScore);
            RoaringBitmap tier0 = IntersectTerms(terms, tier0UpperBound, upperBounds);
            globalCandidates |= tier0;

            if (globalCandidates.Cardinality >= topK * 2)
                return globalCandidates;
        }

        // Tier 1: n-1 terms (drop lowest IDF term)
        if (terms.Count >= 3 && globalCandidates.Cardinality < topK * 3)
        {
            List<TermInfo> tier1Terms = terms.Take(terms.Count - 1).ToList();
            float tier1UpperBound = tier1Terms.Sum(t => t.MaxScore);
            RoaringBitmap tier1 = IntersectTerms(tier1Terms, tier1UpperBound, upperBounds);
            globalCandidates |= tier1;
        }

        // Tier 2: Individual high-IDF terms
        if (globalCandidates.Cardinality < topK * 5)
        {
            // Keep at most 2 truly selective terms.
            TermInfo[] selective = new TermInfo[Math.Min(2, terms.Count)];
            int selCount = 0;
            float idfCutoff = maxIdf * 0.3f;

            foreach (TermInfo termInfo in terms)
            {
                if (termInfo.Idf <= 0f)
                    continue;

                if (termInfo.Idf < idfCutoff)
                    continue; // skip very dense / low-quality terms

                selective[selCount++] = termInfo;
                if (selCount == selective.Length)
                    break;
            }

            for (int si = 0; si < selCount; si++)
            {
                TermInfo termInfo = selective[si];
                IPostingsEnum? postings = termInfo.Term.GetPostingsEnum();
                if (postings == null)
                    continue;

                int[] termDocsBuffer = ArrayPool<int>.Shared.Rent(4096);
                int count = 0;
                List<int> termDocs = new List<int>(); // Fallback or batching

                // Actually we can just batch insert into RoaringBitmap if it supported it, or collect all then insert.
                // For simplicity and performance, we'll collect into a large buffer or resizeable array.
                // Given selective terms are high IDF, they might be sparse but could be large.
                // Let's use a List<int> but only for this part as it's not the main bottleneck identified (it's IntersectTerms).
                // But wait, the task says "List<int> Allocations: IntersectTerms and TrySelectPrefixCandidates".
                // This part (Tier 2) uses List<int> too. Let's fix it if possible.
                // Actually, let's use a rented array and resize if needed.
                
                int capacity = 4096;
                int[] buffer = ArrayPool<int>.Shared.Rent(capacity);
                int pos = 0;

                try
                {
                    while (true)
                    {
                        int docId = postings.NextDoc();
                        if (docId == PostingsEnumConstants.NO_MORE_DOCS)
                            break;

                        if (upperBounds[docId] == 0)
                        {
                            upperBounds[docId] = termInfo.MaxScore;
                        }
                        
                        if (pos >= capacity)
                        {
                            // Resize
                            int[] newBuffer = ArrayPool<int>.Shared.Rent(capacity * 2);
                            Array.Copy(buffer, newBuffer, capacity);
                            ArrayPool<int>.Shared.Return(buffer);
                            buffer = newBuffer;
                            capacity *= 2;
                        }
                        buffer[pos++] = docId;
                    }
                    
                    if (pos > 0)
                    {
                        globalCandidates |= RoaringBitmap.CreateFromSorted(buffer, pos);
                    }
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(buffer);
                }

                if (globalCandidates.Cardinality >= topK * 10)
                    break;
            }
        }

        return globalCandidates;
    }

    /// <summary>
    /// Disjunctive candidate selection for typo / missing-term scenarios.
    /// Updates upperBounds in-place and uses it as the membership set.
    /// </summary>
    private static RoaringBitmap SelectCandidatesDisjunctive(
        List<TermInfo> terms,
        int topK,
        int totalDocs,
        float[] upperBounds)
    {
        float maxIdf = 0f;
        foreach (var t in terms)
            if (t.Idf > maxIdf) maxIdf = t.Idf;

        terms.Sort((a, b) => b.Idf.CompareTo(a.Idf));

        bool hasSelectiveCandidates = false;
        int localCount = 0;

        RoaringBitmap result = RoaringBitmap.Create();

        foreach (TermInfo termInfo in terms)
        {
            bool isLowQuality = termInfo.Idf < (maxIdf * 0.2f);
            if (terms.Count > 1 && isLowQuality && hasSelectiveCandidates)
                continue;

            IPostingsEnum? postings = termInfo.Term.GetPostingsEnum();
            if (postings == null)
                continue;

            int capacity = 4096;
            int[] buffer = ArrayPool<int>.Shared.Rent(capacity);
            int pos = 0;

            try 
            {
                while (true)
                {
                    int docId = postings.NextDoc();
                    if (docId == PostingsEnumConstants.NO_MORE_DOCS)
                        break;

                    float ub = upperBounds[docId];
                    if (ub == 0)
                    {
                        upperBounds[docId] = termInfo.MaxScore;
                        localCount++;
                    }
                    else
                    {
                        upperBounds[docId] = ub + termInfo.MaxScore;
                    }
                    
                    if (pos >= capacity)
                    {
                        int[] newBuffer = ArrayPool<int>.Shared.Rent(capacity * 2);
                        Array.Copy(buffer, newBuffer, capacity);
                        ArrayPool<int>.Shared.Return(buffer);
                        buffer = newBuffer;
                        capacity *= 2;
                    }
                    buffer[pos++] = docId;
                }
                
                if (pos > 0)
                {
                    result |= RoaringBitmap.CreateFromSorted(buffer, pos);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(buffer);
            }

            if (!isLowQuality && localCount > 0)
                hasSelectiveCandidates = true;

            if (localCount >= topK * 100)
                break;
        }

        return result;
    }

    /// <summary>
    /// Intersects all terms (logical AND) using driver->Advance pattern, and assigns a fixed upper bound
    /// for any new docs found. Returns the resulting bitmap.
    /// </summary>
    private static RoaringBitmap IntersectTerms(
        List<TermInfo> terms,
        float tierUpperBound,
        float[] upperBounds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (terms.Count == 0)
            return RoaringBitmap.Create();

        // Collect iterators
        List<IPostingsEnum> enums = new List<IPostingsEnum>(terms.Count);
        foreach (TermInfo termInfo in terms)
        {
            IPostingsEnum? p = termInfo.Term.GetPostingsEnum();
            if (p == null)
                return RoaringBitmap.Create();
            enums.Add(p);
        }
        
        // Sort by cost (ascending)
        enums.Sort((a, b) => a.Cost().CompareTo(b.Cost()));
        
        int advanceCalls = 0;
        
        int capacity = 4096; // Initial capacity
        int[] buffer = ArrayPool<int>.Shared.Rent(capacity);
        int pos = 0;
        
        try
        {
            // Use the first enum as the driver
            IPostingsEnum driver = enums[0];
            int doc = driver.NextDoc();
            
            while (doc != PostingsEnumConstants.NO_MORE_DOCS)
            {
                bool match = true;
                for (int i = 1; i < enums.Count; i++)
                {
                    advanceCalls++;
                    int target = enums[i].Advance(doc);
                    if (target > doc)
                    {
                        match = false;
                        doc = target; // Skip ahead
                        if (doc == PostingsEnumConstants.NO_MORE_DOCS)
                            goto End;
                        
                        // Re-align driver
                        doc = driver.Advance(doc);
                        if (doc == PostingsEnumConstants.NO_MORE_DOCS)
                            goto End;
                            
                        // Restart verification from first non-driver enum
                        i = 0; 
                        break; 
                    }
                }
                
                if (match)
                {
                    if (pos >= capacity)
                    {
                        int[] newBuffer = ArrayPool<int>.Shared.Rent(capacity * 2);
                        Array.Copy(buffer, newBuffer, capacity);
                        ArrayPool<int>.Shared.Return(buffer);
                        buffer = newBuffer;
                        capacity *= 2;
                    }
                    buffer[pos++] = doc;

                    if (upperBounds[doc] == 0)
                    {
                        upperBounds[doc] = tierUpperBound;
                    }
                    doc = driver.NextDoc();
                }
            }

        End:
            sw.Stop();
#if DEBUG
            if (sw.ElapsedMilliseconds > 5)
            {
                Console.WriteLine($"[DEBUG] IntersectAllTerms: {sw.ElapsedMilliseconds}ms, Advance calls: {advanceCalls}, DriverCost: {driver.Cost()}");
            }
#endif
            return RoaringBitmap.CreateFromSorted(buffer, pos);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Utility to filter by upper bound while preserving at least topK candidates.
    /// </summary>
    public static List<int> FilterByUpperBound(
        IEnumerable<int> candidates,
        float[] upperBounds,
        float currentKthScore,
        int topK)
    {
        List<int> filtered = new List<int>();
        
        foreach (int docId in candidates)
        {
            float upperBound = upperBounds[docId];
            if (upperBound > 0)
            {
                if (upperBound >= currentKthScore || filtered.Count < topK)
                {
                    filtered.Add(docId);
                }
            }
        }

        return filtered;
    }

    /// <summary>
    /// Prefix-based candidate selection using RoaringBitmap only.
    /// Matches the original per-prefix-length semantics: we consider each
    /// prefix length independently (no cross-length union), and apply the
    /// same density thresholds as the HashSet-based version.
    /// </summary>
    private static RoaringBitmap TrySelectPrefixCandidates(
        PositionalPrefixIndex prefixIndex,
        string originalQuery,
        int topK,
        int queryTermCount,
        int totalDocs,
        out float upperBound)
    {
        upperBound = 0f;

        string queryLower = originalQuery.ToLowerInvariant();
        int maxPrefixLen = Math.Min(queryLower.Length, prefixIndex.MaxPrefixLength);

        // Evaluate each prefix length independently, from longest to shortest
        for (int len = maxPrefixLen; len >= prefixIndex.MinPrefixLength; len--)
        {
            ReadOnlySpan<char> prefix = queryLower.AsSpan(0, len);
            PrefixPostingList? postingList = prefixIndex.GetPostingList(prefix);
            if (postingList == null || postingList.Count == 0)
                continue;

            RoaringBitmap candidateBits;

            // Case 1: Use precomputed doc-level RoaringBitmap (word-start / first-position docs).
            if (postingList.DocSet != null)
            {
                candidateBits = postingList.DocSet;
            }
            else
            {
                // Case 2: Build a fresh RoaringBitmap for this prefix length only.
                int capacity = 1024;
                int[] buffer = ArrayPool<int>.Shared.Rent(capacity);
                int pos = 0;
                try
                {
                    foreach (ref readonly PrefixPosting posting in postingList.Postings)
                    {
                        if (posting.Position == 0 || posting.IsWordStart)
                        {
                             if (pos >= capacity)
                             {
                                 int[] newBuffer = ArrayPool<int>.Shared.Rent(capacity * 2);
                                 Array.Copy(buffer, newBuffer, capacity);
                                 ArrayPool<int>.Shared.Return(buffer);
                                 buffer = newBuffer;
                                 capacity *= 2;
                             }
                             buffer[pos++] = posting.DocumentId;
                        }
                    }
                    candidateBits = RoaringBitmap.CreateFromSorted(buffer, pos);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(buffer);
                }
            }

            long pop = candidateBits.Cardinality;
            if (pop == 0)
                continue;

            // If this prefix is extremely dense, shorter prefixes will only be denser
            if (pop > (long)topK * 20)
            {
                continue;
            }

            if (pop > 0 && pop <= (long)topK * 10)
            {
                upperBound = queryTermCount * 10f;
                return candidateBits;
            }
        }

        return RoaringBitmap.Create();
    }
}
