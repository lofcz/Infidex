namespace Infidex.Coverage;

/// <summary>
/// Configuration parameters for the Coverage algorithm (Stage 2 - Lexical Matching).
/// </summary>
public class CoverageSetup
{
    /// <summary>
    /// Minimum word length to consider in coverage calculations
    /// </summary>
    public int MinWordSize { get; set; } = 2;
    
    /// <summary>
    /// Maximum word size for Levenshtein fuzzy matching
    /// </summary>
    public int LevenshteinMaxWordSize { get; set; } = 20;

    /// <summary>
    /// Maximum number of typographical errors (0, 1 or 2) that would be tolerated.
    /// Default: 2.
    /// </summary>
    public int NumTypos { get; set; } = 2;

    /// <summary>
    /// Minimum word length for 1-typo correction to be applied.
    /// Default: 3.
    /// </summary>
    public int MinLengthOneTypo { get; set; } = 3;

    /// <summary>
    /// Minimum word length for 2-typo correction to be applied.
    /// Default: 7.
    /// </summary>
    public int MinLengthTwoTypos { get; set; } = 7;
    
    /// <summary>
    /// Minimum absolute number of word matches required
    /// </summary>
    public int CoverageMinWordHitsAbs { get; set; } = 1;
    
    /// <summary>
    /// Minimum relative word matches (from max found)
    /// </summary>
    public int CoverageMinWordHitsRelative { get; set; } = 0;
    
    /// <summary>
    /// Query length threshold for error tolerance
    /// </summary>
    public int CoverageQLimitForErrorTolerance { get; set; } = 5;
    
    /// <summary>
    /// LCS error tolerance as percentage of query length
    /// </summary>
    public double CoverageLcsErrorToleranceRelativeq { get; set; } = 0.2;
    
    /// <summary>
    /// Enable whole query string matching via LCS
    /// </summary>
    public bool CoverWholeQuery { get; set; } = true;
    
    /// <summary>
    /// Enable exact whole word matching
    /// </summary>
    public bool CoverWholeWords { get; set; } = true;
    
    /// <summary>
    /// Enable fuzzy word matching (Levenshtein distance ≤ 1)
    /// </summary>
    public bool CoverFuzzyWords { get; set; } = true;
    
    /// <summary>
    /// Enable joined/split word detection (e.g., "newyork" vs "new york")
    /// </summary>
    public bool CoverJoinedWords { get; set; } = true;
    
    /// <summary>
    /// Enable prefix/suffix matching
    /// </summary>
    public bool CoverPrefixSuffix { get; set; } = true;
    
    /// <summary>
    /// Enable smart result truncation
    /// </summary>
    public bool Truncate { get; set; } = true;
    
    /// <summary>
    /// Enable an additional, cheap lexical pre-screen on TF-IDF candidates
    /// before full coverage is executed. When enabled, the engine can drop
    /// documents that only match extremely common query terms (like "the")
    /// using simple token overlap heuristics, without affecting fuzzy behavior.
    /// </summary>
    public bool EnableLexicalPrescreen { get; set; } = false;
    
    /// <summary>
    /// Minimum score to avoid truncation
    /// </summary>
    public byte TruncationScore { get; set; } = 254;
    
    /// <summary>
    /// Maximum number of candidates to process from relevancy ranking
    /// </summary>
    public int CoverageDepth { get; set; } = 500;
    
    /// <summary>
    /// Creates a default coverage setup
    /// </summary>
    public CoverageSetup()
    {
    }
    
    /// <summary>
    /// Copy constructor - creates a deep copy of the coverage setup
    /// </summary>
    /// <param name="source">Source coverage setup to copy</param>
    internal CoverageSetup(CoverageSetup source)
    {
        MinWordSize = source.MinWordSize;
        LevenshteinMaxWordSize = source.LevenshteinMaxWordSize;
        NumTypos = source.NumTypos;
        MinLengthOneTypo = source.MinLengthOneTypo;
        MinLengthTwoTypos = source.MinLengthTwoTypos;
        CoverageMinWordHitsAbs = source.CoverageMinWordHitsAbs;
        CoverageMinWordHitsRelative = source.CoverageMinWordHitsRelative;
        CoverageQLimitForErrorTolerance = source.CoverageQLimitForErrorTolerance;
        CoverageLcsErrorToleranceRelativeq = source.CoverageLcsErrorToleranceRelativeq;
        CoverWholeQuery = source.CoverWholeQuery;
        CoverWholeWords = source.CoverWholeWords;
        CoverFuzzyWords = source.CoverFuzzyWords;
        CoverJoinedWords = source.CoverJoinedWords;
        CoverPrefixSuffix = source.CoverPrefixSuffix;
        Truncate = source.Truncate;
        TruncationScore = source.TruncationScore;
        CoverageDepth = source.CoverageDepth;
    }
    
    /// <summary>
    /// Creates a default coverage setup tuned for fast, robust matching.
    /// We disable Levenshtein-based fuzzy coverage here because:
    /// - WordMatcher already provides fast LD1/affix candidates.
    /// - Coverage fuzzy is expensive (O(#terms * #docWords * Levenshtein)).
    /// - LCS + whole-word + prefix/suffix are sufficient for our parity tests.
    /// This restores the original "blazingly fast" behavior for hard queries
    /// while preserving lexicographic behavior via other signals.
    /// </summary>
    public static CoverageSetup CreateDefault()
    {
        return new CoverageSetup();
    }
    
    /// <summary>
    /// Creates a minimal coverage setup (exact matching only)
    /// </summary>
    public static CoverageSetup CreateMinimal()
    {
        return new CoverageSetup
        {
            CoverWholeWords = true,
            CoverFuzzyWords = false,
            CoverJoinedWords = false,
            CoverPrefixSuffix = false,
            CoverWholeQuery = false
        };
    }
}
