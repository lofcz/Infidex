namespace Infidex.Metrics;

/// <summary>
/// Implements Jaccard similarity metrics for character-based matching.
/// Provides both frequency-based and set-based similarity calculations.
/// </summary>
public class JaccardMetric
{
    private readonly object _lock = new object();
    private Dictionary<char, int> _queryFrequencies;
    private HashSet<char> _queryCharSet;
    private char[] _queryText;
    
    public JaccardMetric()
    {
        _queryFrequencies = new Dictionary<char, int>();
        _queryCharSet = [];
        _queryText = [];
    }
    
    /// <summary>
    /// Sets the query text and pre-computes its frequency map and character set.
    /// Thread-safe.
    /// </summary>
    public char[] SoughtText
    {
        get
        {
            lock (_lock)
            {
                return _queryText;
            }
        }
        set
        {
            lock (_lock)
            {
                _queryText = value;
                
                // Build frequency map
                _queryFrequencies.Clear();
                foreach (char c in value)
                {
                    if (!_queryFrequencies.TryGetValue(c, out int count))
                    {
                        _queryFrequencies.Add(c, 1);
                    }
                    else
                    {
                        _queryFrequencies[c] = count + 1;
                    }
                }
                
                // Build character set
                _queryCharSet.Clear();
                foreach (char c in value)
                {
                    _queryCharSet.Add(c);
                }
            }
        }
    }
    
    /// <summary>
    /// Calculates Jaccard similarity based on character frequencies.
    /// Formula: intersection / (len(a) + len(b) - intersection)
    /// where intersection counts minimum frequency of each character.
    /// </summary>
    public double JaccardOfAllChars(char[] documentText)
    {
        lock (_lock)
        {
            // Build frequency map for document
            Dictionary<char, int> docFrequencies = new Dictionary<char, int>();
            foreach (char c in documentText)
            {
                if (!docFrequencies.TryGetValue(c, out int count))
                {
                    docFrequencies.Add(c, 1);
                }
                else
                {
                    docFrequencies[c] = count + 1;
                }
            }
            
            double queryLength = _queryText.Length;
            double docLength = documentText.Length;
            double intersection = 0.0;
            
            // Calculate intersection based on minimum frequencies
            foreach (KeyValuePair<char, int> kvp in _queryFrequencies)
            {
                if (docFrequencies.TryGetValue(kvp.Key, out int docCount))
                {
                    intersection += Math.Min(kvp.Value, docCount);
                }
            }
            
            // Jaccard formula
            return intersection / (queryLength + docLength - intersection);
        }
    }
    
    /// <summary>
    /// Calculates Jaccard similarity based on unique character sets.
    /// Formula: |A ∩ B| / |A ∪ B|
    /// where A and B are sets of unique characters.
    /// </summary>
    public double JaccardOfCharSet(char[] documentText)
    {
        lock (_lock)
        {
            // Build character set for document
            HashSet<char> docCharSet = [];
            foreach (char c in documentText)
            {
                docCharSet.Add(c);
            }
            
            double querySetSize = _queryCharSet.Count;
            double docSetSize = docCharSet.Count;
            double intersection = 0.0;
            
            // Calculate intersection
            foreach (char c in _queryCharSet)
            {
                if (docCharSet.Contains(c))
                {
                    intersection += 1.0;
                }
            }
            
            // Jaccard formula for sets
            return intersection / (querySetSize + docSetSize - intersection);
        }
    }
    
    /// <summary>
    /// Convenience method: calculates Jaccard similarity from strings
    /// </summary>
    public double JaccardOfAllChars(string query, string document)
    {
        SoughtText = query.ToCharArray();
        return JaccardOfAllChars(document.ToCharArray());
    }
    
    /// <summary>
    /// Convenience method: calculates set-based Jaccard from strings
    /// </summary>
    public double JaccardOfCharSet(string query, string document)
    {
        SoughtText = query.ToCharArray();
        return JaccardOfCharSet(document.ToCharArray());
    }
}


