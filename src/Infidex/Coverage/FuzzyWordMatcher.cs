using Infidex.Metrics;

namespace Infidex.Coverage;

internal static class FuzzyWordMatcher
{
    // Default parameters for the probabilistic edit-distance model.
    // These capture a "human-typo-ish" regime:
    // - p: per-character error probability (~3–5%)
    // - alpha: tail probability for edits beyond the allowed threshold
    private const double DefaultErrorProbability = 0.04;
    private const double DefaultTailProbability = 0.01;

    public static void Match(ref MatchState state, CoverageSetup setup)
    {
        int qCount = state.QCount;
        int dCount = state.DCount;

        int maxQueryLength = 0;
        for (int i = 0; i < qCount; i++)
            if (state.QActive[i] && state.QueryTokens[i].Length > maxQueryLength) 
                maxQueryLength = state.QueryTokens[i].Length;
        
        if (maxQueryLength == 0) return;

        int maxEditDist;
        
        if (maxQueryLength >= setup.MinLengthTwoTypos) 
            maxEditDist = 2;
        else if (maxQueryLength >= setup.MinLengthOneTypo) 
            maxEditDist = 1;
        else 
            maxEditDist = 0; 

        // Special handling for len=2 queries if default logic forbids typos (maxEditDist=0)
        // If we have very short words (len 2) that are disallowed normal typos, 
        // we conditionally allow 1 typo ONLY if it matches a target of length 3 (Insertion).
        // This supports common cases like "te" -> "the" while avoiding high-noise substitutions like "at" -> "it".
        bool hasSpecialShortWord = (maxQueryLength == 2 && maxEditDist == 0 && setup.NumTypos >= 1);
        if (hasSpecialShortWord)
        {
            maxEditDist = 1;
        }

        // Respect the global cap
        if (maxEditDist > setup.NumTypos)
            maxEditDist = setup.NumTypos;
        
        if (maxEditDist == 0) return;
        
        for (int editDist = 1; editDist <= maxEditDist; editDist++)
        {
            bool anyQ = false;
            for (int i = 0; i < qCount; i++) 
                if (state.QActive[i]) anyQ = true;
            if (!anyQ) break;
            
            for (int i = 0; i < qCount; i++)
            {
                if (!state.QActive[i]) continue;
                StringSlice qSlice = state.QueryTokens[i];
                int qLen = qSlice.Length;
                
                // Skip query tokens that are too short for meaningful fuzzy matching
                if (qLen < setup.MinWordSize) continue;
                
                // Calculate max edits for THIS token
                int tokenMaxEdits = 0;
                if (qLen >= setup.MinLengthTwoTypos) tokenMaxEdits = 2;
                else if (qLen >= setup.MinLengthOneTypo) tokenMaxEdits = 1;
                else tokenMaxEdits = 0;
                
                // Apply special short word logic for individual token
                bool isSpecialShortCase = false;
                if (qLen == 2 && tokenMaxEdits == 0 && setup.NumTypos >= 1)
                {
                    tokenMaxEdits = 1;
                    isSpecialShortCase = true;
                }

                if (tokenMaxEdits > setup.NumTypos) tokenMaxEdits = setup.NumTypos;
                
                if (editDist > tokenMaxEdits) continue;
                
                // For special short case, we only process editDist=1
                if (isSpecialShortCase && editDist != 1) continue;

                // Calculate the valid document word length range for this query token and edit distance
                int minLen = Math.Max(setup.MinWordSize, qLen - editDist);
                int maxLen = Math.Min(setup.LevenshteinMaxWordSize, qLen + editDist);
                if (maxLen > 63) maxLen = 63;
                
                ReadOnlySpan<char> qText = state.QuerySpan.Slice(qSlice.Offset, qSlice.Length);
                
                for (int j = 0; j < dCount; j++)
                {
                    if (!state.DActive[j]) continue;
                    StringSlice dSlice = state.UniqueDocTokens[j];
                    int dLen = dSlice.Length;
                    if (dLen > maxLen || dLen < minLen) continue;
                    
                    ReadOnlySpan<char> dText = state.DocSpan.Slice(dSlice.Offset, dSlice.Length);

                    // Enforce special short word constraints
                    if (isSpecialShortCase)
                    {
                        // Special handling for short words: First character MUST match.
                        // This allows "te" -> "the" (ins) or "te" -> "to" (sub), 
                        // but prevents high-noise matches like "at" -> "cat" (prefix ins) or "at" -> "it" (start sub).
                        if (dText.Length == 0 || char.ToLowerInvariant(dText[0]) != char.ToLowerInvariant(qText[0]))
                            continue;
                    }
                    
                    int dist = LevenshteinDistance.CalculateDamerau(qText, dText, editDist, ignoreCase: true);
                    
                    if (dist <= editDist)
                    {
                        state.WordHits++;
                        state.NumFuzzy += (qLen - dist);
                    
                        state.TermMatchedChars[i] += (qLen - dist);
                        int pos = dSlice.Position;
                        if (state.TermFirstPos[i] == -1 || pos < state.TermFirstPos[i]) 
                            state.TermFirstPos[i] = pos;
                        
                        state.QActive[i] = false;
                        state.DActive[j] = false;
                        break;
                    }
                }
            }
        }
    }

    public static bool AllTermsFullyMatched(ref MatchState state)
    {
        for (int i = 0; i < state.QCount; i++)
        {
            if (state.TermMaxChars[i] > 0 && state.TermMatchedChars[i] < state.TermMaxChars[i])
            {
                return false;
            }
        }
        return true;
    }
}
