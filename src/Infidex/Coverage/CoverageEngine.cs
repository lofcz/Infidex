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
        CoverageSummary summary = CalculateCoverageSummary(query, documentText, lcsSum);
        wordHits = summary.TotalWordHits;
        return summary.AggregateScore;
    }

    /// <summary>
    /// Calculates detailed coverage information for a query-document pair,
    /// including aggregate score and per-term coverage metrics.
    /// </summary>
    public CoverageSummary CalculateCoverageSummary(string query, string documentText, double lcsSum)
    {
        CoverageSummary summary = new CoverageSummary();
        summary.Terms = [];
        summary.AggregateScore = 0;
        summary.TotalWordHits = 0;
        
        int wordHits = 0;
        double queryLength = query.Length;
        
        if (queryLength == 0)
            return summary;
        
        // Tokenize into words
        HashSet<string> queryWords = _tokenizer.GetWordTokensForCoverage(query, _setup.MinWordSize);
        HashSet<string> docWords = _tokenizer.GetWordTokensForCoverage(documentText, _setup.MinWordSize);
        
        if (queryWords.Count == 0)
            return summary;
        
        // Initialize per-term coverage map (distinct query words)
        Dictionary<string, TermCoverageInfo> termCoverage = new Dictionary<string, TermCoverageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string w in queryWords)
        {
            termCoverage[w] = new TermCoverageInfo
            {
                QueryWord = w,
                MaxChars = w.Length,
                MatchedChars = 0.0
            };
        }
        
        double num = 0.0;  // Whole words matched chars
        double num2 = 0.0; // Joined words matched chars
        double num3 = 0.0; // Fuzzy words matched chars
        double num4 = 0.0; // Prefix/suffix matched chars
        byte b = 0; // Order penalty

        bool debug = Environment.GetEnvironmentVariable("INFIDEX_COVERAGE_DEBUG") == "1";
        if (debug)
        {
            string docSnippet = documentText.Length > 60 ? documentText.Substring(0, 60) + "..." : documentText;
            Console.WriteLine($"[COV] query=\"{query}\", doc=\"{docSnippet}\"");
            Console.WriteLine($"[COV]   queryWords=[{string.Join(", ", queryWords)}]");
            Console.WriteLine($"[COV]   docWords=[{string.Join(", ", docWords)}]");
        }
        
        // Algorithm 1: Exact Whole Word Matching
        if (_setup.CoverWholeWords)
        {
            num = CoverWholeWords(queryWords, docWords, termCoverage, out wordHits, ref b);
        }
        
        double num6 = num;
        
        // Algorithm 2: Joined/Split Words
        if (_setup.CoverJoinedWords && queryWords.Count > 0)
        {
            num2 = CoverJoinedWords(queryWords, docWords, termCoverage, out int num7);
            wordHits += num7;
            num6 = num2;
        }
        
        // Algorithm 3: Fuzzy Word Matching (Levenshtein)
        if (_setup.CoverFuzzyWords && queryWords.Count > 0)
        {
            // Normalized edit distance threshold for fuzzy matches:
            // dist(q,d) / max(|q|,|d|) <= maxRelativeDistance
            const double maxRelativeDistance = 0.25;
            int maxQueryLength = 0;
            foreach (string qw in queryWords)
            {
                if (qw.Length > maxQueryLength)
                    maxQueryLength = qw.Length;
            }

            int maxEditDistance = Math.Max(1, (int)Math.Round(maxQueryLength * maxRelativeDistance));

            num3 = CoverFuzzyWords(queryWords, docWords, termCoverage, maxEditDistance, maxRelativeDistance, out int num9);
            wordHits += num9;
            num6 += num3;
        }
        
        // Algorithm 4: Prefix/Suffix Matching
        if (_setup.CoverPrefixSuffix && queryWords.Count > 0)
        {
            if (debug)
            {
                Console.WriteLine($"[COV] Before Prefix/Suffix: queryWords=[{string.Join(", ", queryWords)}], docWords=[{string.Join(", ", docWords)}]");
            }
            num4 = CoverPrefixSuffix(queryWords, docWords, termCoverage, out int num10);
            wordHits += num10;
            if (debug)
            {
                Console.WriteLine($"[COV] After Prefix/Suffix: num4={num4}, wordHits={num10}");
            }
        }
        
        // Handle LCS: if CoverWholeQuery is false, ignore LCS
        if (!_setup.CoverWholeQuery)
        {
            lcsSum = 0.0;
        }
        
        // Combine results: num2 + num + num3 + num4 - penalty
        double num11 = num2 + num + num3 + num4 - b;
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
        
        summary.AggregateScore = final;
        summary.TotalWordHits = wordHits;
        summary.Terms = termCoverage.Values.ToList();
        return summary;
    }
    
    /// <summary>
    /// Algorithm 1: Exact whole word matching with order penalty
    /// Matches t9rpF59p9r exactly
    /// </summary>
    private static double CoverWholeWords(
        HashSet<string> queryWords, 
        HashSet<string> docWords, 
        Dictionary<string, TermCoverageInfo> termCoverage,
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
            num += array[i].Length;
            
            if (termCoverage.TryGetValue(text, out TermCoverageInfo? infoWhole))
            {
                infoWhole.MatchedChars += array[i].Length;
                infoWhole.HasWholeWordMatch = true;
                termCoverage[text] = infoWhole;
            }
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
                num += num2;
            }
            queryWords.Remove(text);
            docWords.Remove(text);
        }
        return num;
    }
    
    /// <summary>
    /// Algorithm 2: Detects joined/split words (e.g., "newyork" vs "new york")
    /// </summary>
    private static double CoverJoinedWords(
        HashSet<string> queryWords, 
        HashSet<string> docWords,
        Dictionary<string, TermCoverageInfo> termCoverage,
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
                
                if (termCoverage.TryGetValue(queryList[i], out TermCoverageInfo? info1))
                {
                    info1.MatchedChars += queryList[i].Length;
                    info1.HasJoinedMatch = true;
                    termCoverage[queryList[i]] = info1;
                }
                if (termCoverage.TryGetValue(queryList[i + 1], out TermCoverageInfo? info2))
                {
                    info2.MatchedChars += queryList[i + 1].Length;
                    info2.HasJoinedMatch = true;
                    termCoverage[queryList[i + 1]] = info2;
                }
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
                
                if (termCoverage.TryGetValue(joined, out TermCoverageInfo? infoJoined))
                {
                    infoJoined.MatchedChars += joined.Length;
                    infoJoined.HasJoinedMatch = true;
                    termCoverage[joined] = infoJoined;
                }
                queryWords.Remove(joined);
                docWords.Remove(docList[i]);
                docWords.Remove(docList[i + 1]);
                break;
            }
        }
        
        return matchedChars;
    }
    
    /// <summary>
    /// Algorithm 3: Fuzzy word matching using Levenshtein distance.
    /// Uses a normalized edit distance threshold:
    ///   dist(q, d) / max(|q|, |d|) <= maxRelativeDistance
    /// to accept matches in a length-independent way.
    /// </summary>
    private double CoverFuzzyWords(
        HashSet<string> queryWords,
        HashSet<string> docWords,
        Dictionary<string, TermCoverageInfo> termCoverage,
        int maxEditDistance,
        double maxRelativeDistance,
        out int wordHits)
    {
        wordHits = 0;
        double matchedChars = 0.0;

        string[] queryArray = queryWords.ToArray();
        string[] docArray = docWords.ToArray();

        for (int i = 1; i <= maxEditDistance; i++)
        {
            for (int q = 0; q < queryArray.Length; q++)
            {
                string qw = queryArray[q];

                int minLen = Math.Max(_setup.MinWordSize + 1, qw.Length - i);
                int maxLen = Math.Min(_setup.LevenshteinMaxWordSize, qw.Length + i);
                if (maxLen > 63)
                {
                    maxLen = 63;
                }

                if (qw.Length > maxLen || qw.Length < minLen)
                {
                    continue;
                }

                // Use our Levenshtein implementation (original uses BitParallelDiagonalLevenshtein64bit)
                for (int d = 0; d < docArray.Length; d++)
                {
                    string dw = docArray[d];

                    if (dw.Length > maxLen || dw.Length < minLen)
                    {
                        continue;
                    }

                    int dist = LevenshteinDistance.Calculate(qw, dw, i);
                    if (dist <= i)
                    {
                        double norm = (double)dist / Math.Max(qw.Length, dw.Length);
                        if (norm <= maxRelativeDistance)
                        {
                            if (docWords.Contains(dw))
                            {
                                wordHits++;
                            }

                            matchedChars += qw.Length - dist;

                            if (termCoverage.TryGetValue(qw, out TermCoverageInfo? infoFuzzy))
                            {
                                infoFuzzy.MatchedChars += qw.Length - dist;
                                infoFuzzy.HasFuzzyMatch = true;
                                termCoverage[qw] = infoFuzzy;
                            }
                            queryWords.Remove(qw);
                            docWords.Remove(dw);
                            break;
                        }
                    }
                }
            }
        }

        return matchedChars;
    }
    
    /// <summary>
    /// Algorithm 4: Prefix and suffix matching
    /// </summary>
    private static double CoverPrefixSuffix(
        HashSet<string> queryWords, 
        HashSet<string> docWords,
        Dictionary<string, TermCoverageInfo> termCoverage,
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
                        // Prefix matches are typically a stronger signal of intent
                        // (e.g., \"sh\" -> \"Shawshank\"), so give them full weight.
                        matchScore = queryWord.Length;
                        isMatch = true;
                    }
                    // Check if query is suffix of doc
                    else if (docWord.EndsWith(queryWord, StringComparison.OrdinalIgnoreCase))
                    {
                        // Suffix matches (e.g., \"sh\" -> \"Cash\") are weaker: they often
                        // correspond to different roots. Give them reduced weight so that,
                        // all else equal, prefix matches outrank suffix-only matches.
                        matchScore = Math.Max(1, queryWord.Length / 2);
                        isMatch = true;
                    }
                }
                
                if (isMatch)
                {
                    matchedChars += matchScore;
                    wordHits++;

                    if (termCoverage.TryGetValue(queryWord, out TermCoverageInfo? infoPrefixSuffix))
                    {
                        infoPrefixSuffix.MatchedChars += matchScore;
                        infoPrefixSuffix.HasPrefixSuffixMatch = true;
                        termCoverage[queryWord] = infoPrefixSuffix;
                    }

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

/// <summary>
/// Per-term coverage information for a single normalized query word.
/// </summary>
public class TermCoverageInfo
{
    public string QueryWord { get; set; } = string.Empty;
    public int MaxChars { get; set; }
    public double MatchedChars { get; set; }
    
    public bool HasWholeWordMatch { get; set; }
    public bool HasJoinedMatch { get; set; }
    public bool HasFuzzyMatch { get; set; }
    public bool HasPrefixSuffixMatch { get; set; }
}

/// <summary>
/// Detailed coverage summary for a query-document pair.
/// Contains the aggregate coverage score plus per-term coverage info.
/// </summary>
public class CoverageSummary
{
    public byte AggregateScore { get; set; }
    public int TotalWordHits { get; set; }
    public List<TermCoverageInfo> Terms { get; set; } = [];
}
