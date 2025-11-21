using Infidex.Api;

namespace Infidex.Core;

/// <summary>
/// Builds facet aggregations from search results.
/// Facets show the distribution of field values across the result set.
/// </summary>
public static class FacetBuilder
{
    /// <summary>
    /// Builds facets for all facetable fields in the result set
    /// </summary>
    /// <param name="results">Search results to aggregate</param>
    /// <param name="documents">Document collection to extract field values from</param>
    /// <param name="fieldSchema">Field schema defining which fields are facetable</param>
    /// <param name="maxFacetsPerField">Maximum number of facet values to return per field</param>
    /// <returns>Dictionary of field name to (value, count) pairs</returns>
    public static Dictionary<string, KeyValuePair<string, int>[]> BuildFacets(
        ScoreEntry[] results,
        DocumentCollection documents,
        DocumentFields? fieldSchema,
        int maxFacetsPerField = 100)
    {
        var facets = new Dictionary<string, KeyValuePair<string, int>[]>();
        
        if (fieldSchema == null || results.Length == 0)
            return facets;
        
        // Get all facetable fields
        List<Field> facetableFields = fieldSchema.GetFacetableFieldList();
        
        if (facetableFields.Count == 0)
            return facets;
        
        // Build facets for each facetable field
        foreach (var field in facetableFields)
        {
            var valueCounts = BuildFacetForField(field.Name, results, documents);
            
            if (valueCounts.Count > 0)
            {
                // Sort by count descending, then by value ascending
                var sortedFacets = valueCounts
                    .OrderByDescending(kvp => kvp.Value)
                    .ThenBy(kvp => kvp.Key)
                    .Take(maxFacetsPerField)
                    .Select(kvp => new KeyValuePair<string, int>(kvp.Key, kvp.Value))
                    .ToArray();
                
                facets[field.Name] = sortedFacets;
            }
        }
        
        return facets;
    }
    
    /// <summary>
    /// Builds facet counts for a single field
    /// </summary>
    private static Dictionary<string, int> BuildFacetForField(
        string fieldName,
        ScoreEntry[] results,
        DocumentCollection documents)
    {
        var valueCounts = new Dictionary<string, int>();
        
        // Count occurrences of each value in the result set
        foreach (var result in results)
        {
            // Get the document (DocumentId in ScoreEntry is the public DocumentKey)
            var doc = documents.GetDocumentByPublicKey(result.DocumentId);
            if (doc == null)
                continue;
            
            // Get the field value
            var field = doc.Fields.GetField(fieldName);
            if (field == null || field.Value == null)
                continue;
            
            // Handle array fields
            if (field.IsArray && field.Value is List<object> arrayValues)
            {
                foreach (var item in arrayValues)
                {
                    string value = item?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(value))
                    {
                        valueCounts[value] = valueCounts.GetValueOrDefault(value) + 1;
                    }
                }
            }
            else
            {
                // Handle scalar fields
                string value = field.Value.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(value))
                {
                    valueCounts[value] = valueCounts.GetValueOrDefault(value) + 1;
                }
            }
        }
        
        return valueCounts;
    }
    
    /// <summary>
    /// Builds facets from all documents (used when query is empty)
    /// </summary>
    public static Dictionary<string, KeyValuePair<string, int>[]> BuildFacetsFromAllDocuments(
        DocumentCollection documents,
        DocumentFields? fieldSchema,
        int maxFacetsPerField = 100)
    {
        var facets = new Dictionary<string, KeyValuePair<string, int>[]>();
        
        if (fieldSchema == null || documents.Count == 0)
            return facets;
        
        // Get all facetable fields
        List<Field> facetableFields = fieldSchema.GetFacetableFieldList();
        
        if (facetableFields.Count == 0)
            return facets;
        
        // Build facets for each facetable field from all documents
        foreach (var field in facetableFields)
        {
            var valueCounts = new Dictionary<string, int>();
            
            // Iterate through all documents
            for (int i = 0; i < documents.Count; i++)
            {
                var doc = documents.GetDocument(i);
                if (doc == null || doc.Deleted)
                    continue;
                
                var docField = doc.Fields.GetField(field.Name);
                if (docField == null || docField.Value == null)
                    continue;
                
                // Handle array fields
                if (docField.IsArray && docField.Value is List<object> arrayValues)
                {
                    foreach (var item in arrayValues)
                    {
                        string value = item?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(value))
                        {
                            valueCounts[value] = valueCounts.GetValueOrDefault(value) + 1;
                        }
                    }
                }
                else
                {
                    // Handle scalar fields
                    string value = docField.Value.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(value))
                    {
                        valueCounts[value] = valueCounts.GetValueOrDefault(value) + 1;
                    }
                }
            }
            
            if (valueCounts.Count > 0)
            {
                // Sort by count descending, then by value ascending
                var sortedFacets = valueCounts
                    .OrderByDescending(kvp => kvp.Value)
                    .ThenBy(kvp => kvp.Key)
                    .Take(maxFacetsPerField)
                    .Select(kvp => new KeyValuePair<string, int>(kvp.Key, kvp.Value))
                    .ToArray();
                
                facets[field.Name] = sortedFacets;
            }
        }
        
        return facets;
    }
}

