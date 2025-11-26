namespace Infidex.Metrics;

/// <summary>
/// Probabilistic model for human typing errors on a word.
/// 
/// Assumes each character in a word of length L is independently
/// "corrupted" (insert / delete / substitute / transpose) with
/// probability <c>p</c>. Then the total number of edits D between the
/// intended word and the typed word is approximately
/// D ~ Binomial(L, p).
/// 
/// For a given word length L, we define a principled maximum edit
/// distance d_max(L) as the smallest integer such that:
/// 
///   Pr[D ≤ d_max(L)] ≥ 1 - α
/// 
/// where α is a small tail probability (e.g. 0.01 or 0.05).
/// </summary>
internal static class EditDistanceModel
{
    /// <summary>
    /// Computes the smallest integer d such that
    ///   Pr[Binomial(L, p) ≤ d] ≥ 1 - alpha.
    /// 
    /// This gives a principled maximum edit distance for a word of
    /// length <paramref name="length"/> under an i.i.d. character
    /// error model with per-character error probability <paramref name="p"/>.
    /// 
    /// For the typical "human typo" regime, p should be small
    /// (e.g. 0.03–0.05) and alpha should be a small tail mass
    /// (e.g. 0.01 or 0.05).
    /// </summary>
    /// <param name="length">Word length L (must be ≥ 0).</param>
    /// <param name="p">
    /// Per-character error probability. Must satisfy 0 &lt; p &lt; 1 for
    /// the Binomial model; values outside this range are clamped to
    /// the nearest meaningful behavior.
    /// </param>
    /// <param name="alpha">
    /// Tail probability α (0 &lt; α &lt; 1). We choose d so that the
    /// remaining tail Pr[D &gt; d] is at most α.
    /// </param>
    /// <returns>
    /// The minimum d such that Pr[D ≤ d] ≥ 1 - α.
    /// Returns 0 when length is 0 or p is effectively 0; returns
    /// <paramref name="length"/> when p is effectively 1.
    /// </returns>
    public static int GetMaxEditsForLength(int length, double p = 0.04, double alpha = 0.01)
    {
        if (length <= 0)
            return 0;

        switch (p)
        {
            // Degenerate cases: no noise or full noise.
            case <= 0.0:
                return 0;
            case >= 1.0:
                return length;
        }

        alpha = alpha switch
        {
            <= 0.0 => 1e-9,
            >= 1.0 => 0.999999999,
            _ => alpha
        };

        double targetCdf = 1.0 - alpha;

        // Start from k = 0: P(D = 0) = (1 - p)^L.
        double q = 1.0 - p;
        double probK = Math.Pow(q, length);
        double cdf = probK;

        int k = 0;
        while (k < length && cdf < targetCdf)
        {
            // Recurrence for Binomial PMF:
            // P(D = k+1) = P(D = k) * (L - k) / (k + 1) * p / (1 - p)
            double multiplier = (double)(length - k) / (k + 1) * (p / q);
            probK *= multiplier;
            cdf += probK;
            k++;
        }

        return k;
    }
}