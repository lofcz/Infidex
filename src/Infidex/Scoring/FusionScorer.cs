using Infidex.Coverage;
using Infidex.Metrics;

namespace Infidex.Scoring;

/// <summary>
/// Pure functions for calculating fusion scores combining coverage and relevancy signals.
/// </summary>
internal static class FusionScorer
{
    internal static bool EnableDebugLogging = true;

    private const float IntentBonusPerSignal = 0.15f;

    /// <summary>
    /// Calculates the fusion score for a (query, document) pair using precomputed signals.
    /// Returns (score, tiebreaker) where score is a float combining precedence (integer part) and semantic (fractional).
    /// </summary>
    public static (float score, byte tiebreaker) Calculate(
        string queryText,
        string documentText,
        CoverageFeatures features,
        float bm25Score,
        int minStemLength,
        char[] delimiters)
    {
        int n = features.FusionSignals.UnfilteredQueryTokenCount > 0 
            ? features.FusionSignals.UnfilteredQueryTokenCount 
            : features.TermsCount;
        bool isSingleTerm = n <= 1;

        bool isComplete = features.TermsCount > 0 && features.TermsWithAnyMatch == features.TermsCount;
        bool isClean = features.TermsCount > 0 && features.TermsPrefixMatched == features.TermsCount;
        bool isExact = features.TermsCount > 0 && features.TermsStrictMatched == features.TermsCount;
        bool startsAtBeginning = features.FirstMatchIndex == 0;
        bool lexicalPrefixLast = features.FusionSignals.LexicalPrefixLast;
        int precedingTerms = Math.Max(0, features.TermsCount - 1);
        bool coveragePrefixLast = features.TermsCount >= 1 &&
                                  features.PrecedingStrictCount == precedingTerms &&
                                  features.LastTokenHasPrefix;
        bool isPrefixLastStrong = lexicalPrefixLast && coveragePrefixLast;
        bool isPerfectDoc = features.FusionSignals.IsPerfectDocLexical;

        // Calculate Precedence (Integer component of the score)
        // We use the same bit-logic as before to define tiers, but result is a float
        int precedence = 0;
        
        // PRECEDENCE BIT STRUCTURE
        // Bits 9-8:     COVERAGE TIER (multi-term only)
        // Bit 7 (128):  EXACT PREFIX or SUBSET MATCH
        // Bit 6 (64):   HIGH-INFO TERM DOMINANCE
        // Bits 5-0:     Quality signals
        
        int coverageTier = 0;
        if (!isSingleTerm && features.TermsCount > 0)
        {
            int matched = features.TermsWithAnyMatch;
            int total = features.TermsCount;
            if (matched >= total)
                coverageTier = 3;
            else if (matched == total - 1)
                coverageTier = 2;
            else if (matched * 2 >= total)
                coverageTier = 1;
            else
                coverageTier = 0;
        }
        
        if (!isSingleTerm && coverageTier > 0)
        {
            precedence |= (coverageTier & 0b11) << 8;
        }
        
        bool isExactPrefix = !isSingleTerm && isClean && startsAtBeginning && lexicalPrefixLast && isComplete;
        bool isSubsetMatch = !isSingleTerm && features.DocTokenCount > 0 && features.WordHits == features.DocTokenCount;
        
        if (isExactPrefix || isSubsetMatch)
        {
            precedence |= 128;
        }
        
        // High-info term dominance logic
        int dominantTermIndex = -1;
        float avgIdfForQuery = 0f;
        if (!isSingleTerm && features.TermsCount >= 2)
        {
            bool hasDominantTerm = false;
            
            if (features.TermIdf != null && 
                features.TermCi != null &&
                features.TermIdf.Length == features.TermsCount &&
                features.TermCi.Length == features.TermsCount)
            {
                avgIdfForQuery = features.TotalIdf > 0f && features.TermsCount > 0
                    ? features.TotalIdf / features.TermsCount
                    : 0f;

                for (int candidateIdx = 0; candidateIdx < features.TermsCount; candidateIdx++)
                {
                    float candidatePower = features.TermIdf[candidateIdx] * features.TermCi[candidateIdx];
                    
                    if (features.TermCi[candidateIdx] <= 0.1f ||
                        features.TermIdf[candidateIdx] <= 0f ||
                        features.TermIdf[candidateIdx] < avgIdfForQuery)
                        continue;
                    
                    float otherTermsPower = 0f;
                    for (int i = 0; i < features.TermsCount; i++)
                    {
                        if (i != candidateIdx)
                        {
                            otherTermsPower += features.TermIdf[i] * features.TermCi[i];
                        }
                    }
                    
                    if (candidatePower > otherTermsPower)
                    {
                        hasDominantTerm = true;
                        dominantTermIndex = candidateIdx;
                        break;
                    }
                }
            }
            
            bool hasStrongAnchor = features.FusionSignals.HasAnchorStem &&
                                   features.TermIdf != null &&
                                   features.TermIdf.Length >= 1 &&
                                   features.TermIdf[0] >= avgIdfForQuery;
            
            if (hasDominantTerm || hasStrongAnchor)
            {
                precedence |= 64;
            }

            int unmatchedTerms = features.TermsCount - features.TermsWithAnyMatch;
            if (hasDominantTerm && unmatchedTerms == 1)
            {
                precedence |= 8;
            }
        }

        if (isSingleTerm)
        {
            if (isComplete) precedence |= (1 << 9);
            if (isClean && features.TermsCount > 0) precedence |= (1 << 8);
            precedence |= ComputeSingleTermPrecedence(isExact, isClean, startsAtBeginning, isComplete);
        }
        else
        {
            int multiTermPrec = ComputeMultiTermPrecedence(
                isPrefixLastStrong, lexicalPrefixLast, isPerfectDoc, features, n, startsAtBeginning, isClean);
            
            // Special case for single-char last term boost
            if (features.FusionSignals.UnfilteredQueryTokenCount > features.TermsCount)
            {
               multiTermPrec += features.FusionSignals.SingleCharLastTokenBoost;
            }
            
            precedence |= multiTermPrec;
        }

        float coverageRatio = features.TermsCount > 0
            ? (float)features.TermsWithAnyMatch / features.TermsCount
            : 0f;

        bool hasPartialCoverage = coverageRatio > 0f && coverageRatio < 1f;

        if (hasPartialCoverage && n >= 2)
        {
            bool hasStemEvidence = features.FusionSignals.HasStemEvidence;
            
            if (hasStemEvidence)
            {
                precedence |= 8;
            }
            else
            {
                int unmatchedTerms = features.TermsCount - features.TermsWithAnyMatch;
                bool lastTermMatched = features.LastTokenHasPrefix ||
                                      (features.TermsCount > 0 && features.TermsWithAnyMatch == features.TermsCount);
                
                bool canBoost = (lastTermMatched || !features.LastTermIsTypeAhead) &&
                                features.TotalIdf > 0f;
                
                if (unmatchedTerms == 1 && canBoost)
                {
                    float missingInfoRatio = features.MissingIdf / features.TotalIdf;
                    float termGap = 1f - coverageRatio;
                    
                    if (missingInfoRatio < termGap)
                    {
                        precedence |= 8;
                    }
                }
            }
        }

        float semantic = ComputeSemanticScore(
            queryText, features, isSingleTerm, bm25Score, coverageRatio);

        // Clamp semantic to 0-0.999 to ensure it acts as a fractional tiebreaker within the precedence tier
        semantic = Math.Clamp(semantic, 0f, 0.999f);

        byte tiebreaker = 0;
        if (n >= 2 && documentText.Length > 0)
        {
            float focus = MathF.Min(1f, (float)queryText.Length / documentText.Length);
            tiebreaker = (byte)(focus * 255f);
        }
        
        // Final Score = Precedence (Integer) + Semantic (Fraction)
        float finalScore = (float)precedence + semantic;
        
        // currently not needed
        if (false && EnableDebugLogging)
        {
            LogExplanation(
                queryText,
                documentText,
                features,
                precedence,
                semantic,
                tiebreaker,
                coverageRatio,
                dominantTermIndex,
                finalScore);
        }
        
        return (finalScore, tiebreaker);
    }
    
