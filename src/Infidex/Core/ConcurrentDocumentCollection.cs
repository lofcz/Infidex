using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Infidex.Core;

/// <summary>
/// Thread-safe collection of indexed documents.
/// Supports multiple concurrent readers with exclusive writer access.
/// </summary>
public sealed class ConcurrentDocumentCollection : IDisposable
{
    private readonly List<Document> _documents;
    private readonly ConcurrentDictionary<long, List<int>> _keyToIds;
    private readonly ReaderWriterLockSlim _lock;
    private volatile int _activeCount; // Non-deleted count
    private bool _disposed;
    
    /// <summary>
    /// Total number of documents (excluding deleted).
    /// Thread-safe without locking.
    /// </summary>
    public int Count => _activeCount;
    
    /// <summary>
    /// Total number of documents including deleted (for internal ID addressing).
    /// </summary>
    public int TotalCount
    {
        get
        {
            _lock.EnterReadLock();
            try { return _documents.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }
    
    public ConcurrentDocumentCollection()
    {
        _documents = new List<Document>();
        _keyToIds = new ConcurrentDictionary<long, List<int>>();
        _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        _activeCount = 0;
    }
    
    #region Adding Documents
    
    /// <summary>
    /// Adds a document to the collection.
    /// Thread-safe with exclusive write access.
    /// </summary>
    public Document AddDocument(Document document)
    {
        _lock.EnterWriteLock();
        try
        {
            int id = _documents.Count;
            document.Id = id;
            _documents.Add(document);
            
            // Update key-to-id mapping (supports aliases)
            _keyToIds.AddOrUpdate(
                document.DocumentKey,
                _ => [id],
                (_, list) => { lock (list) { list.Add(id); } return list; });
            
            if (!document.Deleted)
                Interlocked.Increment(ref _activeCount);
            
            return document;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Adds a document with simple key and text.
    /// </summary>
    public Document AddDocument(long documentKey, string indexedText)
    {
        Document doc = new Document(documentKey, indexedText);
        return AddDocument(doc);
    }
    
    /// <summary>
    /// Adds multiple documents in batch (more efficient than individual adds).
    /// </summary>
    public void AddDocuments(IEnumerable<Document> documents)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (Document document in documents)
            {
                int id = _documents.Count;
                document.Id = id;
                _documents.Add(document);
                
                _keyToIds.AddOrUpdate(
                    document.DocumentKey,
                    _ => [id],
                    (_, list) => { lock (list) { list.Add(id); } return list; });
                
                if (!document.Deleted)
                    Interlocked.Increment(ref _activeCount);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    #endregion
    
    #region Retrieving Documents
    
    /// <summary>
    /// Gets a document by internal ID.
    /// Thread-safe read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Document? GetDocument(int id)
    {
        _lock.EnterReadLock();
        try
        {
            if (id >= 0 && id < _documents.Count)
                return _documents[id];
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets the first document by DocumentKey.
    /// Thread-safe read.
    /// </summary>
    public Document? GetDocumentByKey(long documentKey)
    {
        if (!_keyToIds.TryGetValue(documentKey, out List<int>? ids))
            return null;
        
        _lock.EnterReadLock();
        try
        {
            lock (ids)
            {
                foreach (int id in ids)
                {
                    if (id >= 0 && id < _documents.Count)
                    {
                        Document doc = _documents[id];
                        if (!doc.Deleted)
                            return doc;
                    }
                }
            }
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets all documents with the given DocumentKey (supports aliases).
    /// Thread-safe read.
    /// </summary>
    public List<Document> GetDocumentsByKey(long documentKey)
    {
        List<Document> results = new();
        
        if (!_keyToIds.TryGetValue(documentKey, out List<int>? ids))
            return results;
        
        _lock.EnterReadLock();
        try
        {
            lock (ids)
            {
                foreach (int id in ids)
                {
                    if (id >= 0 && id < _documents.Count)
                    {
                        Document doc = _documents[id];
                        if (!doc.Deleted)
                            results.Add(doc);
                    }
                }
            }
            return results;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets a specific segment of a document.
    /// </summary>
    public Document? GetDocumentOfSegment(long publicKey, int segmentNumber)
    {
        if (!_keyToIds.TryGetValue(publicKey, out List<int>? ids))
            return null;
        
        _lock.EnterReadLock();
        try
        {
            lock (ids)
            {
                foreach (int id in ids)
                {
                    if (id >= 0 && id < _documents.Count)
                    {
                        Document doc = _documents[id];
                        if (doc.SegmentNumber == segmentNumber)
                            return doc;
                    }
                }
            }
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets all non-deleted documents.
    /// Returns a snapshot safe for enumeration.
    /// </summary>
    public IReadOnlyList<Document> GetAllDocuments()
    {
        _lock.EnterReadLock();
        try
        {
            return _documents.Where(d => !d.Deleted).ToList().AsReadOnly();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
    
    #endregion
    
    #region Deleting Documents
    
    /// <summary>
    /// Marks a document as deleted by internal ID.
    /// Thread-safe.
    /// </summary>
    public bool DeleteDocument(int id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (id >= 0 && id < _documents.Count)
            {
                Document doc = _documents[id];
                if (!doc.Deleted)
                {
                    doc.Deleted = true;
                    Interlocked.Decrement(ref _activeCount);
                    return true;
                }
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Marks all documents with the given key as deleted.
    /// Thread-safe.
    /// </summary>
    public int DeleteDocumentsByKey(long documentKey)
    {
        if (!_keyToIds.TryGetValue(documentKey, out List<int>? ids))
            return 0;
        
        _lock.EnterWriteLock();
        try
        {
            int count = 0;
            lock (ids)
            {
                foreach (int id in ids)
                {
                    if (id >= 0 && id < _documents.Count)
                    {
                        Document doc = _documents[id];
                        if (!doc.Deleted)
                        {
                            doc.Deleted = true;
                            Interlocked.Decrement(ref _activeCount);
                            count++;
                        }
                    }
                }
            }
            return count;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Physically removes deleted documents and compacts the collection.
    /// Requires exclusive write access.
    /// </summary>
    public void Compact()
    {
        _lock.EnterWriteLock();
        try
        {
            // Find first deleted
            int firstDeleted = -1;
            for (int i = 0; i < _documents.Count; i++)
            {
                if (_documents[i].Deleted)
                {
                    firstDeleted = i;
                    break;
                }
            }
            
            if (firstDeleted < 0)
                return; // Nothing to compact
            
            // Build compacted list
            List<Document> compacted = new(_documents.Count);
            Dictionary<long, List<int>> newKeyToIds = new();
            
            for (int i = 0; i < _documents.Count; i++)
            {
                Document doc = _documents[i];
                if (doc.Deleted)
                    continue;
                
                doc.Id = compacted.Count;
                compacted.Add(doc);
                
                if (!newKeyToIds.TryGetValue(doc.DocumentKey, out List<int>? ids))
                {
                    ids = new List<int>();
                    newKeyToIds[doc.DocumentKey] = ids;
                }
                ids.Add(doc.Id);
            }
            
            // Swap collections
            _documents.Clear();
            _documents.AddRange(compacted);
            
            _keyToIds.Clear();
            foreach (KeyValuePair<long, List<int>> kvp in newKeyToIds)
                _keyToIds[kvp.Key] = kvp.Value;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    #endregion
    
    /// <summary>
    /// Clears all documents.
    /// Requires exclusive write access.
    /// </summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _documents.Clear();
            _keyToIds.Clear();
            _activeCount = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Acquires a read lock for batch read operations.
    /// </summary>
    public IDisposable AcquireReadLock()
    {
        _lock.EnterReadLock();
        return new LockReleaser(_lock, isWrite: false);
    }
    
    /// <summary>
    /// Acquires a write lock for exclusive access.
    /// </summary>
    public IDisposable AcquireWriteLock()
    {
        _lock.EnterWriteLock();
        return new LockReleaser(_lock, isWrite: true);
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _lock.Dispose();
    }
    
    private sealed class LockReleaser(ReaderWriterLockSlim rwLock, bool isWrite) : IDisposable
    {
        private ReaderWriterLockSlim? _lock = rwLock;
        private readonly bool _isWrite = isWrite;
        
        public void Dispose()
        {
            ReaderWriterLockSlim? l = Interlocked.Exchange(ref _lock, null);
            if (l != null)
            {
                if (_isWrite)
                    l.ExitWriteLock();
                else
                    l.ExitReadLock();
            }
        }
    }
}


