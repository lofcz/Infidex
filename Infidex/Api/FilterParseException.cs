namespace Infidex.Api;

/// <summary>
/// Exception thrown when a filter expression cannot be parsed.
/// Includes context about where the error occurred and helpful suggestions.
/// </summary>
public class FilterParseException : ArgumentException
{
    /// <summary>
    /// The position in the expression where the error occurred
    /// </summary>
    public int Position { get; }
    
    /// <summary>
    /// The original filter expression that failed to parse
    /// </summary>
    public string Expression { get; }
    
    /// <summary>
    /// A helpful suggestion for fixing the error
    /// </summary>
    public string? Suggestion { get; }
    
    public FilterParseException(string message, string expression, int position, string? suggestion = null)
        : base(FormatMessage(message, expression, position, suggestion))
    {
        Expression = expression;
        Position = position;
        Suggestion = suggestion;
    }
    
    private static string FormatMessage(string message, string expression, int position, string? suggestion)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(message);
        sb.AppendLine();
        
        // Show the expression with a pointer to the error position
        if (!string.IsNullOrEmpty(expression))
        {
            sb.AppendLine("Expression:");
            sb.AppendLine($"  {expression}");
            
            // Add pointer to error position
            if (position >= 0 && position < expression.Length + 10)
            {
                sb.Append("  ");
                sb.Append(new string(' ', Math.Min(position, expression.Length)));
                sb.AppendLine("^");
            }
        }
        
        // Add helpful suggestion if provided
        if (!string.IsNullOrEmpty(suggestion))
        {
            sb.AppendLine();
            sb.AppendLine($"Suggestion: {suggestion}");
        }
        
        return sb.ToString();
    }
}

