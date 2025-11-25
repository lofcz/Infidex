using System.Buffers;
using System.Runtime.CompilerServices;

namespace Infidex.Metrics;

/// <summary>
/// Autocomplete-optimized scoring based on LCS (Longest Common Subsequence).
/// 
/// Based on: FuzzySearch.js scoring philosophy
/// 
/// Key insight: For autocomplete, counting MATCHES (LCS) is more intuitive than
/// counting ERRORS (edit distance). Example:
///   - match("uni", "university"): LCS=3, errors=7 → LCS clearly better
///   - match("uni", "hi"): LCS=1, errors=2 → LCS shows it's a poor match
/// 
/// The Jaro-like score normalizes LCS by both string lengths:
///   score = 0.5 * m * (m/|a| + m/|b|) + bonus * prefix
/// 
/// Where m = LCS length, |a| = query length, |b| = candidate length
/// 
/// This scoring is:
/// 1. No tuning required - mathematically derived from string similarity
/// 2. Intuitive - matches what users expect in autocomplete scenarios
/// 3. Fast - uses bit-parallel LCS for strings up to 64 chars
/// </summary>
public static class AutocompleteScoring
{
    /// <summary>
    /// Computes a Jaro-like similarity score based on LCS.
    /// Returns a value in [0, 1] where 1 = perfect match.
    /// </summary>
    /// <param name="query">The search query</param>
    /// <param name="candidate">The candidate string to match against</param>
    /// <param name="prefixBonus">Bonus multiplier for common prefix (default 0.1)</param>
    /// <returns>Similarity score in [0, 1]</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeJaroLikeScore(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, float prefixBonus = 0.1f)
    {
        if (query.IsEmpty || candidate.IsEmpty)
            return 0f;
        
        int queryLen = query.Length;
        int candidateLen = candidate.Length;
        
        // Compute prefix match length
        int prefix = CommonPrefixLength(query, candidate);
        
        // Compute LCS length
        int lcs = ComputeLcsLength(query, candidate);
        
        if (lcs == 0)
            return 0f;
        
        // Jaro-like score formula:
        // score = 0.5 * m * (m/|a| + m/|b|) + bonus * prefix
        // This gives higher weight to matches that cover more of each string
        float coverage = (float)lcs / queryLen + (float)lcs / candidateLen;
        float baseScore = 0.5f * lcs * coverage;
        
        // Add prefix bonus (Winkler-style)
        float prefixScore = prefixBonus * prefix;
        
        // Normalize to [0, 1]
        // Max possible baseScore is when lcs = min(queryLen, candidateLen) and strings equal
        float maxScore = Math.Min(queryLen, candidateLen) + prefixBonus * Math.Min(4, Math.Min(queryLen, candidateLen));
        
        return Math.Clamp((baseScore + prefixScore) / Math.Max(maxScore, 1f), 0f, 1f);
    }
    
    /// <summary>
    /// Computes autocomplete score optimized for type-ahead search.
    /// Higher scores indicate better matches for autocomplete purposes.
    /// 
    /// Scoring factors:
    /// 1. Prefix match (most important for autocomplete)
    /// 2. Word boundary matches
    /// 3. LCS coverage
    /// 4. Candidate length penalty (shorter matches preferred when equal)
    /// </summary>
    /// <param name="query">The search query</param>
    /// <param name="candidate">The candidate string to match</param>
    /// <returns>Score in [0, 255] for byte-compatible scoring</returns>
    public static byte ComputeAutocompleteScore(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate)
    {
        if (query.IsEmpty || candidate.IsEmpty)
            return 0;
        
        int queryLen = query.Length;
        int candidateLen = candidate.Length;
        
        // Compute components
        int prefix = CommonPrefixLength(query, candidate);
        int lcs = ComputeLcsLength(query, candidate);
        
        if (lcs == 0)
            return 0;
        
        // Calculate word boundary matches
        int wordBoundaryMatches = CountWordBoundaryMatches(query, candidate);
        
        float score = 0f;
        
        // Factor 1: Prefix coverage (0-100 points)
        // Full prefix match = 100, partial = proportional
        float prefixCoverage = (float)prefix / queryLen;
        score += prefixCoverage * 100f;
        
        // Factor 2: LCS coverage (0-80 points)
        // How much of the query is found as subsequence
        float lcsCoverage = (float)lcs / queryLen;
        score += lcsCoverage * 80f;
        
        // Factor 3: Word boundary bonus (0-50 points)
        // Matches at word starts are more valuable
        score += Math.Min(wordBoundaryMatches * 10f, 50f);
        
        // Factor 4: Conciseness bonus (0-25 points)
        // Prefer shorter candidates that still match well
        float lengthRatio = (float)queryLen / candidateLen;
        score += lengthRatio * 25f;
        
        // Normalize to 0-255
        return (byte)Math.Clamp(score, 0f, 255f);
    }
    
