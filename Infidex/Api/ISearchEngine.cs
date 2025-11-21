namespace Infidex.Api;

/// <summary>
/// Interface for search engine implementations
/// </summary>
public interface ISearchEngine : IDisposable
{
    /// <summary>
    /// Searches for documents matching the query
    /// </summary>
    Result Search(Query query);
    
    /// <summary>
    /// Gets current system status
    /// </summary>
    SystemStatus GetStatus();
    
    /// <summary>
    /// Builds or rebuilds the search index
    /// </summary>
    void BuildIndex(CancellationToken cancellationToken = default);
}


