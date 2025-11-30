namespace Infidex.Tokenization;

/// <summary>
/// Compact value-type key for short character n-grams (2–3 chars).
/// Encodes length and characters into a single 64-bit value so it can be
/// used as an efficient dictionary key without allocating strings.
/// </summary>
internal readonly struct NGramKey : IEquatable<NGramKey>
{
    // Layout (little-endian):
    //  bits 0-15   : length (0-3)
    //  bits 16-31  : char0
    //  bits 32-47  : char1
    //  bits 48-63  : char2
    private readonly ulong _packed;

    public ulong Value => _packed;

    public NGramKey(ReadOnlySpan<char> span)
    {
        if ((uint)span.Length is < 2 or > 3)
            throw new ArgumentOutOfRangeException(nameof(span), "NGramKey supports lengths 2–3 only.");

        ulong len = (ulong)span.Length & 0xFFFFu;
        ulong c0 = span.Length > 0 ? (ulong)span[0] & 0xFFFFu : 0u;
        ulong c1 = span.Length > 1 ? (ulong)span[1] & 0xFFFFu : 0u;
        ulong c2 = span.Length > 2 ? (ulong)span[2] & 0xFFFFu : 0u;

        _packed = len
                  | (c0 << 16)
                  | (c1 << 32)
                  | (c2 << 48);
    }

    /// <summary>
    /// Length of the n-gram (2–3).
    /// </summary>
    public int Length => (int)(_packed & 0xFFFFu);

    /// <summary>
    /// Decodes the key back into a string.
    /// This is only used when we need the canonical term text (e.g., FST).
    /// </summary>
    public string ToText()
    {
        int len = Length;
        if (len is < 2 or > 3)
            return string.Empty;

        char c0 = (char)((_packed >> 16) & 0xFFFFu);
        char c1 = (char)((_packed >> 32) & 0xFFFFu);
        char c2 = (char)((_packed >> 48) & 0xFFFFu);

        return len switch
        {
            2 => string.Create(2, (c0, c1), static (span, state) =>
            {
                span[0] = state.c0;
                span[1] = state.c1;
            }),
            3 => string.Create(3, (c0, c1, c2), static (span, state) =>
            {
                span[0] = state.c0;
                span[1] = state.c1;
                span[2] = state.c2;
            }),
            _ => string.Empty
        };
    }

    public bool Equals(NGramKey other) => _packed == other._packed;

    public override bool Equals(object? obj) => obj is NGramKey other && Equals(other);

    public override int GetHashCode()
    {
        // Simple 64-bit mix down to 32 bits.
        ulong x = _packed;
        x ^= x >> 33;
        x *= 0xff51afd7ed558ccdUL;
        x ^= x >> 33;
        x *= 0xc4ceb9fe1a85ec53UL;
        x ^= x >> 33;
        return (int)(x ^ (x >> 32));
    }

    public static bool operator ==(NGramKey left, NGramKey right) => left.Equals(right);
    public static bool operator !=(NGramKey left, NGramKey right) => !left.Equals(right);
}
