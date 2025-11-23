using Infidex.Metrics;
using Infidex.Tokenization;
using System.Buffers;

namespace Infidex.Coverage;

public class CoverageEngine
{
    private readonly Tokenizer _tokenizer;
    private readonly CoverageSetup _setup;
    
    public CoverageEngine(Tokenizer tokenizer, CoverageSetup? setup = null)
    {
        _tokenizer = tokenizer;
        _setup = setup ?? CoverageSetup.CreateDefault();
    }
    
    public byte CalculateCoverageScore(string query, string documentText, double lcsSum, out int wordHits)
    {
        var result = CalculateCoverageInternal(query, documentText, lcsSum, out wordHits, 
            out _, out _, out _, out _, out _);
        return result.CoverageScore;
    }
    
    public ushort CalculateRankedScore(string query, string documentText, double lcsSum, byte baseTfidfScore, out int wordHits)
    {
        var result = CalculateCoverageInternal(query, documentText, lcsSum, out wordHits,
            out int docTokenCount, out int termsWithAnyMatch, out int termsFullyMatched, out int termsStrictMatched, out int termsPrefixMatched);
        
        byte coverageScore = result.CoverageScore;
        int firstMatchIndex = result.FirstMatchIndex;
        int termsCount = result.TermsCount;
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
        
        // Precedence Hierarchy (Context-Aware Signal Strength Model):
        // 128: All Terms Found (Fundamental baseline)
        // 64: All Terms Fully Matched (Whole OR Exact Prefix)
        // 32: Primary Tie-Breaker (Context-dependent)
        // 16: Secondary Tie-Breaker (Context-dependent)
        // 8: First Match at Index 0 (Starts with query)
        // 4: Precise Prefix Match (Start of Token)
        // 2, 1: Reserved for future signal enrichment
        
        if (allTermsFound) precedence |= 128;
        if (isFullyMatched) precedence |= 64;
        
        bool isPerfectDoc = (docTokenCount > 0 && wordHits == docTokenCount && allTermsFound);
        
        // Dynamic Precedence Logic:
        // Single-Term Query: Prioritize Strict Whole Word over Perfect Doc.
        //   Rationale: "star" -> "Star Kid" (Exact) should beat "Stardom" (Prefix).
        // Multi-Term Query: Prioritize Perfect Doc over Strict Whole Word.
        //   Rationale: "the hear" -> "The Hearse" (Noise-Free) should beat "Did You Hear..." (Noisy).
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
        
        // Use Bit 4 for Prefix Match
        if (isPrefixMatched) precedence |= 4;
        
        return (ushort)((precedence << 8) | baseFinal);
    }
    
    private readonly struct StringSlice
    {
        public readonly int Offset;
        public readonly int Length;
        public readonly int Position;
        public readonly int Hash;

        public StringSlice(int offset, int length, int position, int hash)
        {
            Offset = offset;
            Length = length;
            Position = position;
            Hash = hash;
        }
    }

