namespace Infidex.Coverage;

internal static class WholeWordMatcher
{
    public static void Match(ref MatchState state)
    {
        int qCount = state.QCount;
        int dCount = state.DCount;
        int pIncrement = qCount > 1 ? 1 : 0;

        for (int i = 0; i < qCount; i++)
        {
            StringSlice qSlice = state.QueryTokens[i];
            
            int matchIndex = -1;
            for (int j = 0; j < dCount; j++)
            {
                if (state.DActive[j])
                {
                    StringSlice dSlice = state.UniqueDocTokens[j];
                    if (dSlice.Hash == qSlice.Hash && dSlice.Length == qSlice.Length)
                    {
                        ReadOnlySpan<char> qText = state.QuerySpan.Slice(qSlice.Offset, qSlice.Length);
                        if (qText.Equals(state.DocSpan.Slice(dSlice.Offset, dSlice.Length), StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndex = j;
                            break;
                        }
                    }
                }
            }
            
            if (matchIndex != -1)
            {
                state.WordHits++;
                state.NumWhole += qSlice.Length;
                
                state.TermMatchedChars[i] += qSlice.Length;
                state.TermHasWhole[i] = true;
                state.TermHasPrefix[i] = true;
                
                int pos = state.UniqueDocTokens[matchIndex].Position;
                if (state.TermFirstPos[i] == -1 || pos < state.TermFirstPos[i]) 
                    state.TermFirstPos[i] = pos;
                
                // Penalty: if doc token at position i doesn't match query token i
                if (dCount > i)
                {
                    StringSlice dSliceI = state.UniqueDocTokens[i];
                    if (dSliceI.Hash != qSlice.Hash || dSliceI.Length != qSlice.Length ||
                        !state.QuerySpan.Slice(qSlice.Offset, qSlice.Length).Equals(
                            state.DocSpan.Slice(dSliceI.Offset, dSliceI.Length), StringComparison.OrdinalIgnoreCase))
                    {
                        state.Penalty++;
                    }
                }
                else
                {
                    state.Penalty++;
                }
                
                if (i < qCount - 1) state.NumWhole += pIncrement;
                
                state.QActive[i] = false;
                state.DActive[matchIndex] = false;
            }
        }
    }
}

