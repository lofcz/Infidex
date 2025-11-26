using System.Runtime.InteropServices;

namespace Infidex.Indexing.ShortQuery;

/// <summary>
/// A compact posting for short prefix queries containing document ID and token position.
/// Designed for memory efficiency and cache-friendly access patterns.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct PrefixPosting : IComparable<PrefixPosting>
{
    /// <summary>Internal document ID.</summary>
    public readonly int DocumentId;
    
    /// <summary>Token position within the document (0-based).</summary>
    public readonly ushort Position;
    
    /// <summary>Whether this is a word-start match (token starts with the prefix).</summary>
    public readonly bool IsWordStart;
    
    public PrefixPosting(int documentId, ushort position, bool isWordStart)
    {
        DocumentId = documentId;
        Position = position;
        IsWordStart = isWordStart;
    }
    
    public int CompareTo(PrefixPosting other)
    {
        int docCompare = DocumentId.CompareTo(other.DocumentId);
        if (docCompare != 0) return docCompare;
        return Position.CompareTo(other.Position);
    }
    
    public override string ToString() => $"Doc:{DocumentId}, Pos:{Position}, WordStart:{IsWordStart}";
}

/// <summary>
/// Compact posting list for a specific prefix.
/// Uses sorted array for efficient iteration and binary search.
/// </summary>
internal sealed class PrefixPostingList
{
    private PrefixPosting[] _postings;
    private int _count;
    private bool _sorted;
    
    /// <summary>Number of postings in the list.</summary>
    public int Count => _count;
    
    /// <summary>Gets the underlying postings span.</summary>
    public ReadOnlySpan<PrefixPosting> Postings => _postings.AsSpan(0, _count);
    
    public PrefixPostingList(int initialCapacity = 16)
    {
        _postings = new PrefixPosting[initialCapacity];
        _count = 0;
        _sorted = true;
    }
    
    /// <summary>
    /// Adds a posting to the list. Call Sort() before querying.
    /// </summary>
    public void Add(int documentId, ushort position, bool isWordStart)
    {
        if (_count >= _postings.Length)
            Array.Resize(ref _postings, _postings.Length * 2);
        
        _postings[_count++] = new PrefixPosting(documentId, position, isWordStart);
        _sorted = false;
    }
    
    /// <summary>
    /// Sorts the posting list by document ID, then position.
    /// </summary>
    public void Sort()
    {
        if (_sorted) return;
        Array.Sort(_postings, 0, _count);
        _sorted = true;
    }
    
    /// <summary>
    /// Compacts the array to exactly fit the current count.
    /// Call after all additions are complete.
    /// </summary>
    public void Compact()
    {
        if (_postings.Length > _count)
            Array.Resize(ref _postings, _count);
    }
    
    /// <summary>
    /// Gets all unique document IDs in the posting list.
    /// </summary>
    public void GetUniqueDocumentIds(HashSet<int> result)
    {
        if (!_sorted) Sort();
        
        int lastDocId = -1;
        for (int i = 0; i < _count; i++)
        {
            int docId = _postings[i].DocumentId;
            if (docId != lastDocId)
            {
                result.Add(docId);
                lastDocId = docId;
            }
        }
    }
    
    /// <summary>
    /// Gets document IDs where the prefix appears at word start (position 0 of a token).
    /// </summary>
    public void GetWordStartDocumentIds(HashSet<int> result)
    {
        if (!_sorted) Sort();
        
        for (int i = 0; i < _count; i++)
        {
            if (_postings[i].IsWordStart)
                result.Add(_postings[i].DocumentId);
        }
    }
    
    /// <summary>
    /// Counts documents where prefix appears at the first position (beginning of text).
    /// </summary>
    public int CountFirstPositionMatches()
    {
        int count = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_postings[i].Position == 0 && _postings[i].IsWordStart)
                count++;
        }
        return count;
    }
    
    /// <summary>
    /// Binary search for the first posting with the given document ID.
    /// Returns the index or -1 if not found.
    /// </summary>
    public int FindFirstForDocument(int documentId)
    {
        if (!_sorted) Sort();
        
        int lo = 0, hi = _count - 1;
        int result = -1;
        
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_postings[mid].DocumentId >= documentId)
            {
                if (_postings[mid].DocumentId == documentId)
                    result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Gets all postings for a specific document.
    /// </summary>
    public void GetPostingsForDocument(int documentId, List<PrefixPosting> result)
    {
        int start = FindFirstForDocument(documentId);
        if (start < 0) return;
        
        for (int i = start; i < _count && _postings[i].DocumentId == documentId; i++)
            result.Add(_postings[i]);
    }
    
    #region Serialization
    
    public void Write(BinaryWriter writer)
    {
        writer.Write(_count);
        for (int i = 0; i < _count; i++)
        {
            writer.Write(_postings[i].DocumentId);
            writer.Write(_postings[i].Position);
            writer.Write(_postings[i].IsWordStart);
        }
    }
    
    public static PrefixPostingList Read(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        PrefixPostingList list = new PrefixPostingList(count);
        
        for (int i = 0; i < count; i++)
        {
            int docId = reader.ReadInt32();
            ushort pos = reader.ReadUInt16();
            bool wordStart = reader.ReadBoolean();
            list._postings[i] = new PrefixPosting(docId, pos, wordStart);
        }
        
        list._count = count;
        list._sorted = true;
        return list;
    }
    
    #endregion
}


