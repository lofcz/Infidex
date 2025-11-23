namespace Infidex.Metrics;

/// <summary>
/// Calculates Levenshtein (edit) distance between strings.
/// Uses bit-parallel algorithm (Myers') for strings ≤64 characters for optimal performance,
/// and falls back to optimized Fastenshtein algorithm for longer strings.
/// </summary>
public static class LevenshteinDistance
{
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

