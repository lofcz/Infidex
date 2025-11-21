namespace Infidex.Api;

/// <summary>
/// Represents a literal constant value in a filter expression.
/// Used primarily as branches in ternary expressions.
/// </summary>
public class LiteralFilter : Filter
{
    /// <summary>
    /// The constant value this filter represents
    /// </summary>
    public object? Value { get; set; }
    
    /// <summary>
    /// Creates a literal filter with a constant value
    /// </summary>
    /// <param name="value">The constant value</param>
    public LiteralFilter(object? value) : base("literal")
    {
        Value = value;
    }
    
    /// <summary>
    /// Always returns the literal value (for use in evaluations)
    /// When used in boolean context, converts value to boolean
    /// </summary>
    public override bool Matches(object? fieldValue)
    {
        // Convert literal to boolean for filter contexts
        if (Value is bool boolValue)
            return boolValue;
        
        if (Value is string strValue)
            return !string.IsNullOrEmpty(strValue);
        
        if (Value is int || Value is long || Value is double || Value is decimal)
            return Convert.ToDouble(Value) != 0;
        
        return Value != null;
    }
    
    /// <summary>
    /// Returns a string representation of the literal
    /// </summary>
    public override string ToString()
    {
        if (Value is string)
            return $"'{Value}'";
        return Value?.ToString() ?? "null";
    }
}

