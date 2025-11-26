using Infidex.Coverage;
using Infidex.Metrics;

namespace Infidex.Scoring;

/// <summary>
/// Pure functions for calculating fusion scores combining coverage and relevancy signals.
/// </summary>
internal static class FusionScorer
{
    private const float IntentBonusPerSignal = 0.15f;
    private const int AnchorStemLength = 3;
    private const int MaxTrailingTermLengthForBonus = 2;

    /// <summary>
    /// Calculates the fusion score for a (query, document) pair.
    /// Returns (score, tiebreaker) where score encodes precedence (high byte) and semantic (low byte).
    /// </summary>
    public static (ushort score, byte tiebreaker) Calculate(
        string queryText,
        string documentText,
        CoverageFeatures features,
        float bm25Score,
        int minStemLength,
        char[] delimiters)
    {
        string[] queryTokens = queryText.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
        string[] docTokens = documentText.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);

        int n = queryTokens.Length;
        bool isSingleTerm = n <= 1;

        bool isComplete = features.TermsCount > 0 && features.TermsWithAnyMatch == features.TermsCount;
        bool isClean = features.TermsCount > 0 && features.TermsPrefixMatched == features.TermsCount;
        bool isExact = features.TermsCount > 0 && features.TermsStrictMatched == features.TermsCount;
        bool startsAtBeginning = features.FirstMatchIndex == 0;

        (bool lexicalPrefixLast, _) = CheckPrefixLastMatch(queryTokens, docTokens);

        int precedingTerms = Math.Max(0, features.TermsCount - 1);
        bool coveragePrefixLast = features.TermsCount >= 1 &&
                                  features.PrecedingStrictCount == precedingTerms &&
                                  features.LastTokenHasPrefix;

        bool isPrefixLastStrong = lexicalPrefixLast && coveragePrefixLast;
        bool isPerfectDoc = ComputePerfectDoc(queryTokens, docTokens);

        int precedence = 0;

        if (isComplete) precedence |= 128;
        if (isClean && features.TermsCount > 0) precedence |= 64;

        if (isSingleTerm)
        {
            precedence |= ComputeSingleTermPrecedence(isExact, isClean, startsAtBeginning, isComplete);
        }
        else
        {
            precedence |= ComputeMultiTermPrecedence(
                isPrefixLastStrong, lexicalPrefixLast, isPerfectDoc, features, n,
                queryTokens, docTokens, documentText);
        }

        float coverageRatio = features.TermsCount > 0
            ? (float)features.TermsWithAnyMatch / features.TermsCount
            : 0f;

        bool hasPartialCoverage = coverageRatio > 0f && coverageRatio < 1f;

