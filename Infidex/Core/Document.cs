using Infidex.Api;

namespace Infidex.Core;

/// <summary>
/// Represents a document in the search engine.
/// A document is the entity being searched for - can describe everything from a product 
/// in an online store to a page in The Collected Works of Shakespeare.
/// Documents contain multiple named fields with configurable weights and properties.
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
    /// Collection of named fields with values, weights, and properties.
    /// This replaces the single IndexedText field for multi-field support.
    /// </summary>
    public DocumentFields Fields { get; set; }
    
    /// <summary>
    /// The concatenated indexed text from all searchable fields.
    /// Generated during indexing from Fields. Read-only.
    /// </summary>
    public string IndexedText { get; internal set; } = string.Empty;
    
    /// <summary>
    /// Client annotation - free text field for user information.
    /// For free use by the client, e.g. for thumbnail_ids, URIs and other references.
    /// This field is NOT indexed or searched.
    /// Examples: thumbnails, URIs, references, metadata, JSON data.
    /// </summary>
    public string? DocumentClientInformation { get; set; }
    
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
    /// Creates a document with a single indexed text field (backward compatibility helper)
    /// </summary>
    public Document(long documentKey, string text)
    {
        DocumentKey = documentKey;
        SegmentNumber = 0;
        Reserved = string.Empty;
        JsonIndex = 0;
        Deleted = false;
        VectorLength = 0f;
        
        // Create single field with the text
        Fields = new DocumentFields();
        Fields.AddField("content", text);
    }
    
    /// <summary>
    /// Creates a segmented document with a single indexed text field
    /// </summary>
    public Document(long documentKey, int segmentNumber, string text)
    {
        DocumentKey = documentKey;
        SegmentNumber = segmentNumber;
        Reserved = string.Empty;
        JsonIndex = 0;
        Deleted = false;
        VectorLength = 0f;
        
        // Create single field with the text
        Fields = new DocumentFields();
        Fields.AddField("content", text);
    }
    
    /// <summary>
    /// Creates a document with fields collection
    /// </summary>
    public Document(long documentKey, DocumentFields fields)
    {
        DocumentKey = documentKey;
        Fields = fields;
        SegmentNumber = 0;
        Reserved = string.Empty;
        JsonIndex = 0;
        Deleted = false;
        VectorLength = 0f;
    }
    
    /// <summary>
    /// Creates a document with full metadata
    /// </summary>
    public Document(
        long documentKey, 
        int segmentNumber, 
        DocumentFields fields,
        string? documentClientInformation = null)
    {
        DocumentKey = documentKey;
        SegmentNumber = segmentNumber;
        Fields = fields;
        DocumentClientInformation = documentClientInformation;
        Reserved = string.Empty;
        JsonIndex = 0;
        Deleted = false;
        VectorLength = 0f;
    }
    
    /// <summary>
    /// Copy constructor
    /// </summary>
    internal Document(Document source)
    {
        Id = source.Id;
        DocumentKey = source.DocumentKey;
        SegmentNumber = source.SegmentNumber;
        Fields = source.Fields;
        IndexedText = source.IndexedText;
        DocumentClientInformation = source.DocumentClientInformation;
        Reserved = source.Reserved;
        JsonIndex = source.JsonIndex;
        Deleted = source.Deleted;
        VectorLength = source.VectorLength;
    }
    
    public override string ToString()
    {
        string preview = IndexedText;
        if (string.IsNullOrEmpty(preview))
        {
            var firstField = Fields?.GetSearchAbleFieldList().FirstOrDefault();
            preview = firstField?.Value?.ToString() ?? "(empty)";
        }
        
        int previewLength = Math.Min(50, preview.Length);
        return $"Doc {DocumentKey}:{SegmentNumber} - {preview.Substring(0, previewLength)}...";
    }
}

