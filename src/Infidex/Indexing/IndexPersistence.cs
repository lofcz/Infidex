using Infidex.Api;
using Infidex.Core;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;
using Infidex.Coverage;

namespace Infidex.Indexing;

/// <summary>
/// Index persistence format with checksum validation, FST, and short query index.
/// This is the canonical on-disk representation; older formats are not supported.
/// </summary>
internal static class IndexPersistence
{
    private static readonly byte[] MAGIC = "INFDX2"u8.ToArray();
    private const uint FORMAT_VERSION = 2;
    
    // Flags
    [Flags]
    public enum IndexFlags : uint
    {
        None = 0,
        HasFst = 1 << 0,
        HasShortQueryIndex = 1 << 1,
        HasWordMatcher = 1 << 2,
        Compressed = 1 << 3, // Reserved for future
        HasDocumentMetadataCache = 1 << 4,
    }
    
    /// <summary>
    /// Saves the complete index to a stream.
    /// </summary>
    public static void Save(
        BinaryWriter writer,
        DocumentCollection documents,
        TermCollection terms,
        FstIndex? fstIndex = null,
        PositionalPrefixIndex? shortQueryIndex = null,
        DocumentMetadataCache? documentMetadataCache = null)
    {
        using MemoryStream dataStream = new();
        using BinaryWriter dataWriter = new(dataStream);
        
        // Determine flags
        IndexFlags flags = IndexFlags.None;
        if (fstIndex != null) flags |= IndexFlags.HasFst;
        if (shortQueryIndex != null) flags |= IndexFlags.HasShortQueryIndex;
        if (documentMetadataCache != null) flags |= IndexFlags.HasDocumentMetadataCache;
        
        // Write header
        writer.Write(MAGIC);
        writer.Write(FORMAT_VERSION);
        writer.Write((uint)flags);
        
        IReadOnlyList<Document> allDocs = documents.GetAllDocuments();
        writer.Write((uint)allDocs.Count);
        writer.Write((uint)terms.Count);
        
        // Calculate header checksum (simple XOR-based)
        uint headerChecksum = CalculateSimpleChecksum([
            FORMAT_VERSION,
            (uint)flags,
            (uint)allDocs.Count,
            (uint)terms.Count
        ]);
        writer.Write(headerChecksum);
        
        // Write documents section
        WriteDocuments(dataWriter, allDocs);
        
        // Write terms section
        WriteTerms(dataWriter, terms);
        
        // Write FST section (if present)
        if (fstIndex != null)
        {
            FstSerializer.Write(dataWriter, fstIndex);
        }
        
        // Write short query index (if present)
        if (shortQueryIndex != null)
        {
            shortQueryIndex.Write(dataWriter);
        }
        
        // Write document metadata cache (if present)
        if (documentMetadataCache != null)
        {
            documentMetadataCache.Write(dataWriter);
        }
        
        // Write data to main stream with explicit length prefix so that callers
        // can append additional sections (e.g., WordMatcher) after the index blob.
        byte[] data = dataStream.ToArray();
        writer.Write((uint)data.Length);
        writer.Write(data);
        
        // Write data checksum
        uint dataChecksum = CalculateSimpleChecksum(data);
        writer.Write(dataChecksum);
    }
    
