using System.Runtime.InteropServices;

namespace Infidex.Coverage;

/// <summary>
/// Precomputed per-document metadata to eliminate string operations during scoring.
/// Built once at index time, consulted during every query.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct DocumentMetadata
{
    /// <summary>First token of the document (lowercased), used for "starts at beginning" checks.</summary>
    public readonly string FirstToken;
    
    /// <summary>Number of tokens in the document.</summary>
    public readonly ushort TokenCount;
    
    /// <summary>Whether document has any tokens (empty doc optimization).</summary>
    public readonly bool HasTokens;
    
    public DocumentMetadata(string firstToken, ushort tokenCount)
    {
        FirstToken = firstToken ?? string.Empty;
        TokenCount = tokenCount;
        HasTokens = tokenCount > 0;
    }
    
    public static DocumentMetadata Empty => new(string.Empty, 0);
}

/// <summary>
/// Cache of precomputed document metadata indexed by internal document ID.
/// Thread-safe for concurrent reads after construction.
/// </summary>
internal sealed class DocumentMetadataCache
{
    private DocumentMetadata[] _metadata;
    private readonly object _lock = new();
    
    public DocumentMetadataCache(int initialCapacity = 1024)
    {
        _metadata = new DocumentMetadata[initialCapacity];
    }
    
    /// <summary>
    /// Sets metadata for a document. Thread-safe.
    /// </summary>
    public void Set(int documentId, DocumentMetadata metadata)
    {
        lock (_lock)
        {
            if (documentId >= _metadata.Length)
            {
                int newSize = Math.Max(documentId + 1, _metadata.Length * 2);
                Array.Resize(ref _metadata, newSize);
            }
            
            _metadata[documentId] = metadata;
        }
    }
    
    /// <summary>
    /// Gets metadata for a document. Returns Empty if not found.
    /// Thread-safe for concurrent reads after construction is complete.
    /// </summary>
    public DocumentMetadata Get(int documentId)
    {
        if (documentId < 0 || documentId >= _metadata.Length)
            return DocumentMetadata.Empty;
        
        return _metadata[documentId];
    }
    
    /// <summary>
    /// Checks if document's first token matches the query token (case-insensitive).
    /// This is the core "starts at beginning" optimization.
    /// </summary>
    public bool StartsWithToken(int documentId, ReadOnlySpan<char> queryToken)
    {
        if (documentId < 0 || documentId >= _metadata.Length)
            return false;
        
        ref readonly DocumentMetadata meta = ref _metadata[documentId];
        if (!meta.HasTokens || meta.FirstToken.Length == 0)
            return false;
        
        return meta.FirstToken.AsSpan().StartsWith(queryToken, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Clears all cached metadata.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_metadata);
        }
    }
    
    /// <summary>
    /// Gets the total number of entries in the cache.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _metadata.Length;
            }
        }
    }
    
    #region Serialization
    
    public void Write(BinaryWriter writer)
    {
        lock (_lock)
        {
            writer.Write(_metadata.Length);
            for (int i = 0; i < _metadata.Length; i++)
            {
                ref readonly DocumentMetadata meta = ref _metadata[i];
                writer.Write(meta.FirstToken ?? string.Empty);
                writer.Write(meta.TokenCount);
            }
        }
    }
    
    public void Read(BinaryReader reader)
    {
        lock (_lock)
        {
            int count = reader.ReadInt32();
            _metadata = new DocumentMetadata[count];
            
            for (int i = 0; i < count; i++)
            {
                string firstToken = reader.ReadString();
                ushort tokenCount = reader.ReadUInt16();
                _metadata[i] = new DocumentMetadata(firstToken, tokenCount);
            }
        }
    }
    
    #endregion
}

