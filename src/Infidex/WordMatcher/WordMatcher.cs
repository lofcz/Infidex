using Infidex.Core;
using Infidex.Filtering;
using Infidex.Tokenization;
using Infidex.Indexing.Fst;
using Infidex.Internalized.Roaring;

namespace Infidex.WordMatcher;

/// <summary>
/// High-performance word-based index for fast exact and fuzzy word matching.
/// Uses FST for efficient prefix/suffix queries and symmetric delete for LD1.
/// Supports exact matching, LD1 (Levenshtein Distance 1), and affix matching.
/// </summary>
internal sealed class WordMatcher : IDisposable
{
    // Builders used during indexing
    private Dictionary<string, List<int>>? _exactBuilder;
    private Dictionary<string, List<int>>? _ld1Builder;
    private Dictionary<int, List<int>>? _fstTermToDocIdsBuilder;

    // Compact indices used during search
    private Dictionary<string, RoaringBitmap>? _exactIndex;
    private Dictionary<string, RoaringBitmap>? _ld1Index;
    private Dictionary<int, RoaringBitmap>? _fstTermToDocIds;

    private readonly char[] _delimiters;
    private readonly WordMatcherSetup _setup;
    private readonly TextNormalizer? _textNormalizer;
    
    // FST for prefix/suffix (replaces AffixIndex)
    private FstBuilder? _fstBuilder;
    private FstIndex? _fstIndex;
    private int _nextFstTermId;
    
    private bool _disposed;
    
    /// <summary>
    /// Upper bound on how many FST affix term IDs we will expand per lookup.
    /// This keeps affix-based candidate generation bounded for very dense prefixes/suffixes.
    /// </summary>
    private const int MaxFstAffixTermsPerQuery = 4096;
    
    public WordMatcher(WordMatcherSetup setup, char[] delimiters, TextNormalizer? textNormalizer = null)
    {
        _setup = setup;
        _delimiters = delimiters;
        _textNormalizer = textNormalizer;
        
        // Initialize builders
        _exactBuilder = new Dictionary<string, List<int>>();
        _ld1Builder = new Dictionary<string, List<int>>();
        
        if (setup.SupportAffix)
        {
            _fstBuilder = new FstBuilder();
            _fstTermToDocIdsBuilder = new Dictionary<int, List<int>>();
            _nextFstTermId = 0;
        }
    }
    
