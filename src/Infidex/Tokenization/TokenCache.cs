using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Infidex.Core;

namespace Infidex.Tokenization;

/// <summary>
/// Thread-safe LRU cache for tokenization results.
/// Caches both query tokenizations and document tokenizations to avoid repeated processing.
/// </summary>
public sealed class TokenCache : IDisposable
{
    /// <summary>
    /// Cached query tokenization result.
    /// </summary>
    public readonly struct CachedQuery
    {
        public readonly string NormalizedText;
        public readonly Shingle[] Shingles;
        public readonly string[] Tokens;
        public readonly long Timestamp;
        
        public CachedQuery(string normalizedText, Shingle[] shingles, string[] tokens)
        {
            NormalizedText = normalizedText;
            Shingles = shingles;
            Tokens = tokens;
            Timestamp = DateTime.UtcNow.Ticks;
        }
    }
    
    /// <summary>
    /// Cached document tokenization result.
    /// </summary>
    public readonly struct CachedDocument
    {
        public readonly int DocumentId;
        public readonly string NormalizedText;
        public readonly string[] Tokens;
        public readonly long Timestamp;
        
        public CachedDocument(int documentId, string normalizedText, string[] tokens)
        {
            DocumentId = documentId;
            NormalizedText = normalizedText;
            Tokens = tokens;
            Timestamp = DateTime.UtcNow.Ticks;
        }
    }
    
    // Query cache - keyed by original query string
    private readonly ConcurrentDictionary<string, CachedQuery> _queryCache;
    private readonly ConcurrentQueue<string> _queryLruQueue;
    private readonly int _maxQueryCacheSize;
    private int _queryCacheSize;
    
    // Document cache - keyed by document ID
    private readonly ConcurrentDictionary<int, CachedDocument> _documentCache;
    private readonly ConcurrentQueue<int> _documentLruQueue;
    private readonly int _maxDocumentCacheSize;
    private int _documentCacheSize;
    
    private readonly ReaderWriterLockSlim _queryLock;
    private readonly ReaderWriterLockSlim _documentLock;
    private bool _disposed;
    
    // Statistics
    private long _queryHits;
    private long _queryMisses;
    private long _documentHits;
    private long _documentMisses;
    
    /// <summary>Query cache hit count.</summary>
    public long QueryHits => Interlocked.Read(ref _queryHits);
    
    /// <summary>Query cache miss count.</summary>
    public long QueryMisses => Interlocked.Read(ref _queryMisses);
    
    /// <summary>Document cache hit count.</summary>
    public long DocumentHits => Interlocked.Read(ref _documentHits);
    
    /// <summary>Document cache miss count.</summary>
    public long DocumentMisses => Interlocked.Read(ref _documentMisses);
    
    /// <summary>Query cache hit rate (0-1).</summary>
    public double QueryHitRate
    {
        get
        {
            long total = QueryHits + QueryMisses;
            return total > 0 ? (double)QueryHits / total : 0;
        }
    }
    
    /// <summary>Document cache hit rate (0-1).</summary>
    public double DocumentHitRate
    {
        get
        {
            long total = DocumentHits + DocumentMisses;
            return total > 0 ? (double)DocumentHits / total : 0;
        }
    }
    
    /// <summary>
    /// Creates a new token cache with specified capacities.
    /// </summary>
    /// <param name="maxQueryCacheSize">Maximum number of cached queries (default 10000).</param>
    /// <param name="maxDocumentCacheSize">Maximum number of cached documents (default 50000).</param>
    public TokenCache(int maxQueryCacheSize = 10000, int maxDocumentCacheSize = 50000)
    {
        _maxQueryCacheSize = maxQueryCacheSize;
        _maxDocumentCacheSize = maxDocumentCacheSize;
        
        _queryCache = new ConcurrentDictionary<string, CachedQuery>(
            concurrencyLevel: Environment.ProcessorCount,
            capacity: maxQueryCacheSize);
        _queryLruQueue = new ConcurrentQueue<string>();
        
        _documentCache = new ConcurrentDictionary<int, CachedDocument>(
            concurrencyLevel: Environment.ProcessorCount,
            capacity: maxDocumentCacheSize);
        _documentLruQueue = new ConcurrentQueue<int>();
        
        _queryLock = new ReaderWriterLockSlim();
        _documentLock = new ReaderWriterLockSlim();
    }
    
    #region Query Cache
    
    /// <summary>
    /// Tries to get a cached query tokenization.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetQuery(string query, out CachedQuery cached)
    {
        if (_queryCache.TryGetValue(query, out cached))
        {
            Interlocked.Increment(ref _queryHits);
            return true;
        }
        
        Interlocked.Increment(ref _queryMisses);
        cached = default;
        return false;
    }
    
