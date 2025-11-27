using Infidex.Api;
using Infidex.Core;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;
using Infidex.Coverage;

namespace Infidex.Indexing;

/// <summary>
/// Serialization logic for VectorModel index persistence.
/// Uses the current binary format with checksum validation.
/// Older formats are not supported.
/// </summary>
internal static class VectorModelPersistence
{
    /// <summary>
    /// Saves the index to a stream.
    /// </summary>
    public static void SaveToStream(BinaryWriter writer, DocumentCollection documents, TermCollection termCollection)
    {
        SaveToStream(writer, documents, termCollection, null, null);
    }
    
    /// <summary>
    /// Saves the index to a stream with optional FST and short query indexes.
    /// </summary>
    public static void SaveToStream(
        BinaryWriter writer,
        DocumentCollection documents,
        TermCollection termCollection,
        FstIndex? fstIndex,
        PositionalPrefixIndex? shortQueryIndex,
        DocumentMetadataCache? documentMetadataCache = null)
    {
        IndexPersistence.Save(writer, documents, termCollection, fstIndex, shortQueryIndex, documentMetadataCache);
    }
    
    /// <summary>
    /// Loads the index from a stream.
    /// </summary>
    public static void LoadFromStream(BinaryReader reader, DocumentCollection documents, TermCollection termCollection, int stopTermLimit)
    {
        LoadFromStream(reader, documents, termCollection, stopTermLimit, out _, out _, out _);
    }
    
    /// <summary>
    /// Loads the index from a stream with FST and short query indexes.
    /// </summary>
    public static void LoadFromStream(
        BinaryReader reader,
        DocumentCollection documents,
        TermCollection termCollection,
        int stopTermLimit,
        out FstIndex? fstIndex,
        out PositionalPrefixIndex? shortQueryIndex,
        out DocumentMetadataCache? documentMetadataCache)
    {
        IndexPersistence.Load(reader, documents, termCollection, stopTermLimit, out fstIndex, out shortQueryIndex, out documentMetadataCache);
    }
    
    /// <summary>
    /// Checks if a file contains a valid index.
    /// </summary>
    public static bool IsValidIndex(string filePath)
    {
        return IndexPersistence.IsValidIndex(filePath);
    }
    
    /// <summary>
    /// Gets format information from an index file.
    /// </summary>
    public static IndexFormatInfo GetFormatInfo(string filePath)
    {
        (bool isValid, uint version, IndexPersistence.IndexFlags flags, uint docCount, uint termCount) = IndexPersistence.GetIndexInfo(filePath);
        
        return new IndexFormatInfo
        {
            IsValid = isValid,
            Version = version,
            IsLegacy = false,
            DocumentCount = (int)docCount,
            TermCount = (int)termCount,
            HasFst = flags.HasFlag(IndexPersistence.IndexFlags.HasFst),
            HasShortQueryIndex = flags.HasFlag(IndexPersistence.IndexFlags.HasShortQueryIndex),
            HasDocumentMetadataCache = flags.HasFlag(IndexPersistence.IndexFlags.HasDocumentMetadataCache)
        };
    }
}

/// <summary>
/// Information about an index file's format.
/// </summary>
public sealed class IndexFormatInfo
{
    public bool IsValid { get; set; }
    public uint Version { get; set; }
    public bool IsLegacy { get; set; }
    public int DocumentCount { get; set; }
    public int TermCount { get; set; }
    public bool HasFst { get; set; }
    public bool HasShortQueryIndex { get; set; }
    public bool HasDocumentMetadataCache { get; set; }
    public string? ErrorMessage { get; set; }
    
    public override string ToString()
    {
        if (!IsValid)
            return $"Invalid: {ErrorMessage ?? "Unknown error"}";
        
        return $"V{Version}: {DocumentCount} docs, {TermCount} terms" +
               (HasFst ? ", FST" : "") +
               (HasShortQueryIndex ? ", ShortQueryIdx" : "");
    }
}
