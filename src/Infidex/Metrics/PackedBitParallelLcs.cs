using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Infidex.Metrics;

/// <summary>
/// Packed Bit-Parallel LCS for multi-word parallel scoring.
/// 
/// Based on: "Increased Bit-Parallelism for Approximate and Multiple String Matching" 
/// (Hyyrö &amp; Navarro, 2006)
/// 
/// Mathematical Foundation:
/// - LCS via bit-vectors: each bit tracks a potential match position
/// - Multiple words packed into single 64-bit register with boundary masks
/// - Time: O(n) where n = document length, regardless of query word count (up to limit)
/// 
/// Key Insight from FuzzySearch.js:
/// "The idea is that changing one bit or changing 32 bits in an integer take the same 
/// amount of time. This means we can search for a 2 character word or 30 character 
/// word with the same computation. We can pack six 5-character words in the same query."
/// 
/// Information-Theoretic Justification:
/// - LCS captures similarity without over-penalizing substitutions (like Damerau-Levenshtein)
/// - Relationship: 2*LCS = |a| + |b| - EditDistance
/// - Counting matches (LCS) is more intuitive than counting errors for autocomplete
/// </summary>
public static class PackedBitParallelLcs
{
    /// <summary>
    /// Maximum total characters that can be packed (64-bit limit minus word separators)
    /// </summary>
    public const int MaxPackedLength = 60;
    
    /// <summary>
    /// Result of packed LCS computation for multiple words.
    /// </summary>
    public readonly struct PackedLcsResult
    {
        /// <summary>LCS values for each packed word (index corresponds to input order)</summary>
        public readonly int[] LcsValues;
        
        /// <summary>Total LCS across all words</summary>
        public readonly int TotalLcs;
        
        /// <summary>Number of words that had any match (LCS > 0)</summary>
        public readonly int MatchedWords;
        
        public PackedLcsResult(int[] lcsValues)
        {
            LcsValues = lcsValues;
            TotalLcs = 0;
            MatchedWords = 0;
            foreach (int lcs in lcsValues)
            {
                TotalLcs += lcs;
                if (lcs > 0) MatchedWords++;
            }
        }
    }
    