    /// <summary>
    /// Loads an index from a stream.
    /// </summary>
    public static void Load(
        BinaryReader reader,
        DocumentCollection documents,
        TermCollection terms,
        int stopTermLimit,
        out FstIndex? fstIndex,
        out PositionalPrefixIndex? shortQueryIndex,
        out DocumentMetadataCache? documentMetadataCache)
    {
        fstIndex = null;
        shortQueryIndex = null;
        documentMetadataCache = null;
        
        // Read and verify magic
        byte[] magic = reader.ReadBytes(6);
        if (!magic.AsSpan().SequenceEqual(MAGIC))
        {
            string magicStr = System.Text.Encoding.ASCII.GetString(magic);
            throw new InvalidDataException($"Invalid index magic: expected INFDX2, got {magicStr}");
        }
        
        // Read and verify version
        uint version = reader.ReadUInt32();
        if (version != FORMAT_VERSION)
        {
            throw new InvalidDataException(
                $"Unsupported index version: {version}. Expected version {FORMAT_VERSION}. " +
                "Please rebuild the index.");
        }
        
        // Read header
        IndexFlags flags = (IndexFlags)reader.ReadUInt32();
        uint docCount = reader.ReadUInt32();
        uint termCount = reader.ReadUInt32();
        uint headerChecksum = reader.ReadUInt32();
        
        // Verify header checksum
        uint expectedHeaderChecksum = CalculateSimpleChecksum([
            FORMAT_VERSION,
            (uint)flags,
            docCount,
            termCount
        ]);
        if (headerChecksum != expectedHeaderChecksum)
        {
            throw new InvalidDataException("Header checksum mismatch - index file may be corrupted.");
        }
        
        // Read length-prefixed data blob and its checksum. This allows additional
        // sections (like WordMatcher) to live after the V2 index without being
        // part of the checksum region.
        uint dataLength = reader.ReadUInt32();
        
        if (dataLength > int.MaxValue)
        {
            throw new InvalidDataException($"Index data section too large: {dataLength} bytes.");
        }
        
        byte[] data = reader.ReadBytes((int)dataLength);
        uint dataChecksum = reader.ReadUInt32();
        
        uint expectedDataChecksum = CalculateSimpleChecksum(data);
        if (dataChecksum != expectedDataChecksum)
        {
            throw new InvalidDataException("Data checksum mismatch - index file may be corrupted.");
        }
        
        // Parse data
        using MemoryStream dataStream = new(data);
        using BinaryReader dataReader = new(dataStream);
        
        // Read documents
        ReadDocuments(dataReader, documents, (int)docCount);
        
        // Read terms
        ReadTerms(dataReader, terms, stopTermLimit, (int)termCount);
        
        // Read FST if present
        if (flags.HasFlag(IndexFlags.HasFst))
        {
            fstIndex = FstSerializer.Read(dataReader);
        }
        
        // Read short query index if present
        if (flags.HasFlag(IndexFlags.HasShortQueryIndex))
        {
            shortQueryIndex = new PositionalPrefixIndex();
            shortQueryIndex.Read(dataReader);
        }
        
        // Read document metadata cache if present
        if (flags.HasFlag(IndexFlags.HasDocumentMetadataCache))
        {
            documentMetadataCache = new DocumentMetadataCache();
            documentMetadataCache.Read(dataReader);
        }
    }
    
