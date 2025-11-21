namespace Infidex.Api;

/// <summary>
/// Filter for range queries (e.g., price between 10 and 100)
/// </summary>
public class RangeFilter : Filter
{
    public IComparable? MinValue { get; set; }
    public IComparable? MaxValue { get; set; }
    public bool IncludeMin { get; set; }
    public bool IncludeMax { get; set; }
    
    public RangeFilter(string fieldName, IComparable? minValue = null, IComparable? maxValue = null, 
                       bool includeMin = true, bool includeMax = true) : base(fieldName)
    {
        MinValue = minValue;
        MaxValue = maxValue;
        IncludeMin = includeMin;
        IncludeMax = includeMax;
    }
    
    public override bool Matches(object? fieldValue)
    {
        if (fieldValue == null)
            return false;
        
        if (fieldValue is not IComparable comparable)
            return false;
        
        // Check minimum
        if (MinValue != null)
        {
            int minCompare = comparable.CompareTo(MinValue);
            if (IncludeMin ? minCompare < 0 : minCompare <= 0)
                return false;
        }
        
        // Check maximum
        if (MaxValue != null)
        {
            int maxCompare = comparable.CompareTo(MaxValue);
            if (IncludeMax ? maxCompare > 0 : maxCompare >= 0)
                return false;
        }
        
        return true;
    }
    
    public override string ToString() => $"{FieldName} in [{MinValue}, {MaxValue}]";
}


