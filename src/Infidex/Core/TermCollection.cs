using Infidex.Core;
using Infidex.Tokenization;

namespace Infidex.Core;

/// <summary>
/// Thread-safe collection of terms in the inverted index.
/// Supports both string-keyed lookup and index-based access for FST integration.
/// </summary>
public class TermCollection
{
    internal Dictionary<string, Term> _termDictionary;
    internal Dictionary<ulong, Term>? _shortTermDictionary;
    private readonly List<Term> _termList; // For index-based access (FST outputs)
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
        _shortTermDictionary = new Dictionary<ulong, Term>();
        _termList = new List<Term>();
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
        _shortTermDictionary?.Clear();
        _termList.Clear();
    }
    
    /// <summary>
    /// Counts term usage in the corpus and returns the term, creating it if necessary.
    /// When <paramref name="forFastInsert"/> is false, this also increments the
    /// document-frequency style counter used for IDF and stop-term detection.
    /// Thread-safe if <see cref="DoLock"/> is enabled.
    /// </summary>
    public Term CountTermUsage(string termText, int stopTermLimit, bool forFastInsert = false)
    {
        return CountTermUsage(termText, stopTermLimit, forFastInsert, out _);
    }
    
    /// <summary>
    /// Counts term usage in the corpus and returns the term, creating it if necessary.
    /// The <paramref name="isNewTerm"/> parameter indicates whether a new term was created.
    /// Thread-safe if <see cref="DoLock"/> is enabled.
    /// </summary>
    public Term CountTermUsage(string termText, int stopTermLimit, bool forFastInsert, out bool isNewTerm)
    {
        return CountTermUsage(termText.AsSpan(), stopTermLimit, forFastInsert, out isNewTerm);
    }

    /// <summary>
    /// Counts term usage in the corpus and returns the term, creating it if necessary.
    /// Optimized for Span input to avoid string allocation for existing short terms.
    /// </summary>
    public Term CountTermUsage(ReadOnlySpan<char> termText, int stopTermLimit, bool forFastInsert, out bool isNewTerm)
    {
        bool shouldLock = _doLock;
        isNewTerm = false;
        
        try
        {
            if (shouldLock)
                _lock.EnterWriteLock();
            
            // Short term optimization
            int len = termText.Length;
            if (len >= 2 && len <= 3 && _shortTermDictionary != null)
            {
                NGramKey key = new NGramKey(termText);
                if (_shortTermDictionary.TryGetValue(key.Value, out Term? term))
                {
                    // Found existing term!
                    if (!forFastInsert)
                    {
                        term.IncrementTermUsageCounter(stopTermLimit);
                    }
                    return term;
                }
                else
                {
                    // New short term
                    string text = termText.ToString();
                    term = new Term(text);
                    _termDictionary.Add(text, term);
                    _shortTermDictionary.Add(key.Value, term);
                    _termList.Add(term);
                    isNewTerm = true;
                    
                    term.IncrementTermUsageCounter(stopTermLimit);
                    return term;
                }
            }
            
            string strKey = termText.ToString();
            
            if (!_termDictionary.TryGetValue(strKey, out Term? existingTerm))
            {
                existingTerm = new Term(strKey);
                _termDictionary.Add(strKey, existingTerm);
                _termList.Add(existingTerm);
                isNewTerm = true;
                
                existingTerm.IncrementTermUsageCounter(stopTermLimit);
                return existingTerm;
            }
            
            if (!forFastInsert)
            {
                existingTerm.IncrementTermUsageCounter(stopTermLimit);
            }
            
            return existingTerm;
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
            
            return _termDictionary.GetValueOrDefault(termText);
        }
        finally
        {
            if (shouldLock)
                _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Gets a term by its index (for FST integration).
    /// Returns null if index is out of range.
    /// </summary>
    public Term? GetTermByIndex(int index)
    {
        bool shouldLock = _doLock;
        
        try
        {
            if (shouldLock)
                _lock.EnterReadLock();
            
            if (index < 0 || index >= _termList.Count)
                return null;
            
            return _termList[index];
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
