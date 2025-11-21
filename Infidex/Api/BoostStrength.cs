namespace Infidex.Api;

/// <summary>
/// Strength of boost to apply to filtered documents.
/// Boosts increase the relevance score of matching documents.
/// </summary>
public enum BoostStrength
{
    /// <summary>
    /// Low boost strength (+1 to score)
    /// </summary>
    Low = 1,
    
    /// <summary>
    /// Medium boost strength (+2 to score)
    /// </summary>
    Med = 2,
    
    /// <summary>
    /// High boost strength (+3 to score)
    /// </summary>
    High = 3
}

