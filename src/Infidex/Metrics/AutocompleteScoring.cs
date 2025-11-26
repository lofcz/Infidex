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
            ReadOnlySpan<char> temp = a;
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
                (prev, curr) = (curr, prev);
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
    /// Bit manipulation helper.
    /// </summary>
    private static class BitOperations
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PopCount(ulong value)
        {
            return System.Numerics.BitOperations.PopCount(value);
        }
    }
}



