using System.Runtime.CompilerServices;

namespace Infidex.Indexing.ShortQuery;

/// <summary>
/// Pre-computed index for instant O(1) short query (1-3 character prefix) resolution.
/// Stores positional information for ranking (first position, word start, etc.).
/// Thread-safe for concurrent read access after construction.
/// </summary>
internal sealed class PositionalPrefixIndex
{
    // Index for 1-char prefixes: direct array indexing by char value
    private readonly PrefixPostingList?[] _singleCharIndex;

    // Index for 2-char and 3-char prefixes: dictionary-based
    private readonly Dictionary<string, PrefixPostingList> _multiCharIndex;

    // Maximum prefix length to index
    private const int MAX_PREFIX_LENGTH = 3;

    // Configuration
    private readonly int _minPrefixLength;
    private readonly int _maxPrefixLength;
    private readonly char[] _delimiters;

    public int MinPrefixLength => _minPrefixLength;
    public int MaxPrefixLength => _maxPrefixLength;

    /// <summary>
    /// Creates a new positional prefix index.
    /// </summary>
    /// <param name="minPrefixLength">Minimum prefix length to index (default 1).</param>
    /// <param name="maxPrefixLength">Maximum prefix length to index (default 3).</param>
    /// <param name="delimiters">Token delimiters for word boundary detection.</param>
    public PositionalPrefixIndex(int minPrefixLength = 1, int maxPrefixLength = 3, char[]? delimiters = null)
    {
        _minPrefixLength = Math.Max(1, minPrefixLength);
        _maxPrefixLength = Math.Min(MAX_PREFIX_LENGTH, maxPrefixLength);
        _delimiters = delimiters ?? [' '];

        // Direct array for single-char lookups (covers most common case)
        _singleCharIndex = new PrefixPostingList?[char.MaxValue + 1];

        // Dictionary for multi-char prefixes
        _multiCharIndex = new Dictionary<string, PrefixPostingList>(StringComparer.Ordinal);
    }

    #region Indexing