    /// <summary>
    /// Computes multi-token autocomplete score.
    /// Handles queries with multiple words, allowing free word order.
    /// </summary>
    public static byte ComputeMultiTokenScore(string query, string candidate, char[] delimiters)
    {
        string[] queryTokens = query.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
        string[] candidateTokens = candidate.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
        
        if (queryTokens.Length == 0 || candidateTokens.Length == 0)
            return 0;
        
        float totalScore = 0f;
        int matchedTokens = 0;
        bool[] usedCandidateTokens = new bool[candidateTokens.Length];
        
        // For each query token, find best matching candidate token
        foreach (string qToken in queryTokens)
        {
            float bestMatch = 0f;
            int bestIndex = -1;
            
            for (int i = 0; i < candidateTokens.Length; i++)
            {
                if (usedCandidateTokens[i])
                    continue;
                
                float tokenScore = ComputeJaroLikeScore(qToken.AsSpan(), candidateTokens[i].AsSpan());
                
                if (tokenScore > bestMatch)
                {
                    bestMatch = tokenScore;
                    bestIndex = i;
                }
            }
            
            if (bestIndex >= 0 && bestMatch > 0.3f) // Minimum match threshold
            {
                usedCandidateTokens[bestIndex] = true;
                totalScore += bestMatch;
                matchedTokens++;
            }
        }
        
        if (matchedTokens == 0)
            return 0;
        
        // Average token score
        float avgScore = totalScore / queryTokens.Length;
        
        // Token coverage bonus: prefer candidates that match more query tokens
        float coverage = (float)matchedTokens / queryTokens.Length;
        
        // Order bonus: tokens in correct order get bonus
        float orderBonus = CalculateOrderBonus(queryTokens, candidateTokens, usedCandidateTokens);
        
        // Combined score
        float finalScore = (avgScore * 0.6f + coverage * 0.3f + orderBonus * 0.1f) * 255f;
        
        return (byte)Math.Clamp(finalScore, 0f, 255f);
    }
    
