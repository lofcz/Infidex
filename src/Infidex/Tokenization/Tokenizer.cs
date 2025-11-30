using Infidex.Core;
using System.Buffers;
using System.Linq;

namespace Infidex.Tokenization;

internal delegate void SpanVisitor<TState>(ReadOnlySpan<char> span, TState state);
internal delegate void SpanVisitorWithPosition<TState>(ReadOnlySpan<char> span, int position, TState state);

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
    
    private string _startPadding;
    private string _stopPadding;
    private SearchValues<char>? _delimiterSearchValues;
    private TokenizerSetup? _tokenizerSetup;

    public TokenizerSetup? TokenizerSetup
    {
        get => _tokenizerSetup;
        set
        {
            _tokenizerSetup = value;
            _delimiterSearchValues = value?.Delimiters != null ? SearchValues.Create(value.Delimiters) : null;
        }
    }

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
    public List<Shingle> TokenizeForIndexing(string text, bool isSegmentContinuation = false)
    {
        List<Shingle> list = new List<Shingle>(text.Length);
        EnumerateTokensForIndexing(text, isSegmentContinuation, list, static (span, pos, l) => 
        {
            l.Add(new Shingle(span.ToString(), 1, pos));
        });
        return list;
    }
    
    /// <summary>
    /// Tokenizes text for indexing using a span-based, allocation-light enumerator.
    /// Supports both n-grams and full words.
    /// </summary>
    internal void EnumerateTokensForIndexing<TState>(string text, bool isSegmentContinuation, TState state, SpanVisitorWithPosition<TState> visitor)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (TextNormalizer != null)
        {
            text = TextNormalizer.Normalize(text);
        }
        
        string startPad = isSegmentContinuation ? string.Empty : _startPadding;
        
        // Add padding
        string paddedText = startPad + text + _stopPadding;
        ReadOnlySpan<char> paddedSpan = paddedText.AsSpan();

        // 1. Generate N-Grams
        GenerateNGramsToVisitor(paddedSpan, state, visitor);

        // 2. Generate Words
        if (TokenizerSetup != null)
        {
            SearchValues<char>? delimiters = _delimiterSearchValues;
            ReadOnlySpan<char> span = text.AsSpan();
            int baseOffset = isSegmentContinuation ? 0 : StartPadSize;
            int minSize = IndexSizes.Length > 0 ? IndexSizes[0] : 1;

            if (delimiters != null)
            {
                int i = 0;
                while (i < span.Length)
                {
                    // Skip delimiters
                    int nextChar = span[i..].IndexOfAnyExcept(delimiters);
                    if (nextChar < 0) break;
                    i += nextChar;

                    // Find token end
                    int tokenEnd = span[i..].IndexOfAny(delimiters);
                    int length = (tokenEnd < 0) ? span.Length - i : tokenEnd;
                    
                    if (length >= minSize)
                    {
                        visitor(span.Slice(i, length), baseOffset + i, state);
                    }
                    
                    i += length;
                }
            }
        }
    }

    /// <summary>
    /// Enumerates tokens for search using a callback to avoid string allocations.
    /// </summary>
    internal void EnumerateShinglesForSearch<TState>(string text, TState state, SpanVisitor<TState> visitor)
    {
        if (TextNormalizer != null)
        {
            text = TextNormalizer.Normalize(text);
        }

        // Extract words
        if (TokenizerSetup != null)
        {
            ReadOnlySpan<char> span = text.AsSpan();
            SearchValues<char>? delimiters = _delimiterSearchValues;
            
            if (delimiters != null)
            {
                int minSize = IndexSizes.Length > 0 ? IndexSizes[0] : 1;
                int i = 0;
                while (i < span.Length)
                {
                    int nextChar = span[i..].IndexOfAnyExcept(delimiters);
                    if (nextChar < 0) break;
                    i += nextChar;

                    int tokenEnd = span[i..].IndexOfAny(delimiters);
                    int length = (tokenEnd < 0) ? span.Length - i : tokenEnd;
                    
                    if (length >= minSize)
                    {
                        visitor(span.Slice(i, length), state);
                    }
                    
                    i += length;
                }
            }
        }

        // Generate shingles
        int paddedLength = StartPadSize + text.Length + StopPadSize;
        char[] pooledBuffer = ArrayPool<char>.Shared.Rent(paddedLength);
        try
        {
            // Fill start padding
            pooledBuffer.AsSpan(0, StartPadSize).Fill(START_PAD_CHAR);
            
            // Copy text
            text.CopyTo(pooledBuffer.AsSpan(StartPadSize));
            
            // Fill stop padding
            pooledBuffer.AsSpan(StartPadSize + text.Length, StopPadSize).Fill(STOP_PAD_CHAR);
            
            ReadOnlySpan<char> paddedSpan = pooledBuffer.AsSpan(0, paddedLength);
            GenerateShinglesToVisitor(paddedSpan, state, visitor);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(pooledBuffer);
        }
        
        if (TokenizerSetup != null && TokenizerSetup.HighResolutionMode)
        {
            int joinedLength = 0;
            ReadOnlySpan<char> span = text.AsSpan();
            SearchValues<char>? delimiters = _delimiterSearchValues;
            
            if (delimiters != null)
            {
                for (int i = 0; i < span.Length; i++)
                {
                    if (!delimiters.Contains(span[i]))
                    {
                        joinedLength++;
                    }
                }
            }
            else
            {
                joinedLength = text.Length;
            }

            int paddedJoinedLength = StartPadSize + joinedLength + StopPadSize;
            char[] joinedBuffer = ArrayPool<char>.Shared.Rent(paddedJoinedLength);
            
            try
            {
                // Fill start padding
                joinedBuffer.AsSpan(0, StartPadSize).Fill(START_PAD_CHAR);
                
                // Copy joined text
                int pos = StartPadSize;
                if (delimiters != null)
                {
                    for (int i = 0; i < span.Length; i++)
                    {
                        char c = span[i];
                        if (!delimiters.Contains(c))
                        {
                            joinedBuffer[pos++] = c;
                        }
                    }
                }
                else
                {
                    text.CopyTo(joinedBuffer.AsSpan(StartPadSize));
                    pos += text.Length;
                }
                
                // Fill stop padding
                joinedBuffer.AsSpan(pos, StopPadSize).Fill(STOP_PAD_CHAR);
                
                ReadOnlySpan<char> paddedJoinedSpan = joinedBuffer.AsSpan(0, paddedJoinedLength);
                GenerateShinglesToVisitor(paddedJoinedSpan, state, visitor);
                
                int minSize = IndexSizes.Length > 0 ? IndexSizes[0] : 1;
                if (joinedLength >= minSize)
                {
                    visitor(paddedJoinedSpan.Slice(StartPadSize, joinedLength), state);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(joinedBuffer);
            }
        }
    }

    public List<Shingle> TokenizeForSearch(string text)
    {
        List<Shingle> list = new List<Shingle>(text.Length);
        EnumerateShinglesForSearch(text, list, static (span, l) => l.Add(new Shingle(span.ToString(), 1, 0)));
        return list;
    }
    
    private void GenerateShinglesToVisitor<TState>(ReadOnlySpan<char> text, TState state, SpanVisitor<TState> visitor)
    {
        int maxSize = IndexSizes[^1];
        if (text.Length <= IndexSizes[0]) maxSize = IndexSizes[0];
        
        foreach (int size in IndexSizes)
        {
            ExtractNGrams(text, size, state, visitor);
            if (size == maxSize) break;
        }
    }
    
    private void GenerateNGramsToVisitor<TState>(ReadOnlySpan<char> text, TState state, SpanVisitorWithPosition<TState> visitor)
    {
        int maxSize = IndexSizes[^1];
        if (text.Length <= IndexSizes[0]) maxSize = IndexSizes[0];
        
        foreach (int size in IndexSizes)
        {
            ExtractNGrams(text, size, state, visitor);
            if (size == maxSize) break;
        }
    }

    private static void ExtractNGrams<TState>(ReadOnlySpan<char> text, int n, TState state, SpanVisitor<TState> visitor)
    {
        if (text.Length < n) return;
        for (int i = 0; i <= text.Length - n; i++)
        {
            ReadOnlySpan<char> span = text.Slice(i, n);
            if (IsAllPadding(span)) continue;
            visitor(span, state);
        }
    }

    private static void ExtractNGrams<TState>(ReadOnlySpan<char> text, int n, TState state, SpanVisitorWithPosition<TState> visitor)
    {
        if (text.Length < n) return;
        for (int i = 0; i <= text.Length - n; i++)
        {
            ReadOnlySpan<char> span = text.Slice(i, n);
            if (IsAllPadding(span)) continue;
            visitor(span, i, state);
        }
    }

    private static bool IsAllPadding(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c != START_PAD_CHAR && c != STOP_PAD_CHAR) return false;
        }
        return true;
    }
    
    public HashSet<string> GetWordTokensForCoverage(string text, int minWordSize)
    {
        if (TokenizerSetup == null) return [];
        string[] words = text.Split(TokenizerSetup.Delimiters, StringSplitOptions.RemoveEmptyEntries);
        HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string word in words)
        {
            if (word.Length >= minWordSize) result.Add(word.ToLowerInvariant());
        }
        return result;
    }
}