    /// <summary>
    /// Checks if a file is a valid index without fully loading it.
    /// </summary>
    public static bool IsValidIndex(string filePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            using BinaryReader reader = new(stream);
            
            if (stream.Length < 26) // Minimum header size
                return false;
            
            byte[] magic = reader.ReadBytes(6);
            if (!magic.AsSpan().SequenceEqual(MAGIC))
                return false;
            
            uint version = reader.ReadUInt32();
            return version == FORMAT_VERSION;
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    /// Gets format info from an index file without loading it.
    /// </summary>
    public static (bool IsValid, uint Version, IndexFlags Flags, uint DocCount, uint TermCount) GetIndexInfo(string filePath)
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            using BinaryReader reader = new(stream);
            
            byte[] magic = reader.ReadBytes(6);
            if (!magic.AsSpan().SequenceEqual(MAGIC))
                return (false, 0, IndexFlags.None, 0, 0);
            
            uint version = reader.ReadUInt32();
            IndexFlags flags = (IndexFlags)reader.ReadUInt32();
            uint docCount = reader.ReadUInt32();
            uint termCount = reader.ReadUInt32();
            
            return (true, version, flags, docCount, termCount);
        }
        catch
        {
            return (false, 0, IndexFlags.None, 0, 0);
        }
    }
    
    /// <summary>
    /// Simple XOR-based checksum for validation.
    /// </summary>
    private static uint CalculateSimpleChecksum(uint[] values)
    {
        uint checksum = 0x12345678;
        foreach (uint v in values)
        {
            checksum ^= v;
            checksum = (checksum << 7) | (checksum >> 25); // Rotate
        }
        return checksum;
    }
    
    /// <summary>
    /// Simple XOR-based checksum for byte arrays.
    /// </summary>
    private static uint CalculateSimpleChecksum(byte[] data)
    {
        uint checksum = 0x12345678;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint value = 0;
            int remaining = Math.Min(4, data.Length - i);
            for (int j = 0; j < remaining; j++)
            {
                value |= (uint)data[i + j] << (j * 8);
            }
            checksum ^= value;
            checksum = (checksum << 7) | (checksum >> 25);
        }
        return checksum;
    }
    
    #region Document Serialization
    
    private static void WriteDocuments(BinaryWriter writer, IReadOnlyList<Document> documents)
    {
        writer.Write(documents.Count);
        
        foreach (Document doc in documents)
        {
            writer.Write(doc.Id);
            writer.Write(doc.DocumentKey);
            writer.Write(doc.IndexedText ?? string.Empty);
            writer.Write(doc.DocumentClientInformation ?? string.Empty);
            writer.Write(doc.SegmentNumber);
            writer.Write(doc.JsonIndex);
            writer.Write(doc.Deleted);
        }
    }
    
    private static void ReadDocuments(BinaryReader reader, DocumentCollection documents, int expectedCount)
    {
        int count = reader.ReadInt32();
        if (count != expectedCount)
        {
            throw new InvalidDataException($"Document count mismatch: header says {expectedCount}, data has {count}");
        }
        
        for (int i = 0; i < count; i++)
        {
            int id = reader.ReadInt32();
            long key = reader.ReadInt64();
            string text = reader.ReadString();
            string info = reader.ReadString();
            int segment = reader.ReadInt32();
            int jsonIdx = reader.ReadInt32();
            bool deleted = reader.ReadBoolean();
            
            DocumentFields fields = new();
            fields.AddField("content", text, Weight.Med, indexable: true);
            
            Document doc = new(key, segment, fields, info)
            {
                JsonIndex = jsonIdx,
                IndexedText = text,
                Deleted = deleted
            };
            
            documents.AddDocument(doc);
        }
    }
    
    #endregion
    
    #region Term Serialization
    
    private static void WriteTerms(BinaryWriter writer, TermCollection terms)
    {
        IEnumerable<Term> allTerms = terms.GetAllTerms();
        
        // Count non-stop terms
        List<Term> termList = allTerms.Where(t => t.DocumentFrequency > 0).ToList();
        writer.Write(termList.Count);
        
        foreach (Term term in termList)
        {
            writer.Write(term.Text ?? string.Empty);
            writer.Write(term.DocumentFrequency);
            
            List<int>? docIds = term.GetDocumentIds();
            List<byte>? weights = term.GetWeights();
            
            int postingCount = docIds?.Count ?? 0;
            writer.Write(postingCount);
            
            if (postingCount > 0 && docIds != null && weights != null)
            {
                // Write postings in packed format
                for (int i = 0; i < postingCount; i++)
                {
                    writer.Write(docIds[i]);
                    writer.Write(weights[i]);
                }
            }
        }
    }
    
    private static void ReadTerms(BinaryReader reader, TermCollection terms, int stopTermLimit, int expectedCount)
    {
        int count = reader.ReadInt32();
        // Note: count may differ from expectedCount due to stop terms
        
        for (int i = 0; i < count; i++)
        {
            string text = reader.ReadString();
            int docFreq = reader.ReadInt32();
            int postingCount = reader.ReadInt32();
            
            Term term = terms.CountTermUsage(text, stopTermLimit, forFastInsert: true);
            term.SetDocumentFrequency(docFreq);
            
            for (int j = 0; j < postingCount; j++)
            {
                int docId = reader.ReadInt32();
                byte weight = reader.ReadByte();
                term.AddForFastInsert(weight, docId);
            }
        }
    }
    
    #endregion
}
