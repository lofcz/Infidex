using Infidex.Core;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;

namespace Infidex.Indexing.Incremental;

/// <summary>
/// In-memory delta index that accumulates changes before merging into the main index.
/// Supports add and delete operations with efficient query-time unioning.
/// Thread-safe for concurrent indexing and searching.
/// </summary>
internal sealed class DeltaIndex : IDisposable
{
    private readonly ConcurrentTermCollection _terms;
    private readonly ConcurrentDocumentCollection _documents;
    private readonly TombstoneTracker _tombstones;
    private readonly FstBuilder _fstBuilder;
    private readonly PositionalPrefixIndex _prefixIndex;
    
    private readonly ReaderWriterLockSlim _indexLock;
    private readonly char[] _delimiters;
    private readonly int _stopTermLimit;
    
    private int _nextTermId;
    private bool _disposed;
    
    /// <summary>Number of documents in the delta.</summary>
    public int DocumentCount => _documents.Count;
    
    /// <summary>Number of deleted documents tracked.</summary>
    public int DeletedCount => _tombstones.DeletedCount;
    
    /// <summary>Number of terms in the delta.</summary>
    public int TermCount => _terms.Count;
    
    /// <summary>Whether the delta has any changes.</summary>
    public bool HasChanges => _documents.Count > 0 || _tombstones.DeletedCount > 0;
    
    public DeltaIndex(char[]? delimiters = null, int stopTermLimit = 1_250_000, int initialTombstoneCapacity = 1000)
    {
        _terms = new ConcurrentTermCollection();
        _documents = new ConcurrentDocumentCollection();
        _tombstones = new TombstoneTracker(initialTombstoneCapacity);
        _fstBuilder = new FstBuilder();
        _prefixIndex = new PositionalPrefixIndex(delimiters: delimiters);
        
        _indexLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        _delimiters = delimiters ?? [' '];
        _stopTermLimit = stopTermLimit;
        _nextTermId = 0;
    }
    
    #region Indexing
    
    /// <summary>
    /// Adds a document to the delta index.
    /// </summary>
    /// <param name="document">Document to add.</param>
    /// <param name="indexedText">Normalized text for indexing.</param>
    /// <returns>The internal document ID in the delta.</returns>
    public int AddDocument(Document document, string indexedText)
    {
        _indexLock.EnterWriteLock();
        try
        {
            Document stored = _documents.AddDocument(document);
            int docId = stored.Id;
            
            // Index terms
            string normalized = indexedText.ToLowerInvariant();
            string[] tokens = normalized.Split(_delimiters, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string token in tokens)
            {
                Term term = _terms.GetOrCreate(token, _stopTermLimit, forFastInsert: false, out bool isNew);
                if (isNew)
                {
                    _fstBuilder.AddForwardOnly(token, Interlocked.Increment(ref _nextTermId));
                }
                term.FirstCycleAdd(docId, _stopTermLimit, removeDuplicates: false);
            }
            
            // Index short prefixes
            _prefixIndex.IndexDocument(normalized, docId);
            
            return docId;
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Marks a document from the main index as deleted.
    /// </summary>
    /// <param name="mainIndexDocumentId">Document ID in the main index.</param>
    public void MarkDeleted(int mainIndexDocumentId)
    {
        _tombstones.ExpandTo(mainIndexDocumentId + 1);
        _tombstones.MarkDeleted(mainIndexDocumentId);
    }
    
    /// <summary>
    /// Marks multiple documents as deleted.
    /// </summary>
    public void MarkDeleted(IEnumerable<int> mainIndexDocumentIds)
    {
        foreach (int id in mainIndexDocumentIds)
            MarkDeleted(id);
    }
    
    #endregion
    
    #region Querying
    
    /// <summary>
    /// Gets a term from the delta, or null if not found.
    /// </summary>
    public Term? GetTerm(string termText)
    {
        return _terms.Get(termText);
    }
    
    /// <summary>
    /// Gets all terms in the delta.
    /// </summary>
    public IEnumerable<Term> GetAllTerms()
    {
        return _terms.GetAllTerms();
    }
    
    /// <summary>
    /// Gets a document from the delta by internal ID.
    /// </summary>
    public Document? GetDocument(int deltaDocId)
    {
        return _documents.GetDocument(deltaDocId);
    }
    
    /// <summary>
    /// Checks if a main index document ID is marked as deleted.
    /// </summary>
    public bool IsDeletedInMain(int mainIndexDocumentId)
    {
        return _tombstones.IsDeleted(mainIndexDocumentId);
    }
    
    /// <summary>
    /// Gets the short prefix posting list for a prefix.
    /// </summary>
    public PrefixPostingList? GetPrefixPostings(ReadOnlySpan<char> prefix)
    {
        return _prefixIndex.GetPostingList(prefix);
    }
    
    #endregion
    
    /// <summary>
    /// Builds and returns the FST index for the delta.
    /// </summary>
    public FstIndex BuildFst()
    {
        _indexLock.EnterReadLock();
        try
        {
            return _fstBuilder.Build();
        }
        finally
        {
            _indexLock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Finalizes the delta index (sorts posting lists, etc.).
    /// </summary>
    public void Finalize()
    {
        _indexLock.EnterWriteLock();
        try
        {
            _prefixIndex.Finalize();
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Clears all data in the delta.
    /// </summary>
    public void Clear()
    {
        _indexLock.EnterWriteLock();
        try
        {
            _terms.Clear();
            _documents.Clear();
            _tombstones.Clear();
            _fstBuilder.Clear();
            _prefixIndex.Clear();
            _nextTermId = 0;
        }
        finally
        {
            _indexLock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Gets the tombstone tracker for merging.
    /// </summary>
    internal TombstoneTracker GetTombstones() => _tombstones;
    
    /// <summary>
    /// Gets the document collection for merging.
    /// </summary>
    internal ConcurrentDocumentCollection GetDocuments() => _documents;
    
    /// <summary>
    /// Gets the term collection for merging.
    /// </summary>
    internal ConcurrentTermCollection GetTerms() => _terms;
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _terms.Dispose();
        _documents.Dispose();
        _indexLock.Dispose();
    }
}


