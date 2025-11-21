namespace Infidex.Api;

/// <summary>
/// Field importance weight for multi-field indexing.
/// Determines how much influence a field has on search relevance.
/// </summary>
public enum Weight
{
    /// <summary>
    /// High importance (multiplier: 1.5x)
    /// Use for primary fields like titles, names, or headings.
    /// </summary>
    High = 0,
    
    /// <summary>
    /// Medium importance (multiplier: 1.25x)
    /// Use for secondary fields like descriptions or summaries.
    /// </summary>
    Med = 1,
    
    /// <summary>
    /// Low importance (multiplier: 1.0x)
    /// Use for tertiary fields like categories, tags, or metadata.
    /// </summary>
    Low = 2
}

