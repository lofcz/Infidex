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
    /// Creates a default normalizer with common replacements.
    /// Includes comprehensive Latin diacritic removal for cross-language search.
    /// </summary>
    public static TextNormalizer CreateDefault()
    {
        // Comprehensive diacritic/accent removal for Latin-based scripts
        // Covers: Czech, Polish, Slovak, Hungarian, Romanian, Turkish, Vietnamese, 
        //         Nordic, German, Spanish, French, Portuguese, Italian, etc.
        Dictionary<char, char> charReplacements = new Dictionary<char, char>
        {
            // Nordic/German (existing)
            { 'Æ', 'E' }, { 'æ', 'e' },
            { 'Ø', 'O' }, { 'ø', 'o' },
            { 'Å', 'A' }, { 'å', 'a' },
            { 'Ä', 'A' }, { 'ä', 'a' },
            { 'Ö', 'O' }, { 'ö', 'o' },
            { 'Ü', 'U' }, { 'ü', 'u' },
            { 'ß', 's' },
            
            // Czech/Slovak háčky (caron)
            { 'Š', 'S' }, { 'š', 's' },
            { 'Č', 'C' }, { 'č', 'c' },
            { 'Ř', 'R' }, { 'ř', 'r' },
            { 'Ž', 'Z' }, { 'ž', 'z' },
            { 'Ň', 'N' }, { 'ň', 'n' },
            { 'Ť', 'T' }, { 'ť', 't' },
            { 'Ď', 'D' }, { 'ď', 'd' },
            { 'Ě', 'E' }, { 'ě', 'e' },
            
            // Czech/Slovak/Polish čárky (acute accents)
            { 'Á', 'A' }, { 'á', 'a' },
            { 'É', 'E' }, { 'é', 'e' },
            { 'Í', 'I' }, { 'í', 'i' },
            { 'Ó', 'O' }, { 'ó', 'o' },
            { 'Ú', 'U' }, { 'ú', 'u' },
            { 'Ý', 'Y' }, { 'ý', 'y' },
            { 'Ů', 'U' }, { 'ů', 'u' },  // Czech kroužek
            
            // Polish specific
            { 'Ą', 'A' }, { 'ą', 'a' },
            { 'Ć', 'C' }, { 'ć', 'c' },
            { 'Ę', 'E' }, { 'ę', 'e' },
            { 'Ł', 'L' }, { 'ł', 'l' },
            { 'Ń', 'N' }, { 'ń', 'n' },
            { 'Ś', 'S' }, { 'ś', 's' },
            { 'Ź', 'Z' }, { 'ź', 'z' },
            { 'Ż', 'Z' }, { 'ż', 'z' },
            
            // Hungarian
            { 'Ő', 'O' }, { 'ő', 'o' },
            { 'Ű', 'U' }, { 'ű', 'u' },
            
            // Romanian
            { 'Ă', 'A' }, { 'ă', 'a' },
            { 'Â', 'A' }, { 'â', 'a' },
            { 'Î', 'I' }, { 'î', 'i' },
            { 'Ș', 'S' }, { 'ș', 's' },
            { 'Ț', 'T' }, { 'ț', 't' },
            
            // Turkish
            { 'Ğ', 'G' }, { 'ğ', 'g' },
            { 'İ', 'I' }, { 'ı', 'i' },
            { 'Ş', 'S' }, { 'ş', 's' },
            
            // French/Spanish/Portuguese
            { 'À', 'A' }, { 'à', 'a' },
            { 'Ç', 'C' }, { 'ç', 'c' },
            { 'È', 'E' }, { 'è', 'e' },
            { 'Ê', 'E' }, { 'ê', 'e' },
            { 'Ë', 'E' }, { 'ë', 'e' },
            { 'Ì', 'I' }, { 'ì', 'i' },
            { 'Ï', 'I' }, { 'ï', 'i' },
            { 'Ñ', 'N' }, { 'ñ', 'n' },
            { 'Ò', 'O' }, { 'ò', 'o' },
            { 'Ô', 'O' }, { 'ô', 'o' },
            { 'Õ', 'O' }, { 'õ', 'o' },
            { 'Ù', 'U' }, { 'ù', 'u' },
            { 'Û', 'U' }, { 'û', 'u' },
            { 'Ÿ', 'Y' }, { 'ÿ', 'y' },
            
            // Icelandic
            { 'Ð', 'D' }, { 'ð', 'd' },
            { 'Þ', 'T' }, { 'þ', 't' },
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


