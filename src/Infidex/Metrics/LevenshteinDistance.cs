using System.Runtime.CompilerServices;

namespace Infidex.Metrics;

/// <summary>
/// Calculates Levenshtein (edit) distance between strings.
/// Includes both Word Levenshtein Distance (WLD) and Prefix Levenshtein Distance (PLD).
/// 
/// Based on: "Efficient Fuzzy Search in Large Text Collections" (Bast & Celikik, 2011)
/// 
/// Key concepts:
/// - WLD(w1, w2): Standard edit distance between two complete words
/// - PLD(p, w): Minimum WLD between prefix p and any prefix of word w
///   Example: PLD("algro", "algorithm") = 1 because WLD("algro", "algo") = 1
/// 
/// The PLD is critical for search-as-you-type scenarios where the query
/// is an incomplete prefix of the intended word.
/// </summary>
public static class LevenshteinDistance
{
    /// <summary>
    /// Dynamic error threshold based on query length.
    /// From Definition 2.1 in Bast & Celikik (2011):
    /// - δ = 1 for short words (≤5 chars)
    /// - δ = 2 for medium words (6-10 chars)
    /// - δ = 3 for long words (>10 chars)
    /// 
    /// This allows more error tolerance on longer queries while
    /// keeping short queries precise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDynamicThreshold(int queryLength)
    {
        if (queryLength <= 5) return 1;
        if (queryLength <= 10) return 2;
        return 3;
    }
    
    /// <summary>
    /// Calculates Prefix Levenshtein Distance (PLD) between a query prefix and a word.
    /// 
    /// Definition 2.2 (Bast & Celikik): PLD(p, w) is the minimum WLD between p
    /// and any prefix of w.
    /// 
    /// Algorithm: Fill only cells within δ of the main diagonal (gray cells in paper's Table I),
    /// then take the minimum value in the last row.
    /// 
    /// Time complexity: O(δ · |w|) - much faster than O(|p| · |w|) for full matrix
    /// </summary>
    /// <param name="prefix">The query prefix (what user has typed)</param>
    /// <param name="word">The dictionary word to match against</param>
    /// <param name="maxErrors">Maximum errors to consider (δ)</param>
    /// <param name="ignoreCase">Whether to ignore case differences</param>
    /// <returns>The minimum edit distance between prefix and any prefix of word</returns>
    public static int CalculatePrefixDistance(
        ReadOnlySpan<char> prefix, 
        ReadOnlySpan<char> word, 
        int maxErrors = int.MaxValue,
        bool ignoreCase = true)
    {
        if (prefix.IsEmpty) return 0;
        if (word.IsEmpty) return prefix.Length;
        
        int m = prefix.Length;  // Query prefix length
        int n = word.Length;    // Word length
        
        // Use dynamic threshold if not specified
        if (maxErrors == int.MaxValue)
        {
            maxErrors = GetDynamicThreshold(m);
        }
        
        // Optimization: If word is shorter than prefix by more than δ,
        // the minimum PLD is at least (m - n) which may exceed threshold
        if (m - n > maxErrors) return maxErrors + 1;
        
        // Allocate the cost array for the band (only cells within δ of diagonal)
        // We use a single row and update it in place
        int bandwidth = 2 * maxErrors + 1;
        Span<int> costs = bandwidth < 512 ? stackalloc int[bandwidth] : new int[bandwidth];
        
        // Initialize: costs[k] represents distance at diagonal offset (k - maxErrors)
        for (int k = 0; k < bandwidth; k++)
        {
            int diagonalOffset = k - maxErrors;
            if (diagonalOffset < 0)
                costs[k] = -diagonalOffset; // Deletion cost from empty prefix
            else if (diagonalOffset == 0)
                costs[k] = 0;
            else
                costs[k] = maxErrors + 1; // Out of band
        }
        
        int minPLD = m; // Track minimum in last row
        
        // Process each character of the word (columns in DP matrix)
        for (int j = 0; j < n; j++)
        {
            char wChar = word[j];
            if (ignoreCase) wChar = char.ToLowerInvariant(wChar);
            
            // Track minimum for this column (for prefix distance)
            int colMin = maxErrors + 1;
            
            // Process the band for this column
            int prevDiag = costs[0];
            
            for (int k = 0; k < bandwidth; k++)
            {
                int i = j + (k - maxErrors); // Row index in full matrix
                
                if (i < 0 || i > m)
                {
                    if (k > 0) prevDiag = costs[k];
                    continue;
                }
                
                int newCost;
                
                if (i == 0)
                {
                    // First row: insertions only
                    newCost = j + 1;
                }
                else
                {
                    char pChar = prefix[i - 1];
                    if (ignoreCase) pChar = char.ToLowerInvariant(pChar);
                    
                    int substitutionCost = (pChar == wChar) ? 0 : 1;
                    
                    // Get costs from neighbors (within band)
                    int diagCost = prevDiag + substitutionCost;
                    int leftCost = (k > 0) ? costs[k - 1] + 1 : maxErrors + 2; // Insertion
                    int upCost = costs[k] + 1; // Deletion
                    
                    newCost = Math.Min(diagCost, Math.Min(leftCost, upCost));
                }
                
                prevDiag = costs[k];
                costs[k] = Math.Min(newCost, maxErrors + 1);
                
                // Track minimum in the last row (when i == m)
                if (i == m && costs[k] < colMin)
                {
                    colMin = costs[k];
                }
            }
            
            // Update global minimum PLD (minimum across all columns in last row)
            if (colMin < minPLD)
            {
                minPLD = colMin;
            }
            
            // Early termination: if all values in band exceed threshold, no match possible
            bool allExceedThreshold = true;
            for (int k = 0; k < bandwidth; k++)
            {
                if (costs[k] <= maxErrors)
                {
                    allExceedThreshold = false;
                    break;
                }
            }
            if (allExceedThreshold && j >= maxErrors)
            {
                return maxErrors + 1;
            }
        }
        
        // The PLD is the minimum value in the last row (m-th row)
        // We've been tracking this in minPLD
        return minPLD;
    }
    
