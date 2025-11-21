using Infidex.Filtering;

namespace Infidex.Api;

/// <summary>
/// Represents a boost configuration that increases relevance scores
/// for documents matching a specific filter.
/// </summary>
public class Boost
{
    /// <summary>
    /// Strength of the boost to apply
    /// </summary>
    public BoostStrength BoostStrength { get; internal set; }
    
    /// <summary>
    /// Filter that identifies documents to boost
    /// </summary>
    public Filter? Filter { get; internal set; }
    
    /// <summary>
    /// Number of documents that will receive this boost
    /// </summary>
    public int DocumentsBoosted
    {
        get
        {
            return Filter?.NumberOfDocumentsInFilter ?? 0;
        }
    }
    
    /// <summary>
    /// Creates a new boost configuration
    /// </summary>
    /// <param name="filter">Filter to identify documents to boost</param>
    /// <param name="strength">Strength of boost to apply</param>
    public Boost(Filter? filter, BoostStrength strength)
    {
        Filter = filter;
        BoostStrength = strength;
    }
}