    /// <summary>
    /// Computes LCS for multiple query words against a document in a single pass.
    /// 
    /// Time: O(n) where n = document length
    /// Space: O(|Σ|) where Σ is alphabet (256 for ASCII)
    /// 
    /// Example: For query words ["uni", "vers", "ity"] and document "university"
    /// Returns LCS values [3, 4, 3] = all characters matched
    /// </summary>
    /// <param name="queryWords">Query words to pack and match</param>
    /// <param name="document">Document to search in</param>
    /// <returns>LCS value for each query word</returns>
    public static PackedLcsResult ComputePackedLcs(ReadOnlySpan<string> queryWords, ReadOnlySpan<char> document)
    {
        if (queryWords.Length == 0 || document.IsEmpty)
            return new PackedLcsResult(new int[queryWords.Length]);
        
        // Calculate total packed length and validate
        int totalLen = 0;
        foreach (var word in queryWords)
        {
            if (word != null)
                totalLen += word.Length;
        }
        
        // If too long for packing, fall back to sequential computation
        if (totalLen > MaxPackedLength)
        {
            return ComputeSequentialLcs(queryWords, document);
        }
        
        // Build packed character map and word boundary mask
        Span<ulong> charMap = stackalloc ulong[256];
        charMap.Clear();
        
        // Track word boundaries: ZM has 0 at each word's last bit, 1 elsewhere
        ulong zm = 0;
        int[] wordLengths = new int[queryWords.Length];
        int[] wordOffsets = new int[queryWords.Length];
        int bitPos = 0;
        
        for (int w = 0; w < queryWords.Length; w++)
        {
            string word = queryWords[w];
            if (word == null || word.Length == 0)
                continue;
            
            wordOffsets[w] = bitPos;
            wordLengths[w] = word.Length;
            
            // Map each character position to its bit
            for (int i = 0; i < word.Length; i++)
            {
                char c = char.ToLowerInvariant(word[i]);
                if (c < 256)
                    charMap[c] |= 1UL << bitPos;
                bitPos++;
            }
            
            // Set boundary mask (0 at word boundary, 1 elsewhere)
            zm |= ((1UL << word.Length) - 1) << wordOffsets[w];
            // Clear the last bit of this word's region (word boundary)
            zm &= ~(1UL << (bitPos - 1));
        }
        
        // Add back boundary bits properly: ZM should have 1s everywhere except word boundaries
        // Word boundaries are the last bit of each word
        zm = 0;
        for (int w = 0; w < queryWords.Length; w++)
        {
            if (wordLengths[w] == 0) continue;
            
            // All bits for this word except the last one
            if (wordLengths[w] > 1)
            {
                ulong wordBits = ((1UL << (wordLengths[w] - 1)) - 1) << wordOffsets[w];
                zm |= wordBits;
            }
        }
        
        ulong mask = (1UL << bitPos) - 1;
        ulong S = mask;
        
        // Process document character by character
        // Modified algorithm from Hyyrö 2006 that prevents carry across word boundaries
        foreach (char ch in document)
        {
            char c = char.ToLowerInvariant(ch);
            ulong charMask = c < 256 ? charMap[c] : 0;
            
            ulong U = S & charMask;
            // Modified addition: (S & ZM) + (U & ZM) prevents carry across boundaries
            S = ((S & zm) + (U & zm)) | (S - U);
        }
        
        // Extract LCS for each word by counting 0 bits in each word's region
        int[] results = new int[queryWords.Length];
        S = ~S & mask; // Invert: 1 bits now represent matches
        
        for (int w = 0; w < queryWords.Length; w++)
        {
            if (wordLengths[w] == 0)
            {
                results[w] = 0;
                continue;
            }
            
            // Extract bits for this word
            ulong wordMask = ((1UL << wordLengths[w]) - 1) << wordOffsets[w];
            ulong wordBits = (S & wordMask) >> wordOffsets[w];
            results[w] = BitOperations.PopCount(wordBits);
        }
        
        return new PackedLcsResult(results);
    }
    
    /// <summary>
    /// Fallback sequential computation for when packed approach exceeds limits.
    /// </summary>
    private static PackedLcsResult ComputeSequentialLcs(ReadOnlySpan<string> queryWords, ReadOnlySpan<char> document)
    {
        int[] results = new int[queryWords.Length];
        
        for (int i = 0; i < queryWords.Length; i++)
        {
            string word = queryWords[i];
            if (word != null && word.Length > 0)
            {
                results[i] = AutocompleteScoring.ComputeLcsLength(word.AsSpan(), document);
            }
        }
        
        return new PackedLcsResult(results);
    }
    
    /// <summary>
    /// Computes Jaro-like similarity score using packed LCS.
    /// 
    /// Score formula (from FuzzySearch.js, mathematically principled):
    /// score = 0.5 * m * (m/|query| + m/|doc|) + prefix_bonus
    /// 
    /// Where m = LCS length. This formula:
    /// 1. Normalizes by both lengths (information-theoretic)
    /// 2. Quadratic in matches (specificity weighting)
    /// 3. No arbitrary tuning constants
    /// </summary>
    /// <param name="queryWords">Query words</param>
    /// <param name="document">Document text</param>
    /// <param name="prefixBonus">Bonus per common prefix character (default 0.1)</param>
    /// <returns>Combined similarity score in [0, 1]</returns>
    public static float ComputePackedJaroScore(
        ReadOnlySpan<string> queryWords, 
        ReadOnlySpan<char> document,
        float prefixBonus = 0.1f)
    {
        if (queryWords.Length == 0 || document.IsEmpty)
            return 0f;
        
        var lcsResult = ComputePackedLcs(queryWords, document);
        if (lcsResult.TotalLcs == 0)
            return 0f;
        
        // Calculate total query length
        int totalQueryLen = 0;
        foreach (var word in queryWords)
        {
            if (word != null)
                totalQueryLen += word.Length;
        }
        
        if (totalQueryLen == 0)
            return 0f;
        
        int docLen = document.Length;
        float m = lcsResult.TotalLcs;
        
        // Jaro-like formula: considers coverage of both strings
        float coverage = m / totalQueryLen + m / docLen;
        float baseScore = 0.5f * m * coverage;
        
        // Prefix bonus (Winkler-style) - count common prefix chars
        int prefixLen = 0;
        int qi = 0, di = 0;
        foreach (var word in queryWords)
        {
            if (word == null) continue;
            foreach (char c in word)
            {
                if (di < docLen && char.ToLowerInvariant(c) == char.ToLowerInvariant(document[di]))
                {
                    prefixLen++;
                    di++;
                }
                else
                {
                    goto done;
                }
                qi++;
            }
        }
        done:
        
        float prefixScore = prefixBonus * Math.Min(prefixLen, 4); // Cap at 4 like Jaro-Winkler
        
        // Normalize
        float maxScore = Math.Min(totalQueryLen, docLen) + prefixBonus * 4;
        return Math.Clamp((baseScore + prefixScore) / maxScore, 0f, 1f);
    }
    
