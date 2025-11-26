namespace Infidex.Coverage;

/// <summary>
/// Mutable state passed through matching phases.
/// </summary>
internal ref struct MatchState
{
    public Span<StringSlice> QueryTokens;
    public Span<StringSlice> UniqueDocTokens;
    public Span<bool> QActive;
    public Span<bool> DActive;
    public Span<float> TermMatchedChars;
    public Span<int> TermMaxChars;
    public Span<bool> TermHasWhole;
    public Span<bool> TermHasJoined;
    public Span<bool> TermHasPrefix;
    public Span<int> TermFirstPos;
    public Span<float> TermIdf;  // IDF (information content) per query term

    public ReadOnlySpan<char> QuerySpan;
    public ReadOnlySpan<char> DocSpan;

    public int QCount;
    public int DCount;
    public int DocTokenCount;

    public int WordHits;
    public double NumWhole;
    public double NumJoined;
    public double NumFuzzy;
    public double NumPrefixSuffix;
    public byte Penalty;
}
