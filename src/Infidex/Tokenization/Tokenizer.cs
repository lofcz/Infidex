using Infidex.Core;

namespace Infidex.Tokenization;

/// <summary>
/// Tokenizes text into shingles (character n-grams) and words.
/// Implements multi-size n-gram generation with padding.
/// </summary>
public class Tokenizer
{
    // Special padding characters (using Unicode private use area)
    public const char START_PAD_CHAR = '\uFFFF';
    public const char STOP_PAD_CHAR = '\uFFFE';
    
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
    /// Tokenizes text for indexing (builds complete shingle list).
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <param name="isSegmentContinuation">True if this is a continuation segment (segment number &gt; 0).</param>
    public List<Shingle> TokenizeForIndexing(string text, bool isSegmentContinuation = false)
    {
        // Keep the legacy allocation-heavy behavior for compatibility.
        // The high-performance indexing path uses span-based n-gram enumeration
        // via EnumerateNGramsForIndexing instead.
        if (TextNormalizer != null)
        {
            text = TextNormalizer.Normalize(text);
        }
        
        // Continuation segments skip start padding (they continue from previous segment)
        string startPad = isSegmentContinuation ? "" : _startPadding;
        
        // Add padding
        string paddedText = startPad + text + _stopPadding;
        
        List<Shingle> shingles = GenerateShingles(paddedText);

        // index full words to support fuzzy correction and exact word matching
        // this ensures that words longer than max n-gram size are present in the index/FST
        if (TokenizerSetup != null)
        {
            ReadOnlySpan<char> span = text.AsSpan();
            char[] delimiters = TokenizerSetup.Delimiters;
            int baseOffset = isSegmentContinuation ? 0 : StartPadSize;
            
            int i = 0;
            while (i < span.Length)
            {
                // Skip delimiters
                int start = i;
                while (start < span.Length && delimiters.Contains(span[start]))
                {
                    start++;
                }
                
                if (start >= span.Length) break;
                
                // Find end of word
                int end = start;
                while (end < span.Length && !delimiters.Contains(span[end]))
                {
                    end++;
                }
                
                int len = end - start;
                if (len >= IndexSizes[0])
                {
                    string word = new string(span.Slice(start, len));
                    // Exact position in padded text
                    shingles.Add(new Shingle(word, 1, baseOffset + start));
                }
                
                i = end;
            }
        }
        
        return shingles;
    }
    
    /// <summary>
    /// Tokenizes text for indexing using a span-based, allocation-light n-gram
    /// enumerator. This is intended for high-performance bulk indexing and is
    /// used by the unified indexer instead of building Shingle objects.
    /// </summary>
    /// <param name="text">Raw text to tokenize (typically the concatenated searchable fields).</param>
    /// <param name="isSegmentContinuation">True if this is a continuation segment (segment number &gt; 0).</param>
    /// <param name="visitor">
    /// Callback invoked for each n-gram with its compact <see cref="NGramKey"/>
    /// and starting position within the padded text.
    /// </param>
    internal void EnumerateNGramsForIndexing(string text, bool isSegmentContinuation, Action<NGramKey, int> visitor)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (TextNormalizer != null)
        {
            text = TextNormalizer.Normalize(text);
        }

        // Continuation segments skip start padding (they continue from previous segment)
        string startPad = isSegmentContinuation ? "" : _startPadding;

        // Add padding
        string paddedText = startPad + text + _stopPadding;

        GenerateNGrams(paddedText.AsSpan(), visitor);
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
        
        // Apply full normalization for search (same as indexing) to enable accent-insensitive matching.
        // This ensures "sciozl" matches "scioškolazlín" because both get normalized to "scioskola zlin".
        if (TextNormalizer != null)
        {
            text = TextNormalizer.Normalize(text);
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
    /// Extracts all n-grams of a specific size from the text into shingle objects.
    /// This is the legacy allocation-heavy path used by <see cref="TokenizeForIndexing"/>
    /// and <see cref="TokenizeForSearch"/> when building <see cref="Shingle"/> lists.
    /// </summary>
    private static void ExtractNGrams(string text, int n, List<Shingle> shingles)
    {
        if (text.Length < n)
            return;
        
        ReadOnlySpan<char> span = text.AsSpan();
        
        for (int i = 0; i <= span.Length - n; i++)
        {
            ReadOnlySpan<char> ngramSpan = span.Slice(i, n);
            
            // Skip if all characters are padding
            if (IsAllPadding(ngramSpan))
                continue;
            
            string ngram = new string(ngramSpan);
            shingles.Add(new Shingle(ngram, 1, i));
        }
    }

    /// <summary>
    /// Generates n-grams for high-performance indexing, invoking the visitor
    /// callback for each n-gram as a compact <see cref="NGramKey"/>.
    /// </summary>
    private void GenerateNGrams(ReadOnlySpan<char> text, Action<NGramKey, int> visitor)
    {
        // Determine max n-gram size to extract
        int maxSize = IndexSizes[^1]; // Last element
        if (text.Length <= IndexSizes[0])
        {
            maxSize = IndexSizes[0];
        }

        foreach (int size in IndexSizes)
        {
            ExtractNGrams(text, size, visitor);

            if (size == maxSize)
                break;
        }
    }

    /// <summary>
    /// Extracts all n-grams of a specific size from the text for the
    /// high-performance indexing path.
    /// </summary>
    private static void ExtractNGrams(ReadOnlySpan<char> text, int n, Action<NGramKey, int> visitor)
    {
        if (text.Length < n)
            return;

        for (int i = 0; i <= text.Length - n; i++)
        {
            ReadOnlySpan<char> ngramSpan = text.Slice(i, n);

            // Skip if all characters are padding
            if (IsAllPadding(ngramSpan))
                continue;

            NGramKey key = new NGramKey(ngramSpan);
            visitor(key, i);
        }
    }
    
    /// <summary>
    /// Consolidates shingles by counting occurrences
    /// </summary>
    private static Shingle[] ConsolidateShingles(List<Shingle> shingles, out Dictionary<string, Shingle> dictionary)
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
    /// Checks if a string consists only of padding characters.
    /// </summary>
    private static bool IsAllPadding(string text) => IsAllPadding(text.AsSpan());

    /// <summary>
    /// Checks if a span consists only of padding characters.
    /// </summary>
    private static bool IsAllPadding(ReadOnlySpan<char> text)
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
