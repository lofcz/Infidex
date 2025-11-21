using Infidex.Coverage;

namespace Infidex.Api;

/// <summary>
/// Represents a search query with all configuration options
/// </summary>
public class Query
{
    public string Text { get; set; }
    public int MaxRecords { get; set; }
    public int TimeOut { get; set; } // Milliseconds
    public bool EnableCoverage { get; set; }
    public CoverageSetup? CoverageSetup { get; set; }
    public int CoverageDepth { get; set; }
    public Filter? Filter { get; set; }
    public string? SortBy { get; set; }
    public bool EnableBoost { get; set; }
    public int MaxBoost { get; set; }
    
    public Query(string text)
    {
        Text = text;
        MaxRecords = 10;
        TimeOut = 5000;
        EnableCoverage = true;
        CoverageDepth = 500;
        EnableBoost = false;
        MaxBoost = 0;
    }
}

