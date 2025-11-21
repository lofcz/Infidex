using System.Text.RegularExpressions;

namespace Infidex.Api;

/// <summary>
/// Filter that uses regular expressions for pattern matching.
/// </summary>
public class RegexFilter : Filter
{
    public string Pattern { get; set; }
    public RegexOptions Options { get; set; }
    private readonly Regex _regex;
    
    public RegexFilter(string fieldName, string pattern, bool caseInsensitive = true) 
        : base(fieldName)
    {
        Pattern = pattern;
        Options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
        
        try
        {
            _regex = new Regex(pattern, Options);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regex pattern: {pattern}", ex);
        }
    }
    
    public override bool Matches(object? fieldValue)
    {
        if (fieldValue == null)
            return false;
        
        string text = fieldValue.ToString() ?? string.Empty;
        return _regex.IsMatch(text);
    }
    
    public override string ToString() => $"{FieldName} MATCHES '{Pattern}'";
}

