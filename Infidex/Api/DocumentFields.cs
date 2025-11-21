using System.Text;

namespace Infidex.Api;

/// <summary>
/// Manages a collection of fields for a document.
/// Provides methods to retrieve indexable fields, concatenate text, and manage field properties.
/// </summary>
public class DocumentFields
{
    private readonly Dictionary<string, Field> _fields;
    
    /// <summary>
    /// Name of the field that serves as the document key
    /// </summary>
    public string NameOfDocumentKeyField { get; set; } = string.Empty;
    
    public DocumentFields()
    {
        _fields = new Dictionary<string, Field>();
    }
    
    /// <summary>
    /// Adds a field to the collection
    /// </summary>
    public void AddField(Field field)
    {
        if (field == null || string.IsNullOrEmpty(field.Name))
            return;
        
        _fields[field.Name] = field;
    }
    
    /// <summary>
    /// Adds a field with basic properties
    /// </summary>
    public void AddField(string name, object? value, Weight weight = Weight.Med, bool indexable = true)
    {
        var field = new Field(name, value, weight)
        {
            Indexable = indexable
        };
        AddField(field);
    }
    
    /// <summary>
    /// Gets a field by name
    /// </summary>
    public Field? GetField(string name)
    {
        return _fields.TryGetValue(name, out var field) ? field : null;
    }
    
    /// <summary>
    /// Gets all fields in the collection
    /// </summary>
    public List<Field> GetFieldList()
    {
        return _fields.Values.ToList();
    }
    
    /// <summary>
    /// Gets the internal field dictionary
    /// </summary>
    internal Dictionary<string, Field> GetFieldDictionary()
    {
        return _fields;
    }
    
    /// <summary>
    /// Gets all indexable fields ordered by weight (highest first)
    /// </summary>
    public List<Field> GetSearchAbleFieldList()
    {
        return _fields.Values
            .Where(f => f.Indexable)
            .OrderBy(f => f.Weight)  // High=0, Med=1, Low=2, so ordering gives High first
            .ToList();
    }
    
    /// <summary>
    /// Gets all filterable fields
    /// </summary>
    public List<Field> GetFilterableFieldList()
    {
        return _fields.Values.Where(f => f.Filterable).ToList();
    }
    
    /// <summary>
    /// Gets all facetable fields
    /// </summary>
    public List<Field> GetFacetableFieldList()
    {
        return _fields.Values.Where(f => f.Facetable).ToList();
    }
    
    /// <summary>
    /// Gets all fields that use word-level indexing
    /// </summary>
    public List<Field> GetExactWordMatchFields()
    {
        return _fields.Values.Where(f => f.WordIndexing).ToList();
    }
    
    /// <summary>
    /// Gets all fields that are actively used (indexed, filterable, sortable, etc.)
    /// </summary>
    internal List<Field> GetUsedFields()
    {
        return _fields.Values
            .Where(f => f.Indexable || f.Facetable || f.Filterable || 
                       f.Sortable || f.PreloadFilters || f.WordIndexing || 
                       f.Name == NameOfDocumentKeyField)
            .ToList();
    }
    
    /// <summary>
    /// Concatenates all searchable fields with a delimiter and returns field boundary markers.
    /// This is the core method for multi-field indexing.
    /// </summary>
    /// <param name="delimiter">Character to separate fields (typically '§')</param>
    /// <param name="concatenatedText">Output: the concatenated text of all indexable fields</param>
    /// <returns>Array of (position, weight) tuples marking where each field starts</returns>
    public (ushort Position, byte Weight)[] GetSearchableTexts(char delimiter, out string concatenatedText)
    {
        var fieldBoundaries = new List<(ushort Position, byte Weight)>();
        var builder = new StringBuilder(100);
        
        List<Field> searchableFields = GetSearchAbleFieldList();
        
        for (int i = 0; i < searchableFields.Count; i++)
        {
            var field = searchableFields[i];
            
            if (field.IsArray && field.Value is List<object> arrayValues)
            {
                // Handle array fields - each element gets its own boundary marker
                foreach (var item in arrayValues)
                {
                    ushort position = (ushort)builder.Length;
                    byte weight = (byte)field.Weight;
                    fieldBoundaries.Add((position, weight));
                    
                    builder.Append(item?.ToString() ?? string.Empty);
                    builder.Append(delimiter);
                }
            }
            else
            {
                // Handle scalar fields
                ushort position = (ushort)builder.Length;
                byte weight = (byte)field.Weight;
                fieldBoundaries.Add((position, weight));
                
                builder.Append(field.Value?.ToString() ?? string.Empty);
                
                if (i < searchableFields.Count - 1)
                {
                    builder.Append(delimiter);
                }
            }
        }
        
        concatenatedText = builder.ToString();
        
        // Sort by position to ensure boundaries are in order
        fieldBoundaries.Sort((a, b) => a.Position.CompareTo(b.Position));
        
        return fieldBoundaries.ToArray();
    }
    
    /// <summary>
    /// Checks if there is a valid document key field
    /// </summary>
    public bool HasKey()
    {
        if (string.IsNullOrEmpty(NameOfDocumentKeyField))
            return false;
        
        var keyField = GetField(NameOfDocumentKeyField);
        if (keyField == null)
            return false;
        
        return keyField.Type == System.Text.Json.JsonValueKind.Number || 
               keyField.Type == System.Text.Json.JsonValueKind.String;
    }
    
    /// <summary>
    /// Clears all fields
    /// </summary>
    public void Clear()
    {
        _fields.Clear();
    }
}

