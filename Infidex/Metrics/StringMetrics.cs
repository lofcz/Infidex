namespace Infidex.Metrics;

/// <summary>
/// Additional string similarity metrics (Jaro-Winkler, LCS, etc.)
/// </summary>
public static class StringMetrics
{
    /// <summary>
    /// Calculates LCS with error tolerance.
    /// Effectively calculates Longest Common Prefix length plus tolerance.
    /// </summary>
    public static int Lcs(string q, string r, int errorTolerance)
    {
        if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(r))
            return 0;

        // Check strict containment first (Optimization / Original behavior)
        // if (q.Length - errorTolerance > r.Length) 
        //    return 0;
            
        if (q == r) return q.Length;
        if (r.Contains(q)) return q.Length;
        
        // Fallback: Common Prefix + Tolerance
        // This explains the scores: "battamam"(8) vs "batman"(6) -> prefix "bat"(3) + tol(1) = 4.
        // "speeding"(8) vs "speeds"(6) -> prefix "speed"(5) + tol(1) = 6.
        int prefixLen = 0;
        int len = Math.Min(q.Length, r.Length);
        for (int i = 0; i < len; i++)
        {
            if (q[i] != r[i]) break;
            prefixLen++;
        }
        
        if (prefixLen == 0) return 0;
        
        return Math.Min(prefixLen + errorTolerance, Math.Min(q.Length, r.Length));
    }

    /// <summary>
    /// Calculates Longest Common Subsequence length
    /// </summary>
    public static int LongestCommonSubsequence(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0;
        
        int m = s1.Length;
        int n = s2.Length;
        
        // Use two rows for space optimization
        int[] previous = new int[n + 1];
        int[] current = new int[n + 1];
        
        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (s1[i - 1] == s2[j - 1])
                    current[j] = previous[j - 1] + 1;
                else
                    current[j] = Math.Max(previous[j], current[j - 1]);
            }
            
            // Swap rows
            (previous, current) = (current, previous);
            Array.Clear(current, 0, current.Length);
        }
        
        return previous[n];
    }
    
    /// <summary>
    /// Calculates Jaro similarity (0.0 to 1.0)
    /// </summary>
    public static double JaroSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
            return 1.0;
        
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0.0;
        
        int len1 = s1.Length;
        int len2 = s2.Length;
        
        // Match window
        int matchWindow = Math.Max(len1, len2) / 2 - 1;
        if (matchWindow < 1) matchWindow = 1;
        
        bool[] s1Matches = new bool[len1];
        bool[] s2Matches = new bool[len2];
        
        int matches = 0;
        int transpositions = 0;
        
        // Find matches
        for (int i = 0; i < len1; i++)
        {
            int start = Math.Max(0, i - matchWindow);
            int end = Math.Min(i + matchWindow + 1, len2);
            
            for (int j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j])
                    continue;
                
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }
        
        if (matches == 0)
            return 0.0;
        
        // Count transpositions
        int k = 0;
        for (int i = 0; i < len1; i++)
        {
            if (!s1Matches[i])
                continue;
            
            while (!s2Matches[k])
                k++;
            
            if (s1[i] != s2[k])
                transpositions++;
            
            k++;
        }
        
        return (matches / (double)len1 +
                matches / (double)len2 +
                (matches - transpositions / 2.0) / matches) / 3.0;
    }
    
    /// <summary>
    /// Calculates Jaro-Winkler similarity with prefix bonus (0.0 to 1.0)
    /// </summary>
    public static double JaroWinklerSimilarity(string s1, string s2, double prefixScale = 0.1)
    {
        double jaro = JaroSimilarity(s1, s2);
        
        // Find common prefix (up to 4 characters)
        int prefixLength = 0;
        int maxPrefix = Math.Min(4, Math.Min(s1.Length, s2.Length));
        
        for (int i = 0; i < maxPrefix; i++)
        {
            if (s1[i] == s2[i])
                prefixLength++;
            else
                break;
        }
        
        return jaro + (prefixLength * prefixScale * (1.0 - jaro));
    }
}