        if (hasPartialCoverage && n >= 2)
        {
            bool hasStemEvidence = CheckStemEvidence(queryTokens, docTokens, minStemLength);
            
            if (hasStemEvidence)
            {
                precedence |= 128;
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
            queryText, queryTokens, docTokens, features, isSingleTerm, bm25Score, coverageRatio);

        byte semanticByte = (byte)Math.Clamp(semantic * 255f, 0, 255);

        byte tiebreaker = 0;
        if (n >= 2 && documentText.Length > 0)
        {
            float focus = MathF.Min(1f, (float)queryText.Length / documentText.Length);
            tiebreaker = (byte)(focus * 255f);
        }

        return ((ushort)((precedence << 8) | semanticByte), tiebreaker);
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

    private static int ComputePhraseQualityBits(
        int suffixRun,
        int longestRun,
        int span,
        int queryTermCount,
        int coverageTermCount,
        int termsWithMatch)
    {
        int bits = 0;

        int minSuffixForStrong = Math.Max(2, Math.Min(coverageTermCount, queryTermCount) - 1);
        if (suffixRun >= minSuffixForStrong)
        {
            bits |= 8;
        }
        else if (suffixRun >= 2)
        {
            bits |= 4;
        }

        if (longestRun >= 3) bits |= 2;
        if (termsWithMatch >= 2 && span == 2) bits |= 1;

        return bits;
    }

    private static int ComputeMultiTermPrecedence(
        bool isPrefixLastStrong,
        bool lexicalPrefixLast,
        bool isPerfectDoc,
        CoverageFeatures features,
        int queryTermCount,
        string[] queryTokens,
        string[] docTokens,
        string documentText)
    {
        bool hasAnchorWithRun = false;
        if (queryTermCount > 0 && queryTokens[0].Length >= 4)
        {
            if (documentText.Contains(queryTokens[0], StringComparison.OrdinalIgnoreCase) &&
                features.LongestPrefixRun >= 2)
            {
                hasAnchorWithRun = true;
            }
        }

        int tier = ComputeMultiTermTier(isPrefixLastStrong, lexicalPrefixLast, isPerfectDoc, hasAnchorWithRun);
        int tierBits = tier << 4;

        int phraseBits = ComputePhraseQualityBits(
            features.SuffixPrefixRun,
            features.LongestPrefixRun,
            features.PhraseSpan,
            queryTermCount,
            features.TermsCount,
            features.TermsWithAnyMatch);

        return tierBits | phraseBits;
    }

    public static bool ComputePerfectDoc(string[] queryTokens, string[] docTokens)
    {
        if (queryTokens.Length == 0 || docTokens.Length == 0)
            return false;

        foreach (string d in docTokens)
        {
            bool explained = false;
            foreach (string q in queryTokens)
            {
                if (d.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                    q.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                {
                    explained = true;
                    break;
                }
            }
            if (!explained)
                return false;
        }
        return true;
    }

    public static bool CheckStemEvidence(string[] queryTokens, string[] docTokens, int minStemLength)
    {
        int unmatchedCount = 0;
        int evidenceCount = 0;

        foreach (string q in queryTokens)
        {
            if (string.IsNullOrEmpty(q) || q.Length < minStemLength)
                continue;

            bool hasWordMatch = false;
            foreach (string d in docTokens)
            {
                if (string.IsNullOrEmpty(d))
                    continue;
                if (d.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                    d.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    hasWordMatch = true;
                    break;
                }
            }

            if (hasWordMatch)
                continue;

            unmatchedCount++;
            foreach (string d in docTokens)
            {
                if (string.IsNullOrEmpty(d) || d.Length < minStemLength)
                    continue;

                if (q.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                {
                    evidenceCount++;
                    break;
                }

                int maxCheck = Math.Min(q.Length, d.Length);
                if (maxCheck >= minStemLength)
                {
                    int prefixLen = 0;
                    for (int i = 0; i < maxCheck; i++)
                    {
                        if (char.ToLowerInvariant(q[i]) == char.ToLowerInvariant(d[i]))
                            prefixLen++;
                        else
                            break;
                    }
                    if (prefixLen >= minStemLength)
                    {
                        evidenceCount++;
                        break;
                    }
                }
            }
        }

        return unmatchedCount > 0 && evidenceCount == unmatchedCount;
    }

    private static float ComputeSemanticScore(
        string queryText,
        string[] queryTokens,
        string[] docTokens,
        CoverageFeatures features,
        bool isSingleTerm,
        float bm25Score,
        float coverageRatio)
    {
        float avgCi = features.TermsCount > 0 ? features.SumCi / features.TermsCount : 0f;
        float semantic;
        
        bool hasPartialCoverage = coverageRatio > 0f && coverageRatio < 1f;

        if (isSingleTerm)
        {
            float lexicalSim = ComputeSingleTermLexicalSimilarity(queryText, docTokens);
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
            semantic = ApplyIntentBonus(semantic, queryTokens, docTokens, features);
            semantic = ApplyTrailingTermBonus(semantic, queryTokens, docTokens);
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
        string[] queryTokens,
        string[] docTokens,
        CoverageFeatures features)
    {
        if (queryTokens.Length < 3 || docTokens.Length == 0)
            return semantic;

        bool hasSuffixPhrase = features.SuffixPrefixRun >= 2;

        bool hasAnchorStem = false;
        string firstToken = queryTokens[0];

        if (firstToken.Length >= AnchorStemLength)
        {
            string stem = firstToken[..AnchorStemLength];
            foreach (string d in docTokens)
            {
                if (!string.IsNullOrEmpty(d) && d.Length >= stem.Length &&
                    d.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    hasAnchorStem = true;
                    break;
                }
            }
        }

        int signalCount = (hasAnchorStem ? 1 : 0) + (hasSuffixPhrase ? 1 : 0);
        if (signalCount > 0)
        {
            float bonus = IntentBonusPerSignal * signalCount;
            semantic = MathF.Min(1f, semantic + bonus);
        }

        return semantic;
    }

    private static float ApplyTrailingTermBonus(float semantic, string[] queryTokens, string[] docTokens)
    {
        if (queryTokens.Length < 2 || docTokens.Length == 0)
            return semantic;

        string lastToken = queryTokens[^1];
        if (lastToken.Length < 1 || lastToken.Length > MaxTrailingTermLengthForBonus)
            return semantic;

        int matchableCount = 0;
        foreach (string d in docTokens)
        {
            if (string.IsNullOrEmpty(d))
                continue;
            if (d.StartsWith(lastToken, StringComparison.OrdinalIgnoreCase) ||
                (d.Length > lastToken.Length && d.Contains(lastToken, StringComparison.OrdinalIgnoreCase)))
            {
                matchableCount++;
            }
        }

        if (matchableCount > 0)
        {
            float matchDensity = (float)matchableCount / docTokens.Length;
            float headroom = 1f - semantic;
            semantic += headroom * matchDensity;
        }

        return semantic;
    }

    /// <summary>
    /// Checks if query matches the prefix-last autocomplete pattern:
    /// preceding tokens require exact match, last token allows prefix match.
    /// </summary>
    public static (bool isPrefixLastMatch, bool allPrecedingExact) CheckPrefixLastMatch(
        string[] queryTokens,
        string[] docTokens)
    {
        if (queryTokens.Length == 0 || docTokens.Length == 0)
            return (false, false);

        if (queryTokens.Length == 1)
        {
            string q = queryTokens[0];
            foreach (string d in docTokens)
            {
                if (!string.IsNullOrEmpty(d) &&
                    d.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    bool isExact = d.Equals(q, StringComparison.OrdinalIgnoreCase);
                    return (true, isExact);
                }
            }
            return (false, false);
        }

        HashSet<string> docTokenSet = new HashSet<string>(docTokens.Length, StringComparer.OrdinalIgnoreCase);
        foreach (string d in docTokens)
        {
            if (!string.IsNullOrEmpty(d))
                docTokenSet.Add(d);
        }

        bool allPrecedingExact = true;
        for (int i = 0; i < queryTokens.Length - 1; i++)
        {
            string q = queryTokens[i];
            if (string.IsNullOrEmpty(q))
                continue;

            if (!docTokenSet.Contains(q))
            {
                allPrecedingExact = false;
                break;
            }
        }

        if (!allPrecedingExact)
            return (false, false);

        string lastToken = queryTokens[^1];
        if (string.IsNullOrEmpty(lastToken))
            return (allPrecedingExact, allPrecedingExact);

        foreach (string d in docTokens)
        {
            if (!string.IsNullOrEmpty(d) &&
                d.StartsWith(lastToken, StringComparison.OrdinalIgnoreCase))
            {
                return (true, allPrecedingExact);
            }
        }

        return (false, false);
    }

    /// <summary>
    /// Lexical similarity for single-term queries using substring and fuzzy matching.
    /// </summary>
    public static float ComputeSingleTermLexicalSimilarity(string queryText, string[] docTokens)
    {
        if (string.IsNullOrEmpty(queryText) || docTokens.Length == 0)
            return 0f;

        string q = queryText.ToLowerInvariant();
        int qLen = q.Length;
        if (qLen < 3)
            return 0f;

        float best = 0f;

        foreach (string token in docTokens)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            string t = token.ToLowerInvariant();
            if (t.Length < 2)
                continue;

            int idx = q.IndexOf(t, StringComparison.Ordinal);
            if (idx >= 0)
            {
                float lenFrac = (float)t.Length / qLen;
                float positionFactor = 1f - (float)idx / qLen;
                float score = lenFrac * positionFactor;
                if (score > best)
                    best = score;
                continue;
            }

            int maxK = Math.Min(qLen, t.Length);
            int bestK = 0;
            for (int len = maxK; len >= 2; len--)
            {
                if (q.AsSpan(qLen - len).Equals(t.AsSpan(0, len), StringComparison.Ordinal))
                {
                    bestK = len;
                    break;
                }
            }

            float prefixSuffixScore = bestK > 0 ? (float)bestK / qLen : 0f;

            float fuzzyScore = 0f;
            int maxEdits = 2;
            int dist = LevenshteinDistance.CalculateDamerau(q, t, maxEdits, ignoreCase: true);
            if (dist <= maxEdits)
            {
                fuzzyScore = (float)(qLen - dist) / qLen;
            }

            float combined = MathF.Max(prefixSuffixScore, fuzzyScore);
            if (combined > best)
                best = combined;
        }

        const int MinSegmentLength = 3;
        if (qLen >= 2 * MinSegmentLength)
        {
            int segLen = Math.Min(2 * MinSegmentLength, qLen / 2);
            string prefixFrag = q[..segLen];
            string suffixFrag = q.Substring(qLen - segLen, segLen);

            int prefixIndex = -1;
            int suffixIndex = -1;

            for (int i = 0; i < docTokens.Length; i++)
            {
                string token = docTokens[i];
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                string t = token.ToLowerInvariant();
                if (t.Length < 3)
                    continue;

                if (prefixIndex == -1 &&
                    (t.StartsWith(prefixFrag, StringComparison.Ordinal) ||
                     prefixFrag.StartsWith(t, StringComparison.Ordinal)))
                {
                    prefixIndex = i;
                }

                if (suffixIndex == -1 &&
                    (t.EndsWith(suffixFrag, StringComparison.Ordinal) ||
                     suffixFrag.EndsWith(t, StringComparison.Ordinal)))
                {
                    suffixIndex = i;
                }

                if (prefixIndex != -1 && suffixIndex != -1)
                    break;
            }

            if (prefixIndex != -1 && suffixIndex != -1 && prefixIndex != suffixIndex)
            {
                float twoSegScore = MathF.Min(1f, (prefixFrag.Length + suffixFrag.Length) / (float)qLen);
                if (twoSegScore > best)
                    best = twoSegScore;
            }
        }

        return best;
    }
}

