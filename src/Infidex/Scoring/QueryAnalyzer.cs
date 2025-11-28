using Infidex.Tokenization;

namespace Infidex.Scoring;

/// <summary>
/// Analyzes query text to determine search strategy (n-gram vs short query).
/// </summary>
internal static class QueryAnalyzer
{
    public static (bool canUseNGrams, bool hasMixedTerms, string longWordsSearchText) Analyze(string searchText, Tokenizer tokenizer)
    {
        int minIndexSize = tokenizer.IndexSizes.Min();
        bool canUseNGrams = false;
        bool hasMixedTerms = false;
        string longWordsSearchText = searchText;

        if (tokenizer.TokenizerSetup != null)
        {
            string[] words = searchText.Split(tokenizer.TokenizerSetup.Delimiters, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == 0)
            {
                canUseNGrams = searchText.Length >= minIndexSize;
            }
            else
            {
                List<string> longWords = [];
                int shortWordsCount = 0;

                foreach (string word in words)
                {
                    if (word.Length >= minIndexSize)
                        longWords.Add(word);
                    else
                        shortWordsCount++;
                }

                if (longWords.Count > 0)
                {
                    canUseNGrams = true;
                    longWordsSearchText = string.Join(' ', longWords);
                }

                if (shortWordsCount > 0 && longWords.Count > 0)
                    hasMixedTerms = true;
            }
        }
        else
        {
            canUseNGrams = searchText.Length >= minIndexSize;
        }

        return (canUseNGrams, hasMixedTerms, longWordsSearchText);
    }

    public static bool CanUseNGrams(string searchText, Tokenizer tokenizer)
    {
        (bool canUseNGrams, _, _) = Analyze(searchText, tokenizer);
        return canUseNGrams;
    }
}

