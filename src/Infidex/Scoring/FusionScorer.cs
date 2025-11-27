using Infidex.Coverage;
using Infidex.Metrics;

namespace Infidex.Scoring;

/// <summary>
/// Pure functions for calculating fusion scores combining coverage and relevancy signals.
/// </summary>
internal static class FusionScorer
{
    // When enabled, FusionScorer will emit detailed explanation logs to the console
    // for each scored (query, document) pair. This is intentionally coarse-grained
    // and should only be turned on in tests or diagnostics.
    internal static bool EnableDebugLogging = false;

    private const float IntentBonusPerSignal = 0.15f;

    /// <summary>
    /// Calculates the fusion score for a (query, document) pair using precomputed signals.
    /// Returns (score, tiebreaker) where score encodes precedence (high byte) and semantic (low byte).
    /// </summary>
    /// <remarks>
    /// This is the Lucene-style approach: all string operations happen in the coverage layer,
    /// fusion scoring only performs numeric operations on precomputed flags and bytes.
    /// </remarks>
    public static (ushort score, byte tiebreaker) Calculate(
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

        int precedence = 0;
        
        // PRECEDENCE BIT STRUCTURE (10 bits total, 0-1023):
        // Bits 9-8:     COVERAGE TIER (multi-term only)
        //               3 → all terms matched
        //               2 → all but one term matched
        //               1 → at least half of terms matched
        //               0 → less than half of terms matched
        // Bit 7 (128):  EXACT PREFIX or SUBSET MATCH (multi-term only)
        // Bit 6 (64):   HIGH-INFO TERM DOMINANCE / STRONG ANCHOR (multi-term only)
        // Bits 5-0:     Quality signals (phrase runs, tier, matching quality) - 64 values
        
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
            precedence |= (coverageTier & 0b11) << 8; // Bits 8-9 for multi-term coverage tier
        }
        
        // EXACT PREFIX BOOST (bit 5): 
        // Conditions (ALL must be true):
        // 1. Multi-term query (!isSingleTerm) - single-term uses different precedence rules
        // 2. Clean match (isClean) - all terms are prefix matches, not fuzzy
        // 3. Starts at beginning (startsAtBeginning) - first match at position 0
        // 4. Lexical prefix-last (lexicalPrefixLast) - all preceding exact + last is prefix
        // 5. Complete coverage (isComplete) - all query terms found
        bool isExactPrefix = !isSingleTerm && isClean && startsAtBeginning && lexicalPrefixLast && isComplete;

        // SUBSET MATCH BOOST (also bit 5):
        // If the query contains ALL the tokens in the document (density = 1.0),
        // this is a very strong signal (Subset Match).
        bool isSubsetMatch = !isSingleTerm && features.DocTokenCount > 0 && features.WordHits == features.DocTokenCount;
        
        if (isExactPrefix || isSubsetMatch)
        {
            precedence |= 128;  // Bit 7: EXACT PREFIX or SUBSET MATCH
        }
        
