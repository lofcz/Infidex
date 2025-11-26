using System.Buffers;
using Infidex.Metrics;

namespace Infidex.Coverage;

internal static class PrefixSuffixMatcher
{
    public static void Match(ref MatchState state)
    {
        int qCount = state.QCount;
        int dCount = state.DCount;

        int[] qIndices = ArrayPool<int>.Shared.Rent(qCount);
        int[] dIndices = ArrayPool<int>.Shared.Rent(dCount);
        
        try
        {
            int activeQCount = 0;
            for (int i = 0; i < qCount; i++) 
                if (state.QActive[i]) qIndices[activeQCount++] = i;
            
            int activeDCount = 0;
            for (int i = 0; i < dCount; i++) 
                if (state.DActive[i]) dIndices[activeDCount++] = i;
            
            SortByLengthDescending(qIndices, activeQCount, state.QueryTokens);
            SortByLengthDescending(dIndices, activeDCount, state.UniqueDocTokens);
            
            // Pass 1: Exact prefix/suffix/contains
            MatchExact(ref state, qIndices, activeQCount, dIndices, activeDCount);
            
            // Pass 2: Fuzzy prefix for remaining
            MatchFuzzyPrefix(ref state, qIndices, activeQCount, dIndices, activeDCount);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(qIndices);
            ArrayPool<int>.Shared.Return(dIndices);
        }
    }

    private static void SortByLengthDescending(int[] indices, int count, Span<StringSlice> tokens)
    {
        for (int i = 1; i < count; i++)
        {
            int currentIdx = indices[i];
            int currentLen = tokens[currentIdx].Length;
            int j = i - 1;
            while (j >= 0 && tokens[indices[j]].Length < currentLen)
            {
                indices[j + 1] = indices[j];
                j--;
            }
            indices[j + 1] = currentIdx;
        }
    }

    private static void MatchExact(
        ref MatchState state,
        int[] qIndices, int activeQCount,
        int[] dIndices, int activeDCount)
    {
        for (int qi = 0; qi < activeQCount; qi++)
        {
            int i = qIndices[qi];
            if (!state.QActive[i]) continue;
            
            StringSlice qSlice = state.QueryTokens[i];
            ReadOnlySpan<char> qText = state.QuerySpan.Slice(qSlice.Offset, qSlice.Length);
            
            for (int di = 0; di < activeDCount; di++)
            {
                int j = dIndices[di];
                if (!state.DActive[j]) continue;
                
                StringSlice dSlice = state.UniqueDocTokens[j];
                if (qSlice.Length == dSlice.Length) continue;
                
                ReadOnlySpan<char> dText = state.DocSpan.Slice(dSlice.Offset, dSlice.Length);
                
                bool isMatch = false;
                bool isPrefix = false;
                double matchScore = 0;
                
                if (qSlice.Length < dSlice.Length)
                {
                    if (dText.StartsWith(qText, StringComparison.OrdinalIgnoreCase))
                    {
                        matchScore = qSlice.Length;
                        isMatch = true;
                        isPrefix = true;
                    }
                    else if (dText.EndsWith(qText, StringComparison.OrdinalIgnoreCase))
                    {
                        matchScore = Math.Max(1, qSlice.Length / 2);
                        isMatch = true;
                    }
                    else if (qSlice.Length >= 4 && dText.Contains(qText, StringComparison.OrdinalIgnoreCase))
                    {
                        matchScore = qSlice.Length * 0.6;
                        isMatch = true;
                    }
                }
                else if (qSlice.Length > dSlice.Length)
                {
                    if (qText.EndsWith(dText, StringComparison.OrdinalIgnoreCase))
                    {
                        matchScore = dSlice.Length;
                        isMatch = true;
                    }
                }
                
                if (isMatch)
                {
                    state.NumPrefixSuffix += matchScore;
                    state.WordHits++;
                    
                    state.TermMatchedChars[i] += (float)matchScore;
                    if (isPrefix) state.TermHasPrefix[i] = true;

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

    private static void MatchFuzzyPrefix(
        ref MatchState state,
        int[] qIndices, int activeQCount,
        int[] dIndices, int activeDCount)
    {
        int qCount = state.QCount;

        for (int qi = 0; qi < activeQCount; qi++)
        {
            int i = qIndices[qi];
            if (!state.QActive[i]) continue;
            
            StringSlice qSlice = state.QueryTokens[i];
            ReadOnlySpan<char> qText = state.QuerySpan.Slice(qSlice.Offset, qSlice.Length);
            
            // Fuzzy matching: requires length >= 4, or >= 2 for the last query term
            if (!(qSlice.Length >= 4 || (i == qCount - 1 && qSlice.Length >= 2)))
                continue;
            
            for (int di = 0; di < activeDCount; di++)
            {
                int j = dIndices[di];
                if (!state.DActive[j]) continue;
                
                StringSlice dSlice = state.UniqueDocTokens[j];
                if (qSlice.Length >= dSlice.Length) continue;
                
                ReadOnlySpan<char> dText = state.DocSpan.Slice(dSlice.Offset, dSlice.Length);
                
                bool isMatch = false;
                double matchScore = 0;
                
                int qLen = qSlice.Length;
                int maxEdits = 1;
                
                int dist = LevenshteinDistance.CalculateDamerau(qText, dText[..qLen], maxEdits, true);
                if (dist <= maxEdits)
                {
                    matchScore = (qLen - dist) * 0.5;
                    if (matchScore < 0.1) matchScore = 0.1;
                    isMatch = true;
                }
                else if (dSlice.Length > qLen)
                {
                    dist = LevenshteinDistance.CalculateDamerau(qText, dText[..(qLen + 1)], maxEdits, true);
                    if (dist <= maxEdits)
                    {
                        matchScore = (qLen - dist) * 0.5;
                        if (matchScore < 0.1) matchScore = 0.1;
                        isMatch = true;
                    }
                    else if (qLen > 1)
                    {
                        dist = LevenshteinDistance.CalculateDamerau(qText, dText[..(qLen - 1)], maxEdits, true);
                        if (dist <= maxEdits)
                        {
                            matchScore = ((qLen - 1) - dist) * 0.5;
                            if (matchScore < 0.1) matchScore = 0.1;
                            isMatch = true;
                        }
                    }
                }
                
                if (isMatch)
                {
                    state.NumPrefixSuffix += matchScore;
                    state.WordHits++;
                    
                    state.TermMatchedChars[i] += (float)matchScore;

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


