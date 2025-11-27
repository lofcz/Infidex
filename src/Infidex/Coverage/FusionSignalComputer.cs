using Infidex.Metrics;
using System.Buffers;

namespace Infidex.Coverage;

/// <summary>
/// Precomputed fusion signals to eliminate string operations in FusionScorer.
/// </summary>
public readonly struct FusionSignals
{
    public readonly int UnfilteredQueryTokenCount;
    public readonly bool LexicalPrefixLast;
    public readonly bool AllPrecedingExact;
    public readonly bool IsPerfectDocLexical;
    public readonly bool HasStemEvidence;
    public readonly bool HasAnchorStem;
    public readonly byte TrailingMatchDensity;
    public readonly byte SingleTermLexicalSim;

    public FusionSignals(
        int unfilteredQueryTokenCount,
        bool lexicalPrefixLast,
        bool allPrecedingExact,
        bool isPerfectDocLexical,
        bool hasStemEvidence,
        bool hasAnchorStem,
        byte trailingMatchDensity,
        byte singleTermLexicalSim)
    {
        UnfilteredQueryTokenCount = unfilteredQueryTokenCount;
        LexicalPrefixLast = lexicalPrefixLast;
        AllPrecedingExact = allPrecedingExact;
        IsPerfectDocLexical = isPerfectDocLexical;
        HasStemEvidence = hasStemEvidence;
        HasAnchorStem = hasAnchorStem;
        TrailingMatchDensity = trailingMatchDensity;
        SingleTermLexicalSim = singleTermLexicalSim;
    }
}

/// <summary>
/// Computes precomputed fusion signals from token/span data to eliminate string operations in FusionScorer.
/// This is the Lucene-style approach: heavy work happens here (with spans), fusion only sees bits/bytes.
/// </summary>
internal static class FusionSignalComputer
{
    private const int AnchorStemLength = 3;
    private const int MaxTrailingTermLengthForBonus = 2;
    