    /// <summary>
    /// Computes LCS length using bit-parallel algorithm for short strings
    /// and standard DP for longer strings.
    /// </summary>
    public static int ComputeLcsLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.IsEmpty || b.IsEmpty)
            return 0;
        
        // For short strings, use bit-parallel algorithm
        // This is based on Crochemore 2001 / Hyyrö 2004
        if (a.Length <= 64 && b.Length <= 64)
        {
            return ComputeLcsBitParallel(a, b);
        }
        
        // Fall back to standard DP for longer strings
        return ComputeLcsDP(a, b);
    }
    
    /// <summary>
    /// Bit-parallel LCS computation (Hyyrö 2004).
    /// Works for strings up to 64 characters.
    /// Time: O(|a| + |b|), Space: O(|Σ|) where Σ is alphabet
    /// </summary>
    private static int ComputeLcsBitParallel(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int m = a.Length;
        int n = b.Length;
        
        if (m > 64)
            return ComputeLcsDP(a, b);
        
        // Build character position bitmap for string a
        // aMap[c] has bit i set if a[i] == c
        Span<ulong> aMap = stackalloc ulong[256]; // ASCII-optimized
        aMap.Clear();
        
        for (int i = 0; i < m; i++)
        {
            char c = char.ToLowerInvariant(a[i]);
            if (c < 256)
                aMap[c] |= 1UL << i;
        }
        
        ulong mask = (1UL << m) - 1;
        ulong S = mask;
        
        // Process string b character by character
        for (int j = 0; j < n; j++)
        {
            char c = char.ToLowerInvariant(b[j]);
            ulong charMask = c < 256 ? aMap[c] : 0;
            
            ulong U = S & charMask;
            S = (S + U) | (S - U);
        }
        
        // Count bits that are 0 (each 0 bit represents a match)
        S = ~S & mask;
        return BitOperations.PopCount(S);
    }
    
    /// <summary>
    /// Standard dynamic programming LCS for longer strings.
    /// Time: O(|a| * |b|), Space: O(min(|a|, |b|))
    /// </summary>
    private static int ComputeLcsDP(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        // Ensure a is the shorter string for space efficiency
        if (a.Length > b.Length)
        {
            var temp = a;
            a = b;
            b = temp;
        }
        
        int m = a.Length;
        int n = b.Length;
        
        // Use only two rows instead of full matrix
        int[] prev = ArrayPool<int>.Shared.Rent(m + 1);
        int[] curr = ArrayPool<int>.Shared.Rent(m + 1);
        
        try
        {
            Array.Clear(prev, 0, m + 1);
            
            for (int j = 1; j <= n; j++)
            {
                Array.Clear(curr, 0, m + 1);
                
                for (int i = 1; i <= m; i++)
                {
                    if (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]))
                    {
                        curr[i] = prev[i - 1] + 1;
                    }
                    else
                    {
                        curr[i] = Math.Max(prev[i], curr[i - 1]);
                    }
                }
                
                // Swap rows
                var temp = prev;
                prev = curr;
                curr = temp;
            }
            
            return prev[m];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(prev);
            ArrayPool<int>.Shared.Return(curr);
        }
    }
    
    /// <summary>
    /// Counts characters that match at word boundaries in the candidate.
    /// Word boundaries are: start of string, after space/punctuation.
    /// </summary>
    private static int CountWordBoundaryMatches(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate)
    {
        int matches = 0;
        int queryIndex = 0;
        bool atWordStart = true;
        
        for (int i = 0; i < candidate.Length && queryIndex < query.Length; i++)
        {
            char c = candidate[i];
            
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                atWordStart = true;
                continue;
            }
            
            if (atWordStart && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[queryIndex]))
            {
                matches++;
                queryIndex++;
            }
            
            atWordStart = false;
        }
        
        return matches;
    }
    
    /// <summary>
    /// Computes common prefix length (case-insensitive).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CommonPrefixLength(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        int i = 0;
        
        while (i < minLen && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
        {
            i++;
        }
        
        return i;
    }
    
    /// <summary>
    /// Calculates bonus for tokens appearing in correct order.
    /// </summary>
    private static float CalculateOrderBonus(string[] queryTokens, string[] candidateTokens, bool[] usedCandidateTokens)
    {
        if (queryTokens.Length < 2)
            return 1f;
        
        int orderedPairs = 0;
        int totalPairs = 0;
        int lastUsedIndex = -1;
        
        foreach (string qToken in queryTokens)
        {
            // Find which candidate index this token matched
            int matchedIndex = -1;
            float bestMatch = 0f;
            
            for (int i = 0; i < candidateTokens.Length; i++)
            {
                if (!usedCandidateTokens[i])
                    continue;
                
                float score = ComputeJaroLikeScore(qToken.AsSpan(), candidateTokens[i].AsSpan());
                if (score > bestMatch)
                {
                    bestMatch = score;
                    matchedIndex = i;
                }
            }
            
            if (matchedIndex >= 0 && lastUsedIndex >= 0)
            {
                totalPairs++;
                if (matchedIndex > lastUsedIndex)
                    orderedPairs++;
            }
            
            if (matchedIndex >= 0)
                lastUsedIndex = matchedIndex;
        }
        
        return totalPairs > 0 ? (float)orderedPairs / totalPairs : 1f;
    }
    
    /// <summary>
    /// Bit manipulation helper.
    /// </summary>
    private static class BitOperations
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopCount(ulong value)
        {
#if NET6_0_OR_GREATER
            return System.Numerics.BitOperations.PopCount(value);
#else
            // Hamming weight algorithm
            value -= (value >> 1) & 0x5555555555555555UL;
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((value * 0x0101010101010101UL) >> 56);
#endif
        }
    }
}

