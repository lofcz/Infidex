namespace Infidex.Tokenization;

/// <summary>
/// Configuration parameters for the tokenizer.
/// </summary>
public class TokenizerSetup
{
    /// <summary>
    /// Delimiter characters used to split text into words
    /// </summary>
    public char[] Delimiters { get; set; }
    
    /// <summary>
    /// When true, enables high-resolution mode with joined tokens
    /// </summary>
    public bool HighResolutionMode { get; set; }
    
    /// <summary>
    /// When true, removes duplicate tokens from the result
    /// </summary>
    public bool RemoveDuplicateTokens { get; set; }
    
    public TokenizerSetup(
        char[]? delimiters = null,
        bool highResolutionMode = false,
        bool removeDuplicateTokens = true)
    {
        Delimiters = delimiters ?? GetDefaultDelimiters();
        HighResolutionMode = highResolutionMode;
        RemoveDuplicateTokens = removeDuplicateTokens;
    }
    
    /// <summary>
    /// Gets the default set of delimiters
    /// </summary>
    public static char[] GetDefaultDelimiters()
    {
        return
        [
            ' ', '-', '/', '.', ',', ':', ';', '\'', '`', '–', '—',
            '*', '&', '\\', '_', '(', ')', '{', '}', '[', ']', '\t'
        ];
    }
    
    /// <summary>
    /// Creates a default tokenizer setup
    /// </summary>
    public static TokenizerSetup CreateDefault()
    {
        return new TokenizerSetup(
            GetDefaultDelimiters(),
            highResolutionMode: false,
            removeDuplicateTokens: true);
    }
}


