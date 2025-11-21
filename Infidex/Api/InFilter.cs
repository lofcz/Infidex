namespace Infidex.Api;

/// <summary>
/// Filter that matches if field value is in a list of values.
/// Equivalent to SQL IN operator.
/// </summary>
public class InFilter : Filter
{
    public object[] Values { get; set; }
    
    public InFilter(string fieldName, params object[] values) : base(fieldName)
    {
        Values = values;
    }
    
    public override bool Matches(object? fieldValue)
    {
        if (fieldValue == null)
            return false;
        
        foreach (var value in Values)
        {
            if (fieldValue.Equals(value))
                return true;
        }
        
        return false;
    }
    
    public override string ToString() => $"{FieldName} IN ({string.Join(", ", Values)})";
}