    /// <summary>
    /// Computes multi-word autocomplete score with free word order.
    /// 
    /// Based on FuzzySearch.js approach:
    /// - Each query word can match any position in document
    /// - Order bonus for properly ordered matches
    /// - Position bonus for matches at word boundaries
    /// </summary>
    public static float ComputeAutocompleteScore(
        ReadOnlySpan<string> queryWords,
        ReadOnlySpan<char> document,
        float tokenOrderBonus = 2.0f,
        float positionDecay = 0.7f)
    {
        if (queryWords.Length == 0 || document.IsEmpty)
            return 0f;
        
        var lcsResult = ComputePackedLcs(queryWords, document);
        if (lcsResult.MatchedWords == 0)
            return 0f;
        
        float score = 0f;
        int totalQueryLen = 0;
        
        // Score each word with position-based weighting
        for (int i = 0; i < queryWords.Length; i++)
        {
            string word = queryWords[i];
            if (word == null || word.Length == 0)
                continue;
            
            totalQueryLen += word.Length;
            int lcs = lcsResult.LcsValues[i];
            
            if (lcs > 0)
            {
                // Jaro-like score for this word
                float wordCoverage = (float)lcs / word.Length + (float)lcs / document.Length;
                float wordScore = 0.5f * lcs * wordCoverage;
                
                // Position bonus: exponential decay (first word worth more)
                float posBonus = 1.0f + MathF.Pow(positionDecay, i);
                
                score += wordScore * posBonus;
            }
        }
        
        // Token order bonus: consecutive tokens in order get bonus
        float orderBonus = 0f;
        if (queryWords.Length > 1)
        {
            int orderedPairs = 0;
            int lastMatchPos = -1;
            
            for (int i = 0; i < queryWords.Length; i++)
            {
                string word = queryWords[i];
                if (word == null || lcsResult.LcsValues[i] == 0)
                    continue;
                
                // Find approximate position in document
                int pos = FindApproximatePosition(word.AsSpan(), document);
                if (pos >= 0)
                {
                    if (lastMatchPos >= 0 && pos > lastMatchPos)
                        orderedPairs++;
                    lastMatchPos = pos;
                }
            }
            
            orderBonus = orderedPairs * tokenOrderBonus;
        }
        
        return Math.Clamp((score + orderBonus) / Math.Max(totalQueryLen, 1), 0f, 1f);
    }
    
    /// <summary>
    /// Finds approximate position of a pattern in document using first match.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindApproximatePosition(ReadOnlySpan<char> pattern, ReadOnlySpan<char> document)
    {
        if (pattern.IsEmpty)
            return -1;
        
        char first = char.ToLowerInvariant(pattern[0]);
        for (int i = 0; i < document.Length; i++)
        {
            if (char.ToLowerInvariant(document[i]) == first)
                return i;
        }
        return -1;
    }
}