        // HIGH-INFO TERM DOMINANCE / STRONG ANCHOR (bit 4):
        //
        // Strong rule for multi-term queries with information-theoretic grounding:
        // Condition 1: ANY single term's discriminative power > sum of all other terms' power
        //   where power[i] = TermIdf[i] * TermCoverage[i]
        // OR
        // Condition 2: First token provides a distinctive anchor stem that appears in the doc
        int dominantTermIndex = -1;
        float avgIdfForQuery = 0f;
        if (!isSingleTerm && features.TermsCount >= 2)
        {
            bool hasDominantTerm = false;
            
            // Use clean word-level IDF if available
            if (features.TermIdf != null && 
                features.TermCi != null &&
                features.TermIdf.Length == features.TermsCount &&
                features.TermCi.Length == features.TermsCount)
            {
                // Compute average per-term IDF so we can distinguish genuinely
                // informative terms from generic ones
                avgIdfForQuery = features.TotalIdf > 0f && features.TermsCount > 0
                    ? features.TotalIdf / features.TermsCount
                    : 0f;

                // Check each term: does its power exceed all others combined?
                for (int candidateIdx = 0; candidateIdx < features.TermsCount; candidateIdx++)
                {
                    float candidatePower = features.TermIdf[candidateIdx] * features.TermCi[candidateIdx];
                    
                    // Only consider terms with meaningful coverage (Ci > 0.1) and some IDF
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
                    
                    // rule: this term's power exceeds all others combined
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
                precedence |= 64;  // Bit 6: HIGH-INFO TERM DOMINANCE
            }

            int unmatchedTerms = features.TermsCount - features.TermsWithAnyMatch;
            if (hasDominantTerm && unmatchedTerms == 1)
            {
                // For documents that have a dominant high-IDF term and exactly one unmatched term,
                // we grant a precedence boost (Bit 3). This ensures they rank at the top of their
                // coverage tier (e.g. above generic matches), but DOES NOT promote them to the
                // next tier. This avoids the regression where a partial match outranks a full match.
                precedence |= 8;
            }
        }

        if (isSingleTerm)
        {
            // For single-term queries: bits 9-8 for completeness/cleanliness, bits 7-3 for quality tier
            if (isComplete) precedence |= (1 << 9);  // Bit 9
            if (isClean && features.TermsCount > 0) precedence |= (1 << 8);  // Bit 8
            precedence |= ComputeSingleTermPrecedence(isExact, isClean, startsAtBeginning, isComplete);
        }
        else
        {
            int multiTermPrec = ComputeMultiTermPrecedence(
                isPrefixLastStrong, lexicalPrefixLast, isPerfectDoc, features, n, startsAtBeginning, isClean);
            
            // SPECIAL CASE: If the query has a filtered single-char last term
            // boost documents that contain it as a prefix match (like "s.r.o.").
            if (features.FusionSignals.UnfilteredQueryTokenCount > features.TermsCount && queryText.Length > 0)
            {
                ReadOnlySpan<char> query = queryText.AsSpan();
                int lastTokenStart = query.Length;
                for (int i = query.Length - 1; i >= 0; i--)
                {
                    bool isDelim = false;
                    for (int d = 0; d < delimiters.Length; d++)
                    {
                        if (query[i] == delimiters[d])
                        {
                            isDelim = true;
                            lastTokenStart = i + 1;
                            break;
                        }
                    }
                    if (isDelim) break;
                    if (i == 0) lastTokenStart = 0;
                }
                
                if (lastTokenStart < query.Length)
                {
                    ReadOnlySpan<char> lastToken = query.Slice(lastTokenStart);
                    // Only apply if last token is single-char letter (likely filtered due to MinWordSize)
                    if (lastToken.Length == 1 && char.IsLetter(lastToken[0]))
                    {
                        char lastCharLower = char.ToLowerInvariant(lastToken[0]);
                        char lastCharUpper = char.ToUpperInvariant(lastToken[0]);
                        
                        ReadOnlySpan<char> doc = documentText.AsSpan();
                        for (int i = 0; i < doc.Length - 2; i++)
                        {
                            char c1 = doc[i];
                            if (c1 != ' ' && c1 != ',') continue;
                            
                            char c2 = doc[i + 1];
                            if (c2 != lastCharLower && c2 != lastCharUpper) continue;
                            
                            char c3 = doc[i + 2];
                            if (c3 == '.')
                            {
                                multiTermPrec += 8;  // Boost within bits 5-3 range
                                break;
                            }
                        }
                    }
                }
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
                // We treat stem evidence as a *moderate* boost within the
                // partial-coverage tier (low bits)
                precedence |= 8;
            }
            else
            {
                // When exactly one term is unmatched, we compare how much *information*
                // is missing versus how many terms are missing. If the information loss
                // is smaller than what raw coordinate coverage suggests, we allow a
                // precedence boost to compensate.
                int unmatchedTerms = features.TermsCount - features.TermsWithAnyMatch;
                bool lastTermMatched = features.LastTokenHasPrefix ||
                                      (features.TermsCount > 0 && features.TermsWithAnyMatch == features.TermsCount);
                
                bool canBoost = (lastTermMatched || !features.LastTermIsTypeAhead) &&
                                features.TotalIdf > 0f;
                
                if (unmatchedTerms == 1 && canBoost)
                {
                    float missingInfoRatio = features.MissingIdf / features.TotalIdf;
                    float termGap = 1f - coverageRatio; // fraction of unmatched terms
                    
                    if (missingInfoRatio < termGap)
                    {
                        precedence |= 8;  // Boost to overcome phrase-run bonuses
                    }
                }
            }
        }

        float semantic = ComputeSemanticScore(
            queryText, features, isSingleTerm, bm25Score, coverageRatio);

        byte semanticByte = (byte)Math.Clamp(semantic * 255f, 0, 255);

        // Tiebreaker: prefer shorter documents (more focused matches)
        byte tiebreaker = 0;
        if (n >= 2 && documentText.Length > 0)
        {
            float focus = MathF.Min(1f, (float)queryText.Length / documentText.Length);
            tiebreaker = (byte)(focus * 255f);
        }
        
        // Use 10 bits for precedence (0-1023) and 6 bits for semantic (0-63).
        // This gives us enough room in precedence to encode consecutive phrase matches
        // vs scattered matches, which is critical
        int semantic6bit = semanticByte >> 2; // Quantize 8-bit (0-255) to 6-bit (0-63)
        ushort finalScore = (ushort)((precedence << 6) | semantic6bit);
        
        if (EnableDebugLogging)
        {
            LogExplanation(
                queryText,
                documentText,
                features,
                precedence,
                semantic6bit,
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
        int semantic6bit,
        byte tiebreaker,
        float coverageRatio,
        int dominantTermIndex,
        ushort finalScore)
    {
  
        // Truncate long doc texts for readability
        string docPreview = documentText.Length <= 120
            ? documentText
            : documentText[..117] + "...";

        if (EnableDebugLogging)
        {
            Console.WriteLine("[EXPLAIN] FusionScorer");
            Console.WriteLine($"  query=\"{queryText}\"");
            Console.WriteLine($"  doc=\"{docPreview}\"");
            Console.WriteLine($"  terms={features.TermsCount}, matched={features.TermsWithAnyMatch}, strict={features.TermsStrictMatched}, prefix={features.TermsPrefixMatched}");
            Console.WriteLine($"  longestRun={features.LongestPrefixRun}, suffixRun={features.SuffixPrefixRun}, span={features.PhraseSpan}");
            Console.WriteLine($"  coverageRatio={coverageRatio:F3}, sumCi={features.SumCi:F3}, lastCi={features.LastTermCi:F3}");
            Console.WriteLine($"  idfCoverage={features.IdfCoverage:F3}, totalIdf={features.TotalIdf:F3}, missingIdf={features.MissingIdf:F3}");
            Console.WriteLine($"  hasAnchorStem={features.FusionSignals.HasAnchorStem}, lexicalPrefixLast={features.FusionSignals.LexicalPrefixLast}, lastTokenHasPrefix={features.LastTokenHasPrefix}");
            Console.WriteLine($"  precedence={precedence} (0x{precedence:X3}), semantic6bit={semantic6bit}, finalScore={finalScore}, tiebreaker={tiebreaker}");
        }

        if (features.TermIdf != null && features.TermCi != null &&
            features.TermIdf.Length == features.TermsCount &&
            features.TermCi.Length == features.TermsCount)
        {
            Console.WriteLine("  per-term:");
            for (int i = 0; i < features.TermsCount; i++)
            {
                float idf = features.TermIdf[i];
                float ci = features.TermCi[i];
                float power = idf * ci;
                string marker = i == dominantTermIndex ? " *DOM*" : "";
                Console.WriteLine($"    term[{i}]: idf={idf:F3}, ci={ci:F3}, power={power:F3}{marker}");
            }
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
        return tier << 3;  // Use bits 3-5 for single-term tier (values 0-4 → 0, 8, 16, 24, 32)
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
        // Use precomputed anchor stem signal
        bool hasAnchorWithRun = features.FusionSignals.HasAnchorStem && features.LongestPrefixRun >= 2;

        // Multi-term precedence uses bits 0-5 (values 0-63)
        // Bits 6-9 are reserved for coverage tier, high-info, exact prefix
        // For multi-term queries, use the tier system which already encodes
        // prefix-last-strong (best), lexical-prefix-last (good), perfect-doc (fair).
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
            // Use precomputed single-term lexical similarity
            float lexicalSim = features.FusionSignals.SingleTermLexicalSim / 255f;
            semantic = (avgCi + lexicalSim) / 2f;
        }
        else if (features.DocTokenCount == 0)
        {
            semantic = avgCi;
        }
        else
        {
            // Use IDF-weighted coverage when available and informative.
            // For partial coverage where exactly one term is unmatched, prefer IDF coverage
            // if it's higher (indicating matched terms are more informative than missing ones).
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

        // Use precomputed anchor stem signal
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

