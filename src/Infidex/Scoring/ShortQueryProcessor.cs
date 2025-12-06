using Infidex.Core;
using Infidex.Indexing;
using Infidex.Indexing.Fst;
using Infidex.Indexing.ShortQuery;
using Infidex.Internalized.CommunityToolkit;
using Infidex.Tokenization;
using System.Buffers;

namespace Infidex.Scoring;

/// <summary>
/// Handles short query searches (single-character and queries below n-gram threshold).
/// Uses FST-based prefix lookup for O(prefix length) term resolution.
/// </summary>
internal static class ShortQueryProcessor
{
    private const int MaxFstTermsPerPrefix = 4096;

    public static ScoreEntry[] SearchSingleCharacter(
        char ch,
        Dictionary<int, byte>? bestSegmentsMap,
        int queryIndex,
        int maxResults,
        IReadOnlyList<Document> documents,
        char[] delimiters)
    {
        ch = char.ToLowerInvariant(ch);
        List<ScoreEntry> rawScores = [];

        foreach (Document doc in documents)
        {
            if (doc.Deleted)
                continue;

            string text = doc.IndexedText ?? string.Empty;
            if (text.Length == 0)
                continue;

            string lower = text.ToLowerInvariant();

            int charCount = 0;
            int firstCharIndex = -1;
            for (int i = 0; i < lower.Length; i++)
            {
                if (lower[i] == ch)
                {
                    charCount++;
                    if (firstCharIndex == -1)
                        firstCharIndex = i;
                }
            }

            if (charCount == 0)
                continue;

            string[] words = lower.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);
            bool hasWordStart = false;
            int firstWordIndex = int.MaxValue;
            int wordStartCount = 0;

            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length > 0 && w[0] == ch)
                {
                    hasWordStart = true;
                    wordStartCount++;
                    if (i < firstWordIndex)
                        firstWordIndex = i;
                }
            }

            bool anyExactToken = false;
            bool firstTokenExact = false;
            if (words.Length > 0)
            {
                firstTokenExact = words[0].Length == 1 && words[0][0] == ch;
                if (firstTokenExact)
                {
                    anyExactToken = true;
                }
                else
                {
                    foreach (string w in words)
                    {
                        if (w.Length == 1 && w[0] == ch)
                        {
                            anyExactToken = true;
                            break;
                        }
                    }
                }
            }

            bool titleEqualsChar = lower.Length == 1 && lower[0] == ch;

            int precedence = 0;
            if (hasWordStart)
            {
                precedence |= 128;
                if (firstWordIndex == 0)
                    precedence |= 64;
            }

            if (anyExactToken) precedence |= 32;
            if (firstTokenExact) precedence |= 16;
            if (titleEqualsChar) precedence |= 8;
            if (words.Length <= 3) precedence |= 32;

            float baseScore;
            if (hasWordStart)
            {
                int posComponent = 255 - Math.Min(firstWordIndex * 16, 240);
                int densityComponent = Math.Min(wordStartCount * 8, 32);
                int raw = Math.Clamp(posComponent + densityComponent, 0, 255);
                baseScore = (float)raw / 255f;
            }
            else
            {
                int posComponent = 200 - Math.Min(Math.Max(firstCharIndex, 0) * 4, 180);
                int densityComponent = Math.Min(charCount * 4, 40);
                int raw = Math.Clamp(posComponent + densityComponent, 0, 200);
                baseScore = (float)Math.Max(1, raw) / 255f;
            }

            // Combine precedence and base score
            float finalScore = (float)precedence + baseScore;
            
            rawScores.Add(new ScoreEntry(finalScore, doc.DocumentKey));

            if (bestSegmentsMap != null)
            {
                int internalId = doc.Id;
                int segmentNumber = doc.SegmentNumber;
                int baseId = internalId - segmentNumber;

                if (baseId >= 0)
                {
                    bestSegmentsMap[baseId] = (byte)segmentNumber;
                }
            }
        }

        ScoreEntry[] consolidated = SegmentProcessor.ConsolidateSegments(rawScores, bestSegmentsMap);

        if (maxResults < int.MaxValue && consolidated.Length > maxResults)
        {
            consolidated = consolidated.Take(maxResults).ToArray();
        }

        return consolidated;
    }

    public static TopKHeap SearchShortQuery(
        string searchText,
        string searchLower,
        int minIndexSize,
        int startPadSize,
        FstIndex? fstIndex,
        TermCollection termCollection,
        DocumentCollection documents,
        Dictionary<int, byte>? bestSegmentsMap,
        char[] delimiters,
        bool enableDebugLogging)
    {
        // Use a generous heap limit since we filter later
        TopKHeap relevancyScores = new TopKHeap(int.MaxValue);
        HashSet<long> matchedDocs = [];
        HashSet<long> firstTokenPrefixDocs = [];
        Dictionary<long, int> docScores = new Dictionary<long, int>();

        List<string> prefixPatterns = BuildPrefixPatterns(searchLower, minIndexSize, startPadSize);

        if (enableDebugLogging)
        {
            Console.WriteLine($"[DEBUG] Generated {prefixPatterns.Count} prefix patterns");
        }

        foreach (string pattern in prefixPatterns)
        {
            if (fstIndex != null)
            {
                int termCount = fstIndex.CountByPrefix(pattern.AsSpan());
                if (termCount == 0)
                    continue;

                int limit = MaxFstTermsPerPrefix > 0
                    ? Math.Min(termCount, MaxFstTermsPerPrefix)
                    : termCount;

                int[] buffer = ArrayPool<int>.Shared.Rent(limit);
                try
                {
                    int count = fstIndex.GetByPrefix(pattern.AsSpan(), buffer.AsSpan(0, limit));
                    for (int i = 0; i < count; i++)
                    {
                        Term? term = termCollection.GetTermByIndex(buffer[i]);
                        if (term != null)
                        {
                            ProcessTermMatches(term, documents, docScores, matchedDocs,
                                firstTokenPrefixDocs, searchLower, bestSegmentsMap, multiplier: 10);
                        }
                    }
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(buffer);
                }
            }
            else
            {
                var matchingTerms = termCollection.GetAllTerms().Where(t => t.Text?.StartsWith(pattern) == true);
                foreach (Term term in matchingTerms)
                {
                    ProcessTermMatches(term, documents, docScores, matchedDocs,
                        firstTokenPrefixDocs, searchLower, bestSegmentsMap, multiplier: 10);
                }
            }
        }

        if (matchedDocs.Count < 100)
        {
            ProcessFuzzyFallback(prefixPatterns, searchLower, termCollection, documents,
                docScores, matchedDocs, firstTokenPrefixDocs, bestSegmentsMap);
        }

        BuildFinalScores(relevancyScores, docScores, documents, searchLower,
            firstTokenPrefixDocs, delimiters);

        return relevancyScores;
    }

    private static List<string> BuildPrefixPatterns(string searchLower, int minIndexSize, int startPadSize)
    {
        List<string> prefixPatterns = [];
        string padPrefix = new string(Tokenizer.START_PAD_CHAR, startPadSize);

        for (int i = 0; i < minIndexSize && i < padPrefix.Length + searchLower.Length; i++)
        {
            int padCount = Math.Max(0, padPrefix.Length - i);
            int queryCount = Math.Min(searchLower.Length, minIndexSize - padCount);

            if (queryCount > 0)
            {
                string prefix = string.Concat(new string(Tokenizer.START_PAD_CHAR, padCount), searchLower.AsSpan(0, queryCount));
                prefixPatterns.Add(prefix);
            }
        }

        prefixPatterns.Add(" " + searchLower);
        return prefixPatterns;
    }

    private static void ProcessTermMatches(
        Term term,
        DocumentCollection documents,
        Dictionary<long, int> docScores,
        HashSet<long> matchedDocs,
        HashSet<long> firstTokenPrefixDocs,
        string searchLower,
        Dictionary<int, byte>? bestSegmentsMap,
        int multiplier)
    {
        List<int>? docIds = term.GetDocumentIds();
        List<byte>? weights = term.GetWeights();

        if (docIds == null || weights == null)
            return;

        for (int i = 0; i < docIds.Count; i++)
        {
            int internalId = docIds[i];
            byte weight = weights[i];

            Document? doc = documents.GetDocument(internalId);
            if (doc == null || doc.Deleted)
                continue;

            int score = weight * multiplier;

            if (!docScores.TryAdd(doc.DocumentKey, score))
            {
                docScores[doc.DocumentKey] += score;
            }
            else
            {
                matchedDocs.Add(doc.DocumentKey);
            }

            if (!firstTokenPrefixDocs.Contains(doc.DocumentKey))
            {
                string titleLower = doc.IndexedText.ToLowerInvariant();
                if (titleLower.StartsWith(searchLower, StringComparison.Ordinal))
                {
                    firstTokenPrefixDocs.Add(doc.DocumentKey);
                }
            }

            if (bestSegmentsMap != null)
            {
                int baseId = internalId - doc.SegmentNumber;
                if (baseId >= 0)
                {
                    bestSegmentsMap[baseId] = (byte)doc.SegmentNumber;
                }
            }
        }
    }

    private static void ProcessFuzzyFallback(
        List<string> prefixPatterns,
        string searchLower,
        TermCollection termCollection,
        DocumentCollection documents,
        Dictionary<long, int> docScores,
        HashSet<long> matchedDocs,
        HashSet<long> firstTokenPrefixDocs,
        Dictionary<int, byte>? bestSegmentsMap)
    {
        foreach (Term term in termCollection.GetAllTerms())
        {
            if (term.Text == null)
                continue;

            bool alreadyMatched = prefixPatterns.Any(p => term.Text.StartsWith(p));
            if (alreadyMatched)
                continue;

            bool hasWordBoundaryMatch = false;
            int charMatchCount = 0;

            foreach (char qChar in searchLower)
            {
                string wordBoundaryPattern = " " + qChar;
                if (term.Text.Contains(wordBoundaryPattern))
                {
                    hasWordBoundaryMatch = true;
                    charMatchCount++;
                }
                else if (term.Text.Contains(qChar))
                {
                    charMatchCount++;
                }
            }

            if (hasWordBoundaryMatch || charMatchCount > 0)
            {
                int multiplier = hasWordBoundaryMatch ? 2 : 1;
                ProcessTermMatches(term, documents, docScores, matchedDocs,
                    firstTokenPrefixDocs, searchLower, bestSegmentsMap, multiplier);
            }
        }
    }

    private static void BuildFinalScores(
        TopKHeap relevancyScores,
        Dictionary<long, int> docScores,
        DocumentCollection documents,
        string searchLower,
        HashSet<long> firstTokenPrefixDocs,
        char[] delimiters)
    {
        int maxScore = docScores.Values.DefaultIfEmpty(0).Max();
        string[] queryTokens = searchLower.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);

        foreach (KeyValuePair<long, int> kvp in docScores)
        {
            Document? doc = documents.GetDocumentByPublicKey(kvp.Key);
            if (doc == null || doc.Deleted)
                continue;

            float normalizedScore = maxScore > 0
                ? (float)kvp.Value / maxScore
                : kvp.Value / 255f; 

            string titleLower = doc.IndexedText.ToLowerInvariant();
            string trimmedTitle = titleLower.Trim();
            string[] words = titleLower.Split(delimiters, StringSplitOptions.RemoveEmptyEntries);

            int precedence = ComputePrecedence(queryTokens, words, searchLower, trimmedTitle,
                firstTokenPrefixDocs.Contains(kvp.Key));

            // Float score = Precedence + Normalized
            float finalScore = (float)precedence + normalizedScore;
            relevancyScores.Add(kvp.Key, finalScore);
        }
    }

    private static int ComputePrecedence(
        string[] queryTokens,
        string[] words,
        string searchLower,
        string trimmedTitle,
        bool firstTokenStartsWithPrefix)
    {
        int precedence = 0;

        if (queryTokens.Length >= 2)
        {
            int tokenMatches = queryTokens.Count(qt => words.Any(w => string.Equals(w, qt, StringComparison.Ordinal)));
            bool allTokensPresent = queryTokens.Length > 0 && tokenMatches == queryTokens.Length;

            if (allTokensPresent)
            {
                precedence |= 8;
                if (words.Length <= queryTokens.Length + 1)
                    precedence |= 2;
            }
            else if (tokenMatches > 0)
            {
                precedence |= 4;
            }
        }
        else
        {
            bool anyTokenExact = false;
            bool firstTokenExact = false;

            if (words.Length > 0)
            {
                firstTokenExact = string.Equals(words[0], searchLower, StringComparison.Ordinal);
                anyTokenExact = firstTokenExact || words.Any(w => string.Equals(w, searchLower, StringComparison.Ordinal));
            }

            bool titleEqualsQuery = string.Equals(trimmedTitle, searchLower, StringComparison.Ordinal);

            if (anyTokenExact) precedence |= 1;
            if (firstTokenStartsWithPrefix) precedence |= 2;
            if (firstTokenExact) precedence |= 4;
            if (titleEqualsQuery) precedence |= 8;
        }

        return precedence;
    }
}
