using System.Collections;
using System.Runtime.CompilerServices;

namespace Infidex.Indexing.Incremental;

/// <summary>
/// Tracks deleted document IDs using a space-efficient bitset.
/// Thread-safe for concurrent read/write access.
/// </summary>
internal sealed class TombstoneTracker
{
    private readonly BitArray _tombstones;
    private readonly object _lock = new object();
    private int _deletedCount;
    
    /// <summary>
    /// Number of deleted documents.
    /// </summary>
    public int DeletedCount => _deletedCount;
    
    /// <summary>
    /// Total capacity (max document ID + 1).
    /// </summary>
    public int Capacity => _tombstones.Length;
    
    public TombstoneTracker(int capacity)
    {
        _tombstones = new BitArray(capacity);
        _deletedCount = 0;
    }
    
    /// <summary>
    /// Marks a document as deleted.
    /// </summary>
    /// <returns>True if the document was not already deleted.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MarkDeleted(int documentId)
    {
        if (documentId < 0 || documentId >= _tombstones.Length)
            return false;
        
        lock (_lock)
        {
            if (_tombstones[documentId])
                return false;
            
            _tombstones[documentId] = true;
            _deletedCount++;
            return true;
        }
    }
    
    /// <summary>
    /// Unmarks a document as deleted (restore).
    /// </summary>
    public bool Restore(int documentId)
    {
        if (documentId < 0 || documentId >= _tombstones.Length)
            return false;
        
        lock (_lock)
        {
            if (!_tombstones[documentId])
                return false;
            
            _tombstones[documentId] = false;
            _deletedCount--;
            return true;
        }
    }
    
    /// <summary>
    /// Checks if a document is deleted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDeleted(int documentId)
    {
        if (documentId < 0 || documentId >= _tombstones.Length)
            return true; // Out of range = deleted
        
        lock (_lock)
        {
            return _tombstones[documentId];
        }
    }
    
    /// <summary>
    /// Gets all deleted document IDs.
    /// </summary>
    public IEnumerable<int> GetDeletedIds()
    {
        lock (_lock)
        {
            for (int i = 0; i < _tombstones.Length; i++)
            {
                if (_tombstones[i])
                    yield return i;
            }
        }
    }
    
    /// <summary>
    /// Expands capacity to accommodate more documents.
    /// </summary>
    public void ExpandTo(int newCapacity)
    {
        if (newCapacity <= _tombstones.Length)
            return;
        
        lock (_lock)
        {
            _tombstones.Length = newCapacity;
        }
    }
    
    /// <summary>
    /// Clears all tombstones.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _tombstones.SetAll(false);
            _deletedCount = 0;
        }
    }
    
    #region Serialization
    
    public void Write(BinaryWriter writer)
    {
        lock (_lock)
        {
            writer.Write(_tombstones.Length);
            writer.Write(_deletedCount);
            
            // Write as packed bytes
            byte[] bytes = new byte[(_tombstones.Length + 7) / 8];
            _tombstones.CopyTo(bytes, 0);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }
    
    public static TombstoneTracker Read(BinaryReader reader)
    {
        int capacity = reader.ReadInt32();
        int deletedCount = reader.ReadInt32();
        int byteCount = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(byteCount);
        
        TombstoneTracker tracker = new TombstoneTracker(capacity);
        BitArray loaded = new BitArray(bytes);
        
        for (int i = 0; i < Math.Min(loaded.Length, capacity); i++)
        {
            tracker._tombstones[i] = loaded[i];
        }
        tracker._deletedCount = deletedCount;
        
        return tracker;
    }
    
    #endregion
}


