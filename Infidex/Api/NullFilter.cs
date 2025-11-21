namespace Infidex.Api;

/// <summary>
/// Filter that checks if a field is null or not null.
/// </summary>
public class NullFilter : Filter
{
    public bool IsNull { get; set; }
    
    public NullFilter(string fieldName, bool isNull = true) : base(fieldName)
    {
        IsNull = isNull;
    }
    
    public override bool Matches(object? fieldValue)
    {
        bool valueIsNull = fieldValue == null || 
                          (fieldValue is string str && string.IsNullOrEmpty(str));
        
        return IsNull ? valueIsNull : !valueIsNull;
    }
    
    public override string ToString() => IsNull ? $"{FieldName} IS NULL" : $"{FieldName} IS NOT NULL";
}

