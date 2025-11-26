using Infidex.Core;
using Infidex.Filtering;
using Infidex.Tokenization;
using Infidex.Indexing.Fst;

namespace Infidex.WordMatcher;

/// <summary>
/// High-performance word-based index for fast exact and fuzzy word matching.
/// Uses FST for efficient prefix/suffix queries and symmetric delete for LD1.
/// Supports exact matching, LD1 (Levenshtein Distance 1), and affix matching.
/// </summary>
internal sealed class WordMatcher : IDisposable
{
    private readonly Dictionary<string, HashSet<int>> _exactIndex;
    private readonly Dictionary<string, HashSet<int>> _ld1Index; // Levenshtein Distance 1
    private readonly char[] _delimiters;
    private readonly WordMatcherSetup _setup;
    private readonly TextNormalizer? _textNormalizer;
    
    // FST for prefix/suffix queries (replaces AffixIndex)
    private FstBuilder? _fstBuilder;
    private FstIndex? _fstIndex;
    private readonly Dictionary<int, HashSet<int>> _fstTermToDocIds; // Maps FST output to doc IDs
    private int _nextFstTermId;
    
    private bool _disposed;
    
    public WordMatcher(WordMatcherSetup setup, char[] delimiters, TextNormalizer? textNormalizer = null)
    {
        _setup = setup;
        _delimiters = delimiters;
        _textNormalizer = textNormalizer;
        _exactIndex = new Dictionary<string, HashSet<int>>();
        _ld1Index = new Dictionary<string, HashSet<int>>();
        
        if (setup.SupportAffix)
        {
            _fstBuilder = new FstBuilder();
            _fstTermToDocIds = new Dictionary<int, HashSet<int>>();
            _nextFstTermId = 0;
        }
        else
        {
            _fstTermToDocIds = new Dictionary<int, HashSet<int>>();
        }
    }
    
    /// <summary>
    /// Clears all indices
    /// </summary>
    public void Clear()
    {
        _exactIndex.Clear();
        _ld1Index.Clear();
        _fstBuilder?.Clear();
        _fstIndex = null;
        _fstTermToDocIds.Clear();
        _nextFstTermId = 0;
    }
    
