using Infidex.Filtering;

namespace Infidex.WordMatcher;

/// <summary>
/// Index for prefix and suffix matching.
/// Enables fast lookup of documents containing words with specific prefixes/suffixes.
/// </summary>
public sealed class AffixIndex : IDisposable
{
    private readonly Dictionary<string, HashSet<int>> _affixIndex;
    private readonly char[] _delimiters;
    private readonly int _minLength;
    private readonly int _maxLength;
    private bool _disposed;
    
    public AffixIndex(int minLength, int maxLength, char[] delimiters)
    {
        _minLength = minLength;
        _maxLength = maxLength;
        _delimiters = delimiters;
        _affixIndex = new Dictionary<string, HashSet<int>>();
    }
    
    /// <summary>
    /// Clears all indexed affixes
    /// </summary>
    public void Clear()
    {
        _affixIndex?.Clear();
    }
    
    /// <summary>
    /// Indexes all words in a sentence for a given document
    /// </summary>
    public void LoadSentence(string input, int docIndex)
    {
        string[] words = input.Split(_delimiters, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (string word in words)
        {
            int length = word.Length;
            // Index all words with at least minLength chars (not restricted by maxLength).
            // The maxLength only controls the prefix/suffix lengths generated, not the word length.
            if (length >= _minLength)
            {
                LoadWord(word, docIndex);
            }
        }
    }
    
    /// <summary>
    /// Indexes all prefixes and suffixes of a word for a given document
    /// </summary>
    public void LoadWord(string word, int docIndex)
    {
        int length = word.Length;
        
        // Index prefixes and suffixes of various lengths
        for (int len = _minLength; len <= _maxLength && len <= length; len++)
        {
            // Add prefix
            string prefix = word.Substring(0, len);
            AddToIndex(prefix, docIndex);
            
            // Add suffix
            string suffix = word.Substring(length - len, len);
            AddToIndex(suffix, docIndex);
        }
    }
    
    /// <summary>
    /// Looks up documents containing words with the given prefixes/suffixes
    /// </summary>
    public bool Lookup(string input, FilterMask? filter, HashSet<int> result)
    {
        int length = input.Length;
        bool found = false;
        
        // Check prefixes (longest first for specificity)
        for (int len = Math.Min(_maxLength, length); len >= _minLength; len--)
        {
            string prefix = input.Substring(0, len);
            
            if (_affixIndex.TryGetValue(prefix, out HashSet<int>? docs))
            {
                foreach (int docId in docs)
                {
                    if (filter == null || filter.IsInFilter(docId))
                    {
                        result.Add(docId);
                        found = true;
                    }
                }
            }
        }
        
        // Check suffixes
        for (int len = _minLength; len <= Math.Min(_maxLength, length); len++)
        {
            string suffix = input.Substring(length - len, len);
            
            if (_affixIndex.TryGetValue(suffix, out HashSet<int>? docs))
            {
                foreach (int docId in docs)
                {
                    if (filter == null || filter.IsInFilter(docId))
                    {
                        result.Add(docId);
                        found = true;
                    }
                }
            }
        }
        
        return found;
    }
    
    private void AddToIndex(string affix, int docIndex)
    {
        if (!_affixIndex.TryGetValue(affix, out HashSet<int>? docs))
        {
            docs = [];
            _affixIndex[affix] = docs;
        }
        docs.Add(docIndex);
    }

    public void Save(BinaryWriter writer)
    {
        writer.Write(_affixIndex.Count);
        foreach (var kvp in _affixIndex)
        {
            writer.Write(kvp.Key);
            writer.Write(kvp.Value.Count);
            foreach (int docId in kvp.Value)
            {
                writer.Write(docId);
            }
        }
    }

    public void Load(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string key = reader.ReadString();
            int docCount = reader.ReadInt32();
            HashSet<int> docs = new HashSet<int>(docCount);
            for (int j = 0; j < docCount; j++)
            {
                docs.Add(reader.ReadInt32());
            }
            _affixIndex[key] = docs;
        }
    }
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        
        if (disposing)
        {
            Clear();
        }
        
        _disposed = true;
    }
    
    ~AffixIndex() => Dispose(false);
}


