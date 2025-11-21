using Infidex.Api;
using Infidex.Core;
using Infidex.Indexing;

namespace Infidex.Json;

/// <summary>
/// Represents a searchable field with its own search engine instance.
/// Enables multi-field search with per-field weighting.
/// </summary>
public class IndexAbleField
{
    internal VectorModel? _vectorModel;
    
    public string FieldName { get; set; }
    public float Weight { get; set; }
    
    public IndexAbleField(string fieldName, float weight = 1.0f)
    {
        FieldName = fieldName;
        Weight = weight;
    }
    
    /// <summary>
    /// Indexes documents for this field
    /// </summary>
    public void IndexDocuments(IEnumerable<Document> documents, VectorModel vectorModel)
    {
        _vectorModel = vectorModel;
        
        foreach (Document doc in documents)
        {
            if (!string.IsNullOrEmpty(doc.IndexedText))
            {
                vectorModel.IndexDocument(doc);
            }
        }
    }
    
    /// <summary>
    /// Searches this field
    /// </summary>
    public ScoreArray Search(Query query)
    {
        if (_vectorModel == null)
            return new ScoreArray();
        
        return _vectorModel.Search(query.Text);
    }
}

