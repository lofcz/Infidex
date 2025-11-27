namespace Infidex.Coverage;

internal readonly struct CoverageResult(
    byte coverageScore, 
    int termsCount, 
    int firstMatchIndex, 
    float sumCi, 
    float lastTermCi = 0f,
    float weightedCoverage = 0f,
    bool lastTermIsTypeAhead = false,
    float idfCoverage = 0f,
    float totalIdf = 0f,
    float missingIdf = 0f,
    float[]? termIdf = null,
    float[]? termCi = null)
{
    public readonly byte CoverageScore = coverageScore;
    public readonly int TermsCount = termsCount;
    public readonly int FirstMatchIndex = firstMatchIndex;
    public readonly float SumCi = sumCi;
    public readonly float LastTermCi = lastTermCi;
    public readonly float WeightedCoverage = weightedCoverage;
    public readonly bool LastTermIsTypeAhead = lastTermIsTypeAhead;
    public readonly float IdfCoverage = idfCoverage;        // Information-weighted coverage
    public readonly float TotalIdf = totalIdf;              // Total information in query
    public readonly float MissingIdf = missingIdf;          // Information mass of unmatched terms
    public readonly float[]? TermIdf = termIdf;             // Per-token IDF array (clean word-level)
    public readonly float[]? TermCi = termCi;               // Per-token coverage (Ci) array
}