    /// <summary>
    /// Indexes a document's text, extracting all short prefixes with positions.
    /// </summary>
    /// <param name="text">Document text (should be normalized/lowercased).</param>
    /// <param name="documentId">Internal document ID.</param>
    public void IndexDocument(string text, int documentId)
    {
        if (string.IsNullOrEmpty(text))
            return;

        ReadOnlySpan<char> textSpan = text.AsSpan();
        HashSet<char> delimiterSet = new HashSet<char>(_delimiters);

        int tokenIndex = 0;
        int i = 0;

        // Skip leading delimiters
        while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
            i++;

        while (i < textSpan.Length)
        {
            // Find end of current token
            int tokenStart = i;
            while (i < textSpan.Length && !delimiterSet.Contains(textSpan[i]))
                i++;

            int tokenLength = i - tokenStart;

            if (tokenLength > 0)
            {
                // Index prefixes of this token
                IndexTokenPrefixes(textSpan.Slice(tokenStart, tokenLength), documentId, (ushort)tokenIndex);
                tokenIndex++;
            }

            // Skip delimiters to next token
            while (i < textSpan.Length && delimiterSet.Contains(textSpan[i]))
                i++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IndexTokenPrefixes(ReadOnlySpan<char> token, int documentId, ushort tokenPosition)
    {
        int maxLen = Math.Min(token.Length, _maxPrefixLength);

        for (int prefixLen = _minPrefixLength; prefixLen <= maxLen; prefixLen++)
        {
            if (prefixLen == 1)
            {
                // Single char - use direct array
                char c = token[0];
                ref PrefixPostingList? list = ref _singleCharIndex[c];
                list ??= new PrefixPostingList();
                list.Add(documentId, tokenPosition, isWordStart: true);
            }
            else
            {
                // Multi-char - use dictionary
                string prefix = token.Slice(0, prefixLen).ToString();
                if (!_multiCharIndex.TryGetValue(prefix, out PrefixPostingList? list))
                {
                    list = new PrefixPostingList();
                    _multiCharIndex[prefix] = list;
                }
                list.Add(documentId, tokenPosition, isWordStart: true);
            }
        }
    }

    /// <summary>
    /// Freezes the index after all documents have been added.
    /// Sorts all posting lists and compacts memory.
    /// </summary>
    public void Freeze()
    {
        // Finalize single-char lists
        for (int i = 0; i < _singleCharIndex.Length; i++)
        {
            PrefixPostingList? list = _singleCharIndex[i];
            if (list != null)
            {
                list.Sort();
                list.Compact();
                list.BuildDocSet();
            }
        }

        // Finalize multi-char lists
        foreach (PrefixPostingList list in _multiCharIndex.Values)
        {
            list.Sort();
            list.Compact();
            list.BuildDocSet();
        }
    }

    /// <summary>
    /// Clears all indexed data.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_singleCharIndex);
        _multiCharIndex.Clear();
    }

    #endregion

    #region Querying

    /// <summary>
    /// Gets the posting list for a prefix, or null if not found.
    /// Complexity: O(1) for single char, O(1) average for multi-char.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PrefixPostingList? GetPostingList(ReadOnlySpan<char> prefix)
    {
        if (prefix.IsEmpty || prefix.Length > _maxPrefixLength)
            return null;

        if (prefix.Length == 1)
            return _singleCharIndex[prefix[0]];

        // For multi-char, we need to create a string key
        // This is unavoidable with Dictionary<string, T>
        return _multiCharIndex.GetValueOrDefault(prefix.ToString());
    }

    /// <summary>
    /// Gets all unique document IDs matching the prefix.
    /// </summary>
    public void GetDocumentIds(ReadOnlySpan<char> prefix, HashSet<int> result)
    {
        PrefixPostingList? list = GetPostingList(prefix);
        list?.GetUniqueDocumentIds(result);
    }

    /// <summary>
    /// Gets document IDs where the prefix appears at word start.
    /// </summary>
    public void GetWordStartDocumentIds(ReadOnlySpan<char> prefix, HashSet<int> result)
    {
        PrefixPostingList? list = GetPostingList(prefix);
        list?.GetWordStartDocumentIds(result);
    }

    /// <summary>
    /// Checks if any document contains the prefix.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasPrefix(ReadOnlySpan<char> prefix)
    {
        PrefixPostingList? list = GetPostingList(prefix);
        return list != null && list.Count > 0;
    }

    /// <summary>
    /// Returns the number of documents matching the prefix.
    /// </summary>
    public int CountDocuments(ReadOnlySpan<char> prefix)
    {
        PrefixPostingList? list = GetPostingList(prefix);
        if (list == null) return 0;

        HashSet<int> docs = new HashSet<int>();
        list.GetUniqueDocumentIds(docs);
        return docs.Count;
    }

    /// <summary>
    /// Enumerates all prefixes and their posting lists.
    /// Intended for building secondary structures like champion lists.
    /// </summary>
    internal IEnumerable<(string Prefix, PrefixPostingList List)> GetAllPrefixes()
    {
        // Single-character prefixes from array index
        for (int i = 0; i < _singleCharIndex.Length; i++)
        {
            PrefixPostingList? list = _singleCharIndex[i];
            if (list != null && list.Count > 0)
            {
                char c = (char)i;
                yield return (c.ToString(), list);
            }
        }

        // Multi-character prefixes from dictionary
        foreach ((string prefix, PrefixPostingList list) in _multiCharIndex)
        {
            if (list.Count > 0)
                yield return (prefix, list);
        }
    }

    #endregion

    #region Serialization

    public void Write(BinaryWriter writer)
    {
        // Write single-char index
        int singleCharCount = 0;
        for (int i = 0; i < _singleCharIndex.Length; i++)
            if (_singleCharIndex[i] != null) singleCharCount++;

        writer.Write(singleCharCount);
        for (int i = 0; i < _singleCharIndex.Length; i++)
        {
            PrefixPostingList? list = _singleCharIndex[i];
            if (list != null)
            {
                writer.Write((ushort)i);
                list.Write(writer);
            }
        }

        // Write multi-char index
        writer.Write(_multiCharIndex.Count);
        foreach ((string prefix, PrefixPostingList list) in _multiCharIndex)
        {
            writer.Write(prefix);
            list.Write(writer);
        }
    }

    public void Read(BinaryReader reader)
    {
        Clear();

        // Read single-char index
        int singleCharCount = reader.ReadInt32();
        for (int i = 0; i < singleCharCount; i++)
        {
            char c = (char)reader.ReadUInt16();
            _singleCharIndex[c] = PrefixPostingList.Read(reader);
        }

        // Read multi-char index
        int multiCharCount = reader.ReadInt32();
        for (int i = 0; i < multiCharCount; i++)
        {
            string prefix = reader.ReadString();
            _multiCharIndex[prefix] = PrefixPostingList.Read(reader);
        }
    }

    #endregion
}
