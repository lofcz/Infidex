using Infidex.Core;
using Infidex.Indexing;
using Infidex.Indexing.ShortQuery;
using Infidex.Utilities;
using System.Buffers;

namespace Infidex.Scoring;

/// <summary>
/// Implements WAND/MaxScore algorithm for efficient candidate selection.
/// Selects candidates in tiers based on term combinations, with provable upper bounds
/// to enable early termination.
/// </summary>
internal class TieredCandidateSelector
{
    private readonly struct TermInfo
    {
        public readonly Term Term;
        public readonly float Idf;
        public readonly float MaxScore;
        public readonly int QueryOccurrences;

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
    /// Returns documents sorted by their upper-bound potential scores.
    /// </summary>
    public static HashSet<int> SelectCandidates(
        List<Term> queryTerms,
        int topK,
        int totalDocs,
        float avgDocLength,
        Indexing.ShortQuery.PositionalPrefixIndex? prefixIndex,
        string? originalQuery,
        out Dictionary<int, float> upperBounds)
    {
        var termInfos = new Indexing.Bm25Scorer.TermScoreInfo[queryTerms.Count];
        for(int i=0; i<queryTerms.Count; i++)
        {
            Term t = queryTerms[i];
            float idf = t.DocumentFrequency > 0 ? MathF.Log10((float)totalDocs / t.DocumentFrequency) : 0;
            float maxScore = idf * 2.2f;
            termInfos[i] = new Indexing.Bm25Scorer.TermScoreInfo(t, idf, maxScore);
        }
        return SelectCandidates(termInfos, topK, totalDocs, avgDocLength, prefixIndex, originalQuery, out upperBounds);
    }

    public static HashSet<int> SelectCandidates(
        Indexing.Bm25Scorer.TermScoreInfo[] queryTerms,
        int topK,
        int totalDocs,
        float avgDocLength,
        Indexing.ShortQuery.PositionalPrefixIndex? prefixIndex,
        string? originalQuery,
        out Dictionary<int, float> upperBounds)
    {
        upperBounds = new Dictionary<int, float>();
        
        if (queryTerms.Length == 0)
            return [];

        if (prefixIndex != null && !string.IsNullOrEmpty(originalQuery))
        {
            HashSet<int> prefixCandidates = TrySelectPrefixCandidates(
                prefixIndex, originalQuery, topK, queryTerms.Length, totalDocs, out float prefixUpperBound);
            
            if (prefixCandidates.Count > 0)
            {
                foreach (int docId in prefixCandidates)
                {
                    upperBounds[docId] = prefixUpperBound;
                }
                
                if (prefixCandidates.Count >= Math.Min(topK * 2, 100))
                {
                    return prefixCandidates;
                }
            }
        }

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
            if (info.Term is Term t) qOcc = t.QueryOccurrences;
            
            terms.Add(new TermInfo(info.Term, info.Idf, info.MaxScore, qOcc));
        }
        
        if (terms.Count == 0)
        {
            upperBounds.Clear();
            return [];
        }

        bool hasPotentialTypo = false;
        foreach (TermInfo termInfo in terms)
        {
            if (termInfo.Term.DocumentFrequency < 10)
            {
                hasPotentialTypo = true;
                break;
            }
        }
        
        if (hasPotentialTypo || missingTerms > 0 || queryTerms.Length == 1)
        {
            return SelectCandidatesDisjunctive(terms, topK, totalDocs, upperBounds);
        }

        // Sort by IDF descending
        terms.Sort((a, b) => b.Idf.CompareTo(a.Idf));

        HashSet<int> candidates = [];

        // Tier 0: Full AND (all terms required)
        if (terms.Count >= 2)
        {
            HashSet<int> tier0 = IntersectAllTerms(terms);
            float tier0UpperBound = terms.Sum(t => t.MaxScore);
            
            foreach (int docId in tier0)
            {
                candidates.Add(docId);
                upperBounds[docId] = tier0UpperBound;
            }

            if (candidates.Count >= topK * 2)
                return candidates;
        }

        // Tier 1: n-1 terms (drop lowest IDF term)
        if (terms.Count >= 3 && candidates.Count < topK * 3)
        {
            List<TermInfo> tier1Terms = terms.Take(terms.Count - 1).ToList();
            HashSet<int> tier1 = IntersectAllTerms(tier1Terms);
            float tier1UpperBound = tier1Terms.Sum(t => t.MaxScore);

            foreach (int docId in tier1)
            {
                if (candidates.Add(docId))
                {
                    upperBounds[docId] = tier1UpperBound;
                }
            }
        }

        // Tier 2: Individual high-IDF terms
        if (candidates.Count < topK * 5)
        {
            foreach (TermInfo termInfo in terms.Take(Math.Min(2, terms.Count)))
            {
                IPostingsEnum? postings = termInfo.Term.GetPostingsEnum();
                if (postings == null)
                    continue;

                while (true)
                {
                    int docId = postings.NextDoc();
                    if (docId == PostingsEnumConstants.NO_MORE_DOCS)
                        break;

                    if (candidates.Add(docId))
                    {
                        upperBounds[docId] = termInfo.MaxScore;
                    }
                }

                if (candidates.Count >= topK * 10)
                    break;
            }
        }

        return candidates;
    }

