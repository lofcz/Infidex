using Infidex.Coverage;
using Infidex.Filtering;

namespace Infidex.Api;

/// <summary>
/// Comprehensive search query with support for faceting, filtering, sorting, and boosting.
/// </summary>
public class Query
{
    /// <summary>
    /// The search query text
    /// </summary>
    public string Text { get; set; } = string.Empty;
    
    /// <summary>
    /// Maximum number of records to return
    /// </summary>
    public int MaxNumberOfRecordsToReturn { get; set; } = 10;
    
    /// <summary>
    /// Enable coverage engine for lexical matching
    /// </summary>
    public bool EnableCoverage { get; set; } = true;
    
    /// <summary>
    /// Enable facet calculation in results
    /// </summary>
    public bool EnableFacets { get; set; }
    
    /// <summary>
    /// Enable boost application to filtered documents
    /// </summary>
    public bool EnableBoost { get; set; }
    
    /// <summary>
    /// Number of top TF-IDF candidates to pass to coverage engine
    /// </summary>
    public int CoverageDepth { get; set; } = 500;
    
    /// <summary>
    /// Custom coverage setup (overrides engine default if provided)
    /// </summary>
    public CoverageSetup? CoverageSetup { get; set; }
    
    /// <summary>
    /// Filter to apply to search results
    /// </summary>
    public Filter? Filter { get; set; }
    
    /// <summary>
    /// Boost configurations to apply
    /// </summary>
    public Boost[]? Boosts { get; set; }
    
    /// <summary>
    /// Field to sort results by (null for relevance sorting)
    /// </summary>
    public Field? SortBy { get; set; }
    
    /// <summary>
    /// Sort direction (true for ascending, false for descending)
    /// </summary>
    public bool SortAscending { get; set; }
    
    /// <summary>
    /// Remove duplicate document keys (consolidate segments)
    /// </summary>
    public bool RemoveDuplicates { get; set; } = true;
    
    /// <summary>
    /// Search timeout in milliseconds (clamped to 0-10000)
    /// </summary>
    public int TimeOutLimitMilliseconds { get; set; } = 1000;
    
    /// <summary>
    /// Logging prefix for debug output
    /// </summary>
    public string LogPrefix { get; set; } = string.Empty;
    
    /// <summary>
    /// Maximum boost value from all boosts combined
    /// </summary>
    public int MaxBoost
    {
        get
        {
            if (!EnableBoost || Boosts == null)
                return 0;
            
            int total = 0;
            foreach (var boost in Boosts)
            {
                total += (int)boost.BoostStrength;
            }
            return total;
        }
    }
    
    /// <summary>
    /// Total number of documents boosted across all boost configurations
    /// </summary>
    public int DocumentsBoosted
    {
        get
        {
            if (!EnableBoost || Boosts == null)
                return 0;
            
            int total = 0;
            foreach (var boost in Boosts)
            {
                total += boost.DocumentsBoosted;
            }
            return total;
        }
    }
    
    /// <summary>
    /// Precompiled filter bytecode (optional performance optimization).
    /// When set, the search engine will use this bytecode instead of compiling the Filter.
    /// </summary>
    public byte[]? CompiledFilterBytecode { get; set; }
    
    /// <summary>
    /// Creates a default query
    /// </summary>
    public Query()
    {
    }
    
    /// <summary>
    /// Creates a query with text and max results
    /// </summary>
    /// <param name="queryText">Search text</param>
    /// <param name="maxNumberOfRecordsToReturn">Maximum results to return</param>
    public Query(string queryText, int maxNumberOfRecordsToReturn = 10)
    {
        Text = queryText;
        MaxNumberOfRecordsToReturn = maxNumberOfRecordsToReturn;
        EnableCoverage = true;
        RemoveDuplicates = true;
    }
    
    /// <summary>
    /// Copy constructor - creates a deep copy of the query
    /// </summary>
    /// <param name="source">Source query to copy</param>
    internal Query(Query source)
    {
        Text = source.Text;
        MaxNumberOfRecordsToReturn = source.MaxNumberOfRecordsToReturn;
        EnableCoverage = source.EnableCoverage;
        EnableFacets = source.EnableFacets;
        EnableBoost = source.EnableBoost;
        CoverageDepth = source.CoverageDepth;
        CoverageSetup = source.CoverageSetup != null ? new CoverageSetup(source.CoverageSetup) : null;
        Filter = source.Filter;
        Boosts = source.Boosts;
        SortBy = source.SortBy;
        SortAscending = source.SortAscending;
        RemoveDuplicates = source.RemoveDuplicates;
        TimeOutLimitMilliseconds = source.TimeOutLimitMilliseconds;
        LogPrefix = source.LogPrefix;
        CompiledFilterBytecode = source.CompiledFilterBytecode;
    }
}
