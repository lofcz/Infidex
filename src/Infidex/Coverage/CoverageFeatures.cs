namespace Infidex.Coverage;

public readonly struct CoverageFeatures
{
    public readonly byte CoverageScore;
    public readonly int TermsCount;
    public readonly int TermsWithAnyMatch;
    public readonly int TermsFullyMatched;
    public readonly int TermsStrictMatched;
    public readonly int TermsPrefixMatched;
    public readonly int FirstMatchIndex;
    public readonly float SumCi;
    public readonly int WordHits;
    public readonly int DocTokenCount;
    public readonly int LongestPrefixRun;
    public readonly int SuffixPrefixRun;
    public readonly int PhraseSpan;
    public readonly int PrecedingStrictCount;
    public readonly bool LastTokenHasPrefix;
    public readonly float LastTermCi;
    public readonly float WeightedCoverage;
    public readonly bool LastTermIsTypeAhead;
    public readonly float IdfCoverage;      // Information-weighted coverage (IDF-based)
    public readonly float TotalIdf;         // Total information content of query
    public readonly float MissingIdf;       // Information content of unmatched terms
    
    // Per-token IDF array: clean word-level discriminative power for each query token.
    // Indexed by token position (0 to TermsCount-1). NULL if word-level IDF cache unavailable.
    public readonly float[]? TermIdf;
    
    // Per-token coverage (Ci) array: character coverage ratio for each query token.
    // Indexed by token position (0 to TermsCount-1). NULL if not computed.
    public readonly float[]? TermCi;
    
    // Precomputed fusion signals (Lucene-style: no string ops in fusion layer)
    public readonly FusionSignals FusionSignals;

    public CoverageFeatures(
        byte coverageScore,
        int termsCount,
        int termsWithAnyMatch,
        int termsFullyMatched,
        int termsStrictMatched,
        int termsPrefixMatched,
        int firstMatchIndex,
        float sumCi,
        int wordHits,
        int docTokenCount,
        int longestPrefixRun,
        int suffixPrefixRun,
        int phraseSpan,
        int precedingStrictCount = 0,
        bool lastTokenHasPrefix = false,
        float lastTermCi = 0f,
        float weightedCoverage = 0f,
        bool lastTermIsTypeAhead = false,
        float idfCoverage = 0f,
        float totalIdf = 0f,
        float missingIdf = 0f,
        float[]? termIdf = null,
        float[]? termCi = null,
        FusionSignals fusionSignals = default)
    {
        CoverageScore = coverageScore;
        TermsCount = termsCount;
        TermsWithAnyMatch = termsWithAnyMatch;
        TermsFullyMatched = termsFullyMatched;
        TermsStrictMatched = termsStrictMatched;
        TermsPrefixMatched = termsPrefixMatched;
        FirstMatchIndex = firstMatchIndex;
        SumCi = sumCi;
        WordHits = wordHits;
        DocTokenCount = docTokenCount;
        LongestPrefixRun = longestPrefixRun;
        SuffixPrefixRun = suffixPrefixRun;
        PhraseSpan = phraseSpan;
        PrecedingStrictCount = precedingStrictCount;
        LastTokenHasPrefix = lastTokenHasPrefix;
        LastTermCi = lastTermCi;
        WeightedCoverage = weightedCoverage;
        LastTermIsTypeAhead = lastTermIsTypeAhead;
        IdfCoverage = idfCoverage;
        TotalIdf = totalIdf;
        MissingIdf = missingIdf;
        TermIdf = termIdf;
        TermCi = termCi;
        FusionSignals = fusionSignals;
    }
}