    /// <summary>
    /// Overload for string inputs.
    /// </summary>
    public static int CalculatePrefixDistance(string prefix, string word, int maxErrors = int.MaxValue)
    {
        return CalculatePrefixDistance(prefix.AsSpan(), word.AsSpan(), maxErrors);
    }
    
    /// <summary>
    /// Checks if a word is a "fuzzy completion" of a prefix within the dynamic threshold.
    /// A word w is a fuzzy completion of prefix p if PLD(p, w) ≤ δ(|p|).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFuzzyCompletion(ReadOnlySpan<char> prefix, ReadOnlySpan<char> word)
    {
        int threshold = GetDynamicThreshold(prefix.Length);
        return CalculatePrefixDistance(prefix, word, threshold) <= threshold;
    }
    
    /// <summary>
    /// Checks if two words are similar within the dynamic threshold.
    /// Uses WLD (full word Levenshtein distance).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSimilarWord(ReadOnlySpan<char> word1, ReadOnlySpan<char> word2)
    {
        int maxLen = Math.Max(word1.Length, word2.Length);
        int threshold = GetDynamicThreshold(maxLen);
        return Calculate(word1, word2, threshold) <= threshold;
    }

    /// <summary>
    /// Calculates Levenshtein distance using Span to avoid allocations.
    /// Uses stack-allocated memory for short strings.
    /// </summary>
    public static int Calculate(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, int maxErrors = int.MaxValue, bool ignoreCase = false)
    {
        if (pattern.IsEmpty)
            return text.Length;
        
        if (text.IsEmpty)
            return pattern.Length;

        // Ensure pattern is the shorter one to minimize row size
        if (pattern.Length > text.Length)
        {
            var temp = pattern;
            pattern = text;
            text = temp;
        }

        int m = pattern.Length;
        int n = text.Length;

        // Reuse stack memory for costs
        // We need m + 1 size
        Span<int> costs = m < 512 ? stackalloc int[m + 1] : new int[m + 1];

        for (int i = 0; i <= m; i++)
        {
            costs[i] = i;
        }

        for (int j = 0; j < n; j++)
        {
            char tVal = text[j];
            if (ignoreCase) tVal = char.ToUpperInvariant(tVal);
            
            int diagonal = costs[0];
            costs[0] = j + 1;
            
            int minCost = costs[0];

            for (int i = 0; i < m; i++)
            {
                int left = costs[i + 1];
                int up = costs[i];
                
                char pVal = pattern[i];
                if (ignoreCase) pVal = char.ToUpperInvariant(pVal);
                
                int cost;
                if (tVal == pVal)
                {
                    cost = diagonal;
                }
                else
                {
                    int insertion = up + 1;
                    int deletion = left + 1;
                    int substitution = diagonal + 1;
                    
                    cost = insertion;
                    if (deletion < cost) cost = deletion;
                    if (substitution < cost) cost = substitution;
                }

                diagonal = left;
                costs[i + 1] = cost;
                
                if (cost < minCost) minCost = cost;
            }
            
            // Early exit
            if (minCost > maxErrors)
            {
                return maxErrors + 1;
            }
        }

        return costs[m];
    }
    
