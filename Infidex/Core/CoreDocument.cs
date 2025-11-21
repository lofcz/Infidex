namespace Infidex.Core;

/// <summary>
/// Represents a document or document segment in the core search engine.
/// </summary>
public class CoreDocument
{
    /// <summary>
    /// External/public document key
    /// </summary>
    public long DocumentKey { get; set; }
    
    /// <summary>
    /// Segment number (0 for non-segmented documents)
    /// </summary>
    public int SegmentNumber { get; set; }
    
    /// <summary>
    /// Text to be indexed
    /// </summary>
    public string IndexedText { get; set; }
    
    /// <summary>
    /// Client/user-provided information
    /// </summary>
    public string DocumentClientInformation { get; set; }
    
    /// <summary>
    /// Internal JSON/field index
    /// </summary>
    public int JsonIndex { get; set; }
    
    /// <summary>
    /// Reserved for storing original text (used in first segment)
    /// </summary>
    public string? Reserved { get; set; }
    
    /// <summary>
    /// Whether this document is marked for deletion
    /// </summary>
    public bool IsDeleted { get; set; }
    
    public CoreDocument(long documentKey, int segmentNumber, string indexedText, string clientInfo, int jsonIndex)
    {
        DocumentKey = documentKey;
        SegmentNumber = segmentNumber;
        IndexedText = indexedText;
        DocumentClientInformation = clientInfo;
        JsonIndex = jsonIndex;
        IsDeleted = false;
    }
    
    public CoreDocument(CoreDocument source)
    {
        DocumentKey = source.DocumentKey;
        SegmentNumber = source.SegmentNumber;
        IndexedText = source.IndexedText;
        DocumentClientInformation = source.DocumentClientInformation;
        JsonIndex = source.JsonIndex;
        Reserved = source.Reserved;
        IsDeleted = source.IsDeleted;
    }
    
    public override string ToString() => $"Doc {DocumentKey}:{SegmentNumber} - {IndexedText.Substring(0, Math.Min(50, IndexedText.Length))}...";
}


