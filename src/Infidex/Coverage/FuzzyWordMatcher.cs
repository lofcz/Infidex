using Infidex.Metrics;

namespace Infidex.Coverage;

internal static class FuzzyWordMatcher
{
    public static void Match(ref MatchState state, int minWordSize, int levenshteinMaxWordSize)
    {
        int qCount = state.QCount;
        int dCount = state.DCount;

        int maxQueryLength = 0;
        for (int i = 0; i < qCount; i++)
            if (state.QActive[i] && state.QueryTokens[i].Length > maxQueryLength) 
                maxQueryLength = state.QueryTokens[i].Length;
        
        if (maxQueryLength == 0) return;
        
        double maxRelDist = 0.25;
        int maxEditDist = Math.Max(1, (int)Math.Round(maxQueryLength * maxRelDist));
        
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

