namespace Infidex.Api;

/// <summary>
/// Filter for exact value matching
/// </summary>
public class ValueFilter : Filter
{
    public object Value { get; set; }
    
    public ValueFilter(string fieldName, object value) : base(fieldName)
    {
        Value = value;
    }
    
    public override bool Matches(object? fieldValue)
    {
        if (fieldValue == null && Value == null)
            return true;
        
        if (fieldValue == null || Value == null)
            return false;
        
        return fieldValue.Equals(Value);
    }
    
    public override string ToString() => $"{FieldName} == {Value}";
}