    private static void LogExplanation(
        string queryText,
        string documentText,
        CoverageFeatures features,
        int precedence,
        float semantic,
        byte tiebreaker,
        float coverageRatio,
        int dominantTermIndex,
        float finalScore)
    {
        string docPreview = documentText.Length <= 120
            ? documentText
            : documentText[..117] + "...";

        if (EnableDebugLogging)
        {
            Console.WriteLine("[EXPLAIN] FusionScorer");
            Console.WriteLine($"  query=\"{queryText}\"");
            Console.WriteLine($"  doc=\"{docPreview}\"");
            Console.WriteLine($"  precedence={precedence}, semantic={semantic:F4}, finalScore={finalScore:F4}");
        }
    }

    private static int ComputeSingleTermTier(bool isExact, bool isClean, bool startsAtBeginning, bool isComplete)
    {
        if (!isComplete) return 0;

        if (startsAtBeginning)
        {
            if (isExact) return 4;
            if (isClean) return 3;
        }
        else
        {
            if (isExact) return 2;
            if (isClean) return 1;
        }

        return 0;
    }

    private static int ComputeSingleTermPrecedence(bool isExact, bool isClean, bool startsAtBeginning, bool isComplete)
    {
        int tier = ComputeSingleTermTier(isExact, isClean, startsAtBeginning, isComplete);
        return tier << 3;
    }

