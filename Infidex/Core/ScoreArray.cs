namespace Infidex.Core;

/// <summary>
/// Bucket-based score storage that provides O(1) insertion and O(n) top-K retrieval.
/// Uses score values (0-255) as array indices for ultra-fast sorting.
/// </summary>
public class ScoreArray
{
    private readonly List<long>[] _buckets;
    private int _totalCount;
    
    public ScoreArray()
    {
        // Create 256 buckets (one for each possible byte value)
        _buckets = new List<long>[256];
        for (int i = 0; i < 256; i++)
        {
            _buckets[i] = [];
        }
        _totalCount = 0;
    }
    
    /// <summary>
    /// Adds a document with its score. O(1) operation.
    /// </summary>
    public void Add(long documentId, byte score)
    {
        _buckets[score].Add(documentId);
        _totalCount++;
    }
    
    /// <summary>
    /// Updates a document's score. If the document doesn't exist, adds it.
    /// </summary>
    public void Update(long documentId, byte score)
    {
        // For now, just add - in production, you'd track and remove old scores
        Add(documentId, score);
    }
    
    /// <summary>
    /// Gets the top K results by iterating from highest score to lowest.
    /// O(n) operation but extremely fast due to bucket structure.
    /// </summary>
    public ScoreEntry[] GetTopK(int k)
    {
        List<ScoreEntry> results = [];
        
        // Iterate from bucket 255 down to 0 (highest to lowest scores)
        for (int score = 255; score >= 0 && results.Count < k; score--)
        {
            foreach (long docId in _buckets[score])
            {
                results.Add(new ScoreEntry((byte)score, docId));
                if (results.Count >= k)
                    break;
            }
        }
        
        return results.ToArray();
    }
    
    /// <summary>
    /// Gets all results sorted by score (highest first)
    /// </summary>
    public ScoreEntry[] GetAll()
    {
        return GetTopK(_totalCount);
    }
    
    /// <summary>
    /// Clears all scores
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < 256; i++)
        {
            _buckets[i].Clear();
        }
        _totalCount = 0;
    }
    
    /// <summary>
    /// Gets the total number of entries
    /// </summary>
    public int Count => _totalCount;
}