    /// <summary>
    /// Indexes a document's text
    /// </summary>
    public void Load(string text, int docIndex)
    {
        // Apply full normalization (including accent removal) for consistent matching
        string normalizedText = text.ToLowerInvariant();
        if (_textNormalizer != null)
        {
            normalizedText = _textNormalizer.Normalize(normalizedText);
        }
        
        string[] words = normalizedText.Split(_delimiters, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (string word in words)
        {
            int length = word.Length;
            
            // Exact word index
            if (length >= _setup.MinimumWordSizeExact && length <= _setup.MaximumWordSizeExact)
            {
                AddToIndex(_exactIndex, word, docIndex);
            }
            
            // LD1 index - generate all words within edit distance 1
            if (_setup.SupportLD1 && 
                length >= _setup.MinimumWordSizeLD1 && 
                length <= _setup.MaximumWordSizeLD1)
            {
                GenerateLD1Variants(word, docIndex);
            }
            
            // FST for prefix/suffix (replaces AffixIndex)
            if (_setup.SupportAffix && _fstBuilder != null && length >= _setup.MinimumWordSizeLD1)
            {
                IndexWordInFst(word, docIndex);
            }
        }
    }
    
    /// <summary>
    /// Finalizes the index after all documents have been loaded.
    /// Must be called before querying affix matches.
    /// </summary>
    public void FinalizeIndex()
    {
        if (_fstBuilder != null)
        {
            _fstIndex = _fstBuilder.Build();
        }
    }
    
    private void IndexWordInFst(string word, int docIndex)
    {
        // Get or create term ID for this word
        int termId;
        int existingOutput = _fstIndex?.GetExact(word.AsSpan()) ?? -1;
        
        if (existingOutput < 0)
        {
            // New word - add to FST builder
            termId = _nextFstTermId++;
            _fstBuilder!.Add(word, termId);
        }
        else
        {
            termId = existingOutput;
        }
        
        // Map term to document
        if (!_fstTermToDocIds.TryGetValue(termId, out HashSet<int>? docs))
        {
            docs = [];
            _fstTermToDocIds[termId] = docs;
        }
        docs.Add(docIndex);
    }
    
    /// <summary>
    /// Looks up documents containing the exact word
    /// </summary>
    public HashSet<int> Lookup(string query, FilterMask? filter = null)
    {
        HashSet<int> results = [];
        string normalized = query.ToLowerInvariant();
        if (_textNormalizer != null)
        {
            normalized = _textNormalizer.Normalize(normalized);
        }
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
    /// Looks up documents using affix (prefix/suffix) matching via FST.
    /// </summary>
    public HashSet<int> LookupAffix(string query, FilterMask? filter = null)
    {
        HashSet<int> results = [];
        
        if (_fstIndex == null)
        {
            // FST not built yet - finalize first
            FinalizeIndex();
            if (_fstIndex == null)
                return results;
        }
        
        string normalized = query.ToLowerInvariant();
        if (_textNormalizer != null)
        {
            normalized = _textNormalizer.Normalize(normalized);
        }
        
        // Get matching term IDs from FST
        List<int> termIds = [];
        
        // Prefix matches
        _fstIndex.GetByPrefix(normalized.AsSpan(), termIds);
        
        // Suffix matches
        _fstIndex.GetBySuffix(normalized.AsSpan(), termIds);
        
        // Convert term IDs to document IDs
        foreach (int termId in termIds)
        {
            if (_fstTermToDocIds.TryGetValue(termId, out HashSet<int>? docs))
            {
                foreach (int docId in docs)
                {
                    if (filter == null || filter.IsInFilter(docId))
                        results.Add(docId);
                }
            }
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
    
    private static void AddToIndex(Dictionary<string, HashSet<int>> index, string word, int docIndex)
    {
        if (!index.TryGetValue(word, out HashSet<int>? docs))
        {
            docs = [];
            index[word] = docs;
        }
        docs.Add(docIndex);
    }

    public void Save(BinaryWriter writer)
    {
        // Ensure FST is built if affix support is enabled so that we can
        // persist affix data for parity after reload. This is cheap if the
        // index has already been finalized.
        if (_setup.SupportAffix && _fstIndex == null && _fstBuilder != null && _fstTermToDocIds.Count > 0)
        {
            FinalizeIndex();
        }

        // Save Exact Index
        writer.Write(_exactIndex.Count);
        foreach (KeyValuePair<string, HashSet<int>> kvp in _exactIndex)
        {
            writer.Write(kvp.Key);
            writer.Write(kvp.Value.Count);
            foreach (int docId in kvp.Value)
            {
                writer.Write(docId);
            }
        }

        // Save LD1 Index
        writer.Write(_ld1Index.Count);
        foreach (KeyValuePair<string, HashSet<int>> kvp in _ld1Index)
        {
            writer.Write(kvp.Key);
            writer.Write(kvp.Value.Count);
            foreach (int docId in kvp.Value)
            {
                writer.Write(docId);
            }
        }

        // Save FST index (replaces AffixIndex)
        bool hasFst = _fstIndex != null;
        writer.Write(hasFst);
        if (hasFst && _fstIndex != null)
        {
            FstSerializer.Write(writer, _fstIndex);
            
            // Save term-to-docIds mapping
            writer.Write(_fstTermToDocIds.Count);
            foreach (KeyValuePair<int, HashSet<int>> kvp in _fstTermToDocIds)
            {
                writer.Write(kvp.Key);
                writer.Write(kvp.Value.Count);
                foreach (int docId in kvp.Value)
                {
                    writer.Write(docId);
                }
            }
        }
    }

    public void Load(BinaryReader reader)
    {
        Clear();
        
        // Load Exact Index
        int exactCount = reader.ReadInt32();
        for (int i = 0; i < exactCount; i++)
        {
            string key = reader.ReadString();
            int docCount = reader.ReadInt32();
            HashSet<int> docs = new HashSet<int>(docCount);
            for (int j = 0; j < docCount; j++)
            {
                docs.Add(reader.ReadInt32());
            }
            _exactIndex[key] = docs;
        }

        // Load LD1 Index
        int ld1Count = reader.ReadInt32();
        for (int i = 0; i < ld1Count; i++)
        {
            string key = reader.ReadString();
            int docCount = reader.ReadInt32();
            HashSet<int> docs = new HashSet<int>(docCount);
            for (int j = 0; j < docCount; j++)
            {
                docs.Add(reader.ReadInt32());
            }
            _ld1Index[key] = docs;
        }

        // Load FST index
        bool hasFst = reader.ReadBoolean();
        if (hasFst)
        {
            _fstIndex = FstSerializer.Read(reader);
            
            // Load term-to-docIds mapping
            int termCount = reader.ReadInt32();
            for (int i = 0; i < termCount; i++)
            {
                int termId = reader.ReadInt32();
                int docCount = reader.ReadInt32();
                HashSet<int> docs = new HashSet<int>(docCount);
                for (int j = 0; j < docCount; j++)
                {
                    docs.Add(reader.ReadInt32());
                }
                _fstTermToDocIds[termId] = docs;
                _nextFstTermId = Math.Max(_nextFstTermId, termId + 1);
            }
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
    
    ~WordMatcher() => Dispose(false);
}
