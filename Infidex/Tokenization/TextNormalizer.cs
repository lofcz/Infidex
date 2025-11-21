namespace Infidex.Tokenization;

/// <summary>
/// Handles text normalization including character and string replacements.
/// </summary>
public class TextNormalizer
{
    private readonly Dictionary<string, string> _stringReplacements;
    private readonly Dictionary<char, char> _charReplacements;
    
    /// <summary>
    /// When true, replacements are only applied during indexing (one-way mode)
    /// </summary>
    public bool OneWayMode { get; }
    
    public TextNormalizer(
        Dictionary<string, string>? stringReplacements = null,
        Dictionary<char, char>? charReplacements = null,
        bool oneWayMode = false)
    {
        _stringReplacements = stringReplacements ?? new Dictionary<string, string>();
        _charReplacements = charReplacements ?? new Dictionary<char, char>();
        OneWayMode = oneWayMode;
    }
    
    /// <summary>
    /// Applies string replacements to the text
    /// </summary>
    public string ReplaceStrings(string text)
    {
        foreach (KeyValuePair<string, string> kvp in _stringReplacements)
        {
            text = text.Replace(kvp.Key, kvp.Value);
        }
        return text;
    }
    
    /// <summary>
    /// Applies character replacements to the text
    /// </summary>
    public string ReplaceChars(string text)
    {
        if (_charReplacements.Count == 0)
            return text;
        
        char[] chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (_charReplacements.TryGetValue(chars[i], out char replacement))
            {
                chars[i] = replacement;
            }
        }
        return new string(chars);
    }
    
    /// <summary>
    /// Applies all normalization (string then char replacements)
    /// </summary>
    public string Normalize(string text)
    {
        text = ReplaceStrings(text);
        text = ReplaceChars(text);
        return text;
    }
    
    /// <summary>
    /// Creates a default normalizer with common replacements
    /// </summary>
    public static TextNormalizer CreateDefault()
    {
        Dictionary<char, char> charReplacements = new Dictionary<char, char>
        {
            { 'Æ', 'E' }, { 'æ', 'e' },
            { 'Ø', 'O' }, { 'ø', 'o' },
            { 'Å', 'A' }, { 'å', 'a' },
            { 'Ä', 'A' }, { 'ä', 'a' },
            { 'Ö', 'O' }, { 'ö', 'o' },
            { 'Ü', 'U' }, { 'ü', 'u' },
            { 'ß', 's' }
        };
        
        Dictionary<string, string> stringReplacements = new Dictionary<string, string>
        {
            { "  ", " " }, // Double space to single
            { "\t", " " }, // Tab to space
            { "\n", " " }, // Newline to space
            { "\r", " " }  // Carriage return to space
        };
        
        return new TextNormalizer(stringReplacements, charReplacements, oneWayMode: true);
    }
}