    public static FusionSignals ComputeSignals(
        ReadOnlySpan<char> querySpan,
        ReadOnlySpan<char> docSpan,
        Span<StringSlice> queryTokens,
        Span<StringSlice> docTokens,
        int qCount,
        int dCount,
        int minStemLength,
        DocumentMetadata docMetadata = default)
    {
        bool lexicalPrefixLast = false;
        bool allPrecedingExact = false;
        bool isPerfectDocLexical = false;
        bool hasStemEvidence = false;
        bool hasAnchorStem = false;
        byte trailingMatchDensity = 0;
        byte singleTermLexicalSim = 0;

        if (qCount == 0 || dCount == 0)
        {
            return new FusionSignals(qCount, lexicalPrefixLast, allPrecedingExact, isPerfectDocLexical, hasStemEvidence, hasAnchorStem, trailingMatchDensity, singleTermLexicalSim);
        }

        // 1. CheckPrefixLastMatch
        (lexicalPrefixLast, allPrecedingExact) = CheckPrefixLastMatch(querySpan, docSpan, queryTokens, docTokens, qCount, dCount);

        // 2. ComputePerfectDoc
        isPerfectDocLexical = ComputePerfectDoc(querySpan, docSpan, queryTokens, docTokens, qCount, dCount);

        // 3. CheckStemEvidence
        if (qCount >= 2)
        {
            hasStemEvidence = CheckStemEvidence(querySpan, docSpan, queryTokens, docTokens, qCount, dCount, minStemLength);
        }

        // 4. HasAnchorStem (for intent bonus) - OPTIMIZED with precomputed metadata
        if (qCount > 0 && queryTokens[0].Length >= AnchorStemLength)
        {
            ReadOnlySpan<char> firstQueryToken = querySpan.Slice(queryTokens[0].Offset, queryTokens[0].Length);
            ReadOnlySpan<char> stem = firstQueryToken[..AnchorStemLength];

            // Fast path: check if document's first token matches the query stem
            if (docMetadata.HasTokens && docMetadata.FirstToken.Length >= stem.Length)
            {
                if (docMetadata.FirstToken.AsSpan().StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    hasAnchorStem = true;
                }
                else
                {
                    // Fallback: scan remaining document tokens
                    for (int i = 1; i < dCount; i++)
                    {
                        ReadOnlySpan<char> docToken = docSpan.Slice(docTokens[i].Offset, docTokens[i].Length);
                        if (docToken.Length >= stem.Length &&
                            docToken.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                        {
                            hasAnchorStem = true;
                            break;
                        }
                    }
                }
            }
            else if (!docMetadata.HasTokens)
            {
                // No precomputed metadata available, use original logic
                for (int i = 0; i < dCount; i++)
                {
                    ReadOnlySpan<char> docToken = docSpan.Slice(docTokens[i].Offset, docTokens[i].Length);
                    if (docToken.Length >= stem.Length &&
                        docToken.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    {
                        hasAnchorStem = true;
                        break;
                    }
                }
            }
        }

        // 5. TrailingMatchDensity (for trailing term bonus)
        if (qCount >= 2)
        {
            StringSlice lastToken = queryTokens[qCount - 1];
            if (lastToken.Length >= 1 && lastToken.Length <= MaxTrailingTermLengthForBonus)
            {
                ReadOnlySpan<char> lastQueryToken = querySpan.Slice(lastToken.Offset, lastToken.Length);
                int matchableCount = 0;

                for (int i = 0; i < dCount; i++)
                {
                    ReadOnlySpan<char> docToken = docSpan.Slice(docTokens[i].Offset, docTokens[i].Length);
                    if (docToken.StartsWith(lastQueryToken, StringComparison.OrdinalIgnoreCase) ||
                        (docToken.Length > lastQueryToken.Length && 
                         docToken.Contains(lastQueryToken, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchableCount++;
                    }
                }

                if (matchableCount > 0)
                {
                    float matchDensity = (float)matchableCount / dCount;
                    trailingMatchDensity = (byte)Math.Clamp(matchDensity * 255f, 0f, 255f);
                }
            }
        }

        // 6. SingleTermLexicalSim (for single-term queries)
        if (qCount == 1)
        {
            ReadOnlySpan<char> queryToken = querySpan.Slice(queryTokens[0].Offset, queryTokens[0].Length);
            float sim = ComputeSingleTermLexicalSimilarity(queryToken, docSpan, docTokens, dCount);
            singleTermLexicalSim = (byte)Math.Clamp(sim * 255f, 0f, 255f);
        }

        return new FusionSignals(qCount, lexicalPrefixLast, allPrecedingExact, isPerfectDocLexical, hasStemEvidence, hasAnchorStem, trailingMatchDensity, singleTermLexicalSim);
    }

    private static (bool isPrefixLastMatch, bool allPrecedingExact) CheckPrefixLastMatch(
        ReadOnlySpan<char> querySpan,
        ReadOnlySpan<char> docSpan,
        Span<StringSlice> queryTokens,
        Span<StringSlice> docTokens,
        int qCount,
        int dCount)
    {
        if (qCount == 0 || dCount == 0)
            return (false, false);

        if (qCount == 1)
        {
            ReadOnlySpan<char> q = querySpan.Slice(queryTokens[0].Offset, queryTokens[0].Length);
            for (int i = 0; i < dCount; i++)
            {
                ReadOnlySpan<char> d = docSpan.Slice(docTokens[i].Offset, docTokens[i].Length);
                if (d.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                {
                    bool isExact = d.Equals(q, StringComparison.OrdinalIgnoreCase);
                    return (true, isExact);
                }
            }
            return (false, false);
        }

        // Check if all preceding query tokens have exact matches in doc
        // Use linear scan instead of HashSet - faster for small token counts (typically <10)
        // and avoids allocation on every document during search
        bool allPrecedingExact = true;
        for (int i = 0; i < qCount - 1; i++)
        {
            ReadOnlySpan<char> q = querySpan.Slice(queryTokens[i].Offset, queryTokens[i].Length);
            if (q.Length == 0)
                continue;

            bool foundExact = false;
            for (int j = 0; j < dCount; j++)
            {
                ReadOnlySpan<char> d = docSpan.Slice(docTokens[j].Offset, docTokens[j].Length);
                if (d.Equals(q, StringComparison.OrdinalIgnoreCase))
                {
                    foundExact = true;
                    break;
                }
            }

            if (!foundExact)
            {
                allPrecedingExact = false;
                break;
            }
        }

        if (!allPrecedingExact)
            return (false, false);

        ReadOnlySpan<char> lastToken = querySpan.Slice(queryTokens[qCount - 1].Offset, queryTokens[qCount - 1].Length);
        if (lastToken.Length == 0)
            return (allPrecedingExact, allPrecedingExact);

        for (int i = 0; i < dCount; i++)
        {
            ReadOnlySpan<char> d = docSpan.Slice(docTokens[i].Offset, docTokens[i].Length);
            if (d.StartsWith(lastToken, StringComparison.OrdinalIgnoreCase))
            {
                return (true, allPrecedingExact);
            }
        }

        return (false, false);
    }

    private static bool ComputePerfectDoc(
        ReadOnlySpan<char> querySpan,
        ReadOnlySpan<char> docSpan,
        Span<StringSlice> queryTokens,
        Span<StringSlice> docTokens,
        int qCount,
        int dCount)
    {
        if (qCount == 0 || dCount == 0)
            return false;

        foreach (StringSlice dSlice in docTokens[..dCount])
        {
            ReadOnlySpan<char> d = docSpan.Slice(dSlice.Offset, dSlice.Length);
            bool explained = false;

            foreach (StringSlice qSlice in queryTokens[..qCount])
            {
                ReadOnlySpan<char> q = querySpan.Slice(qSlice.Offset, qSlice.Length);
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

    private static bool CheckStemEvidence(
        ReadOnlySpan<char> querySpan,
        ReadOnlySpan<char> docSpan,
        Span<StringSlice> queryTokens,
        Span<StringSlice> docTokens,
        int qCount,
        int dCount,
        int minStemLength)
    {
        int unmatchedCount = 0;
        int evidenceCount = 0;

        for (int qi = 0; qi < qCount; qi++)
        {
            ReadOnlySpan<char> q = querySpan.Slice(queryTokens[qi].Offset, queryTokens[qi].Length);
            if (q.Length < minStemLength)
                continue;

            bool hasWordMatch = false;
            for (int di = 0; di < dCount; di++)
            {
                ReadOnlySpan<char> d = docSpan.Slice(docTokens[di].Offset, docTokens[di].Length);
                if (d.Length == 0)
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

            for (int di = 0; di < dCount; di++)
            {
                ReadOnlySpan<char> d = docSpan.Slice(docTokens[di].Offset, docTokens[di].Length);
                if (d.Length < minStemLength)
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

    private static float ComputeSingleTermLexicalSimilarity(
        ReadOnlySpan<char> query,
        ReadOnlySpan<char> docSpan,
        Span<StringSlice> docTokens,
        int dCount)
    {
        int qLen = query.Length;
        if (qLen < 3)
            return 0f;

        // Lowercase query once
        Span<char> qLower = stackalloc char[qLen];
        query.ToLowerInvariant(qLower);

        float best = 0f;

        for (int i = 0; i < dCount; i++)
        {
            ReadOnlySpan<char> token = docSpan.Slice(docTokens[i].Offset, docTokens[i].Length);
            if (token.Length < 2)
                continue;

            // Lowercase doc token
            Span<char> tLower = stackalloc char[token.Length];
            token.ToLowerInvariant(tLower);

            // Substring match
            int idx = qLower.IndexOf(tLower);
            if (idx >= 0)
            {
                float lenFrac = (float)tLower.Length / qLen;
                float positionFactor = 1f - (float)idx / qLen;
                float score = lenFrac * positionFactor;
                if (score > best)
                    best = score;
                continue;
            }

            // Prefix-suffix match
            int maxK = Math.Min(qLen, tLower.Length);
            int bestK = 0;
            for (int len = maxK; len >= 2; len--)
            {
                if (qLower[(qLen - len)..].Equals(tLower[..len], StringComparison.Ordinal))
                {
                    bestK = len;
                    break;
                }
            }

            float prefixSuffixScore = bestK > 0 ? (float)bestK / qLen : 0f;

            // Fuzzy match (with length cap)
            float fuzzyScore = 0f;
            const int maxEdits = 2;
            const int maxTokenLengthForFuzzy = 32;

            if (tLower.Length <= maxTokenLengthForFuzzy)
            {
                int dist = LevenshteinDistance.CalculateDamerau(
                    new string(qLower),
                    new string(tLower),
                    maxEdits,
                    ignoreCase: false);

                if (dist <= maxEdits)
                {
                    fuzzyScore = (float)(qLen - dist) / qLen;
                }
            }

            float combined = MathF.Max(prefixSuffixScore, fuzzyScore);
            if (combined > best)
                best = combined;
        }

        // Two-segment heuristic
        const int MinSegmentLength = 3;
        if (qLen >= 2 * MinSegmentLength)
        {
            int segLen = Math.Min(2 * MinSegmentLength, qLen / 2);
            ReadOnlySpan<char> prefixFrag = qLower[..segLen];
            ReadOnlySpan<char> suffixFrag = qLower.Slice(qLen - segLen, segLen);

            int prefixIndex = -1;
            int suffixIndex = -1;

            for (int i = 0; i < dCount; i++)
            {
                ReadOnlySpan<char> token = docSpan.Slice(docTokens[i].Offset, docTokens[i].Length);
                if (token.Length < 3)
                    continue;

                Span<char> tLower = stackalloc char[token.Length];
                token.ToLowerInvariant(tLower);

                if (prefixIndex == -1 &&
                    (tLower.StartsWith(prefixFrag, StringComparison.Ordinal) ||
                     prefixFrag.StartsWith(tLower, StringComparison.Ordinal)))
                {
                    prefixIndex = i;
                }

                if (suffixIndex == -1 &&
                    (tLower.EndsWith(suffixFrag, StringComparison.Ordinal) ||
                     suffixFrag.EndsWith(tLower, StringComparison.Ordinal)))
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