    private static HashSet<int> SelectCandidatesDisjunctive(
        List<TermInfo> terms,
        int topK,
        int totalDocs,
        Dictionary<int, float> upperBounds)
    {
        HashSet<int> candidates = [];

        float maxIdf = 0f;
        foreach(var t in terms) if(t.Idf > maxIdf) maxIdf = t.Idf;

        terms.Sort((a, b) => b.Idf.CompareTo(a.Idf));

        bool hasSelectiveCandidates = false;

        foreach (TermInfo termInfo in terms)
        {
            bool isLowQuality = termInfo.Idf < (maxIdf * 0.2f);
            if (terms.Count > 1 && isLowQuality && hasSelectiveCandidates)
                continue;

            IPostingsEnum? postings = termInfo.Term.GetPostingsEnum();
            if (postings == null)
                continue;

            while (true)
            {
                int docId = postings.NextDoc();
                if (docId == PostingsEnumConstants.NO_MORE_DOCS)
                    break;

                if (candidates.Add(docId))
                {
                    upperBounds[docId] = termInfo.MaxScore;
                }
                else
                {
                    upperBounds[docId] += termInfo.MaxScore;
                }
            }
            
            if (!isLowQuality && candidates.Count > 0)
                hasSelectiveCandidates = true;

            if (candidates.Count >= topK * 100)
                break;
        }

        return candidates;
    }

    private static HashSet<int> IntersectAllTerms(List<TermInfo> terms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (terms.Count == 0)
            return [];

        // Collect iterators
        List<IPostingsEnum> enums = new List<IPostingsEnum>(terms.Count);
        foreach (TermInfo termInfo in terms)
        {
            IPostingsEnum? p = termInfo.Term.GetPostingsEnum();
            if (p == null) return [];
            enums.Add(p);
        }
        
        // Sort by cost (ascending)
        enums.Sort((a, b) => a.Cost().CompareTo(b.Cost()));
        
        HashSet<int> result = [];
        int advanceCalls = 0;
        
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
                    
                    // Re-align driver (and others implicitly in next loop)
                    doc = driver.Advance(doc);
                    if (doc == PostingsEnumConstants.NO_MORE_DOCS)
                        goto End;
                        
                    // Restart verification from first non-driver enum
                    i = 0; 
                    break; 
                }
                
                // If target == doc, continue to next enum
            }
            
            if (match)
            {
                result.Add(doc);
                doc = driver.NextDoc();
            }
        }

    End:
        sw.Stop();
        if (sw.ElapsedMilliseconds > 5)
        {
            Console.WriteLine($"[DEBUG] IntersectAllTerms: {sw.ElapsedMilliseconds}ms, Advance calls: {advanceCalls}, DriverCost: {driver.Cost()}, Result: {result.Count}");
        }
        return result;
    }

    public static List<int> FilterByUpperBound(
        HashSet<int> candidates,
        Dictionary<int, float> upperBounds,
        float currentKthScore,
        int topK)
    {
        List<int> filtered = new List<int>(candidates.Count);

        foreach (int docId in candidates)
        {
            if (upperBounds.TryGetValue(docId, out float upperBound))
            {
                if (upperBound >= currentKthScore || filtered.Count < topK)
                {
                    filtered.Add(docId);
                }
            }
        }

        return filtered;
    }

    private static HashSet<int> TrySelectPrefixCandidates(
        Indexing.ShortQuery.PositionalPrefixIndex prefixIndex,
        string originalQuery,
        int topK,
        int queryTermCount,
        int totalDocs,
        out float upperBound)
    {
        HashSet<int> candidates = [];
        upperBound = 0f;

        string queryLower = originalQuery.ToLowerInvariant();
        
        int maxPrefixLen = Math.Min(queryLower.Length, prefixIndex.MaxPrefixLength);
        
        for (int len = maxPrefixLen; len >= prefixIndex.MinPrefixLength; len--)
        {
            ReadOnlySpan<char> prefix = queryLower.AsSpan(0, len);
            PrefixPostingList? postingList = prefixIndex.GetPostingList(prefix);
            
            if (postingList == null || postingList.Count == 0)
                continue;
            
            foreach (ref readonly Indexing.ShortQuery.PrefixPosting posting in postingList.Postings)
            {
                if (posting.Position == 0 || posting.IsWordStart)
                {
                    candidates.Add(posting.DocumentId);
                }
            }
            
            if (candidates.Count > 0 && candidates.Count <= topK * 10)
            {
                upperBound = queryTermCount * 10f; 
                return candidates;
            }
            
            if (candidates.Count > topK * 20)
            {
                candidates.Clear();
                continue;
            }
        }
        
        return candidates;
    }
}
