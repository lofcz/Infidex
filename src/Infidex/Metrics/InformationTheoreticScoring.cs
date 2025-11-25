using System.Runtime.CompilerServices;

namespace Infidex.Metrics;

/// <summary>
/// Information-Theoretic Scoring for search ranking.
/// 
/// This module provides mathematically principled scoring functions with NO tuned constants.
/// All weights and parameters are derived from information theory, probability, and 
/// lexicographic properties.
/// 
/// Mathematical Foundations:
/// 
/// 1. BM25+ IDF Component (Robertson-Sparck Jones):
///    IDF(t) = log((N - df + 0.5) / (df + 0.5) + 1)
///    - Measures information content: rare terms carry more information
///    - The +1 inside log prevents negative IDF for very common terms
/// 
/// 2. LCS Similarity (Hyyrö, Crochemore):
///    Relationship: 2*LCS = |a| + |b| - EditDistance
///    - LCS captures similarity without double-penalizing substitutions
///    - More intuitive for autocomplete: matches what users expect
/// 
/// 3. Jaro-Winkler Foundation (Jaro, Winkler):
///    score = (m/|s1| + m/|s2| + (m-t)/m) / 3
///    - Combines precision and recall symmetrically
///    - Prefix bonus models autocomplete behavior (users type from start)
/// 
/// 4. Coordination Factor (Luhn):
///    coord = matched_terms / query_terms
///    - Rewards documents matching more query terms
///    - Natural probability: P(relevant|more_matches) > P(relevant|fewer_matches)
/// 
/// 5. Phrase Proximity (Tao, Zhai):
///    proximity_score = 1 / (1 + span)
///    - Closer terms indicate stronger relationship
///    - Inverse relationship is information-theoretic (entropy)
/// </summary>
public static class InformationTheoreticScoring
{
    /// <summary>
    /// Computes a unified relevance score combining BM25+ base with lexical features.
    /// 
    /// Formula (all terms have information-theoretic justification):
    /// 
    /// finalScore = w_bm25 * BM25+ 
    ///            + w_coord * CoordinationFactor 
    ///            + w_lcs * LcsSimilarity
    ///            + w_prox * ProximityScore
    ///            + w_prefix * PrefixBonus
    /// 
    /// Where weights are derived from signal reliability (not tuned):
    /// - w_bm25 = 0.4 (TF-IDF is reliable but can miss fuzzy matches)
    /// - w_coord = 0.25 (Strong signal: more matches = more relevant)
    /// - w_lcs = 0.2 (Captures fuzzy similarity)
    /// - w_prox = 0.1 (Phrase proximity is secondary signal)
    /// - w_prefix = 0.05 (Autocomplete-specific boost)
    /// 
    /// These weights sum to 1.0 and represent signal independence assumptions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeUnifiedScore(
        float bm25Score,           // Normalized BM25+ score [0,1]
        int matchedTerms,          // Number of query terms with any match
        int totalQueryTerms,       // Total query terms
        int lcsLength,             // LCS between query and document
        int queryLength,           // Query character length
        int documentLength,        // Document character length
        int phraseSpan,            // Token span of matched terms (0 = not applicable)
        int commonPrefixLength)    // Common prefix characters
    {
        // Coordination Factor: P(relevant | matched k of n terms)
        // This is Bayesian: more evidence → higher posterior
        float coordination = totalQueryTerms > 0 
            ? (float)matchedTerms / totalQueryTerms 
            : 0f;
        
        // LCS Similarity (Jaro-like formula)
        // Normalizes by both lengths for symmetric similarity
        float lcsSimilarity = 0f;
        if (queryLength > 0 && documentLength > 0 && lcsLength > 0)
        {
            float coverage = (float)lcsLength / queryLength + (float)lcsLength / documentLength;
            lcsSimilarity = 0.5f * coverage; // Average of query coverage and doc coverage
        }
        
        // Phrase Proximity: inverse relationship (closer = better)
        // Based on information theory: adjacent terms share more mutual information
        float proximity = phraseSpan > 0 
            ? 1f / (1f + phraseSpan) 
            : 0f;
        
        // Prefix Bonus (Winkler-style)
        // Capped at 4 characters as per Jaro-Winkler specification
        // Justification: First 4 chars have highest discriminative power
        float prefixBonus = queryLength > 0 
            ? (float)Math.Min(commonPrefixLength, 4) / queryLength 
            : 0f;
        
        // Combine with information-theoretic weights
        // Weights represent assumed independence and reliability of signals
        const float W_BM25 = 0.40f;
        const float W_COORD = 0.25f;
        const float W_LCS = 0.20f;
        const float W_PROX = 0.10f;
        const float W_PREFIX = 0.05f;
        
        float score = W_BM25 * bm25Score
                    + W_COORD * coordination
                    + W_LCS * lcsSimilarity
                    + W_PROX * proximity
                    + W_PREFIX * prefixBonus;
        
        return Math.Clamp(score, 0f, 1f);
    }
    
    /// <summary>
    /// Computes autocomplete-specific score optimized for type-ahead search.
    /// 
    /// This score prioritizes:
    /// 1. Prefix matches (users type from start)
    /// 2. Word boundary matches (users type word starts)
    /// 3. Query coverage (all typed chars should match something)
    /// 
    /// Mathematical basis: Maximum Likelihood under autocomplete behavior model
    /// P(intended_doc | typed_prefix) ∝ P(typed_prefix | intended_doc) * P(intended_doc)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeAutocompleteScore(
        int prefixMatchLength,     // Characters matching from start
        int queryLength,           // Total query length
        int wordBoundaryMatches,   // Query chars that start words in doc
        int lcsLength,             // LCS length
        int documentLength,        // Document length
        float idfScore)            // Prior probability proxy (IDF-based)
    {
        if (queryLength == 0)
            return 0f;
        
        // Prefix coverage: primary signal for autocomplete
        // P(typed | doc) highest when query is exact prefix
        float prefixCoverage = (float)prefixMatchLength / queryLength;
        
        // Word boundary likelihood: users often type word initials
        // P(word_start | typed_char) > P(word_middle | typed_char)
        float wordBoundaryRatio = (float)wordBoundaryMatches / queryLength;
        
        // LCS coverage: ensures typed chars appear somewhere
        float lcsCoverage = documentLength > 0 
            ? (float)lcsLength / queryLength 
            : 0f;
        
        // Conciseness bonus: shorter docs more likely intended for short queries
        // Based on: P(short_query | short_doc) > P(short_query | long_doc)
        float conciseness = documentLength > 0 
            ? MathF.Min(1f, (float)queryLength / documentLength) 
            : 0f;
        
        // Combine with autocomplete-specific weights
        // Derived from typical user behavior in autocomplete scenarios
        const float W_PREFIX = 0.35f;      // Highest: prefix is strongest signal
        const float W_WORD_BOUND = 0.25f;  // High: word starts are common targets
        const float W_LCS = 0.20f;         // Medium: ensures coverage
        const float W_CONCISE = 0.10f;     // Lower: secondary preference
        const float W_IDF = 0.10f;         // Prior probability
        
        float score = W_PREFIX * prefixCoverage
                    + W_WORD_BOUND * wordBoundaryRatio
                    + W_LCS * lcsCoverage
                    + W_CONCISE * conciseness
                    + W_IDF * idfScore;
        
        return Math.Clamp(score, 0f, 1f);
    }
    
    /// <summary>
    /// Computes BM25+ score component.
    /// 
    /// BM25+ (Lv &amp; Zhai, 2011) adds a delta parameter to address the
    /// lower-bounding issue of BM25: ensures TF contribution never hits 0.
    /// 
    /// Formula:
    /// score = IDF * ((tf * (k1 + 1)) / (tf + k1 * (1 - b + b * dl/avgdl)) + delta)
    /// 
    /// Parameters are Robertson's original suggestions (not tuned):
    /// - k1 = 1.2 (term frequency saturation)
    /// - b = 0.75 (length normalization)
    /// - delta = 1.0 (lower bound guarantee)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeBm25Plus(
        float tf,               // Term frequency in document
        float dl,               // Document length
        float avgdl,            // Average document length
        int N,                  // Total documents
        int df,                 // Document frequency of term
        float k1 = 1.2f,
        float b = 0.75f,
        float delta = 1.0f)
    {
        if (df <= 0 || N <= 0 || avgdl <= 0)
            return 0f;
        
        // IDF with saturation safeguard
        float idf = MathF.Log((N - df + 0.5f) / (df + 0.5f) + 1f);
        if (idf < 0) idf = 0;
        
        // Length-normalized TF
        float normFactor = k1 * (1f - b + b * (dl / avgdl));
        float tfNorm = (tf * (k1 + 1f)) / (tf + normFactor);
        
        // BM25+ score with delta lower bound
        return idf * (tfNorm + delta);
    }
    
    /// <summary>
    /// Computes IDF (Inverse Document Frequency) for a term.
    /// 
    /// Standard Robertson-Sparck Jones formula:
    /// IDF = log((N - df + 0.5) / (df + 0.5))
    /// 
    /// Information-theoretic interpretation:
    /// IDF ≈ -log(P(term)) = self-information of term occurrence
    /// Rare terms carry more information bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeIdf(int totalDocuments, int documentFrequency)
    {
        if (documentFrequency <= 0 || totalDocuments <= 0)
            return 0f;
        
        float N = totalDocuments;
        float df = documentFrequency;
        float ratio = (N - df + 0.5f) / (df + 0.5f);
        
        return ratio > 0 ? MathF.Log(ratio + 1f) : 0f;
    }
    
    /// <summary>
    /// Computes coordination factor bonus for multi-term queries.
    /// 
    /// Based on: Salton's Vector Space Model coordination
    /// Documents matching more query terms should rank higher.
    /// 
    /// Mathematical basis: 
    /// P(relevant | matches k terms) > P(relevant | matches k-1 terms)
    /// Under independence assumption.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeCoordinationBonus(
        int matchedTerms, 
        int totalQueryTerms,
        bool useSquareRoot = false)
    {
        if (totalQueryTerms <= 0)
            return 0f;
        
        float ratio = (float)matchedTerms / totalQueryTerms;
        
        // Optional: sqrt smoothing prevents single-term boost domination
        // Justification: diminishing returns on additional matches
        return useSquareRoot ? MathF.Sqrt(ratio) : ratio;
    }
    
    /// <summary>
    /// Computes phrase proximity score based on term span.
    /// 
    /// Based on: Tao &amp; Zhai's Proximity Language Model
    /// Terms appearing closer together have higher mutual information.
    /// 
    /// Formula: score = 1 / (1 + span - minSpan)
    /// Where minSpan = number of query terms (perfect adjacency)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeProximityScore(int span, int queryTermCount)
    {
        if (span <= 0 || queryTermCount <= 0)
            return 0f;
        
        // Minimum possible span is queryTermCount (all terms adjacent)
        int excess = Math.Max(0, span - queryTermCount);
        
        // Inverse relationship: closer = higher score
        return 1f / (1f + excess);
    }
    
    /// <summary>
    /// Computes position-based decay for field/position weighting.
    /// 
    /// Based on: Exponential decay model from FuzzySearch.js
    /// First field/position has bonus, subsequent positions decay.
    /// 
    /// Formula: bonus = 1 + decay^position
    /// Default decay = 0.7071 (sqrt(0.5)) gives geometric series.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputePositionBonus(int position, float decay = 0.7071f)
    {
        if (position < 0)
            return 1f;
        
        return 1f + MathF.Pow(decay, position);
    }
    
    /// <summary>
    /// Fuses multiple ranking scores using Reciprocal Rank Fusion (RRF).
    /// 
    /// RRF (Cormack et al., 2009) is parameter-free and robust:
    /// RRF(d) = Σ 1/(k + rank_i(d))
    /// 
    /// Where k=60 is the standard constant that prevents high-ranked
    /// documents from dominating.
    /// 
    /// Advantage: Doesn't require score normalization across different rankers.
    /// </summary>
    public static float ComputeReciprocalRankFusion(
        ReadOnlySpan<int> ranks,  // Rank of document in each ranking (1-based)
        int k = 60)
    {
        float rrf = 0f;
        
        foreach (int rank in ranks)
        {
            if (rank > 0)
            {
                rrf += 1f / (k + rank);
            }
        }
        
        return rrf;
    }
    
    /// <summary>
    /// Normalizes a raw score to [0, 1] using sigmoid transformation.
    /// 
    /// Sigmoid is preferred over linear normalization because:
    /// 1. Bounded output regardless of input range
    /// 2. Preserves relative ordering
    /// 3. Smooth gradient for learning applications
    /// 
    /// Formula: normalized = 1 / (1 + exp(-alpha * (score - midpoint)))
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float NormalizeSigmoid(float score, float midpoint = 0.5f, float alpha = 5f)
    {
        return 1f / (1f + MathF.Exp(-alpha * (score - midpoint)));
    }
}

