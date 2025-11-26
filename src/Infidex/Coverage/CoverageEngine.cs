using System.Buffers;
using System.Collections.Concurrent;
using Infidex.Tokenization;
using Infidex.Core;
using Infidex.Indexing;

namespace Infidex.Coverage;

public class CoverageEngine
{
    private readonly Tokenizer _tokenizer;
    private readonly CoverageSetup _setup;
    private TermCollection? _termCollection;
    private int _totalDocuments;
    private readonly ConcurrentDictionary<string, float[]> _queryIdfCache = new();
    
    public CoverageEngine(Tokenizer tokenizer, CoverageSetup? setup = null)
    {
        _tokenizer = tokenizer;
        _setup = setup ?? CoverageSetup.CreateDefault();
    }
    
    /// <summary>
    /// Sets the term collection and document count for IDF computation.
    /// Should be called once after indexing is complete.
    /// </summary>
    public void SetCorpusStatistics(TermCollection termCollection, int totalDocuments)
    {
        _termCollection = termCollection;
        _totalDocuments = totalDocuments;
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
            lastTokenHasPrefix,
            result.LastTermCi,
            result.WeightedCoverage,
            result.LastTermIsTypeAhead,
            result.IdfCoverage,
            result.TotalIdf,
            result.MissingIdf);
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

        float[]? termIdfArray = null;
        Span<float> termIdf = qCount <= CoverageTokenizer.MaxStackTerms
            ? stackalloc float[qCount]
            : (termIdfArray = ArrayPool<float>.Shared.Rent(qCount));

        // Precompute per-query term IDF once per distinct query text to avoid
        // repeating n-gram lookups for every candidate document.
        if (_termCollection != null && _totalDocuments > 0)
        {
            if (!_queryIdfCache.TryGetValue(query, out float[]? cached) || cached.Length < qCount)
            {
                cached = new float[qCount];
                for (int i = 0; i < qCount; i++)
                {
                    cached[i] = ComputeTermIdf(queryTokens[i], querySpan);
                }
                _queryIdfCache[query] = cached;
            }

            for (int i = 0; i < qCount; i++)
            {
                termMaxChars[i] = queryTokens[i].Length;
                termIdf[i] = cached[i];
            }
        }
        else
        {
            // Fallback: approximate IDF from term length only.
            for (int i = 0; i < qCount; i++)
            {
                termMaxChars[i] = queryTokens[i].Length;
                termIdf[i] = MathF.Log2(termMaxChars[i] + 1);
            }
        }

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
            TermIdf = termIdf[..qCount],
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
            if (termIdfArray != null) ArrayPool<float>.Shared.Return(termIdfArray);
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
    
    /// <summary>
    /// Computes IDF for a query term by averaging IDF over its constituent n-grams.
    /// Returns a default value if term collection is not available.
    /// </summary>
    private float ComputeTermIdf(StringSlice termSlice, ReadOnlySpan<char> querySpan)
    {
        if (_termCollection == null || _totalDocuments == 0)
        {
            // Fallback: use term length as a proxy for information content
            return MathF.Log2(termSlice.Length + 1);
        }
        
        ReadOnlySpan<char> termSpan = querySpan.Slice(termSlice.Offset, termSlice.Length);
        
        // Generate n-grams for this term and compute average IDF
        int[] ngramSizes = _tokenizer.IndexSizes;
        float idfSum = 0f;
        int ngramCount = 0;
        
        foreach (int size in ngramSizes)
        {
            if (termSpan.Length < size)
                continue;
                
            for (int i = 0; i <= termSpan.Length - size; i++)
            {
                ReadOnlySpan<char> ngram = termSpan.Slice(i, size);
                string ngramText = new string(ngram);
                
                Term? term = _termCollection.GetTerm(ngramText);
                if (term != null && term.DocumentFrequency > 0)
                {
                    float idf = Bm25Scorer.ComputeIdf(_totalDocuments, term.DocumentFrequency);
                    idfSum += idf;
                    ngramCount++;
                }
            }
        }
        
        // Return average IDF, or a default based on term length if no n-grams found
        return ngramCount > 0 
            ? idfSum / ngramCount 
            : MathF.Log2(termSpan.Length + 1);
    }
}
