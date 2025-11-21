namespace Infidex.Api;

/// <summary>
/// Filter for string operations: CONTAINS, STARTS WITH, ENDS WITH, LIKE.
/// </summary>
public class StringFilter : Filter
{
    public enum StringOperation
    {
        Contains,
        StartsWith,
        EndsWith,
        Like
    }
    
    public StringOperation Operation { get; set; }
    public string Pattern { get; set; }
    public bool CaseInsensitive { get; set; }
    
    public StringFilter(string fieldName, StringOperation operation, string pattern, bool caseInsensitive = true) 
        : base(fieldName)
    {
        Operation = operation;
        Pattern = pattern;
        CaseInsensitive = caseInsensitive;
    }
    
    public override bool Matches(object? fieldValue)
    {
        if (fieldValue == null)
            return false;
        
        string text = fieldValue.ToString() ?? string.Empty;
        string pattern = Pattern;
        
        if (CaseInsensitive)
        {
            text = text.ToLowerInvariant();
            pattern = pattern.ToLowerInvariant();
        }
        
        return Operation switch
        {
            StringOperation.Contains => text.Contains(pattern),
            StringOperation.StartsWith => text.StartsWith(pattern),
            StringOperation.EndsWith => text.EndsWith(pattern),
            StringOperation.Like => MatchesLikePattern(text, pattern),
            _ => false
        };
    }
    
    private bool MatchesLikePattern(string text, string pattern)
    {
        // Convert SQL LIKE pattern to simple matching
        // % = any characters, _ = single character
        string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        
        return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern);
    }
    
    public override string ToString() => Operation switch
    {
        StringOperation.Contains => $"{FieldName} CONTAINS '{Pattern}'",
        StringOperation.StartsWith => $"{FieldName} STARTS WITH '{Pattern}'",
        StringOperation.EndsWith => $"{FieldName} ENDS WITH '{Pattern}'",
        StringOperation.Like => $"{FieldName} LIKE '{Pattern}'",
        _ => base.ToString()
    };
}

