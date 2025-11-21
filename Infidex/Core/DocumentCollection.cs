namespace Infidex.Core;

/// <summary>
/// Manages the collection of indexed documents.
/// </summary>
public class DocumentCollection
{
    private readonly List<Document> _documents;
    private readonly Dictionary<long, List<int>> _documentKeyToIds; // Supports aliases (same key, multiple docs)
    
    public DocumentCollection()
    {
        _documents = [];
        _documentKeyToIds = new Dictionary<long, List<int>>();
    }
    
    /// <summary>
    /// Adds a document to the collection (modern API)
    /// </summary>
    public Document AddDocument(Document document)
    {
        int id = _documents.Count;
        document.Id = id;
        _documents.Add(document);
        
        // Support aliases - same DocumentKey can map to multiple documents
        if (!_documentKeyToIds.ContainsKey(document.DocumentKey))
            _documentKeyToIds[document.DocumentKey] = [];
        
        _documentKeyToIds[document.DocumentKey].Add(id);
        return document;
    }
    
    /// <summary>
    /// Adds a document to the collection (simple API for backward compatibility)
    /// </summary>
    public Document AddDocument(long documentKey, string indexedText)
    {
        Document doc = new Document(documentKey, indexedText);
        return AddDocument(doc);
    }
    
    /// <summary>
    /// Gets a document by internal ID
    /// </summary>
    public Document? GetDocument(int id)
    {
        if (id >= 0 && id < _documents.Count)
            return _documents[id];
        return null;
    }
    
    /// <summary>
    /// Gets document(s) by DocumentKey (supports aliases - can return multiple)
    /// </summary>
    public List<Document> GetDocumentsByKey(long documentKey)
    {
        List<Document> results = [];
        if (_documentKeyToIds.TryGetValue(documentKey, out List<int>? ids))
        {
            foreach (int id in ids)
            {
                Document? doc = GetDocument(id);
                if (doc != null && !doc.Deleted)
                    results.Add(doc);
            }
        }
        return results;
    }
    
    /// <summary>
    /// Gets first document by DocumentKey (for simple cases)
    /// </summary>
    public Document? GetDocumentByPublicKey(long documentKey)
    {
        List<Document> docs = GetDocumentsByKey(documentKey);
        return docs.FirstOrDefault();
    }
    
    /// <summary>
    /// Gets all documents for a PublicKey (including segments), used for segment consolidation
    /// </summary>
    public List<Document> GetDocumentsForPublicKey(long publicKey)
    {
        List<Document> results = [];
        if (_documentKeyToIds.TryGetValue(publicKey, out List<int>? ids))
        {
            foreach (int id in ids)
            {
                Document? doc = GetDocument(id);
                if (doc != null)
                    results.Add(doc);
            }
        }
        return results;
    }
    
    /// <summary>
    /// Gets a specific segment of a document by PublicKey and SegmentNumber
    /// </summary>
    public Document? GetDocumentOfSegment(long publicKey, int segmentNumber)
    {
        if (!_documentKeyToIds.TryGetValue(publicKey, out List<int>? ids))
            return null;
        
        foreach (int id in ids)
        {
            Document? doc = GetDocument(id);
            if (doc != null && doc.SegmentNumber == segmentNumber)
                return doc;
        }
        return null;
    }
    
    /// <summary>
    /// Removes deleted documents from the collection
    /// </summary>
    public void RemoveDeletedDocuments()
    {
        // In a production system, this would compact the collection
        // For now, we just mark them deleted
        foreach (Document doc in _documents)
        {
            if (doc.Deleted)
            {
                // Remove from lookup
                if (_documentKeyToIds.TryGetValue(doc.DocumentKey, out List<int>? ids))
                {
                    ids.Remove(doc.Id);
                }
            }
        }
    }
    
    /// <summary>
    /// Marks documents with a specific DocumentKey as deleted
    /// </summary>
    public void DeleteDocumentsByKey(long documentKey)
    {
        List<Document> docs = GetDocumentsByKey(documentKey);
        foreach (Document doc in docs)
        {
            doc.Deleted = true;
        }
    }
    
    /// <summary>
    /// Gets all documents (excluding deleted)
    /// </summary>
    public IReadOnlyList<Document> GetAllDocuments()
    {
        return _documents.Where(d => !d.Deleted).ToList().AsReadOnly();
    }
    
    /// <summary>
    /// Total number of documents (excluding deleted)
    /// </summary>
    public int Count => _documents.Count(d => !d.Deleted);
    
    /// <summary>
    /// Clears all documents
    /// </summary>
    public void Clear()
    {
        _documents.Clear();
        _documentKeyToIds.Clear();
    }
}