    private static int ComputeMultiTermTier(
        bool isPrefixLastStrong,
        bool lexicalPrefixLast,
        bool isPerfectDoc,
        bool hasAnchorWithRun)
    {
        if (isPrefixLastStrong) return 3;
        if (lexicalPrefixLast) return 2;
        if (isPerfectDoc || hasAnchorWithRun) return 1;
        return 0;
    }

    private static int ComputeMultiTermPrecedence(
        bool isPrefixLastStrong,
        bool lexicalPrefixLast,
        bool isPerfectDoc,
        CoverageFeatures features,
        int queryTermCount,
        bool startsAtBeginning,
        bool isClean)
    {
        bool hasAnchorWithRun = features.FusionSignals.HasAnchorStem && features.LongestPrefixRun >= 2;
        int tier = ComputeMultiTermTier(isPrefixLastStrong, lexicalPrefixLast, isPerfectDoc, hasAnchorWithRun);
        return tier;
    }
    
    private static float ComputeSemanticScore(
        string queryText,
        CoverageFeatures features,
        bool isSingleTerm,
        float bm25Score,
        float coverageRatio)
    {
        float avgCi = features.TermsCount > 0 ? features.SumCi / features.TermsCount : 0f;
        float semantic;
        
        bool hasPartialCoverage = coverageRatio is > 0f and < 1f;

        if (isSingleTerm)
        {
            float lexicalSim = features.FusionSignals.SingleTermLexicalSim / 255f;
            semantic = (avgCi + lexicalSim) / 2f;
        }
        else if (features.DocTokenCount == 0)
        {
            semantic = avgCi;
        }
        else
        {
            int unmatchedTerms = features.TermsCount - features.TermsWithAnyMatch;
            bool lastTermMatched = features.LastTokenHasPrefix || 
                                  (features.TermsCount > 0 && features.TermsWithAnyMatch == features.TermsCount);
            bool canUseIdf = (lastTermMatched || !features.LastTermIsTypeAhead) && 
                            features.TotalIdf > 0f;
            
            bool useIdfCoverage = hasPartialCoverage && unmatchedTerms == 1 && canUseIdf &&
                                 features.IdfCoverage > coverageRatio;
            
            float baseCoverage = useIdfCoverage ? features.IdfCoverage : avgCi;
                
            float density = (float)features.WordHits / features.DocTokenCount;
            semantic = baseCoverage * density;
            semantic = ApplyIntentBonus(semantic, features);
            semantic = ApplyTrailingTermBonus(semantic, features);
        }

        float coverageGap = 1f - coverageRatio;

        if (hasPartialCoverage && bm25Score >= coverageGap)
        {
            semantic = coverageRatio * semantic + coverageGap * bm25Score;
        }

        return semantic;
    }

    private static float ApplyIntentBonus(
        float semantic,
        CoverageFeatures features)
    {
        if (features.TermsCount < 3)
            return semantic;

        bool hasSuffixPhrase = features.SuffixPrefixRun >= 2;
        bool hasAnchorStem = features.FusionSignals.HasAnchorStem;

        int signalCount = (hasAnchorStem ? 1 : 0) + (hasSuffixPhrase ? 1 : 0);
        if (signalCount > 0)
        {
            float bonus = IntentBonusPerSignal * signalCount;
            semantic = MathF.Min(1f, semantic + bonus);
        }

        return semantic;
    }

    private static float ApplyTrailingTermBonus(float semantic, CoverageFeatures features)
    {
        if (features.TermsCount < 2)
            return semantic;
        
        float matchDensity = features.FusionSignals.TrailingMatchDensity / 255f;
        if (matchDensity > 0f)
        {
            float headroom = 1f - semantic;
            semantic += headroom * matchDensity;
        }

        return semantic;
    }
}