    /// <summary>
    /// Calculates Levenshtein distance (compatibility overload).
    /// </summary>
    public static int Calculate(string pattern, string text, int maxErrors = int.MaxValue)
    {
        return Calculate(pattern.AsSpan(), text.AsSpan(), maxErrors, ignoreCase: false);
    }
    
    /// <summary>
    /// Checks if two strings are within a given edit distance.
    /// </summary>
    public static bool IsWithinDistance(string pattern, string text, int maxDistance)
    {
        int distance = Calculate(pattern.AsSpan(), text.AsSpan(), maxDistance, ignoreCase: false);
        return distance <= maxDistance;
    }
    
    
    /// <summary>
    /// Calculates Damerau-Levenshtein distance (includes transpositions of adjacent characters).
    /// Optimized for restricted edit distance (checks if dist <= maxDistance).
    /// </summary>
    public static int CalculateDamerau(ReadOnlySpan<char> source, ReadOnlySpan<char> target, int maxDistance, bool ignoreCase = false)
    {
        // If lengths differ, Damerau distance is at least the length difference.
        // Optimization: If length diff > maxDistance, fail early.
        int lenDiff = Math.Abs(source.Length - target.Length);
        if (lenDiff > maxDistance) return maxDistance + 1;
        
        int dist = Calculate(source, target, maxDistance + 1, ignoreCase); // Allow +1 for potential swap reduction
        
        // If standard Levenshtein is already small enough, just return it.
        if (dist <= maxDistance) return dist;
        
        // If dist is slightly too high (editDist + 1), check if a single transposition can fix it.
        // Restricted Damerau: we only look for one transposition to save a "distance point".
        if (dist <= maxDistance + 1)
        {
            // Scan for mismatch
            int len = source.Length;
            for (int i = 0; i < len - 1; i++)
            {
                // Ensure we don't go out of bounds on target
                if (i >= target.Length) break;

                char s1 = ignoreCase ? char.ToLowerInvariant(source[i]) : source[i];
                char t1 = ignoreCase ? char.ToLowerInvariant(target[i]) : target[i];
                
                if (s1 != t1)
                {
                    // Check bounds for swap partner in target
                    if (i + 1 >= target.Length) break;

                    // Mismatch. Check if swap fixes it.
                    char s2 = ignoreCase ? char.ToLowerInvariant(source[i+1]) : source[i+1];
                    char t2 = ignoreCase ? char.ToLowerInvariant(target[i+1]) : target[i+1];
                    
                    if (s1 == t2 && s2 == t1)
                    {
                        // Found valid swap.
                        // Check remaining string match.
                        // Cost = 1 (swap) + Levenshtein(rest).
                        // Rest starts at i+2.
                        int remainingBudget = maxDistance - 1;
                        if (remainingBudget < 0) return maxDistance + 1;
                        
                        var sRest = (i + 2 < len) ? source.Slice(i + 2) : ReadOnlySpan<char>.Empty;
                        var tRest = (i + 2 < target.Length) ? target.Slice(i + 2) : ReadOnlySpan<char>.Empty;
                        
                        int restDist = Calculate(sRest, tRest, remainingBudget, ignoreCase);
                        
                        if (restDist <= remainingBudget)
                        {
                            return 1 + restDist;
                        }
                    }
                    break; // Only check first mismatch
                }
            }
        }
        
        return dist;
    }
}

