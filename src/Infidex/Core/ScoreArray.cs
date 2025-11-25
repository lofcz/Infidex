namespace Infidex.Core;

/// <summary>
/// Bucket-based score storage that provides O(1) insertion and O(n) top-K retrieval.
/// Supports scores 0-65535 (ushort). Optimized for sparse data using range tracking.
/// </summary>
public class ScoreArray
{
    private readonly List<long>?[] _buckets;
    // Optimization: Bitmap to track active buckets. 
    // 65536 bits = 8192 bytes = 1024 ulongs. Fits in L1 cache.
    // Allows GetTopK to skip empty buckets without accessing the large pointer array.
    private readonly ulong[] _activeBucketsBitmap; 
    private int _maxScore = -1;
    private int _minScore = 65536;

    public ScoreArray()
    {
        // Create 65536 buckets (one for each possible ushort value)
        // Lazy initialization: array of nulls
        _buckets = new List<long>?[65536];
        _activeBucketsBitmap = new ulong[1024]; // 65536 / 64
        Count = 0;
    }
    
    /// <summary>
    /// Adds a document with its score. O(1) operation.
    /// </summary>
    public void Add(long documentId, ushort score)
    {
        if (_buckets[score] == null)
        {
            _buckets[score] = new List<long>();
            // Set bit in bitmap
            _activeBucketsBitmap[score >> 6] |= (1UL << (score & 63));
        }
        
        _buckets[score]!.Add(documentId);
        Count++;
        
        if (score > _maxScore) _maxScore = score;
        if (score < _minScore) _minScore = score;
    }
    
    /// <summary>
    /// Updates a document's score. If the document doesn't exist, adds it.
    /// Note: This is expensive if we don't know the old score, so we scan.
    /// For ScoreArray usage pattern (re-scoring), we often just Add.
    /// Update logic assumes we might need to remove from old bucket.
    /// </summary>
    public void Update(long documentId, ushort score)
    {
        // Remove any existing occurrences of this document from all active buckets
        // Optimization: scan only within known range
        if (Count > 0 && _maxScore >= 0)
        {
            // Iterate only buckets that might have data
            for (int s = _minScore; s <= _maxScore; s++)
            {
                var bucket = _buckets[s];
                if (bucket != null && bucket.Count > 0)
                {
                    for (int i = bucket.Count - 1; i >= 0; i--)
                    {
                        if (bucket[i] == documentId)
                        {
                            bucket.RemoveAt(i);
                            Count--;
                        }
                    }
                }
            }
        }

        Add(documentId, score);
    }
    
    /// <summary>
    /// Gets the top K results by iterating from highest score to lowest.
    /// O(n) operation but extremely fast due to bucket structure.
    /// Optimized to use bit operations for skipping empty buckets.
    /// </summary>
    public ScoreEntry[] GetTopK(int k)
    {
        List<ScoreEntry> results = new List<ScoreEntry>();
        
        if (Count == 0 || _maxScore < 0)
            return Array.Empty<ScoreEntry>();
            
        int maxChunkIndex = _maxScore >> 6;
        int minChunkIndex = _minScore >> 6;

        // Iterate through 64-bit chunks from high to low
        for (int i = maxChunkIndex; i >= minChunkIndex && results.Count < k; i--)
        {
            ulong chunk = _activeBucketsBitmap[i];
            if (chunk == 0) continue;

            // Iterate bits in the chunk from high (63) to low (0)
            // Score = (i * 64) + bitIndex
            // We check bits down to 0 or until minScore is reached
            
            // Optimization: Use TrailingZeroCount/LeadingZeroCount could be faster, 
            // but manual loop is simple and correct for endianness logic here.
            // Since we need high-to-low, we check bit 63 down to 0.
            for (int bit = 63; bit >= 0; bit--)
            {
                if ((chunk & (1UL << bit)) != 0)
                {
                    int score = (i << 6) | bit;
                    
                    // Respect bounds (though bitmap should handle this mostly)
                    if (score > _maxScore) continue; 
                    if (score < _minScore) break; // Passed minimum, stop

                    var bucket = _buckets[score];
                    if (bucket != null)
                    {
                        foreach (long docId in bucket)
                        {
                            results.Add(new ScoreEntry((ushort)score, docId));
                            if (results.Count >= k)
                                goto Done; // Break out of all loops
                        }
                    }
                }
            }
        }
        
        Done:
        return results.ToArray();
    }
    
    /// <summary>
    /// Gets all results sorted by score (highest first)
    /// </summary>
    public ScoreEntry[] GetAll()
    {
        return GetTopK(Count);
    }
    
    /// <summary>
    /// Clears all scores
    /// </summary>
    public void Clear()
    {
        if (_maxScore >= 0)
        {
            // Only clear used buckets
            for (int i = _minScore; i <= _maxScore; i++)
            {
                if (_buckets[i] != null)
                    _buckets[i]!.Clear();
            }
            
            // Clear bitmap (only need to clear range we touched)
            int minChunk = _minScore >> 6;
            int maxChunk = _maxScore >> 6;
            Array.Clear(_activeBucketsBitmap, minChunk, maxChunk - minChunk + 1);
        }
        Count = 0;
        _maxScore = -1;
        _minScore = 65536;
    }
    
    /// <summary>
    /// Gets the total number of entries
    /// </summary>
    public int Count { get; private set; }
}
