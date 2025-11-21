using Infidex.Core;

namespace Infidex.Api;

/// <summary>
/// Represents search results with metadata
/// </summary>
public class Result
{
    public ScoreEntry[] Entries { get; set; }
    public int TotalCandidates { get; set; }
    public int ExecutionTimeMs { get; set; }
    public bool TimedOut { get; set; }
    
    public Result(ScoreEntry[] entries, int totalCandidates)
    {
        Entries = entries;
        TotalCandidates = totalCandidates;
        ExecutionTimeMs = 0;
        TimedOut = false;
    }
}


