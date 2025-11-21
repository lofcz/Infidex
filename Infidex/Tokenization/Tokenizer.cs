using Infidex.Core;

namespace Infidex.Tokenization;

/// <summary>
/// Tokenizes text into shingles (character n-grams) and words.
/// Implements multi-size n-gram generation with padding.
/// </summary>
public class Tokenizer
{
    // Special padding characters (using Unicode private use area)
    private const char START_PAD_CHAR = '\uFFFF';
    private const char STOP_PAD_CHAR = '\uFFFE';
    
    /// <summary>
    /// N-gram sizes to extract (e.g., [2, 3] for 2-grams and 3-grams)
    /// </summary>
    public int[] IndexSizes { get; set; }
    
    /// <summary>
    /// Number of padding characters at the start
    /// </summary>
    public int StartPadSize { get; set; }
    
    /// <summary>
    /// Number of padding characters at the end
    /// </summary>
    public int StopPadSize { get; set; }
    
    /// <summary>
    /// Text normalizer for character/string replacements
    /// </summary>
    public TextNormalizer? TextNormalizer { get; set; }
    
    /// <summary>
    /// Tokenizer setup configuration
    /// </summary>
    public TokenizerSetup? TokenizerSetup { get; set; }
    
    private string _startPadding;
    private string _stopPadding;
    
    public Tokenizer(
        int[] indexSizes,
        int startPadSize = 2,
        int stopPadSize = 0,
        TextNormalizer? textNormalizer = null,
        TokenizerSetup? tokenizerSetup = null)
    {
        IndexSizes = indexSizes;
        StartPadSize = startPadSize;
        StopPadSize = stopPadSize;
        TextNormalizer = textNormalizer;
        TokenizerSetup = tokenizerSetup;
        
        _startPadding = new string(START_PAD_CHAR, startPadSize);
        _stopPadding = new string(STOP_PAD_CHAR, stopPadSize);
    }
    
    /// <summary>
    /// Tokenizes text for indexing (builds complete shingle list)
    /// </summary>
    /// <param name="text">The text to tokenize</param>
    /// <param name="isSegmentContinuation">True if this is a continuation segment (segment number > 0)</param>
    public List<Shingle> TokenizeForIndexing(string text, bool isSegmentContinuation = false)
    {
        if (TextNormalizer != null)
        {
            text = TextNormalizer.Normalize(text);
        }
        
        // Continuation segments skip start padding (they continue from previous segment)
        int actualStartPad = isSegmentContinuation ? 0 : StartPadSize;
        string startPad = isSegmentContinuation ? "" : _startPadding;
        
        // Add padding
        string paddedText = startPad + text + _stopPadding;
        
        return GenerateShingles(paddedText);
    }
    
    /// <summary>
    /// Tokenizes text for searching (includes both shingles and words)
    /// </summary>
    public Shingle[] TokenizeForSearch(string text, out Dictionary<string, Shingle> shingleDictionary, bool getJoined = false)
    {
        string[] words = [];
        
        // Extract words if tokenizer setup is available
        if (TokenizerSetup != null && !getJoined)
        {
            words = text.Split(TokenizerSetup.Delimiters, StringSplitOptions.RemoveEmptyEntries);
        }
        
        // Apply normalization
        if (TextNormalizer != null)
        {
            text = TextNormalizer.ReplaceStrings(text);
        }
        
        // Handle high-resolution mode
        if (TokenizerSetup != null && getJoined && TokenizerSetup.HighResolutionMode)
        {
            text = StripDelimiters(text);
            text = _startPadding + text + _stopPadding;
        }
        
        // Generate shingles
        List<Shingle> shingles = GenerateShingles(text);
        
        // Add word tokens if available
        if (words.Length > 0)
        {
            if (TokenizerSetup?.RemoveDuplicateTokens == true)
            {
                words = words.Distinct().ToArray();
            }
            
            foreach (string word in words)
            {
                if (word.Length >= IndexSizes[0])
                {
                    shingles.Add(new Shingle(word, 1, 0));
                }
            }
        }
        
        // HIGH RESOLUTION MODE: Recursively tokenize with joined terms
        if (TokenizerSetup != null && TokenizerSetup.HighResolutionMode && !getJoined)
        {
            Shingle[] secondPass = TokenizeForSearch(text, out Dictionary<string, Shingle> dict2, true);
            foreach (Shingle shingle in secondPass)
            {
                shingles.Add(shingle);
            }
        }
        
        // Build dictionary and count occurrences
        return ConsolidateShingles(shingles, out shingleDictionary);
    }
    
    /// <summary>
    /// Generates shingles of various sizes from the text
    /// </summary>
    private List<Shingle> GenerateShingles(string text)
    {
        List<Shingle> shingles = [];
        
        // Determine max n-gram size to extract
        int maxSize = IndexSizes[^1]; // Last element
        if (text.Length <= IndexSizes[0])
        {
            maxSize = IndexSizes[0];
        }
        
        // Generate n-grams of each configured size
        foreach (int size in IndexSizes)
        {
            ExtractNGrams(text, size, shingles);
            
            if (size == maxSize)
                break;
        }
        
        return shingles;
    }
    
    /// <summary>
    /// Extracts all n-grams of a specific size from the text
    /// </summary>
    private void ExtractNGrams(string text, int n, List<Shingle> shingles)
    {
        if (text.Length < n)
            return;
        
        for (int i = 0; i <= text.Length - n; i++)
        {
            string ngram = text.Substring(i, n);
            
            // Skip if all characters are padding
            if (IsAllPadding(ngram))
                continue;
            
            shingles.Add(new Shingle(ngram, 1, i));
        }
    }
    
    /// <summary>
    /// Consolidates shingles by counting occurrences
    /// </summary>
    private Shingle[] ConsolidateShingles(List<Shingle> shingles, out Dictionary<string, Shingle> dictionary)
    {
        dictionary = new Dictionary<string, Shingle>();
        
        foreach (Shingle shingle in shingles)
        {
            if (dictionary.TryGetValue(shingle.Text, out Shingle? existing))
            {
                existing.Occurrences++;
            }
            else
            {
                dictionary[shingle.Text] = shingle;
            }
        }
        
        return dictionary.Values.ToArray();
    }
    
    /// <summary>
    /// Strips delimiter characters from text
    /// </summary>
    private string StripDelimiters(string text)
    {
        if (TokenizerSetup == null)
            return text;
        
        char[] chars = text.ToCharArray();
        List<char> result = new List<char>(chars.Length);
        
        foreach (char c in chars)
        {
            if (!TokenizerSetup.Delimiters.Contains(c))
            {
                result.Add(c);
            }
        }
        
        return new string(result.ToArray());
    }
    
    /// <summary>
    /// Checks if a string consists only of padding characters
    /// </summary>
    private bool IsAllPadding(string text)
    {
        foreach (char c in text)
        {
            if (c != START_PAD_CHAR && c != STOP_PAD_CHAR)
                return false;
        }
        return true;
    }
    
    /// <summary>
    /// Gets word tokens for coverage calculations
    /// </summary>
    public HashSet<string> GetWordTokensForCoverage(string text, int minWordSize)
    {
        if (TokenizerSetup == null)
            return [];
        
        string[] words = text.Split(TokenizerSetup.Delimiters, StringSplitOptions.RemoveEmptyEntries);
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (string word in words)
        {
            if (word.Length >= minWordSize)
            {
                result.Add(word.ToLowerInvariant());
            }
        }
        
        return result;
    }
}

