using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Infidex.Core;

/// <summary>
/// Thread-safe collection of terms in the inverted index.
/// Supports multiple concurrent readers with exclusive writer access.
/// Uses lock-free reads via ConcurrentDictionary and ReaderWriterLockSlim for structural changes.
/// </summary>
public sealed class ConcurrentTermCollection : IDisposable
{
    private readonly ConcurrentDictionary<string, Term> _terms;
    private readonly ReaderWriterLockSlim _structureLock;
    private volatile int _termCount;
    private bool _disposed;
    
    /// <summary>
    /// Gets the number of terms in the collection.
    /// Thread-safe without locking.
    /// </summary>
    public int Count => _termCount;
    
    public ConcurrentTermCollection()
    {
        _terms = new ConcurrentDictionary<string, Term>(
            concurrencyLevel: Environment.ProcessorCount * 2,
            capacity: 10000);
        _structureLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        _termCount = 0;
    }
    
    /// <summary>
    /// Gets or creates a term, incrementing its usage counter.
    /// Thread-safe for concurrent indexing.
    /// </summary>
    /// <param name="termText">The term text.</param>
    /// <param name="stopTermLimit">Maximum document frequency before becoming a stop term.</param>
    /// <param name="forFastInsert">If true, skip incrementing document frequency counter.</param>
    /// <returns>The term (existing or newly created).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Term GetOrCreate(string termText, int stopTermLimit, bool forFastInsert = false)
    {
        return GetOrCreate(termText, stopTermLimit, forFastInsert, out _);
    }
    
    /// <summary>
    /// Gets or creates a term, incrementing its usage counter.
    /// Thread-safe for concurrent indexing.
    /// </summary>
    /// <param name="termText">The term text.</param>
    /// <param name="stopTermLimit">Maximum document frequency before becoming a stop term.</param>
    /// <param name="forFastInsert">If true, skip incrementing document frequency counter.</param>
    /// <param name="isNewTerm">Output: true if a new term was created.</param>
    /// <returns>The term (existing or newly created).</returns>
    public Term GetOrCreate(string termText, int stopTermLimit, bool forFastInsert, out bool isNewTerm)
    {
        isNewTerm = false;
        
        // Fast path: term already exists (lock-free read)
        if (_terms.TryGetValue(termText, out Term? existingTerm))
        {
            if (!forFastInsert)
                existingTerm.IncrementTermUsageCounter(stopTermLimit);
            return existingTerm;
        }
        
        // Slow path: may need to create new term
        // Use GetOrAdd for atomic creation
        bool wasAdded = false;
        Term term = _terms.GetOrAdd(termText, _ =>
        {
            wasAdded = true;
            Term newTerm = new Term(termText);
            newTerm.IncrementTermUsageCounter(stopTermLimit);
            return newTerm;
        });
        
        if (wasAdded)
        {
            Interlocked.Increment(ref _termCount);
            isNewTerm = true;
        }
        else if (!forFastInsert)
        {
            term.IncrementTermUsageCounter(stopTermLimit);
        }
        
        return term;
    }
    
    /// <summary>
    /// Gets a term by its text, or null if not found.
    /// Thread-safe, lock-free read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Term? Get(string termText)
    {
        _terms.TryGetValue(termText, out Term? term);
        return term;
    }
    
    /// <summary>
    /// Checks if a term exists in the collection.
    /// Thread-safe, lock-free read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(string termText)
    {
        return _terms.ContainsKey(termText);
    }
    
    /// <summary>
    /// Gets all terms in the collection.
    /// Returns a snapshot that is safe to enumerate during concurrent modifications.
    /// </summary>
    public IEnumerable<Term> GetAllTerms()
    {
        // ConcurrentDictionary.Values returns a snapshot-like enumerable
        return _terms.Values;
    }
    
    /// <summary>
    /// Gets all term keys in the collection.
    /// </summary>
    public IEnumerable<string> GetAllKeys()
    {
        return _terms.Keys;
    }
    
    /// <summary>
    /// Clears all terms from the collection.
    /// Requires exclusive write access.
    /// </summary>
    public void Clear()
    {
        _structureLock.EnterWriteLock();
        try
        {
            foreach (Term term in _terms.Values)
                term.Clear();
            _terms.Clear();
            _termCount = 0;
        }
        finally
        {
            _structureLock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Removes a term from the collection.
    /// Thread-safe.
    /// </summary>
    public bool Remove(string termText)
    {
        if (_terms.TryRemove(termText, out Term? term))
        {
            term.Clear();
            Interlocked.Decrement(ref _termCount);
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Acquires a read lock for batch read operations.
    /// Multiple readers can hold the lock simultaneously.
    /// </summary>
    public IDisposable AcquireReadLock()
    {
        _structureLock.EnterReadLock();
        return new ReadLockReleaser(_structureLock);
    }
    
    /// <summary>
    /// Acquires a write lock for exclusive structural modifications.
    /// Blocks all readers and other writers.
    /// </summary>
    public IDisposable AcquireWriteLock()
    {
        _structureLock.EnterWriteLock();
        return new WriteLockReleaser(_structureLock);
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Clear();
        _structureLock.Dispose();
    }
    
    private sealed class ReadLockReleaser(ReaderWriterLockSlim rwLock) : IDisposable
    {
        private ReaderWriterLockSlim? _lock = rwLock;
        
        public void Dispose()
        {
            ReaderWriterLockSlim? l = Interlocked.Exchange(ref _lock, null);
            l?.ExitReadLock();
        }
    }
    
    private sealed class WriteLockReleaser(ReaderWriterLockSlim rwLock) : IDisposable
    {
        private ReaderWriterLockSlim? _lock = rwLock;
        
        public void Dispose()
        {
            ReaderWriterLockSlim? l = Interlocked.Exchange(ref _lock, null);
            l?.ExitWriteLock();
        }
    }
}