    /// <summary>
    /// Clears all indices
    /// </summary>
    public void Clear()
    {
        _exactBuilder?.Clear();
        _ld1Builder?.Clear();
        _fstTermToDocIdsBuilder?.Clear();

        _exactIndex = null;
        _ld1Index = null;
        _fstTermToDocIds = null;

        _fstBuilder?.Clear();
        _fstIndex = null;
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
            if (length >= _setup.MinimumWordSizeExact && length <= _setup.MaximumWordSizeExact && _exactBuilder != null)
            {
                AddToIndex(_exactBuilder, word, docIndex);
            }
            
            // LD1 index - generate all words within edit distance 1
            if (_setup.SupportLD1 && 
                length >= _setup.MinimumWordSizeLD1 && 
                length <= _setup.MaximumWordSizeLD1 && 
                _ld1Builder != null)
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
    /// Must be called before querying.
    /// </summary>
    public void FinalizeIndex()
    {
        // 1. Convert Exact Builder -> Index
        if (_exactBuilder != null)
        {
            _exactIndex = new Dictionary<string, RoaringBitmap>(_exactBuilder.Count);
            foreach (var kvp in _exactBuilder)
            {
                _exactIndex[kvp.Key] = RoaringBitmap.Create(kvp.Value);
            }
            _exactBuilder = null; // Free memory
        }

        // 2. Convert LD1 Builder -> Index
        if (_ld1Builder != null)
        {
            _ld1Index = new Dictionary<string, RoaringBitmap>(_ld1Builder.Count);
            foreach (var kvp in _ld1Builder)
            {
                _ld1Index[kvp.Key] = RoaringBitmap.Create(kvp.Value);
            }
            _ld1Builder = null;
        }

        // 3. Convert FST Builder -> Index
        if (_fstBuilder != null)
        {
            _fstIndex = _fstBuilder.Build();
            _fstBuilder = null; // Free builder, keep index
        }

        if (_fstTermToDocIdsBuilder != null)
        {
            _fstTermToDocIds = new Dictionary<int, RoaringBitmap>(_fstTermToDocIdsBuilder.Count);
            foreach (var kvp in _fstTermToDocIdsBuilder)
            {
                _fstTermToDocIds[kvp.Key] = RoaringBitmap.Create(kvp.Value);
            }
            _fstTermToDocIdsBuilder = null;
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
        if (_fstTermToDocIdsBuilder != null)
        {
            if (!_fstTermToDocIdsBuilder.TryGetValue(termId, out List<int>? docs))
            {
                docs = [];
                _fstTermToDocIdsBuilder[termId] = docs;
            }
            if (docs.Count == 0 || docs[docs.Count - 1] != docIndex)
            {
                docs.Add(docIndex);
            }
        }
    }
    
    /// <summary>
    /// Looks up documents containing the exact word
    /// </summary>
    public RoaringBitmap? Lookup(string query, FilterMask? filter = null)
    {
        if (_exactIndex == null) FinalizeIndex();

        RoaringBitmap? result = null;
        string normalized = query.ToLowerInvariant();
        if (_textNormalizer != null)
        {
            normalized = _textNormalizer.Normalize(normalized);
        }
        int length = normalized.Length;

        // 1. Exact match
        // Handles: Target == Query
        AccumulateMatches(normalized, filter, _exactIndex!, ref result);
        
        // LD1 match logic using Symmetric Delete strategy
        if (_setup.SupportLD1 && 
            length >= _setup.MinimumWordSizeLD1 && 
            length <= _setup.MaximumWordSizeLD1 && 
            _ld1Index != null && _exactIndex != null)
        {
            // 2. Deletion in Target (Target has 1 extra char)
            // Target="word", Query="wor". Target deletion "wor" is in _ld1Index.
            AccumulateMatches(normalized, filter, _ld1Index, ref result);

            // Generate deletions of Query for remaining cases
            for (int i = 0; i < length; i++)
            {
                string queryDeletion = normalized.Remove(i, 1);
                
                // 3. Substitution (Target len == Query len, 1 char diff)
                // Target="ward", Query="word". 
                // Target deletion "wrd" is in _ld1Index.
                // Query deletion "wrd" matches.
                AccumulateMatches(queryDeletion, filter, _ld1Index, ref result);
                
                // 4. Insertion in Target (Target has 1 less char)
                // Target="word", Query="woord".
                // Query deletion "word" matches Target in _exactIndex.
                AccumulateMatches(queryDeletion, filter, _exactIndex, ref result);
            }
        }
        
        return result;
    }

    private static void AccumulateMatches(string key, FilterMask? filter, Dictionary<string, RoaringBitmap> index, ref RoaringBitmap? result)
    {
        if (index.TryGetValue(key, out RoaringBitmap? matches))
        {
            // TODO: FilterMask support in RoaringBitmap?
            // For now, assuming RoaringBitmap can handle filtering or we do it later.
            // But to support current API, we perform union.
            
            // Note: FilterMask is typically for deleted docs. RoaringBitmap doesn't support predicate filter directly without iteration.
            // If filter is crucial, we might need to apply it after. 
            // However, typical usage passes filter=null.
            
            if (result == null)
            {
                result = matches;
            }
            else
            {
                // result = result | matches; 
                // RoaringBitmap | operator creates a NEW bitmap.
                // Since 'matches' is from index (immutable conceptually), and result might be accumulating.
                result |= matches;
            }
        }
    }
    
    /// <summary>
    /// Looks up documents using affix (prefix/suffix) matching via FST.
    /// </summary>
    public RoaringBitmap? LookupAffix(string query, FilterMask? filter = null)
    {
        if (_fstIndex == null) FinalizeIndex();
        
        if (_fstIndex == null || _fstTermToDocIds == null)
            return null;
        
        string normalized = query.ToLowerInvariant();
        if (_textNormalizer != null)
        {
            normalized = _textNormalizer.Normalize(normalized);
        }
        
        // Get matching term IDs from FST with a bounded budget
        List<int> termIds = [];
        ReadOnlySpan<char> span = normalized.AsSpan();

        int prefixCount = _fstIndex.CountByPrefix(span);
        int suffixCount = _fstIndex.CountBySuffix(span);
        int remainingBudget = MaxFstAffixTermsPerQuery;

        if (prefixCount == 0 && suffixCount == 0)
            return null;

        if (prefixCount > 0 && remainingBudget > 0)
        {
            int take = remainingBudget > 0 ? Math.Min(prefixCount, remainingBudget) : 0;
            if (take > 0)
            {
                int[] buffer = System.Buffers.ArrayPool<int>.Shared.Rent(take);
                try
                {
                    int written = _fstIndex.GetByPrefix(span, buffer.AsSpan(0, take));
                    for (int i = 0; i < written; i++) termIds.Add(buffer[i]);
                    remainingBudget -= written;
                }
                finally
                {
                    System.Buffers.ArrayPool<int>.Shared.Return(buffer);
                }
            }
        }

        if (suffixCount > 0 && remainingBudget > 0)
        {
            int take = remainingBudget > 0 ? Math.Min(suffixCount, remainingBudget) : 0;
            if (take > 0)
            {
                int[] buffer = System.Buffers.ArrayPool<int>.Shared.Rent(take);
                try
                {
                    int written = _fstIndex.GetBySuffix(span, buffer.AsSpan(0, take));
                    for (int i = 0; i < written; i++) termIds.Add(buffer[i]);
                    remainingBudget -= written;
                }
                finally
                {
                    System.Buffers.ArrayPool<int>.Shared.Return(buffer);
                }
            }
        }
        
        RoaringBitmap? result = null;

        // Union all matching term docsets
        foreach (int termId in termIds)
        {
            if (_fstTermToDocIds.TryGetValue(termId, out RoaringBitmap? matches))
            {
                 if (result == null)
                    result = matches;
                else
                    result |= matches;
            }
        }
        
        return result;
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
            if (_ld1Builder != null)
            {
                AddToIndex(_ld1Builder, variant, docIndex);
            }
        }
    }
    
    private static void AddToIndex(Dictionary<string, List<int>> index, string word, int docIndex)
    {
        if (!index.TryGetValue(word, out List<int>? docs))
        {
            docs = [];
            index[word] = docs;
        }
        // Assuming strictly increasing docIndex during indexing
        if (docs.Count == 0 || docs[docs.Count - 1] != docIndex)
        {
            docs.Add(docIndex);
        }
    }

    public void Save(BinaryWriter writer)
    {
        FinalizeIndex();

        // Save Exact Index
        writer.Write(_exactIndex?.Count ?? 0);
        if (_exactIndex != null)
        {
            foreach (KeyValuePair<string, RoaringBitmap> kvp in _exactIndex)
            {
                writer.Write(kvp.Key);
                // Serialize RoaringBitmap directly
                using (MemoryStream ms = new MemoryStream())
                {
                    RoaringBitmap.Serialize(kvp.Value, ms);
                    byte[] bytes = ms.ToArray();
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                }
            }
        }

        // Save LD1 Index
        writer.Write(_ld1Index?.Count ?? 0);
        if (_ld1Index != null)
        {
            foreach (KeyValuePair<string, RoaringBitmap> kvp in _ld1Index)
            {
                writer.Write(kvp.Key);
                using (MemoryStream ms = new MemoryStream())
                {
                    RoaringBitmap.Serialize(kvp.Value, ms);
                    byte[] bytes = ms.ToArray();
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                }
            }
        }

        // Save FST index (replaces AffixIndex)
        bool hasFst = _fstIndex != null;
        writer.Write(hasFst);
        if (hasFst && _fstIndex != null)
        {
            FstSerializer.Write(writer, _fstIndex);
            
            // Save term-to-docIds mapping
            writer.Write(_fstTermToDocIds?.Count ?? 0);
            if (_fstTermToDocIds != null)
            {
                foreach (KeyValuePair<int, RoaringBitmap> kvp in _fstTermToDocIds)
                {
                    writer.Write(kvp.Key);
                    using (MemoryStream ms = new MemoryStream())
                    {
                        RoaringBitmap.Serialize(kvp.Value, ms);
                        byte[] bytes = ms.ToArray();
                        writer.Write(bytes.Length);
                        writer.Write(bytes);
                    }
                }
            }
        }
    }

    public void Load(BinaryReader reader)
    {
        Clear();
        
        // Load Exact Index
        int exactCount = reader.ReadInt32();
        _exactIndex = new Dictionary<string, RoaringBitmap>(exactCount);
        for (int i = 0; i < exactCount; i++)
        {
            string key = reader.ReadString();
            int bytesLength = reader.ReadInt32();
            byte[] bytes = reader.ReadBytes(bytesLength);
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                _exactIndex[key] = RoaringBitmap.Deserialize(ms);
            }
        }

        // Load LD1 Index
        int ld1Count = reader.ReadInt32();
        _ld1Index = new Dictionary<string, RoaringBitmap>(ld1Count);
        for (int i = 0; i < ld1Count; i++)
        {
            string key = reader.ReadString();
            int bytesLength = reader.ReadInt32();
            byte[] bytes = reader.ReadBytes(bytesLength);
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                _ld1Index[key] = RoaringBitmap.Deserialize(ms);
            }
        }

        // Load FST index
        bool hasFst = reader.ReadBoolean();
        if (hasFst)
        {
            _fstIndex = FstSerializer.Read(reader);
            
            // Load term-to-docIds mapping
            int termCount = reader.ReadInt32();
            _fstTermToDocIds = new Dictionary<int, RoaringBitmap>(termCount);
            for (int i = 0; i < termCount; i++)
            {
                int termId = reader.ReadInt32();
                int bytesLength = reader.ReadInt32();
                byte[] bytes = reader.ReadBytes(bytesLength);
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    _fstTermToDocIds[termId] = RoaringBitmap.Deserialize(ms);
                }
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
