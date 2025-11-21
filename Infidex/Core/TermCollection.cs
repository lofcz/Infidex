namespace Infidex.Core;

/// <summary>
/// Thread-safe collection of terms in the inverted index.
/// </summary>
public class TermCollection
{
    internal Dictionary<string, Term> _termDictionary;
    private readonly ReaderWriterLockSlim _lock;
    private volatile bool _doLock;
    
    /// <summary>
    /// Controls whether locking is enabled (for multi-threaded scenarios)
    /// </summary>
    public bool DoLock
    {
        get => _doLock;
        set => _doLock = value;
    }
    
    public TermCollection()
    {
        _termDictionary = new Dictionary<string, Term>();
        _lock = new ReaderWriterLockSlim();
        _doLock = false;
    }
    
    /// <summary>
    /// Clears all terms and their data.
    /// </summary>
    public void ClearAllData()
    {
        foreach (Term term in _termDictionary.Values)
        {
            term.Clear();
        }
        _termDictionary.Clear();
    }
    
    /// <summary>
    /// Counts term usage in the corpus and returns the term, creating it if necessary.
    /// When <paramref name="forFastInsert"/> is false, this also increments the
    /// document-frequency style counter used for IDF and stop-term detection.
    /// Thread-safe if <see cref="DoLock"/> is enabled.
    /// </summary>
    public Term CountTermUsage(string termText, int stopTermLimit, bool forFastInsert = false)
    {
        bool shouldLock = _doLock;
        
        try
        {
            if (shouldLock)
                _lock.EnterWriteLock();
            
            if (!_termDictionary.TryGetValue(termText, out Term? term))
            {
                term = new Term(termText);
                _termDictionary.Add(termText, term);
                
                // Initial usage count
                term.IncrementTermUsageCounter(stopTermLimit);
                return term;
            }
            
            if (!forFastInsert)
            {
                term.IncrementTermUsageCounter(stopTermLimit);
            }
            
            return term;
        }
        finally
        {
            if (shouldLock)
                _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Gets a term by its text key. Thread-safe if <see cref="DoLock"/> is enabled.
    /// </summary>
    public Term? GetTerm(string termText)
    {
        bool shouldLock = _doLock;
        
        try
        {
            if (shouldLock)
                _lock.EnterReadLock();
            
            if (_termDictionary.TryGetValue(termText, out Term? term))
                return term;
            
            return null;
        }
        finally
        {
            if (shouldLock)
                _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets all terms in the collection.
    /// </summary>
    public IEnumerable<Term> GetAllTerms() => _termDictionary.Values;
    
    /// <summary>
    /// Gets the total number of unique terms.
    /// </summary>
    public int Count => _termDictionary.Count;
}


