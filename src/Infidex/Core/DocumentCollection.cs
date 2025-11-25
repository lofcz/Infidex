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
                if (doc is { Deleted: false })
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
    /// Physically removes all documents marked as <see cref="Document.Deleted"/> and
    /// compacts internal IDs and key lookups.
    /// </summary>
    /// <remarks>
    /// - Builds a new compacted list of documents containing only non-deleted entries.
    /// - Reassigns <see cref="Document.Id"/> to be dense and zero-based.
    /// - Rebuilds the <c>DocumentKey</c> → internal ID lookup, preserving alias support.
    /// </remarks>
    public void RemoveDeletedDocuments()
    {
        // Find the first deleted document index.
        int firstDeletedIndex = -1;
        for (int i = 0; i < _documents.Count; i++)
        {
            if (_documents[i].Deleted)
            {
                firstDeletedIndex = i;
                break;
            }
        }

        if (firstDeletedIndex == -1)
        {
            return;
        }

        List<Document> compacted = new List<Document>(_documents.Count);
        Dictionary<long, List<int>> newKeyToIds = new Dictionary<long, List<int>>();

        // 1. Keep all documents before the first deletion (we must repopulate the new dictionary).
        for (int i = 0; i < firstDeletedIndex; i++)
        {
            Document doc = _documents[i];
            compacted.Add(doc);
            
            if (!newKeyToIds.TryGetValue(doc.DocumentKey, out List<int>? ids))
            {
                ids = [];
                newKeyToIds[doc.DocumentKey] = ids;
            }
            ids.Add(doc.Id);
        }

        // 2. Process the rest: skip deleted, renumber surviving
        for (int i = firstDeletedIndex; i < _documents.Count; i++)
        {
            Document doc = _documents[i];
            if (doc.Deleted)
            {
                continue;
            }

            // Assign new dense internal ID
            doc.Id = compacted.Count;
            compacted.Add(doc);

            if (!newKeyToIds.TryGetValue(doc.DocumentKey, out List<int>? ids))
            {
                ids = [];
                newKeyToIds[doc.DocumentKey] = ids;
            }
            ids.Add(doc.Id);
        }

        // 3. Swap collections
        _documents.Clear();
        _documents.AddRange(compacted);

        _documentKeyToIds.Clear();
        foreach (KeyValuePair<long, List<int>> kvp in newKeyToIds)
        {
            _documentKeyToIds[kvp.Key] = kvp.Value;
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

