using Infidex.Metrics;
using Infidex.Tokenization;

namespace Infidex.Coverage;

/// <summary>
/// Implements the Coverage algorithm suite for lexical matching (Stage 2).
/// Includes 5 different matching algorithms: exact, fuzzy, joined, prefix/suffix, and LCS.
/// </summary>
public class CoverageEngine
{
    private readonly Tokenizer _tokenizer;
    private readonly CoverageSetup _setup;
    
    public CoverageEngine(Tokenizer tokenizer, CoverageSetup? setup = null)
    {
        _tokenizer = tokenizer;
        _setup = setup ?? CoverageSetup.CreateDefault();
    }
    
    /// <summary>
    /// Calculates coverage score for a query-document pair.
    /// Returns a score from 0-255 based on lexical overlap.
    /// Matches CoreSearchEngine.CoverageWord exactly.
    /// </summary>
    /// <param name="query">The search query</param>
    /// <param name="documentText">The document text to match against</param>
    /// <param name="lcsSum">Pre-calculated LCS value (from Metrics.Lcs)</param>
    /// <param name="wordHits">Output: number of word matches found</param>
    public byte CalculateCoverageScore(string query, string documentText, double lcsSum, out int wordHits)
    {
        wordHits = 0;
        double queryLength = query.Length;
        
        if (queryLength == 0)
            return 0;
        
        // Tokenize into words
        HashSet<string> queryWords = _tokenizer.GetWordTokensForCoverage(query, _setup.MinWordSize);
        HashSet<string> docWords = _tokenizer.GetWordTokensForCoverage(documentText, _setup.MinWordSize);
        
        if (queryWords.Count == 0)
            return 0;
        
        double num = 0.0;  // Whole words matched chars
        double num2 = 0.0; // Joined words matched chars
        double num3 = 0.0; // Fuzzy words matched chars
        double num4 = 0.0; // Prefix/suffix matched chars
        byte b = 0; // Order penalty

        bool debug = Environment.GetEnvironmentVariable("INFIDEX_COVERAGE_DEBUG") == "1";
        if (debug)
        {
            Console.WriteLine($"[COV] query=\"{query}\", docSnippet=\"{documentText[..Math.Min(documentText.Length, 60)]}\"");
            Console.WriteLine($"[COV]   MinWordSize={_setup.MinWordSize}, LevenshteinMaxWordSize={_setup.LevenshteinMaxWordSize}, CoverWholeQuery={_setup.CoverWholeQuery}");
            Console.WriteLine($"[COV]   queryWords=[{string.Join(", ", queryWords)}]");
            Console.WriteLine($"[COV]   docWords=[{string.Join(", ", docWords)}]");
        }
        
        // Algorithm 1: Exact Whole Word Matching
        if (_setup.CoverWholeWords)
        {
            num = CoverWholeWords(queryWords, docWords, out wordHits, ref b);
            if (num >= queryLength)
            {
                byte score = (byte)(255 - b);
                if (debug)
                    Console.WriteLine($"[COV] WholeWords early-exit: num={num}, penalty={b}, score={score}");
                return score;
            }
        }
        
        double num6 = num;
        
        // Algorithm 2: Joined/Split Words
        if (_setup.CoverJoinedWords && queryWords.Count > 0)
        {
            num2 = CoverJoinedWords(queryWords, docWords, out int num7);
            wordHits += num7;
            num6 = num2;
            if (num6 >= queryLength)
            {
                if (debug)
                    Console.WriteLine($"[COV] JoinedWords early-exit: num2={num2}, score=255");
                return byte.MaxValue;
            }
        }
        
        // Algorithm 3: Fuzzy Word Matching (Levenshtein)
        if (_setup.CoverFuzzyWords && queryWords.Count > 0)
        {
            int num8 = 1; // Max edit distance
            num3 = CoverFuzzyWords(queryWords, docWords, num8, out int num9);
            wordHits += num9;
            num6 += num3;
            if (num6 >= queryLength)
            {
                if (debug)
                    Console.WriteLine($"[COV] Fuzzy early-exit: num3={num3}, score=255");
                return byte.MaxValue;
            }
        }
        
        // Algorithm 4: Prefix/Suffix Matching
        if (_setup.CoverPrefixSuffix && queryWords.Count > 0)
        {
            num4 = CoverPrefixSuffix(queryWords, docWords, out int num10);
            wordHits += num10;
            if (num6 + num4 >= queryLength)
            {
                if (debug)
                    Console.WriteLine($"[COV] PrefixSuffix early-exit: num4={num4}, score=255");
                return byte.MaxValue;
            }
        }
        
        // Handle LCS: if CoverWholeQuery is false, ignore LCS
        if (!_setup.CoverWholeQuery)
        {
            lcsSum = 0.0;
        }
        
        // Combine results: num2 + num + num3 + num4 - penalty
        double num11 = num2 + num + num3 + num4 - (double)(int)b;
        if (debug)
        {
            Console.WriteLine($"[COV] totals: whole={num}, joined={num2}, fuzzy={num3}, affix={num4}, penalty={b}, combined={num11}, lcsSum={lcsSum}");
        }
        
        // Use LCS as fallback if no word matches
        if (num11 == 0.0 && lcsSum > 2.0)
        {
            num11 = lcsSum - 2.0;
            if (debug)
                Console.WriteLine($"[COV] LCS fallback: adjustedCombined={num11}");
        }
        
        // Calculate coverage ratio and convert to byte score
        byte final = (byte)Math.Min(num11 / queryLength * 255.0, 255.0);
        if (debug)
            Console.WriteLine($"[COV] finalScore={final}");
        return final;
    }
    