    private CoverageResult CalculateCoverageInternal(string query, string documentText, double lcsSum, 
        out int wordHits, out int docTokenCount, out int termsWithAnyMatch, out int termsFullyMatched, out int termsStrictMatched, out int termsPrefixMatched)
    {
        wordHits = 0;
        docTokenCount = 0;
        termsWithAnyMatch = 0;
        termsFullyMatched = 0;
        termsStrictMatched = 0;
        termsPrefixMatched = 0;
        
        if (query.Length == 0) 
            return new CoverageResult(0, 0, -1, 0);

        const int MaxStackTerms = 256;
        
        int queryLen = query.Length;
        int docLen = documentText.Length;
        
        int maxQueryTokens = queryLen / 2 + 1;
        StringSlice[]? queryTokenArray = null;
        Span<StringSlice> queryTokens = maxQueryTokens <= MaxStackTerms 
            ? stackalloc StringSlice[maxQueryTokens] 
            : (queryTokenArray = ArrayPool<StringSlice>.Shared.Rent(maxQueryTokens));

        int qCount = TokenizeToSpan(query, queryTokens, _setup.MinWordSize);
        if (qCount == 0)
        {
            if (queryTokenArray != null) ArrayPool<StringSlice>.Shared.Return(queryTokenArray);
            return new CoverageResult(0, 0, -1, 0);
        }
        
        // Deduplicate Query Tokens (Set Semantics)
        // Optimized: Use HashCodes to speed up deduplication and future matching
        ReadOnlySpan<char> qSpanFull = query.AsSpan();
        int uniqueQCount = 0;
        
        for (int i = 0; i < qCount; i++)
        {
            bool duplicate = false;
            var current = queryTokens[i];
            var currentSpan = qSpanFull.Slice(current.Offset, current.Length);
            int currentHash = string.GetHashCode(currentSpan, StringComparison.OrdinalIgnoreCase);
            
            for (int j = 0; j < uniqueQCount; j++)
            {
                var existing = queryTokens[j];
                // Check Hash first (fast int comparison)
                if (existing.Hash == currentHash && existing.Length == current.Length)
                {
                    if (qSpanFull.Slice(existing.Offset, existing.Length).Equals(currentSpan, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
            }
            if (!duplicate)
            {
                // Store with Hash for later use
                queryTokens[uniqueQCount++] = new StringSlice(current.Offset, current.Length, current.Position, currentHash);
            }
        }
        
        qCount = uniqueQCount; // Work with unique query tokens
        Span<StringSlice> activeQueryTokens = queryTokens[..qCount];

        // --- Tokenize Document ---
        int maxDocTokens = docLen / 2 + 1;
        StringSlice[]? docTokenArray = null;
        Span<StringSlice> docTokens = maxDocTokens <= MaxStackTerms
            ? stackalloc StringSlice[maxDocTokens]
            : (docTokenArray = ArrayPool<StringSlice>.Shared.Rent(maxDocTokens));

        int dCountRaw = TokenizeToSpan(documentText, docTokens, _setup.MinWordSize);
        docTokenCount = dCountRaw; // Total tokens
        
        // Deduplicate Document Tokens for Set Semantics
        // Optimized with HashCode check
        
        StringSlice[]? uniqueDocTokenArray = null;
        Span<StringSlice> uniqueDocTokens = dCountRaw <= MaxStackTerms
            ? stackalloc StringSlice[dCountRaw]
            : (uniqueDocTokenArray = ArrayPool<StringSlice>.Shared.Rent(dCountRaw));
            
        int dCount = 0;
        ReadOnlySpan<char> dSpanFull = documentText.AsSpan();
        
        for (int i = 0; i < dCountRaw; i++)
        {
            var current = docTokens[i];
            var currentSpan = dSpanFull.Slice(current.Offset, current.Length);
            int currentHash = string.GetHashCode(currentSpan, StringComparison.OrdinalIgnoreCase);
            
            bool duplicate = false;
            
            // Check against already added unique tokens
            for (int j = 0; j < dCount; j++)
            {
                var existing = uniqueDocTokens[j];
                if (existing.Hash == currentHash && existing.Length == current.Length)
                {
                    if (dSpanFull.Slice(existing.Offset, existing.Length).Equals(currentSpan, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        // We keep the FIRST position (already in existing)
                        break;
                    }
                }
            }
            
            if (!duplicate)
            {
                uniqueDocTokens[dCount++] = new StringSlice(current.Offset, current.Length, current.Position, currentHash);
            }
        }
        
        Span<StringSlice> activeDocTokens = uniqueDocTokens[..dCount];

        // --- Data Structures ---
        // Use stackalloc for small arrays, rent for large
        bool[]? qActiveArray = null;
        Span<bool> qActive = qCount <= MaxStackTerms 
            ? stackalloc bool[qCount] 
            : (qActiveArray = ArrayPool<bool>.Shared.Rent(qCount));
        qActive[..qCount].Fill(true);

        bool[]? dActiveArray = null;
        Span<bool> dActive = dCount <= MaxStackTerms
            ? stackalloc bool[dCount]
            : (dActiveArray = ArrayPool<bool>.Shared.Rent(dCount));
        dActive[..dCount].Fill(true);

        // Stats
        float[]? termMatchedCharsArray = null;
        Span<float> termMatchedChars = qCount <= MaxStackTerms
            ? stackalloc float[qCount]
            : (termMatchedCharsArray = ArrayPool<float>.Shared.Rent(qCount));
        termMatchedChars[..qCount].Clear();

        int[]? termMaxCharsArray = null;
        Span<int> termMaxChars = qCount <= MaxStackTerms
            ? stackalloc int[qCount]
            : (termMaxCharsArray = ArrayPool<int>.Shared.Rent(qCount));
        
        bool[]? termHasWholeArray = null;
        Span<bool> termHasWhole = qCount <= MaxStackTerms
            ? stackalloc bool[qCount]
            : (termHasWholeArray = ArrayPool<bool>.Shared.Rent(qCount));
        termHasWhole[..qCount].Clear();

        bool[]? termHasJoinedArray = null;
        Span<bool> termHasJoined = qCount <= MaxStackTerms
            ? stackalloc bool[qCount]
            : (termHasJoinedArray = ArrayPool<bool>.Shared.Rent(qCount));
        termHasJoined[..qCount].Clear();

        bool[]? termHasPrefixArray = null;
        Span<bool> termHasPrefix = qCount <= MaxStackTerms
            ? stackalloc bool[qCount]
            : (termHasPrefixArray = ArrayPool<bool>.Shared.Rent(qCount));
        termHasPrefix[..qCount].Clear();

        int[]? termFirstPosArray = null;
        Span<int> termFirstPos = qCount <= MaxStackTerms
            ? stackalloc int[qCount]
            : (termFirstPosArray = ArrayPool<int>.Shared.Rent(qCount));
        termFirstPos[..qCount].Fill(-1);

        for(int i=0; i<qCount; i++) 
        {
            termMaxChars[i] = activeQueryTokens[i].Length;
        }

        double num = 0.0;
        double num2 = 0.0;
        double num3 = 0.0;
        double num4 = 0.0;
        byte penalty = 0;

        try
        {
            // 1. Whole Words
            if (_setup.CoverWholeWords)
            {
                int pIncrement = qCount > 1 ? 1 : 0;
                for (int i = 0; i < qCount; i++)
                {
                    var qSlice = activeQueryTokens[i];
                    
                    // Find match in active doc tokens
                    int matchIndex = -1;
                    for (int j = 0; j < dCount; j++)
                    {
                        if (dActive[j])
                        {
                            var dSlice = activeDocTokens[j];
                            // Hash check first - extremely fast rejection
                            if (dSlice.Hash == qSlice.Hash && dSlice.Length == qSlice.Length)
                            {
                                ReadOnlySpan<char> qText = qSpanFull.Slice(qSlice.Offset, qSlice.Length);
                                if (qText.Equals(dSpanFull.Slice(dSlice.Offset, dSlice.Length), StringComparison.OrdinalIgnoreCase))
                                {
                                    matchIndex = j;
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (matchIndex != -1)
                    {
                        wordHits++;
                        num += qSlice.Length;
                        
                        termMatchedChars[i] += qSlice.Length;
                        termHasWhole[i] = true;
                        termHasPrefix[i] = true;
                        
                        int pos = activeDocTokens[matchIndex].Position;
                        if (termFirstPos[i] == -1 || pos < termFirstPos[i]) termFirstPos[i] = pos;
                        
                        // Penalty logic
                        if (dCount > i)
                        {
                            var dSliceI = activeDocTokens[i];
                            // Check inequality
                            if (dSliceI.Hash != qSlice.Hash || dSliceI.Length != qSlice.Length ||
                                !qSpanFull.Slice(qSlice.Offset, qSlice.Length).Equals(
                                    dSpanFull.Slice(dSliceI.Offset, dSliceI.Length), StringComparison.OrdinalIgnoreCase))
                            {
                                penalty++;
                            }
                        }
                        else
                        {
                            penalty++;
                        }
                        
                        if (i < qCount - 1) num += pIncrement;
                        
                        qActive[i] = false;
                        dActive[matchIndex] = false;
                    }
                }
            }

            // 2. Joined Words
            if (_setup.CoverJoinedWords && qCount > 0)
            {
                // Query joined
                for (int i = 0; i < qCount - 1; i++)
                {
                    if (!qActive[i] || !qActive[i+1]) continue;
                    
                    // Find next active query word
                    int nextIdx = -1;
                    for (int k = i + 1; k < qCount; k++)
                    {
                        if (qActive[k]) { nextIdx = k; break; }
                    }
                    if (nextIdx == -1) break;
                    
                    var q1 = activeQueryTokens[i];
                    var q2 = activeQueryTokens[nextIdx];
                    
                    // Virtual join comparison
                    int joinedLen = q1.Length + q2.Length;
                    
                    int matchIndex = -1;
                    for (int j = 0; j < dCount; j++)
                    {
                        if (dActive[j])
                        {
                            var dSlice = activeDocTokens[j];
                            if (dSlice.Length == joinedLen)
                            {
                                ReadOnlySpan<char> dText = dSpanFull.Slice(dSlice.Offset, dSlice.Length);
                                ReadOnlySpan<char> q1Text = qSpanFull.Slice(q1.Offset, q1.Length);
                                ReadOnlySpan<char> q2Text = qSpanFull.Slice(q2.Offset, q2.Length);
                                
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
                        num2 += joinedLen;
                        wordHits += 2;
                        
                        termMatchedChars[i] += q1.Length;
                        termHasJoined[i] = true;
                        termHasPrefix[i] = true;
                        int pos = activeDocTokens[matchIndex].Position;
                        if (termFirstPos[i] == -1 || pos < termFirstPos[i]) termFirstPos[i] = pos;
                        
                        termMatchedChars[nextIdx] += q2.Length;
                        termHasJoined[nextIdx] = true;
                        if (termFirstPos[nextIdx] == -1 || pos < termFirstPos[nextIdx]) termFirstPos[nextIdx] = pos;
                        
                        qActive[i] = false;
                        qActive[nextIdx] = false;
                        dActive[matchIndex] = false;
                    }
                }
                
                // Doc joined
                for (int i = 0; i < dCount - 1; i++)
                {
                    if (!dActive[i]) continue;
                    int nextIdx = -1;
                    for (int k = i + 1; k < dCount; k++) { if (dActive[k]) { nextIdx = k; break; } }
                    if (nextIdx == -1) break;
                    
                    var d1 = activeDocTokens[i];
                    var d2 = activeDocTokens[nextIdx];
                    int joinedLen = d1.Length + d2.Length;
                    
                    int matchIndex = -1;
                    for (int j = 0; j < qCount; j++)
                    {
                        if (qActive[j])
                        {
                            var qSlice = activeQueryTokens[j];
                            if (qSlice.Length == joinedLen)
                            {
                                ReadOnlySpan<char> qText = qSpanFull.Slice(qSlice.Offset, qSlice.Length);
                                ReadOnlySpan<char> d1Text = dSpanFull.Slice(d1.Offset, d1.Length);
                                ReadOnlySpan<char> d2Text = dSpanFull.Slice(d2.Offset, d2.Length);
                                
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
                        num2 += joinedLen;
                        wordHits += 1;
                        
                        termMatchedChars[matchIndex] += joinedLen;
                        termHasJoined[matchIndex] = true;
                        termHasPrefix[matchIndex] = true;
                        int pos = d1.Position;
                        if (termFirstPos[matchIndex] == -1 || pos < termFirstPos[matchIndex]) termFirstPos[matchIndex] = pos;
                        
                        qActive[matchIndex] = false;
                        dActive[i] = false;
                        dActive[nextIdx] = false;
                    }
                }
            }

            // Check if fuzzy needed
            bool allTermsFullyMatched = true;
            for (int i = 0; i < qCount; i++)
            {
                if (termMaxChars[i] > 0 && termMatchedChars[i] < termMaxChars[i])
                {
                    allTermsFullyMatched = false;
                    break;
                }
            }

            // 3. Fuzzy
            if (_setup.CoverFuzzyWords && qCount > 0 && !allTermsFullyMatched)
            {
                int maxQueryLength = 0;
                for(int i=0; i<qCount; i++) if (qActive[i] && activeQueryTokens[i].Length > maxQueryLength) maxQueryLength = activeQueryTokens[i].Length;
                
                if (maxQueryLength > 0)
                {
                    double maxRelDist = 0.25;
                    int maxEditDist = Math.Max(1, (int)Math.Round(maxQueryLength * maxRelDist));
                    
                    for (int editDist = 1; editDist <= maxEditDist; editDist++)
                    {
                        bool anyQ = false;
                        for(int i=0; i<qCount; i++) if (qActive[i]) anyQ = true;
                        if (!anyQ) break;
                        
                        for (int i = 0; i < qCount; i++)
                        {
                            if (!qActive[i]) continue;
                            var qSlice = activeQueryTokens[i];
                            int qLen = qSlice.Length;
                            
                            int minLen = Math.Max(_setup.MinWordSize + 1, qLen - editDist);
                            int maxLen = Math.Min(_setup.LevenshteinMaxWordSize, qLen + editDist);
                            if (maxLen > 63) maxLen = 63;
                            if (qLen > maxLen || qLen < minLen) continue;
                            
                            ReadOnlySpan<char> qText = qSpanFull.Slice(qSlice.Offset, qSlice.Length);
                            
                            for (int j = 0; j < dCount; j++)
                            {
                                if (!dActive[j]) continue;
                                var dSlice = activeDocTokens[j];
                                int dLen = dSlice.Length;
                                if (dLen > maxLen || dLen < minLen) continue;
                                
                                ReadOnlySpan<char> dText = dSpanFull.Slice(dSlice.Offset, dSlice.Length);
                                
                                // Optimized to use Span and avoid string allocations
                                int dist = LevenshteinDistance.CalculateDamerau(qText, dText, editDist, ignoreCase: true);
                                
                                if (dist <= editDist)
                                {
                                    wordHits++;
                                    num3 += (qLen - dist);
                                
                                    termMatchedChars[i] += (qLen - dist);
                                    termHasPrefix[i] = true;
                                    int pos = dSlice.Position;
                                    if (termFirstPos[i] == -1 || pos < termFirstPos[i]) termFirstPos[i] = pos;
                                    
                                    qActive[i] = false;
                                    dActive[j] = false;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // 4. Prefix/Suffix
            if (_setup.CoverPrefixSuffix && qCount > 0)
            {
                int[] qIndices = ArrayPool<int>.Shared.Rent(qCount);
                int[] dIndices = ArrayPool<int>.Shared.Rent(dCount);
                
                int activeQCount = 0;
                for(int i=0; i<qCount; i++) if(qActive[i]) qIndices[activeQCount++] = i;
                
                int activeDCount = 0;
                for(int i=0; i<dCount; i++) if(dActive[i]) dIndices[activeDCount++] = i;
                
                // Sort Q indices
                for (int i = 1; i < activeQCount; i++)
                {
                    int currentIdx = qIndices[i];
                    int currentLen = activeQueryTokens[currentIdx].Length;
                    int j = i - 1;
                    while (j >= 0 && activeQueryTokens[qIndices[j]].Length < currentLen) // Descending
                    {
                        qIndices[j + 1] = qIndices[j];
                        j--;
                    }
                    qIndices[j + 1] = currentIdx;
                }

                // Sort D indices
                for (int i = 1; i < activeDCount; i++)
                {
                    int currentIdx = dIndices[i];
                    int currentLen = activeDocTokens[currentIdx].Length;
                    int j = i - 1;
                    while (j >= 0 && activeDocTokens[dIndices[j]].Length < currentLen) // Descending
                    {
                        dIndices[j + 1] = dIndices[j];
                        j--;
                    }
                    dIndices[j + 1] = currentIdx;
                }
                
                for (int qi = 0; qi < activeQCount; qi++)
                {
                    int i = qIndices[qi];
                    var qSlice = activeQueryTokens[i];
                    ReadOnlySpan<char> qText = qSpanFull.Slice(qSlice.Offset, qSlice.Length);
                    
                    for (int di = 0; di < activeDCount; di++)
                    {
                        int j = dIndices[di];
                        if (!dActive[j]) 
                            continue; // Might have been consumed in inner loop?
                        
                        var dSlice = activeDocTokens[j];
                        if (qSlice.Length == dSlice.Length) 
                            continue;
                        
                        ReadOnlySpan<char> dText = dSpanFull.Slice(dSlice.Offset, dSlice.Length);
                        
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
                            // Exact Substring Match (Contains)
                            // Signal Strength: Moderate (0.6). Weaker than Prefix, stronger than random noise.
                            else if (qSlice.Length >= 4 && dText.Contains(qText, StringComparison.OrdinalIgnoreCase))
                            {
                                matchScore = qSlice.Length * 0.6;
                                isMatch = true;
                            }
                            // Fuzzy Prefix Match
                            // If we didn't match exactly, check if qText is "close" to being a prefix of dText.
                            // Signal Strength: Low/Moderate (0.5 * Similarity).
                            // Requires qText length >= 4 to avoid high false positive rate on short terms.
                            else if (qSlice.Length >= 4)
                            {
                                int qLen = qSlice.Length;
                                int maxEdits = 1; 
                                
                                // Check len (substitution)
                                int dist = LevenshteinDistance.CalculateDamerau(qText, dText[..qLen], maxEdits, true);
                                if (dist <= maxEdits)
                                {
                                    // Score = (MatchedLength) * ConfidencePenalty(0.5)
                                    matchScore = (qLen - dist) * 0.5;
                                    if (matchScore < 0.1) matchScore = 0.1;
                                    isMatch = true;
                                    isPrefix = true;
                                }
                                // Check len+1 (deletion in query / insertion in doc)
                                else if (dSlice.Length > qLen)
                                {
                                    dist = LevenshteinDistance.CalculateDamerau(qText, dText[..(qLen + 1)], maxEdits, true);
                                    if (dist <= maxEdits)
                                    {
                                        matchScore = (qLen - dist) * 0.5;
                                        if (matchScore < 0.1) matchScore = 0.1;
                                        isMatch = true;
                                        isPrefix = true;
                                    }
                                    else if (qLen > 1) // len-1 (insertion in query / deletion in doc)
                                    {
                                        dist = LevenshteinDistance.CalculateDamerau(qText, dText[..(qLen - 1)], maxEdits, true);
                                        if (dist <= maxEdits)
                                        {
                                            matchScore = ((qLen - 1) - dist) * 0.5;
                                            if (matchScore < 0.1) matchScore = 0.1;
                                            isMatch = true;
                                            isPrefix = true;
                                        }
                                    }
                                }
                            }
                        }
                        
                        if (isMatch)
                        {
                            num4 += matchScore;
                            wordHits++;
                            
                            termMatchedChars[i] += (float)matchScore;
                            if (isPrefix) termHasPrefix[i] = true;

                            int pos = dSlice.Position;
                            if (termFirstPos[i] == -1 || pos < termFirstPos[i]) termFirstPos[i] = pos;
                            
                            qActive[i] = false;
                            dActive[j] = false;
                            break;
                        }
                    }
                }
                
                ArrayPool<int>.Shared.Return(qIndices);
                ArrayPool<int>.Shared.Return(dIndices);
            }
        }
        finally
        {
            if (queryTokenArray != null) ArrayPool<StringSlice>.Shared.Return(queryTokenArray);
            if (docTokenArray != null) ArrayPool<StringSlice>.Shared.Return(docTokenArray);
            if (uniqueDocTokenArray != null) ArrayPool<StringSlice>.Shared.Return(uniqueDocTokenArray);
            if (qActiveArray != null) ArrayPool<bool>.Shared.Return(qActiveArray);
            if (dActiveArray != null) ArrayPool<bool>.Shared.Return(dActiveArray);
            if (termMatchedCharsArray != null) ArrayPool<float>.Shared.Return(termMatchedCharsArray);
            if (termMaxCharsArray != null) ArrayPool<int>.Shared.Return(termMaxCharsArray);
            if (termHasWholeArray != null) ArrayPool<bool>.Shared.Return(termHasWholeArray);
            if (termHasJoinedArray != null) ArrayPool<bool>.Shared.Return(termHasJoinedArray);
            if (termHasPrefixArray != null) ArrayPool<bool>.Shared.Return(termHasPrefixArray);
            if (termFirstPosArray != null) ArrayPool<int>.Shared.Return(termFirstPosArray);
        }

        // Final Scoring
        if (!_setup.CoverWholeQuery) lcsSum = 0.0;
        
        double num11 = num2 + num + num3 + num4 - penalty;
        if (num11 == 0.0 && lcsSum > 2.0) num11 = lcsSum - 2.0;
        
        byte coverageScore = (byte)Math.Min(num11 / queryLen * 255.0, 255.0);
        
        float sumCi = 0f;
        int firstMatchIndex = -1;

        for (int i = 0; i < qCount; i++)
        {
            if (termMaxChars[i] <= 0) continue;
            float ci = Math.Min(1.0f, termMatchedChars[i] / termMaxChars[i]);
            sumCi += ci;
            if (ci > 0) termsWithAnyMatch++;
            // Relaxed definition: If we matched enough characters to cover the term, it's fully matched.
            // This allows Exact Prefix matches (where matchScore == qLen) to count as Exact,
            // which allows them to beat Fuzzy matches that happen to be earlier in the text.
            bool isFullyMatched = termMatchedChars[i] >= (termMaxChars[i] - 0.01f);
            if (isFullyMatched) termsFullyMatched++;
            
            // Strict definition: Requires Whole or Joined match.
            bool isStrict = (termHasWhole[i] || termHasJoined[i]) && isFullyMatched;
            if (isStrict) termsStrictMatched++;

            if (termHasPrefix[i]) termsPrefixMatched++;
            
            if (termFirstPos[i] >= 0)
            {
                if (firstMatchIndex == -1 || termFirstPos[i] < firstMatchIndex) firstMatchIndex = termFirstPos[i];
            }
        }
        
        return new CoverageResult(coverageScore, qCount, firstMatchIndex, sumCi);
    }

    // Internal result structure for coverage calculation
    private readonly struct CoverageResult
    {
        public readonly byte CoverageScore;
        public readonly int TermsCount;
        public readonly int FirstMatchIndex;
        public readonly float SumCi;
        
        public CoverageResult(byte coverageScore, int termsCount, int firstMatchIndex, float sumCi)
        {
            CoverageScore = coverageScore;
            TermsCount = termsCount;
            FirstMatchIndex = firstMatchIndex;
            SumCi = sumCi;
        }
    }

    private int TokenizeToSpan(string text, Span<StringSlice> tokens, int minWordSize)
    {
        int count = 0;
        int max = tokens.Length;
        ReadOnlySpan<char> span = text.AsSpan();
        ReadOnlySpan<char> delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];

        int currentPos = 0;

        while (!span.IsEmpty)
        {
            // Skip delimiters
            int nextTokenIndex = span.IndexOfAnyExcept(delimiters);
            if (nextTokenIndex < 0) break;

            span = span[nextTokenIndex..];
            currentPos += nextTokenIndex;

            // Find end of token
            int delimiterIndex = span.IndexOfAny(delimiters);
            int tokenLen = (delimiterIndex < 0) ? span.Length : delimiterIndex;

            if (tokenLen >= minWordSize)
            {
                if (count < max)
                {
                    // Note: Hash is computed in deduplication step to keep this tight loop faster
                    tokens[count++] = new StringSlice(currentPos, tokenLen, currentPos, 0);
                }
            }

            currentPos += tokenLen;
            if (delimiterIndex < 0) break;
            span = span[tokenLen..];
        }
        
        return count;
    }
}

