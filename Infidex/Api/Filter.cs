namespace Infidex.Api;

/// <summary>
/// Base class for document filters
/// </summary>
public abstract class Filter
{
    public string FieldName { get; set; }
    
    protected Filter(string fieldName)
    {
        FieldName = fieldName;
    }
    
    public abstract bool Matches(object? fieldValue);
}