    /// <summary>
    /// Algorithm 1: Exact whole word matching with order penalty
    /// Matches t9rpF59p9r exactly
    /// </summary>
    private double CoverWholeWords(
        HashSet<string> queryWords, 
        HashSet<string> docWords, 
        out int wordHits,
        ref byte penalty)
    {
        double num = 0.0;
        wordHits = 0;
        int count = queryWords.Count;
        int num2 = 0;
        if (count > 1)
        {
            num2 = 1;
        }
        string[] array = queryWords.ToArray();
        string[] array2 = docWords.ToArray();
        for (int i = 0; i < array.Length; i++)
        {
            string text = array[i];
            if (!docWords.Contains(text))
            {
                continue;
            }
            wordHits++;
            num += (double)array[i].Length;
            if (array2.Length > i)
            {
                if (array2[i] != text)
                {
                    penalty++;
                }
            }
            else
            {
                penalty++;
            }
            if (i < count - 1)
            {
                num += (double)num2;
            }
            queryWords.Remove(text);
            docWords.Remove(text);
        }
        return num;
    }
    
    /// <summary>
    /// Algorithm 2: Detects joined/split words (e.g., "newyork" vs "new york")
    /// </summary>
    private double CoverJoinedWords(
        HashSet<string> queryWords, 
        HashSet<string> docWords, 
        out int wordHits)
    {
        double matchedChars = 0.0;
        wordHits = 0;
        
        List<string> queryList = queryWords.ToList();
        List<string> docList = docWords.ToList();
        
        // Check consecutive query words joined in document
        for (int i = 0; i < queryList.Count - 1; i++)
        {
            string joined = queryList[i] + queryList[i + 1];
            
            if (docWords.Contains(joined))
            {
                matchedChars += joined.Length;
                wordHits += 2;
                queryWords.Remove(queryList[i]);
                queryWords.Remove(queryList[i + 1]);
                docWords.Remove(joined);
                break;
            }
        }
        
        // Check consecutive doc words joined in query
        for (int i = 0; i < docList.Count - 1; i++)
        {
            string joined = docList[i] + docList[i + 1];
            
            if (queryWords.Contains(joined))
            {
                matchedChars += joined.Length;
                wordHits += 1;
                queryWords.Remove(joined);
                docWords.Remove(docList[i]);
                docWords.Remove(docList[i + 1]);
                break;
            }
        }
        
        return matchedChars;
    }
    
    /// <summary>
    /// Algorithm 3: Fuzzy word matching using Levenshtein distance
    /// Matches CeBpwYr5OP exactly
    /// </summary>
    private double CoverFuzzyWords(
        HashSet<string> queryWords, 
        HashSet<string> docWords, 
        int maxEditDistance,
        out int wordHits)
    {
        wordHits = 0;
        double num = 0.0;
        string[] array = queryWords.ToArray();
        string[] array2 = docWords.ToArray();
        for (int i = 1; i <= maxEditDistance; i++)
        {
            for (int j = 0; j < array.Length; j++)
            {
                int num2 = Math.Max(_setup.MinWordSize + 1, array[j].Length - i);
                int num3 = Math.Min(_setup.LevenshteinMaxWordSize, array[j].Length + i);
                if (num3 > 63)
                {
                    num3 = 63;
                }
                if (array[j].Length > num3 || array[j].Length < num2)
                {
                    continue;
                }
                // Use our Levenshtein implementation (original uses BitParallelDiagonalLevenshtein64bit)
                for (int k = 0; k < array2.Length; k++)
                {
                    if (array2[k].Length > num3 || array2[k].Length < num2)
                    {
                        continue;
                    }
                    int num4 = LevenshteinDistance.Calculate(array[j], array2[k], i);
                    if (num4 <= i)
                    {
                        if (docWords.Contains(array2[k]))
                        {
                            wordHits++;
                        }
                        num += (double)(array[j].Length - num4);
                        queryWords.Remove(array[j]);
                        docWords.Remove(array2[k]);
                        break;
                    }
                }
            }
        }
        return num;
    }
    
    /// <summary>
    /// Algorithm 4: Prefix and suffix matching
    /// </summary>
    private double CoverPrefixSuffix(
        HashSet<string> queryWords, 
        HashSet<string> docWords, 
        out int wordHits)
    {
        double matchedChars = 0.0;
        wordHits = 0;
        
        List<string> queryList = queryWords.OrderByDescending(w => w.Length).ToList();
        List<string> docList = docWords.OrderByDescending(w => w.Length).ToList();
        List<(string query, string doc)> matched = [];
        
        foreach (string queryWord in queryList)
        {
            foreach (string docWord in docList)
            {
                if (queryWord.Length == docWord.Length)
                    continue;
                
                bool isMatch = false;
                int matchScore = 0;

                if (queryWord.Length < docWord.Length)
                {
                    // Check if query is prefix of doc
                    if (docWord.StartsWith(queryWord, StringComparison.OrdinalIgnoreCase))
                    {
                        matchScore = queryWord.Length - 1;
                        isMatch = true;
                    }
                    // Check if query is suffix of doc
                    else if (docWord.EndsWith(queryWord, StringComparison.OrdinalIgnoreCase))
                    {
                        matchScore = queryWord.Length - 1;
                        isMatch = true;
                    }
                }
                
                if (isMatch)
                {
                    matchedChars += matchScore;
                    wordHits++;
                    matched.Add((queryWord, docWord));
                    break;
                }
            }
        }
        
        // Remove matched words
        foreach ((string query, string doc) in matched)
        {
            queryWords.Remove(query);
            docWords.Remove(doc);
        }
        
        return matchedChars;
    }
}
