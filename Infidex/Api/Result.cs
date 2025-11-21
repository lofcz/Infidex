using Infidex.Core;

namespace Infidex.Api;

/// <summary>
/// Represents search results with metadata, facets, and query execution information.
/// </summary>
public class Result
{
    /// <summary>
    /// Matching documents with relevance scores
    /// </summary>
    public ScoreEntry[] Records { get; set; }
    
    /// <summary>
    /// Facet aggregations: field name -> (value, count) pairs
    /// Only populated when Query.EnableFacets = true
    /// </summary>
    public Dictionary<string, KeyValuePair<string, int>[]>? Facets { get; set; }
    
    /// <summary>
    /// Index of the last record in the result set (for pagination)
    /// </summary>
    public int TruncationIndex { get; set; }
    
    /// <summary>
    /// Score of the last record in the result set (for pagination)
    /// </summary>
    public byte TruncationScore { get; set; }
    
    /// <summary>
    /// Indicates if the query execution timed out
    /// </summary>
    public bool DidTimeOut { get; set; }
    
    /// <summary>
    /// Total number of candidates processed (for diagnostics)
    /// </summary>
    public int TotalCandidates { get; set; }
    
    /// <summary>
    /// Query execution time in milliseconds (for diagnostics)
    /// </summary>
    public int ExecutionTimeMs { get; set; }
    
    /// <summary>
    /// Backwards compatibility: alias for Records
    /// </summary>
    [Obsolete("Use Records instead")]
    public ScoreEntry[] Entries => Records;
    
    /// <summary>
    /// Backwards compatibility: alias for DidTimeOut
    /// </summary>
    [Obsolete("Use DidTimeOut instead")]
    public bool TimedOut => DidTimeOut;
    
    /// <summary>
    /// Creates a result with full metadata
    /// </summary>
    public Result(ScoreEntry[] records, Dictionary<string, KeyValuePair<string, int>[]>? facets, 
                  int truncationIndex, byte truncationScore, bool didTimeOut)
    {
        Records = records;
        Facets = facets;
        TruncationIndex = truncationIndex;
        TruncationScore = truncationScore;
        DidTimeOut = didTimeOut;
        TotalCandidates = records.Length;
        ExecutionTimeMs = 0;
    }
    
    /// <summary>
    /// Creates a simple result (backwards compatibility)
    /// </summary>
    public Result(ScoreEntry[] records, int totalCandidates)
    {
        Records = records;
        TotalCandidates = totalCandidates;
        ExecutionTimeMs = 0;
        DidTimeOut = false;
        TruncationIndex = records.Length > 0 ? records.Length - 1 : 0;
        TruncationScore = records.Length > 0 ? records[^1].Score : (byte)0;
    }
    
    /// <summary>
    /// Creates an empty result
    /// </summary>
    public static Result MakeEmptyResult(bool timedOut = false)
    {
        return new Result(Array.Empty<ScoreEntry>(), null, 0, 0, timedOut);
    }
}


