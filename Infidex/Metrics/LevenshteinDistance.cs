namespace Infidex.Metrics;

/// <summary>
/// Calculates Levenshtein (edit) distance between strings.
/// Uses bit-parallel algorithm (Myers') for strings ≤64 characters for optimal performance,
/// and falls back to optimized Fastenshtein algorithm for longer strings.
/// </summary>
public static class LevenshteinDistance
{
    /// <summary>
    /// Calculates Levenshtein distance with automatic algorithm selection.
    /// Uses bit-parallel for short strings (≤64 chars), Fastenshtein for longer strings.
    /// </summary>
    public static int Calculate(string pattern, string text, int maxErrors = int.MaxValue)
    {
        if (string.IsNullOrEmpty(pattern))
            return text?.Length ?? 0;
        
        if (string.IsNullOrEmpty(text))
            return pattern.Length;
        
        // Use bit-parallel for short strings, Fastenshtein for longer ones
        if (pattern.Length <= 64 && text.Length <= 64)
            return BitParallel64(pattern, text, maxErrors);
        else
            return Fastenshtein(pattern, text);
    }
    
    /// <summary>
    /// Fastenshtein implementation: optimized standard Levenshtein algorithm.
    /// More reliable for general cases and longer strings.
    /// Based on: https://github.com/DanHarltey/Fastenshtein
    /// </summary>
    private static int Fastenshtein(string value1, string value2)
    {
        if (value2.Length == 0)
        {
            return value1.Length;
        }

        int[] costs = new int[value2.Length];

        // Add indexing for insertion to first row
        for (int i = 0; i < costs.Length;)
        {
            costs[i] = ++i;
        }

        for (int i = 0; i < value1.Length; i++)
        {
            // cost of the first index
            int cost = i;
            int previousCost = i;

            // cache value for inner loop to avoid index lookup and bounds checking
            char value1Char = value1[i];

            for (int j = 0; j < value2.Length; j++)
            {
                int currentCost = cost;

                // assigning this here reduces the array reads we do
                cost = costs[j];

                if (value1Char != value2[j])
                {
                    if (previousCost < currentCost)
                    {
                        currentCost = previousCost;
                    }

                    if (cost < currentCost)
                    {
                        currentCost = cost;
                    }

                    ++currentCost;
                }

                /* 
                 * Improvement: swapping variables here results in performance improvement
                 * for modern Intel CPUs
                 */
                costs[j] = currentCost;
                previousCost = currentCost;
            }
        }

        return costs[costs.Length - 1];
    }
    
    /// <summary>
    /// Bit-parallel algorithm for patterns up to 64 characters (Myers' algorithm).
    /// Uses bit manipulation for extremely fast computation.
    /// </summary>
    private static int BitParallel64(string pattern, string text, int maxErrors)
    {
        int m = pattern.Length;
        
        // Early termination: if length difference exceeds max errors, impossible to match
        if (Math.Abs(m - text.Length) > maxErrors)
            return maxErrors + 1;
        
        // Build pattern match vectors (PM)
        Dictionary<char, ulong> patternMasks = new Dictionary<char, ulong>();
        for (int i = 0; i < m; i++)
        {
            char c = pattern[i];
            if (!patternMasks.ContainsKey(c))
                patternMasks[c] = 0;
            patternMasks[c] |= (1UL << i);
        }
        
        // Initialize bit vectors
        ulong VP = ulong.MaxValue; // Vertical positive (all 1s)
        ulong VN = 0;              // Vertical negative (all 0s)
        ulong score = (ulong)m;    // Current edit distance
        
        // Process each character in text
        for (int j = 0; j < text.Length; j++)
        {
            char c = text[j];
            
            // Get pattern match for this character (or 0 if not in pattern)
            ulong PM = patternMasks.TryGetValue(c, out ulong mask) ? mask : 0;
            
            // Calculate horizontal differences (D0 matrix values)
            ulong X = PM | VN;
            ulong D0 = ((VP + (X & VP)) ^ VP) | X;
            ulong HN = VP & D0;
            ulong HP = VN | ~(VP | D0);
            
            // Update vertical differences for next iteration
            X = (HP << 1) | 1;
            VN = X & D0;
            VP = (HN << 1) | ~(X | D0);
            
            // Update score based on last row
            ulong lastBit = 1UL << (m - 1);
            if ((HP & lastBit) != 0)
                score++;
            else if ((HN & lastBit) != 0)
                score--;
            
            // Early termination if score exceeds max errors
            if (score > (ulong)(m + maxErrors))
                return maxErrors + 1;
        }
        
        return (int)score;
    }
    
    
    /// <summary>
    /// Checks if two strings are within a given edit distance threshold
    /// </summary>
    public static bool IsWithinDistance(string pattern, string text, int maxDistance)
    {
        return Calculate(pattern, text, maxDistance) <= maxDistance;
    }
}

