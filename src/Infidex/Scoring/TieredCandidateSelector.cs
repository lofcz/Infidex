using Infidex.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Infidex.Indexing.ShortQuery;

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
    /// 
    /// PREFIX PRECEDENCE (provable quality tiers):
    /// Tier 0: Prefix match at position 0 - HIGHEST
    /// Tier 1: Prefix match at word start - HIGH
    /// Tier 2: Conjunctive (AND) of all terms - MEDIUM
    /// Tier 3: Disjunctive (OR) fallback - LOW
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
        upperBounds = new Dictionary<int, float>();
        
        if (queryTerms.Count == 0)
            return [];

        // TIER 0-1: PREFIX PRECEDENCE - Use PositionalPrefixIndex for high-quality prefix matches
        // This encodes the rule: "prefix at start > prefix at word boundary > substring match"
        if (prefixIndex != null && !string.IsNullOrEmpty(originalQuery))
        {
            HashSet<int> prefixCandidates = TrySelectPrefixCandidates(
                prefixIndex, originalQuery, topK, queryTerms, totalDocs, out float prefixUpperBound);
            
            if (prefixCandidates.Count > 0)
            {
                // Found high-quality prefix matches - these have precedence
                foreach (int docId in prefixCandidates)
                {
                    upperBounds[docId] = prefixUpperBound;
                }
                
                // If we have enough prefix candidates, return immediately
                // These are provably higher quality than fuzzy matches
                if (prefixCandidates.Count >= Math.Min(topK * 2, 100))
                {
                    return prefixCandidates;
                }
            }
        }

        // TIER 2-3: TERM-BASED SELECTION - Build term info sorted by IDF (descending) - high IDF terms are most selective
        List<TermInfo> terms = new List<TermInfo>(queryTerms.Count);
        int missingTerms = 0;
        
        foreach (Term term in queryTerms)
        {
            if (term.DocumentFrequency <= 0)
            {
                missingTerms++;
                continue;
            }

            float idf = MathF.Log10((float)totalDocs / term.DocumentFrequency);
            float maxScore = idf * 2.2f; // Rough upper bound for BM25 (k1=1.2, b=0.75)
            terms.Add(new TermInfo(term, idf, maxScore, term.QueryOccurrences));
        }
        
        // If we have no valid terms at all, then we must return empty.
        if (terms.Count == 0)
        {
            upperBounds.Clear();
            return [];
        }

        // Check if any term has very low DF (likely rare/typo term)
        bool hasPotentialTypo = false;
        foreach (TermInfo termInfo in terms)
        {
            if (termInfo.Term.DocumentFrequency < 10)
            {
                hasPotentialTypo = true;
                break;
            }
        }
        
        // For typo queries, missing terms, or single-term queries, use disjunctive mode
        if (hasPotentialTypo || missingTerms > 0 || queryTerms.Count == 1)
        {
            return SelectCandidatesDisjunctive(queryTerms, topK, totalDocs, upperBounds);
        }

        // Sort by IDF descending - process most selective terms first
        terms.Sort((a, b) => b.Idf.CompareTo(a.Idf));

        HashSet<int> candidates = [];

        // Tier 0: Full AND (all terms required) - highest quality
        if (terms.Count >= 2)
        {
            HashSet<int> tier0 = IntersectAllTerms(terms);
            float tier0UpperBound = terms.Sum(t => t.MaxScore);
            
            foreach (int docId in tier0)
            {
                candidates.Add(docId);
                upperBounds[docId] = tier0UpperBound;
            }

            // If we have enough high-quality candidates, we're done
            if (candidates.Count >= topK * 2)
                return candidates;
        }

        // Tier 1: n-1 terms (drop lowest IDF term) - still high quality
        if (terms.Count >= 3 && candidates.Count < topK * 3)
        {
            // Drop the lowest IDF term (last in sorted list)
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

        // Tier 2: Individual high-IDF terms (fallback for rare terms)
        if (candidates.Count < topK * 5)
        {
            // Only use top 2 most selective terms
            foreach (TermInfo termInfo in terms.Take(Math.Min(2, terms.Count)))
            {
                List<int>? docIds = termInfo.Term.GetDocumentIds();
                if (docIds == null)
                    continue;

                foreach (int docId in docIds)
                {
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

    /// <summary>
    /// Disjunctive (OR) mode: Returns union of posting lists for typo/fuzzy queries.
    /// Preserves recall by not requiring all terms to be present.
    /// Optimized to ignore low-IDF (stop) words when other selective terms are present.
    /// </summary>
    private static HashSet<int> SelectCandidatesDisjunctive(
        List<Term> queryTerms,
        int topK,
        int totalDocs,
        Dictionary<int, float> upperBounds)
    {
        HashSet<int> candidates = [];

        // Sort by IDF descending - high IDF terms are most selective
        List<TermInfo> terms = new List<TermInfo>(queryTerms.Count);
        float maxIdf = 0f;
        
        foreach (Term term in queryTerms)
        {
            if (term.DocumentFrequency <= 0)
                continue;

            float idf = MathF.Log10((float)totalDocs / term.DocumentFrequency);
            float maxScore = idf * 2.2f;
            
            if (idf > maxIdf) maxIdf = idf;
            
            terms.Add(new TermInfo(term, idf, maxScore, term.QueryOccurrences));
        }

        terms.Sort((a, b) => b.Idf.CompareTo(a.Idf));

        // Add documents from terms (OR logic)
        // Optimization: Stop adding candidates if we hit low-quality terms
        // provided we have already found some candidates.
        // This prevents "the" (IDF~0) from exploding
        
        bool hasSelectiveCandidates = false;

        foreach (TermInfo termInfo in terms)
        {
            // Skip low-IDF terms (like stop words) if we have other selective terms.
            // Heuristic: if term IDF is < 20% of max IDF, and we have > 1 term, skip it for candidate generation.
            bool isLowQuality = termInfo.Idf < (maxIdf * 0.2f);
            if (terms.Count > 1 && isLowQuality && hasSelectiveCandidates)
                continue;

            List<int>? docIds = termInfo.Term.GetDocumentIds();
            if (docIds == null)
                continue;

            foreach (int docId in docIds)
            {
                if (candidates.Add(docId))
                {
                    upperBounds[docId] = termInfo.MaxScore;
                }
                else
                {
                    // Document already seen - increase upper bound
                    upperBounds[docId] += termInfo.MaxScore;
                }
            }
            
            if (!isLowQuality && candidates.Count > 0)
                hasSelectiveCandidates = true;

            // Early stop if we have enough candidates
            if (candidates.Count >= topK * 100)
                break;
        }

        return candidates;
    }

    /// <summary>
    /// Intersects posting lists of all terms to find documents containing all terms.
    /// Uses sorted merge for O(n) performance instead of O(n*m).
    /// </summary>
    private static HashSet<int> IntersectAllTerms(List<TermInfo> terms)
    {
        if (terms.Count == 0)
            return [];

        if (terms.Count == 1)
        {
            List<int>? docIds = terms[0].Term.GetDocumentIds();
            return docIds != null ? [..docIds] : [];
        }

        // Get all posting lists
        List<List<int>> postingLists = new List<List<int>>(terms.Count);
        foreach (TermInfo termInfo in terms)
        {
            List<int>? docIds = termInfo.Term.GetDocumentIds();
            if (docIds == null || docIds.Count == 0)
                return []; // If any term has no postings, intersection is empty
            
            postingLists.Add(docIds);
        }

        // Sort by length - start with shortest list for efficiency
        postingLists.Sort((a, b) => a.Count.CompareTo(b.Count));

        // Start with shortest list
        HashSet<int> result = new HashSet<int>(postingLists[0]);

        // Intersect with remaining lists using binary search
        for (int i = 1; i < postingLists.Count; i++)
        {
            List<int> currentList = postingLists[i];
            result.RemoveWhere(docId => currentList.BinarySearch(docId) < 0);

            if (result.Count == 0)
                return result; // Early exit if intersection is empty
        }

        return result;
    }

    /// <summary>
    /// Filters candidates by checking if their upper bound score can compete with current top-K.
    /// This enables early termination - we can skip scoring documents that can't make top-K.
    /// </summary>
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
                // Only consider if upper bound exceeds current K-th best score
                if (upperBound >= currentKthScore || filtered.Count < topK)
                {
                    filtered.Add(docId);
                }
            }
        }

        return filtered;
    }

    /// <summary>
    /// Tries to select candidates using prefix matching from PositionalPrefixIndex.
    /// Returns documents where the query (or significant prefix) matches at word start or position 0.
    /// </summary>
    private static HashSet<int> TrySelectPrefixCandidates(
        Indexing.ShortQuery.PositionalPrefixIndex prefixIndex,
        string originalQuery,
        int topK,
        List<Term> queryTerms,
        int totalDocs,
        out float upperBound)
    {
        HashSet<int> candidates = [];
        upperBound = 0f;

        // Normalize query for prefix matching
        string queryLower = originalQuery.ToLowerInvariant();
        
        // Try prefixes of increasing length (up to full query or max prefix length)
        int maxPrefixLen = Math.Min(queryLower.Length, prefixIndex.MaxPrefixLength);
        
        // Start with longest prefix first (most selective)
        for (int len = maxPrefixLen; len >= prefixIndex.MinPrefixLength; len--)
        {
            ReadOnlySpan<char> prefix = queryLower.AsSpan(0, len);
            PrefixPostingList? postingList = prefixIndex.GetPostingList(prefix);
            
            if (postingList == null || postingList.Count == 0)
                continue;
            
            // Collect documents with this prefix, prioritizing position 0 and word starts
            foreach (ref readonly Indexing.ShortQuery.PrefixPosting posting in postingList.Postings)
            {
                // Accept: position 0 (document start) OR word start
                if (posting.Position == 0 || posting.IsWordStart)
                {
                    candidates.Add(posting.DocumentId);
                }
            }
            
            // If we found candidates with a good prefix, use them
            if (candidates.Count > 0 && candidates.Count <= topK * 10)
            {
                // Compute upper bound: assume perfect BM25 score for all query terms
                upperBound = queryTerms.Count * 10f; // Rough upper bound
                return candidates;
            }
            
            // If too many candidates (prefix too common), try shorter prefix
            if (candidates.Count > topK * 20)
            {
                candidates.Clear();
                continue;
            }
        }
        
        return candidates;
    }
}

