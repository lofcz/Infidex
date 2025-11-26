namespace Infidex.Coverage;

internal static class JoinedWordMatcher
{
    public static void Match(ref MatchState state)
    {
        MatchQueryJoined(ref state);
        MatchDocJoined(ref state);
    }

    private static void MatchQueryJoined(ref MatchState state)
    {
        int qCount = state.QCount;
        int dCount = state.DCount;

        for (int i = 0; i < qCount - 1; i++)
        {
            if (!state.QActive[i] || !state.QActive[i + 1]) continue;
            
            int nextIdx = -1;
            for (int k = i + 1; k < qCount; k++)
            {
                if (state.QActive[k]) { nextIdx = k; break; }
            }
            if (nextIdx == -1) break;
            
            StringSlice q1 = state.QueryTokens[i];
            StringSlice q2 = state.QueryTokens[nextIdx];
            int joinedLen = q1.Length + q2.Length;
            
            int matchIndex = -1;
            for (int j = 0; j < dCount; j++)
            {
                if (state.DActive[j])
                {
                    StringSlice dSlice = state.UniqueDocTokens[j];
                    if (dSlice.Length == joinedLen)
                    {
                        ReadOnlySpan<char> dText = state.DocSpan.Slice(dSlice.Offset, dSlice.Length);
                        ReadOnlySpan<char> q1Text = state.QuerySpan.Slice(q1.Offset, q1.Length);
                        ReadOnlySpan<char> q2Text = state.QuerySpan.Slice(q2.Offset, q2.Length);
                        
                        if (dText.StartsWith(q1Text, StringComparison.OrdinalIgnoreCase) &&
                            dText.EndsWith(q2Text, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndex = j;
                            break;
                        }
                    }
                }
            }
            
            if (matchIndex != -1)
            {
                state.NumJoined += joinedLen;
                state.WordHits += 2;
                
                state.TermMatchedChars[i] += q1.Length;
                state.TermHasJoined[i] = true;
                state.TermHasPrefix[i] = true;
                int pos = state.UniqueDocTokens[matchIndex].Position;
                if (state.TermFirstPos[i] == -1 || pos < state.TermFirstPos[i]) 
                    state.TermFirstPos[i] = pos;
                
                state.TermMatchedChars[nextIdx] += q2.Length;
                state.TermHasJoined[nextIdx] = true;
                if (state.TermFirstPos[nextIdx] == -1 || pos < state.TermFirstPos[nextIdx]) 
                    state.TermFirstPos[nextIdx] = pos;
                
                state.QActive[i] = false;
                state.QActive[nextIdx] = false;
                state.DActive[matchIndex] = false;
            }
        }
    }

    private static void MatchDocJoined(ref MatchState state)
    {
        int qCount = state.QCount;
        int dCount = state.DCount;

        for (int i = 0; i < dCount - 1; i++)
        {
            if (!state.DActive[i]) continue;
            int nextIdx = -1;
            for (int k = i + 1; k < dCount; k++) 
            { 
                if (state.DActive[k]) { nextIdx = k; break; } 
            }
            if (nextIdx == -1) break;
            
            StringSlice d1 = state.UniqueDocTokens[i];
            StringSlice d2 = state.UniqueDocTokens[nextIdx];
            int joinedLen = d1.Length + d2.Length;
            
            int matchIndex = -1;
            for (int j = 0; j < qCount; j++)
            {
                if (state.QActive[j])
                {
                    StringSlice qSlice = state.QueryTokens[j];
                    if (qSlice.Length == joinedLen)
                    {
                        ReadOnlySpan<char> qText = state.QuerySpan.Slice(qSlice.Offset, qSlice.Length);
                        ReadOnlySpan<char> d1Text = state.DocSpan.Slice(d1.Offset, d1.Length);
                        ReadOnlySpan<char> d2Text = state.DocSpan.Slice(d2.Offset, d2.Length);
                        
                        if (qText.StartsWith(d1Text, StringComparison.OrdinalIgnoreCase) &&
                            qText.EndsWith(d2Text, StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndex = j;
                            break;
                        }
                    }
                }
            }
            
            if (matchIndex != -1)
            {
                state.NumJoined += joinedLen;
                state.WordHits += 1;
                
                state.TermMatchedChars[matchIndex] += joinedLen;
                state.TermHasJoined[matchIndex] = true;
                state.TermHasPrefix[matchIndex] = true;
                int pos = d1.Position;
                if (state.TermFirstPos[matchIndex] == -1 || pos < state.TermFirstPos[matchIndex]) 
                    state.TermFirstPos[matchIndex] = pos;
                
                state.QActive[matchIndex] = false;
                state.DActive[i] = false;
                state.DActive[nextIdx] = false;
            }
        }
    }
}