    /// <summary>
    /// Caches a query tokenization result.
    /// </summary>
    public void CacheQuery(string query, string normalizedText, Shingle[] shingles, string[] tokens)
    {
        CachedQuery cached = new CachedQuery(normalizedText, shingles, tokens);
        
        if (_queryCache.TryAdd(query, cached))
        {
            _queryLruQueue.Enqueue(query);
            int newSize = Interlocked.Increment(ref _queryCacheSize);
            
            // Evict if over capacity
            if (newSize > _maxQueryCacheSize)
            {
                EvictQueryEntries(newSize - _maxQueryCacheSize);
            }
        }
        else
        {
            // Update existing entry
            _queryCache[query] = cached;
        }
    }
    
    private void EvictQueryEntries(int count)
    {
        for (int i = 0; i < count && _queryLruQueue.TryDequeue(out string? key); i++)
        {
            if (_queryCache.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _queryCacheSize);
            }
        }
    }
    
    /// <summary>
    /// Clears the query cache.
    /// </summary>
    public void ClearQueryCache()
    {
        _queryLock.EnterWriteLock();
        try
        {
            _queryCache.Clear();
            while (_queryLruQueue.TryDequeue(out _)) { }
            _queryCacheSize = 0;
        }
        finally
        {
            _queryLock.ExitWriteLock();
        }
    }
    
    #endregion
    
    #region Document Cache
    
    /// <summary>
    /// Tries to get cached document tokens.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetDocument(int documentId, out CachedDocument cached)
    {
        if (_documentCache.TryGetValue(documentId, out cached))
        {
            Interlocked.Increment(ref _documentHits);
            return true;
        }
        
        Interlocked.Increment(ref _documentMisses);
        cached = default;
        return false;
    }
    
    /// <summary>
    /// Caches document tokenization result.
    /// </summary>
    public void CacheDocument(int documentId, string normalizedText, string[] tokens)
    {
        CachedDocument cached = new CachedDocument(documentId, normalizedText, tokens);
        
        if (_documentCache.TryAdd(documentId, cached))
        {
            _documentLruQueue.Enqueue(documentId);
            int newSize = Interlocked.Increment(ref _documentCacheSize);
            
            if (newSize > _maxDocumentCacheSize)
            {
                EvictDocumentEntries(newSize - _maxDocumentCacheSize);
            }
        }
        else
        {
            _documentCache[documentId] = cached;
        }
    }
    
    private void EvictDocumentEntries(int count)
    {
        for (int i = 0; i < count && _documentLruQueue.TryDequeue(out int key); i++)
        {
            if (_documentCache.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _documentCacheSize);
            }
        }
    }
    
    /// <summary>
    /// Invalidates a specific document from the cache.
    /// </summary>
    public void InvalidateDocument(int documentId)
    {
        if (_documentCache.TryRemove(documentId, out _))
        {
            Interlocked.Decrement(ref _documentCacheSize);
        }
    }
    
    /// <summary>
    /// Clears the document cache.
    /// </summary>
    public void ClearDocumentCache()
    {
        _documentLock.EnterWriteLock();
        try
        {
            _documentCache.Clear();
            while (_documentLruQueue.TryDequeue(out _)) { }
            _documentCacheSize = 0;
        }
        finally
        {
            _documentLock.ExitWriteLock();
        }
    }
    
    #endregion
    
    /// <summary>
    /// Clears all caches.
    /// </summary>
    public void Clear()
    {
        ClearQueryCache();
        ClearDocumentCache();
    }
    
    /// <summary>
    /// Resets statistics.
    /// </summary>
    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _queryHits, 0);
        Interlocked.Exchange(ref _queryMisses, 0);
        Interlocked.Exchange(ref _documentHits, 0);
        Interlocked.Exchange(ref _documentMisses, 0);
    }
    
    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        return new CacheStatistics
        {
            QueryCacheSize = _queryCacheSize,
            QueryCacheCapacity = _maxQueryCacheSize,
            QueryHits = QueryHits,
            QueryMisses = QueryMisses,
            QueryHitRate = QueryHitRate,
            DocumentCacheSize = _documentCacheSize,
            DocumentCacheCapacity = _maxDocumentCacheSize,
            DocumentHits = DocumentHits,
            DocumentMisses = DocumentMisses,
            DocumentHitRate = DocumentHitRate
        };
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Clear();
        _queryLock.Dispose();
        _documentLock.Dispose();
    }
}

/// <summary>
/// Statistics for the token cache.
/// </summary>
public sealed class CacheStatistics
{
    public int QueryCacheSize { get; set; }
    public int QueryCacheCapacity { get; set; }
    public long QueryHits { get; set; }
    public long QueryMisses { get; set; }
    public double QueryHitRate { get; set; }
    
    public int DocumentCacheSize { get; set; }
    public int DocumentCacheCapacity { get; set; }
    public long DocumentHits { get; set; }
    public long DocumentMisses { get; set; }
    public double DocumentHitRate { get; set; }
    
    public override string ToString()
    {
        return $"Query: {QueryCacheSize}/{QueryCacheCapacity} ({QueryHitRate:P1} hit), " +
               $"Document: {DocumentCacheSize}/{DocumentCacheCapacity} ({DocumentHitRate:P1} hit)";
    }
}


