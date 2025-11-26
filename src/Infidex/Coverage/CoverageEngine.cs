using System.Buffers;
using Infidex.Tokenization;

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
        CoverageResult result = CalculateCoverageInternal(query, documentText, lcsSum, out wordHits, 
            out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        return result.CoverageScore;
    }
    
    public ushort CalculateRankedScore(string query, string documentText, double lcsSum, byte baseTfidfScore, out int wordHits)
    {
        CoverageResult result = CalculateCoverageInternal(query, documentText, lcsSum, out wordHits,
            out int docTokenCount,
            out int termsWithAnyMatch,
            out int termsFullyMatched,
            out int termsStrictMatched,
            out int termsPrefixMatched,
            out _,  // longestPrefixRun
            out _,  // suffixPrefixRun
            out _,  // phraseSpan
            out _,  // precedingStrictCount
            out _); // lastTokenHasPrefix
        
        return CoverageScorer.CalculateRankedScore(
            result,
            docTokenCount,
            wordHits,
            termsWithAnyMatch,
            termsFullyMatched,
            termsStrictMatched,
            termsPrefixMatched,
            baseTfidfScore);
    }

    public CoverageFeatures CalculateFeatures(string query, string documentText, double lcsSum)
    {
        CoverageResult result = CalculateCoverageInternal(
            query,
            documentText,
            lcsSum,
            out int wordHits,
            out int docTokenCount,
            out int termsWithAnyMatch,
            out int termsFullyMatched,
            out int termsStrictMatched,
            out int termsPrefixMatched,
            out int longestPrefixRun,
            out int suffixPrefixRun,
            out int phraseSpan,
            out int precedingStrictCount,
            out bool lastTokenHasPrefix);

        return new CoverageFeatures(
            result.CoverageScore,
            result.TermsCount,
            termsWithAnyMatch,
            termsFullyMatched,
            termsStrictMatched,
            termsPrefixMatched,
            result.FirstMatchIndex,
            result.SumCi,
            wordHits,
            docTokenCount,
            longestPrefixRun,
            suffixPrefixRun,
            phraseSpan,
            precedingStrictCount,
            lastTokenHasPrefix);
    }

    private CoverageResult CalculateCoverageInternal(string query, string documentText, double lcsSum, 
        out int wordHits,
        out int docTokenCount,
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
        wordHits = 0;
        docTokenCount = 0;
        termsWithAnyMatch = 0;
        termsFullyMatched = 0;
        termsStrictMatched = 0;
        termsPrefixMatched = 0;
        longestPrefixRun = 0;
        suffixPrefixRun = 0;
        phraseSpan = 0;
        precedingStrictCount = 0;
        lastTokenHasPrefix = false;
        
        if (query.Length == 0) 
            return new CoverageResult(0, 0, -1, 0);

        ReadOnlySpan<char> delimiters = _tokenizer.TokenizerSetup?.Delimiters ?? [' '];
        int queryLen = query.Length;
        int docLen = documentText.Length;

        // Allocate token arrays
        int maxQueryTokens = queryLen / 2 + 1;
        StringSlice[]? queryTokenArray = null;
        Span<StringSlice> queryTokens = maxQueryTokens <= CoverageTokenizer.MaxStackTerms 
            ? stackalloc StringSlice[maxQueryTokens] 
            : (queryTokenArray = ArrayPool<StringSlice>.Shared.Rent(maxQueryTokens));

        int qCountRaw = CoverageTokenizer.TokenizeToSpan(query, queryTokens, _setup.MinWordSize, delimiters);
        if (qCountRaw == 0)
        {
            if (queryTokenArray != null) ArrayPool<StringSlice>.Shared.Return(queryTokenArray);
            return new CoverageResult(0, 0, -1, 0);
        }

        ReadOnlySpan<char> querySpan = query.AsSpan();
        int qCount = CoverageTokenizer.DeduplicateQueryTokens(queryTokens, qCountRaw, querySpan);

        int maxDocTokens = docLen / 2 + 1;
        StringSlice[]? docTokenArray = null;
        Span<StringSlice> docTokens = maxDocTokens <= CoverageTokenizer.MaxStackTerms
            ? stackalloc StringSlice[maxDocTokens]
            : (docTokenArray = ArrayPool<StringSlice>.Shared.Rent(maxDocTokens));

        ReadOnlySpan<char> docSpan = documentText.AsSpan();
        int dCountRaw = CoverageTokenizer.TokenizeToSpan(documentText, docTokens, _setup.MinWordSize, delimiters);
        docTokenCount = dCountRaw;

        StringSlice[]? uniqueDocTokenArray = null;
        Span<StringSlice> uniqueDocTokens = dCountRaw <= CoverageTokenizer.MaxStackTerms
            ? stackalloc StringSlice[dCountRaw]
            : (uniqueDocTokenArray = ArrayPool<StringSlice>.Shared.Rent(dCountRaw));

        int dCount = CoverageTokenizer.DeduplicateDocTokens(docTokens, dCountRaw, uniqueDocTokens, docSpan);

        // Tracking arrays
        bool[]? qActiveArray = null;
        Span<bool> qActive = qCount <= CoverageTokenizer.MaxStackTerms 
            ? stackalloc bool[qCount] 
            : (qActiveArray = ArrayPool<bool>.Shared.Rent(qCount));
        qActive[..qCount].Fill(true);

        bool[]? dActiveArray = null;
        Span<bool> dActive = dCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc bool[dCount]
            : (dActiveArray = ArrayPool<bool>.Shared.Rent(dCount));
        dActive[..dCount].Fill(true);

        float[]? termMatchedCharsArray = null;
        Span<float> termMatchedChars = qCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc float[qCount]
            : (termMatchedCharsArray = ArrayPool<float>.Shared.Rent(qCount));
        termMatchedChars[..qCount].Clear();

        int[]? termMaxCharsArray = null;
        Span<int> termMaxChars = qCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc int[qCount]
            : (termMaxCharsArray = ArrayPool<int>.Shared.Rent(qCount));
        
        bool[]? termHasWholeArray = null;
        Span<bool> termHasWhole = qCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc bool[qCount]
            : (termHasWholeArray = ArrayPool<bool>.Shared.Rent(qCount));
        termHasWhole[..qCount].Clear();

        bool[]? termHasJoinedArray = null;
        Span<bool> termHasJoined = qCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc bool[qCount]
            : (termHasJoinedArray = ArrayPool<bool>.Shared.Rent(qCount));
        termHasJoined[..qCount].Clear();

        bool[]? termHasPrefixArray = null;
        Span<bool> termHasPrefix = qCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc bool[qCount]
            : (termHasPrefixArray = ArrayPool<bool>.Shared.Rent(qCount));
        termHasPrefix[..qCount].Clear();

        int[]? termFirstPosArray = null;
        Span<int> termFirstPos = qCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc int[qCount]
            : (termFirstPosArray = ArrayPool<int>.Shared.Rent(qCount));
        termFirstPos[..qCount].Fill(-1);

        for (int i = 0; i < qCount; i++) 
            termMaxChars[i] = queryTokens[i].Length;

        // Build MatchState
        MatchState state = new MatchState
        {
            QueryTokens = queryTokens[..qCount],
            UniqueDocTokens = uniqueDocTokens[..dCount],
            QActive = qActive[..qCount],
            DActive = dActive[..dCount],
            TermMatchedChars = termMatchedChars[..qCount],
            TermMaxChars = termMaxChars[..qCount],
            TermHasWhole = termHasWhole[..qCount],
            TermHasJoined = termHasJoined[..qCount],
            TermHasPrefix = termHasPrefix[..qCount],
            TermFirstPos = termFirstPos[..qCount],
            QuerySpan = querySpan,
            DocSpan = docSpan,
            QCount = qCount,
            DCount = dCount,
            DocTokenCount = docTokenCount
        };

        try
        {
            if (_setup.CoverWholeWords)
                WholeWordMatcher.Match(ref state);

            if (_setup.CoverJoinedWords && qCount > 0)
                JoinedWordMatcher.Match(ref state);

            if (_setup.CoverPrefixSuffix && qCount > 0)
                PrefixSuffixMatcher.Match(ref state);

            if (_setup.CoverFuzzyWords && qCount > 0 && !FuzzyWordMatcher.AllTermsFullyMatched(ref state))
                FuzzyWordMatcher.Match(ref state, _setup.MinWordSize, _setup.LevenshteinMaxWordSize);
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

        wordHits = state.WordHits;

        return CoverageScorer.CalculateFinalScore(
            ref state,
            queryLen,
            lcsSum,
            _setup.CoverWholeQuery,
            out termsWithAnyMatch,
            out termsFullyMatched,
            out termsStrictMatched,
            out termsPrefixMatched,
            out longestPrefixRun,
            out suffixPrefixRun,
            out phraseSpan,
            out precedingStrictCount,
            out lastTokenHasPrefix);
    }
}
