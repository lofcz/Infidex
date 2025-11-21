namespace Infidex.Api;

/// <summary>
/// Represents the current status of the search engine
/// </summary>
public class SystemStatus
{
    public int DocumentCount { get; set; }
    public bool ReIndexRequired { get; set; }
    public bool TooLongSearchText { get; set; }
    public bool TooLongClientText { get; set; }
    public int IndexProgress { get; set; } // 0-100%
    
    public SystemStatus()
    {
        DocumentCount = 0;
        ReIndexRequired = false;
        TooLongSearchText = false;
        TooLongClientText = false;
        IndexProgress = 0;
    }
}


