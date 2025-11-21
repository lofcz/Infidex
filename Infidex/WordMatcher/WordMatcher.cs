using Infidex.Core;
using Infidex.Filtering;
using Infidex.Metrics;

namespace Infidex.WordMatcher;

/// <summary>
/// High-performance word-based index for fast exact and fuzzy word matching.
/// Supports exact matching, LD1 (Levenshtein Distance 1), and affix matching.
/// </summary>
public class WordMatcher : IDisposable
{
    private readonly Dictionary<string, HashSet<int>> _exactIndex;
    private readonly Dictionary<string, HashSet<int>> _ld1Index; // Levenshtein Distance 1
    private readonly AffixIndex? _affixIndex;
    private readonly char[] _delimiters;
    private readonly WordMatcherSetup _setup;
    private bool _disposed;
    
    public WordMatcher(WordMatcherSetup setup, char[] delimiters)
    {
        _setup = setup;
        _delimiters = delimiters;
        _exactIndex = new Dictionary<string, HashSet<int>>();
        _ld1Index = new Dictionary<string, HashSet<int>>();
        
        if (setup.SupportAffix)
        {
            // In the original implementation, the affix index uses the LD1
            // min/max word sizes (not the exact-match sizes). This controls
            // which word lengths participate in prefix/suffix matching.
            _affixIndex = new AffixIndex(
                setup.MinimumWordSizeLD1,
                setup.MaximumWordSizeLD1,
                delimiters);
        }
    }
    
    /// <summary>
    /// Clears all indices
    /// </summary>
    public void Clear()
    {
        _exactIndex.Clear();
        _ld1Index.Clear();
        _affixIndex?.Clear();
    }
    
    /// <summary>
    /// Indexes a document's text
    /// </summary>
    public void Load(string text, int docIndex)
    {
        string[] words = text.Split(_delimiters, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (string word in words)
        {
            string normalized = word.ToLowerInvariant();
            int length = normalized.Length;
            
            // Exact word index
            if (length >= _setup.MinimumWordSizeExact && length <= _setup.MaximumWordSizeExact)
            {
                AddToIndex(_exactIndex, normalized, docIndex);
            }
            
            // LD1 index - generate all words within edit distance 1
            if (_setup.SupportLD1 && 
                length >= _setup.MinimumWordSizeLD1 && 
                length <= _setup.MaximumWordSizeLD1)
            {
                GenerateLD1Variants(normalized, docIndex);
            }
        }
        
        // Affix index
        if (_affixIndex != null)
        {
            _affixIndex.LoadSentence(text.ToLowerInvariant(), docIndex);
        }
    }
    
    /// <summary>
    /// Looks up documents containing the exact word
    /// </summary>
    public HashSet<int> Lookup(string query, FilterMask? filter = null)
    {
        HashSet<int> results = [];
        string normalized = query.ToLowerInvariant();
        int length = normalized.Length;

        // 1. Exact match
        // Handles: Target == Query
        TryFindMatches(normalized, filter, _exactIndex, results);
        
        // LD1 match logic using Symmetric Delete strategy
        if (_setup.SupportLD1 && 
            length >= _setup.MinimumWordSizeLD1 && 
            length <= _setup.MaximumWordSizeLD1)
        {
            // 2. Deletion in Target (Target has 1 extra char)
            // Target="word", Query="wor". Target deletion "wor" is in _ld1Index.
            TryFindMatches(normalized, filter, _ld1Index, results);

            // Generate deletions of Query for remaining cases
            for (int i = 0; i < length; i++)
            {
                string queryDeletion = normalized.Remove(i, 1);
                
                // 3. Substitution (Target len == Query len, 1 char diff)
                // Target="ward", Query="word". 
                // Target deletion "wrd" is in _ld1Index.
                // Query deletion "wrd" matches.
                TryFindMatches(queryDeletion, filter, _ld1Index, results);
                
                // 4. Insertion in Target (Target has 1 less char)
                // Target="word", Query="woord".
                // Query deletion "word" matches Target in _exactIndex.
                TryFindMatches(queryDeletion, filter, _exactIndex, results);
            }
        }
        
        return results;
    }

    private static void TryFindMatches(string key, FilterMask? filter, Dictionary<string, HashSet<int>> index, HashSet<int> results)
    {
        if (index.TryGetValue(key, out HashSet<int>? docs))
        {
            foreach (int docId in docs)
            {
                if (filter == null || filter.IsInFilter(docId))
                    results.Add(docId);
            }
        }
    }
    
    /// <summary>
    /// Looks up documents using affix (prefix/suffix) matching
    /// </summary>
    public HashSet<int> LookupAffix(string query, FilterMask? filter = null)
    {
        HashSet<int> results = [];
        
        if (_affixIndex != null)
        {
            _affixIndex.Lookup(query.ToLowerInvariant(), filter, results);
        }
        
        return results;
    }
    
    /// <summary>
    /// Generates all LD1 variants of a word and adds them to the index
    /// </summary>
    private void GenerateLD1Variants(string word, int docIndex)
    {
        // Strategy: Symmetric Delete Algorithm (FastSS)
        // We only index deletions (1 char removed).
        // Substitutions and insertions are handled at search time by generating deletions of the query.
        
        // Deletions - one character removed
        // We generate deletions for all words that meet the LD1 size criteria
        for (int i = 0; i < word.Length; i++)
        {
            string variant = word.Remove(i, 1);
            AddToIndex(_ld1Index, variant, docIndex);
        }
    }
    
    private void AddToIndex(Dictionary<string, HashSet<int>> index, string word, int docIndex)
    {
        if (!index.TryGetValue(word, out HashSet<int>? docs))
        {
            docs = [];
            index[word] = docs;
        }
        docs.Add(docIndex);
    }
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            Clear();
            _affixIndex?.Dispose();
        }
        
        _disposed = true;
    }
}
