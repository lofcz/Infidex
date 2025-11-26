namespace Infidex.Coverage;

internal static class CoverageTokenizer
{
    public const int MaxStackTerms = 256;

    public static int TokenizeToSpan(string text, Span<StringSlice> tokens, int minWordSize, ReadOnlySpan<char> delimiters)
    {
        int count = 0;
        int max = tokens.Length;
        ReadOnlySpan<char> span = text.AsSpan();
        int currentPos = 0;

        while (!span.IsEmpty)
        {
            int nextTokenIndex = span.IndexOfAnyExcept(delimiters);
            if (nextTokenIndex < 0) break;

            span = span[nextTokenIndex..];
            currentPos += nextTokenIndex;

            int delimiterIndex = span.IndexOfAny(delimiters);
            int tokenLen = (delimiterIndex < 0) ? span.Length : delimiterIndex;

            if (tokenLen >= minWordSize)
            {
                if (count < max)
                {
                    tokens[count++] = new StringSlice(currentPos, tokenLen, currentPos, 0);
                }
            }

            currentPos += tokenLen;
            if (delimiterIndex < 0) break;
            span = span[tokenLen..];
        }
        
        return count;
    }

    public static int DeduplicateQueryTokens(Span<StringSlice> tokens, int rawCount, ReadOnlySpan<char> textSpan)
    {
        int uniqueCount = 0;
        
        for (int i = 0; i < rawCount; i++)
        {
            bool duplicate = false;
            StringSlice current = tokens[i];
            ReadOnlySpan<char> currentSpan = textSpan.Slice(current.Offset, current.Length);
            int currentHash = string.GetHashCode(currentSpan, StringComparison.OrdinalIgnoreCase);
            
            for (int j = 0; j < uniqueCount; j++)
            {
                StringSlice existing = tokens[j];
                if (existing.Hash == currentHash && existing.Length == current.Length)
                {
                    if (textSpan.Slice(existing.Offset, existing.Length).Equals(currentSpan, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
            }
            if (!duplicate)
            {
                tokens[uniqueCount++] = new StringSlice(current.Offset, current.Length, current.Position, currentHash);
            }
        }
        
        return uniqueCount;
    }

    public static int DeduplicateDocTokens(
        Span<StringSlice> rawTokens,
        int rawCount,
        Span<StringSlice> uniqueTokens,
        ReadOnlySpan<char> textSpan)
    {
        int uniqueCount = 0;
        
        for (int i = 0; i < rawCount; i++)
        {
            StringSlice current = rawTokens[i];
            ReadOnlySpan<char> currentSpan = textSpan.Slice(current.Offset, current.Length);
            int currentHash = string.GetHashCode(currentSpan, StringComparison.OrdinalIgnoreCase);
            
            bool duplicate = false;
            for (int j = 0; j < uniqueCount; j++)
            {
                StringSlice existing = uniqueTokens[j];
                if (existing.Hash == currentHash && existing.Length == current.Length)
                {
                    if (textSpan.Slice(existing.Offset, existing.Length).Equals(currentSpan, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
            }
            
            if (!duplicate)
            {
                uniqueTokens[uniqueCount++] = new StringSlice(current.Offset, current.Length, current.Position, currentHash);
            }
        }
        
        return uniqueCount;
    }
}
