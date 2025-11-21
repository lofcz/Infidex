namespace Infidex.Core;

/// <summary>
/// Inverted index that maps terms to their document occurrences.
/// </summary>
public class InvertedIndex
{
    private readonly Dictionary<string, Term> _terms;
    private readonly int _stopTermLimit;
    
    public InvertedIndex(int stopTermLimit = 1_250_000)
    {
        _terms = new Dictionary<string, Term>();
        _stopTermLimit = stopTermLimit;
    }
    
    /// <summary>
    /// Gets or creates a term
    /// </summary>
    public Term GetOrCreateTerm(string text)
    {
        if (!_terms.TryGetValue(text, out Term? term))
        {
            term = new Term(text);
            _terms[text] = term;
        }
        return term;
    }
    
    /// <summary>
    /// Gets a term if it exists
    /// </summary>
    public Term? GetTerm(string text)
    {
        _terms.TryGetValue(text, out Term? term);
        return term;
    }
    
    /// <summary>
    /// Checks if a term should be filtered (too common)
    /// </summary>
    public bool IsStopTerm(Term term)
    {
        return term.DocumentFrequency > _stopTermLimit;
    }
    
    /// <summary>
    /// Gets all terms
    /// </summary>
    public IEnumerable<Term> GetAllTerms() => _terms.Values;
    
    /// <summary>
    /// Gets the vocabulary size
    /// </summary>
    public int VocabularySize => _terms.Count;
    
    /// <summary>
    /// Clears the index
    /// </summary>
    public void Clear()
    {
        _terms.Clear();
    }
}


