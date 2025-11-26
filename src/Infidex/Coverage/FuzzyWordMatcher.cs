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

    public static void Match(ref MatchState state, int minWordSize, int levenshteinMaxWordSize)
    {
        int qCount = state.QCount;
        int dCount = state.DCount;

        int maxQueryLength = 0;
        for (int i = 0; i < qCount; i++)
            if (state.QActive[i] && state.QueryTokens[i].Length > maxQueryLength) 
                maxQueryLength = state.QueryTokens[i].Length;
        
        if (maxQueryLength == 0) return;

        // Use a principled maximum edit distance based on a simple
        // Binomial(L, p) error model instead of an uncalibrated
        // relative distance knob.
        //
        // We choose the smallest d such that:
        //   Pr[D ≤ d] ≥ 1 - alpha
        //
        // where D ~ Binomial(L, p), p ≈ DefaultErrorProbability and
        // alpha ≈ DefaultTailProbability.
        int maxEditDist = EditDistanceModel.GetMaxEditsForLength(
            maxQueryLength,
            DefaultErrorProbability,
            DefaultTailProbability);

        // Ensure we allow at least one edit for non-empty queries.
        if (maxEditDist < 1)
            maxEditDist = 1;
        
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
                // but allow 2-char tokens since they can fuzzy-match 3-char words (e.g., "te" → "the")
                if (qLen < minWordSize) continue;
                
                // Calculate the valid document word length range for this query token and edit distance
                int minLen = Math.Max(minWordSize, qLen - editDist);
                int maxLen = Math.Min(levenshteinMaxWordSize, qLen + editDist);
                if (maxLen > 63) maxLen = 63;
                
                ReadOnlySpan<char> qText = state.QuerySpan.Slice(qSlice.Offset, qSlice.Length);
                
                for (int j = 0; j < dCount; j++)
                {
                    if (!state.DActive[j]) continue;
                    StringSlice dSlice = state.UniqueDocTokens[j];
                    int dLen = dSlice.Length;
                    if (dLen > maxLen || dLen < minLen) continue;
                    
                    ReadOnlySpan<char> dText = state.DocSpan.Slice(dSlice.Offset, dSlice.Length);
                    
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

