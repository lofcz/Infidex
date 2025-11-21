using System.Text.Json;

namespace Infidex.Api;

/// <summary>
/// Represents a single field within a document.
/// Fields can have different weights, types, and indexing properties.
/// </summary>
public class Field
{
    /// <summary>
    /// Field name (e.g., "title", "description", "category")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Field value (can be string, number, bool, etc.)
    /// </summary>
    public object? Value { get; set; }
    
    /// <summary>
    /// Field type (String, Number, True, False, Null, etc.)
    /// </summary>
    public JsonValueKind Type { get; set; } = JsonValueKind.String;
    
    /// <summary>
    /// Whether this is an array field
    /// </summary>
    public bool IsArray { get; set; }
    
    /// <summary>
    /// Field importance weight for search relevance
    /// </summary>
    public Weight Weight { get; set; } = Weight.Med;
    
    /// <summary>
    /// Custom weight multiplier (overrides Weight enum if >= 0)
    /// </summary>
    public float WeightAsFloat { get; set; } = -1f;
    
    /// <summary>
    /// Whether this field should be included in the search index
    /// </summary>
    public bool Indexable { get; set; } = true;
    
    /// <summary>
    /// Whether this field can be used for filtering
    /// </summary>
    public bool Filterable { get; set; }
    
    /// <summary>
    /// Whether this field can be used for sorting
    /// </summary>
    public bool Sortable { get; set; }
    
    /// <summary>
    /// Whether this field can be used for faceting
    /// </summary>
    public bool Facetable { get; set; }
    
    /// <summary>
    /// Whether to use word-level matching for this field
    /// </summary>
    public bool WordIndexing { get; set; }
    
    /// <summary>
    /// Whether this field is optional (can be null/empty)
    /// </summary>
    public bool Optional { get; set; }
    
    /// <summary>
    /// Configuration number to use for this field
    /// </summary>
    public int ConfigNumber { get; set; } = 400;
    
    /// <summary>
    /// Whether to preload filter values for this field
    /// </summary>
    public bool PreloadFilters { get; set; }
    
    /// <summary>
    /// Start position of value in source text (for substring extraction)
    /// </summary>
    internal long? ValueStart { get; set; }
    
    /// <summary>
    /// Length of value in source text (for substring extraction)
    /// </summary>
    internal long? ValueLength { get; set; }
    
    /// <summary>
    /// Number of unique values seen for this field
    /// </summary>
    internal int UniqueValues { get; set; }
    
    /// <summary>
    /// Set of unique values for this field (used for faceting/filtering)
    /// </summary>
    internal HashSet<string>? UniqueSet { get; set; }
    
    public Field()
    {
    }
    
    public Field(string name, object? value, Weight weight = Weight.Med)
    {
        Name = name;
        Value = value;
        Weight = weight;
        Type = DetermineType(value);
    }
    
    /// <summary>
    /// Gets the effective weight multiplier for this field
    /// </summary>
    internal float GetWeightMultiplier(float[] configuredWeights)
    {
        if (WeightAsFloat >= 0f)
            return WeightAsFloat;
        
        return configuredWeights[(int)Weight];
    }
    
    private static JsonValueKind DetermineType(object? value)
    {
        return value switch
        {
            null => JsonValueKind.Null,
            string => JsonValueKind.String,
            bool b => b ? JsonValueKind.True : JsonValueKind.False,
            int or long or double or float => JsonValueKind.Number,
            _ => JsonValueKind.Object
        };
    }
}

