namespace Infidex.Api;

/// <summary>
/// Filter based on a custom predicate function
/// </summary>
public class DerivedFilter : Filter
{
    public Func<object?, bool> Predicate { get; set; }
    
    public DerivedFilter(string fieldName, Func<object?, bool> predicate) : base(fieldName)
    {
        Predicate = predicate;
    }
    
    public override bool Matches(object? fieldValue)
    {
        return Predicate(fieldValue);
    }
    
    public override string ToString() => $"{FieldName} (custom)";
}


