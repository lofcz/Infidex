namespace Infidex.Api;

/// <summary>
/// Represents a ternary conditional filter (condition ? true_value : false_value).
/// Evaluates a condition and returns one of two possible filter results.
/// Supports chaining for multi-way conditionals: a ? b : c ? d : e
/// </summary>
public class TernaryFilter : Filter
{
    /// <summary>
    /// The condition to evaluate (must result in a boolean)
    /// </summary>
    public Filter Condition { get; set; }
    
    /// <summary>
    /// The filter to evaluate if the condition is true
    /// </summary>
    public Filter TrueValue { get; set; }
    
    /// <summary>
    /// The filter to evaluate if the condition is false
    /// </summary>
    public Filter FalseValue { get; set; }
    
    /// <summary>
    /// Creates a ternary conditional filter
    /// </summary>
    /// <param name="condition">Boolean condition to evaluate</param>
    /// <param name="trueValue">Filter result if condition is true</param>
    /// <param name="falseValue">Filter result if condition is false</param>
    public TernaryFilter(Filter condition, Filter trueValue, Filter falseValue) 
        : base("ternary")
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        TrueValue = trueValue ?? throw new ArgumentNullException(nameof(trueValue));
        FalseValue = falseValue ?? throw new ArgumentNullException(nameof(falseValue));
    }
    
    /// <summary>
    /// Evaluates the ternary conditional against a field value
    /// </summary>
    public override bool Matches(object? fieldValue)
    {
        // Evaluate the condition
        bool conditionResult = Condition.Matches(fieldValue);
        
        // Return the appropriate branch based on condition
        return conditionResult ? TrueValue.Matches(fieldValue) : FalseValue.Matches(fieldValue);
    }
    
    /// <summary>
    /// Returns a string representation of the ternary filter
    /// </summary>
    public override string ToString()
    {
        return $"({Condition} ? {TrueValue} : {FalseValue})";
    }
}

