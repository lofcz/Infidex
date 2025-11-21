namespace Infidex.Core;

/// <summary>
/// Represents a document in the search engine.
/// A document is the entity being searched for - can describe everything from a product 
/// in an online store to a page in The Collected Works of Shakespeare.
/// </summary>
public class Document
{
    /// <summary>
    /// Internal document ID (auto-assigned, used for indexing)
    /// </summary>
    public int Id { get; internal set; }
    
    /// <summary>
    /// 64-bit foreign key, needed to support update, delete and filter operations.
    /// Can be used to create aliases by using the same DocumentKey for multiple documents.
    /// If update, delete or filter functions are required, a foreign key (DocumentKey) is needed.
    /// </summary>
    public long DocumentKey { get; set; }
    
    /// <summary>
    /// Segment number for large documents split into parts.
    /// Can be used to add extra info such as line numbers in a book or page numbers.
    /// Set to 0 for non-segmented documents.
    /// Used by the client when searching in large "body texts".
    /// </summary>
    public int SegmentNumber { get; set; }
    
    /// <summary>
    /// The text to be searched and indexed.
    /// This is the indexed text field.
    /// </summary>
    public string IndexedText { get; set; }
    
    /// <summary>
    /// Client annotation - free text field for user information.
    /// For free use by the client, e.g. for thumbnail_ids, URIs and other references.
    /// This field is NOT indexed or searched.
    /// Examples: thumbnails, URIs, references, metadata, JSON data.
    /// </summary>
    public string DocumentClientInformation { get; set; }
    
    /// <summary>
    /// Reserved field for internal use (e.g., storing original text in first segment)
    /// </summary>
    public string Reserved { get; set; }
    
    /// <summary>
    /// JSON field index (for multi-field indexing)
    /// </summary>
    public int JsonIndex { get; set; }
    
    /// <summary>
    /// Marked for deletion (soft delete)
    /// </summary>
    public bool Deleted { get; set; }
    
    /// <summary>
    /// Vector length for TF-IDF normalization (internal use)
    /// </summary>
    internal float VectorLength { get; set; }
    
    /// <summary>
    /// Creates a document with indexed text only (simple constructor)
    /// </summary>
    public Document(long documentKey, string indexedText)
    {
        DocumentKey = documentKey;
        IndexedText = indexedText ?? string.Empty;
        DocumentClientInformation = string.Empty;
        SegmentNumber = 0;
        Reserved = string.Empty;
        JsonIndex = 0;
        Deleted = false;
        VectorLength = 0f;
    }
    
    /// <summary>
    /// Creates a document with full metadata (complete constructor)
    /// </summary>
    public Document(
        long documentKey, 
        int segmentNumber, 
        string indexedText, 
        string documentClientInformation)
    {
        DocumentKey = documentKey;
        SegmentNumber = segmentNumber;
        IndexedText = indexedText ?? string.Empty;
        DocumentClientInformation = documentClientInformation ?? string.Empty;
        Reserved = string.Empty;
        JsonIndex = 0;
        Deleted = false;
        VectorLength = 0f;
    }
    
    /// <summary>
    /// Copy constructor - Document(Document document)
    /// </summary>
    public Document(Document source)
    {
        Id = source.Id;
        DocumentKey = source.DocumentKey;
        SegmentNumber = source.SegmentNumber;
        IndexedText = source.IndexedText;
        DocumentClientInformation = source.DocumentClientInformation;
        Reserved = source.Reserved;
        JsonIndex = source.JsonIndex;
        Deleted = source.Deleted;
        VectorLength = source.VectorLength;
    }
    
    public override string ToString() => 
        $"Doc {DocumentKey}:{SegmentNumber} - {IndexedText.Substring(0, Math.Min(50, IndexedText.Length))}...";
}

