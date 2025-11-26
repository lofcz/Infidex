namespace Infidex.Coverage;

internal static class CoverageScorer
{
    public static CoverageResult CalculateFinalScore(
        ref MatchState state,
        int queryLen,
        double lcsSum,
        bool coverWholeQuery,
        out int termsWithAnyMatch,
        out int termsFullyMatched,
        out int termsStrictMatched,
        out int termsPrefixMatched,
        out int longestPrefixRun,
        out int suffixPrefixRun,
        out int phraseSpan,
        out int precedingStrictCount,
        out bool lastTokenHasPrefix)
    {
        termsWithAnyMatch = 0;
        termsFullyMatched = 0;
        termsStrictMatched = 0;
        termsPrefixMatched = 0;
        longestPrefixRun = 0;
        suffixPrefixRun = 0;
        phraseSpan = 0;
        precedingStrictCount = 0;
        lastTokenHasPrefix = false;

        int qCount = state.QCount;

        if (!coverWholeQuery) lcsSum = 0.0;
        
        double num11 = state.NumJoined + state.NumWhole + state.NumFuzzy + state.NumPrefixSuffix - state.Penalty;
        if (num11 == 0.0 && lcsSum > 2.0) num11 = lcsSum - 2.0;
        
        byte coverageScore = (byte)Math.Min(num11 / queryLen * 255.0, 255.0);
        
        float sumCi = 0f;
        float weightedCoverageSum = 0f;
        float totalWeight = 0f;
        float idfWeightedSum = 0f;
        float totalIdf = 0f;
        float missingIdf = 0f;
        int totalTermChars = 0;
        float lastTermCi = 0f;
        float lastTermIdf = 0f;
        int firstMatchIndex = -1;
        int minPos = int.MaxValue;
        int maxPos = -1;

        for (int i = 0; i < qCount; i++)
        {
            if (state.TermMaxChars[i] <= 0) continue;
            
            float ci = Math.Min(1.0f, state.TermMatchedChars[i] / state.TermMaxChars[i]);
            sumCi += ci;
            if (ci > 0) termsWithAnyMatch++;
            
            totalTermChars += state.TermMaxChars[i];
            
            // Weight by term length (longer terms are more informative)
            int termLen = state.TermMaxChars[i];
            float termWeight = termLen;
            totalWeight += termWeight;
            weightedCoverageSum += ci * termWeight;
            
            // IDF-based weighting: use information content instead of raw length
            float idf = state.TermIdf[i];
            totalIdf += idf;
            idfWeightedSum += ci * idf;
            if (ci < 1.0f)
            {
                missingIdf += (1.0f - ci) * idf;
            }
            
            if (i == qCount - 1)
            {
                lastTermCi = ci;
                lastTermIdf = idf;
            }
            
            bool isFullyMatched = state.TermMatchedChars[i] >= (state.TermMaxChars[i] - 0.01f);
            if (isFullyMatched) termsFullyMatched++;
            
            bool isStrict = (state.TermHasWhole[i] || state.TermHasJoined[i]) && isFullyMatched;
            if (isStrict) termsStrictMatched++;

            if (state.TermHasPrefix[i]) termsPrefixMatched++;
            
            if (state.TermFirstPos[i] >= 0)
            {
                if (firstMatchIndex == -1 || state.TermFirstPos[i] < firstMatchIndex) 
                    firstMatchIndex = state.TermFirstPos[i];

                if (state.TermFirstPos[i] < minPos) minPos = state.TermFirstPos[i];
                if (state.TermFirstPos[i] > maxPos) maxPos = state.TermFirstPos[i];
            }
        }
        
        // Normalize weighted coverage by total weight
        float normalizedWeightedCoverage = totalWeight > 0f ? weightedCoverageSum / totalWeight : 0f;
        
        // Normalize IDF-weighted coverage
        float idfCoverage = totalIdf > 0f ? idfWeightedSum / totalIdf : 0f;
        
        // Detect if last term is likely type-ahead using information share
        // A term is type-ahead if it contributes less information than we would
        // expect from an "average" term in the query. We approximate this by comparing
        // the last term's IDF share to 1/(q+1), where q is the number of unique query terms.
        bool lastTermIsTypeAhead = false;
        if (qCount > 0 && totalIdf > 0f)
        {
            float idfShare = lastTermIdf / totalIdf;
            float idfThreshold = 1f / (qCount + 1);
            lastTermIsTypeAhead = idfShare <= idfThreshold;
        }

        // Single-term LCS boost
        if (qCount == 1 && queryLen > 0 && lcsSum > 0.0)
        {
            float ciLcs = (float)Math.Min(1.0, lcsSum / queryLen);
            if (ciLcs > sumCi)
            {
                sumCi = ciLcs;
            }
        }

        // Longest consecutive prefix run
        int currentRun = 0;
        for (int i = 0; i < qCount; i++)
        {
            bool prefixHit = state.TermHasPrefix[i] && state.TermMaxChars[i] > 0 && state.TermMatchedChars[i] > 0;
            if (prefixHit)
            {
                currentRun++;
                if (currentRun > longestPrefixRun) longestPrefixRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }

        // Suffix run
        int suffixRun = 0;
        for (int i = qCount - 1; i >= 0; i--)
        {
            bool prefixHit = state.TermHasPrefix[i] && state.TermMaxChars[i] > 0 && state.TermMatchedChars[i] > 0;
            if (prefixHit)
            {
                suffixRun++;
            }
            else
            {
                break;
            }
        }
        suffixPrefixRun = suffixRun;

        // Phrase span
        if (minPos != int.MaxValue && maxPos >= minPos && termsWithAnyMatch >= 2)
        {
            phraseSpan = (maxPos - minPos) + 1;
        }

        // Prefix-last semantics
        if (qCount >= 1)
        {
            int lastIdx = qCount - 1;
            lastTokenHasPrefix = state.TermHasPrefix[lastIdx] && state.TermMatchedChars[lastIdx] > 0;

            if (qCount >= 2)
            {
                for (int i = 0; i < qCount - 1; i++)
                {
                    bool isStrict = (state.TermHasWhole[i] || state.TermHasJoined[i]) && 
                                    state.TermMatchedChars[i] >= (state.TermMaxChars[i] - 0.01f);
                    if (isStrict)
                    {
                        precedingStrictCount++;
                    }
                }
            }
        }
        
        return new CoverageResult(coverageScore, qCount, firstMatchIndex, sumCi, lastTermCi, normalizedWeightedCoverage, lastTermIsTypeAhead, idfCoverage, totalIdf, missingIdf);
    }

    public static ushort CalculateRankedScore(
        CoverageResult result,
        int docTokenCount,
        int wordHits,
        int termsWithAnyMatch,
        int termsFullyMatched,
        int termsStrictMatched,
        int termsPrefixMatched,
        byte baseTfidfScore)
    {
        int termsCount = result.TermsCount;
        byte coverageScore = result.CoverageScore;
        int firstMatchIndex = result.FirstMatchIndex;
        float sumCi = result.SumCi;

        float coordCoverage = termsCount > 0 ? sumCi / termsCount : 0;
        float termCompleteness = termsCount > 0 ? (float)termsFullyMatched / termsCount : 0;
        float combinedCoverage = (0.5f * coordCoverage) + (0.5f * termCompleteness);
        int coverageTier = (int)Math.Clamp(combinedCoverage * 63f, 0f, 63f);
        byte baseScore = coverageScore <= baseTfidfScore ? baseTfidfScore : coverageScore;
        float finalQ = baseScore / 255f;
        int finalQualityTier = (int)Math.Clamp(finalQ * 3f, 0f, 3f);
        
        byte baseFinal = (byte)((coverageTier << 2) | finalQualityTier);
        
        int precedence = 0;
        bool allTermsFound = termsWithAnyMatch == termsCount;
        bool isFullyMatched = termsFullyMatched == termsCount;
        bool isStrictWhole = termsStrictMatched == termsCount;
        bool isPrefixMatched = termsPrefixMatched == termsCount;
        
        if (allTermsFound) precedence |= 128;
        if (isFullyMatched) precedence |= 64;
        
        bool isPerfectDoc = (docTokenCount > 0 && wordHits == docTokenCount && allTermsFound);
        
        if (termsCount == 1)
        {
            if (isStrictWhole) precedence |= 32;
            if (isPerfectDoc) precedence |= 16;
        }
        else
        {
            if (isPerfectDoc) precedence |= 32;
            if (isStrictWhole) precedence |= 16;
        }
        
        if (firstMatchIndex == 0) precedence |= 8;
        if (isPrefixMatched) precedence |= 4;
        
        return (ushort)((precedence << 8) | baseFinal);
    }
}


