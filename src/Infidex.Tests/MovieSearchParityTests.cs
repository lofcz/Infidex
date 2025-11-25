using System.Diagnostics;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using Infidex;
using Infidex.Api;
using Infidex.Core;

namespace Infidex.Tests;

public class MovieRecord
{
    [Name("title")]
    public string Title { get; set; } = string.Empty;
    
    [Name("description")]
    public string Description { get; set; } = string.Empty;
    
    [Name("genre")]
    public string Genre { get; set; } = string.Empty;
    
    [Name("year")]
    public string Year { get; set; } = string.Empty;
}

/// <summary>
/// Shared test logic for movie search parity.
/// Allows verifying search behavior across different engine configurations (Memory vs Persisted).
/// </summary>
public abstract class MovieSearchParityTestsBase
{
    protected abstract SearchEngine GetEngine();
    protected abstract List<MovieRecord> GetMovies();

    [TestMethod]
    public void RedemptionSh_PrefersShawshankOverOtherRedemptionTitles()
    {
        var engine = GetEngine();
        
        var result = engine.Search(new Query("redemption sh", 10));
        var records = result.Records;

        Assert.IsTrue(records.Length >= 2, "Expected at least two results for 'redemption sh'.");

        long firstId = records[0].DocumentId;
        long secondId = records[1].DocumentId;

        var firstDoc = engine.GetDocument(firstId);
        var secondDoc = engine.GetDocument(secondId);

        Assert.IsNotNull(firstDoc, "First result document should not be null.");
        Assert.IsNotNull(secondDoc, "Second result document should not be null.");

        string firstTitle = firstDoc!.IndexedText;
        string secondTitle = secondDoc!.IndexedText;

        // Lock-in: The Shawshank Redemption must be the top result
        Assert.AreEqual("The Shawshank Redemption", firstTitle);

        // And it must have a strictly higher score than the second result
        Assert.IsTrue(records[0].Score > records[1].Score,
            "The Shawshank Redemption should have a higher score than the next-best match.");
    }

    [TestMethod]
    public void Shawshank_Query_PrefersShawshankTitle()
    {
        var engine = GetEngine();
        
        var result = engine.Search(new Query("Shawshank", 10));
        var records = result.Records;

        Assert.IsTrue(records.Length >= 1, "Expected at least one result for 'Shawshank'.");

        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(firstDoc);
        Assert.AreEqual("The Shawshank Redemption", firstDoc!.IndexedText);
    }

    [TestMethod]
    public void Shaaawshank_Typo_StillPrefersShawshankTitle()
    {
        var engine = GetEngine();
        
        var result = engine.Search(new Query("Shaaawshank", 10));
        var records = result.Records;

        Assert.IsTrue(records.Length >= 1, "Expected at least one result for 'Shaaawshank'.");

        Console.WriteLine("Results for 'Shaaawshank':");
        for (int i = 0; i < records.Length; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
        }

        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(firstDoc);
        Assert.AreEqual("The Shawshank Redemption", firstDoc!.IndexedText);
    }

    [TestMethod]
    public void RedeptionSh_Typo_StillPrefersShawshankOverRedemptionTitles()
    {
        var engine = GetEngine();
        
        var result = engine.Search(new Query("redeption sh", 10));
        var records = result.Records;

        Assert.IsTrue(records.Length >= 2, "Expected at least two results for 'redeption sh'.");

        var firstDoc = engine.GetDocument(records[0].DocumentId);
        var secondDoc = engine.GetDocument(records[1].DocumentId);

        Assert.IsNotNull(firstDoc, "First result document should not be null.");
        Assert.IsNotNull(secondDoc, "Second result document should not be null.");

        string firstTitle = firstDoc!.IndexedText;
        string secondTitle = secondDoc!.IndexedText;

        // Lock-in: even with the 'redeption' typo, we expect the engine to infer the user's intent
        // and return The Shawshank Redemption as the best match over pure 'Redemption' titles.
        Assert.AreEqual("The Shawshank Redemption", firstTitle);

        // And it must outrank the next-best candidate
        Assert.IsTrue(records[0].Score > records[1].Score,
            $"Expected '{firstTitle}' to have a higher score than '{secondTitle}'.");
    }

    [TestMethod]
    public void RedptionSh_TwoTypos_StillPrefersShawshankOverRedemptionTitles()
    {
        var engine = GetEngine();
        
        var result = engine.Search(new Query("redption sh", 10));
        var records = result.Records;

        Assert.IsTrue(records.Length >= 2, "Expected at least two results for 'redption sh'.");

        var firstDoc = engine.GetDocument(records[0].DocumentId);
        var secondDoc = engine.GetDocument(records[1].DocumentId);

        Assert.IsNotNull(firstDoc, "First result document should not be null.");
        Assert.IsNotNull(secondDoc, "Second result document should not be null.");

        string firstTitle = firstDoc!.IndexedText;
        string secondTitle = secondDoc!.IndexedText;

        // Lock-in: even with two typos in 'redemption' ('redption'), we expect the engine to infer the user's intent
        // and return The Shawshank Redemption as the best match over pure 'Redemption' titles.
        Assert.AreEqual("The Shawshank Redemption", firstTitle);

        // And it must outrank the next-best candidate
        Assert.IsTrue(records[0].Score > records[1].Score,
            $"Expected '{firstTitle}' to have a higher score than '{secondTitle}'.");
    }

    [TestMethod]
    public void Shawsh_PrefersShawshankOverShaws()
    {
        var engine = GetEngine();
        var movies = GetMovies();

        var result = engine.Search(new Query("shawsh", 10));
        var records = result.Records;

        Assert.IsTrue(records.Length >= 1, "Expected at least one result for 'shawsh'.");

        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(firstDoc, "First result document should not be null.");

        string firstTitle = firstDoc!.IndexedText;

        // Lock-in: "shawsh" is a better match for "Shawshank" than "Shaws"
        // because it covers more of the query (6/6 chars vs 5/6 chars)
        Assert.AreEqual("The Shawshank Redemption", firstTitle,
            "Query 'shawsh' should match 'Shawshank' better than 'Shaws'");

        // If "Artie Shaws Class in Swing" is present in the results, ensure it does not outrank Shawshank.
        int shawsIndex = Array.FindIndex(
            records,
            r => movies[(int)r.DocumentId].Title == "Artie Shaws Class in Swing");

        if (shawsIndex >= 0)
        {
            var shawsEntry = records[shawsIndex];
            Assert.IsTrue(records[0].Score > shawsEntry.Score,
                $"Expected '{firstTitle}' to have a higher score than 'Artie Shaws Class in Swing'.");
        }
    }

    [TestMethod]
    public void RedemptionShan_PrefersShawshank()
    {
        var engine = GetEngine();
        
        // This fails if "shan" isn't treated as a prefix for "Shawshank"
        // or if fuzzy matching interferes incorrectly.
        var result = engine.Search(new Query("redemption shan", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0);
        
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        string topTitle = firstDoc!.IndexedText;
        Console.WriteLine($"Top Result: {topTitle}");
        
        Assert.IsTrue(topTitle.Contains("Shawshank", StringComparison.OrdinalIgnoreCase), 
            $"Expected Shawshank Redemption but got '{topTitle}'");
    }

    [TestMethod]
    public void TheAmtrix_FindsTheMatrix()
    {
        var engine = GetEngine();
        
        // "the amtrix" contains a typo (transposition of 'm' and 'a', or insertion?)
        // "matrix" vs "amtrix"
        // m-a-t-r-i-x
        // a-m-t-r-i-x
        // Dist is 2 (substitution m->a, a->m) OR 2 (swap).
        // Levenshtein distance:
        // matrix
        // amtrix
        // 1. delete m -> atrix
        // 2. insert m -> amatrix (no)
        // 1. sub m->a -> aatrix. 2. sub a->m -> amtrix. Dist 2.
        
        // If max edit distance is 1 (default for short words?), it fails.
        // matrix is 6 chars. 0.25 * 6 = 1.5 -> 2? 
        // CoverageEngine: int maxEditDist = Math.Max(1, (int)Math.Round(maxQueryLength * maxRelDist));
        // maxRelDist = 0.25.
        // If len=6, 6*0.25 = 1.5. Round(1.5) = 2.
        // So it SHOULD support edit dist 2 for length 6.
        
        var result = engine.Search(new Query("the amtrix", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0, "Should find results");
        
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        string topTitle = firstDoc!.IndexedText;
        Console.WriteLine($"Top Result: {topTitle}");
        
        // "The Matrix" should be top, or at least highly ranked.
        // Currently it seems we only match "the".
        bool foundMatrix = records.Any(r => engine.GetDocument(r.DocumentId)!.IndexedText == "The Matrix");
        Assert.IsTrue(foundMatrix, "Should find 'The Matrix' in top 10");
        
        Assert.AreEqual("The Matrix", topTitle, "The Matrix should be the top result");
    }

    [TestMethod]
    public void TheAmmtrix_FindsTheMatrix()
    {
        var engine = GetEngine();
        
        // "ammtrix" -> "matrix"
        // Dist 3 Levenshtein (Sub, Sub, Del) or (Ins, Swap, Del?)
        // Damerau: Swap (am->ma) + Del (m). Dist 2.
        // Requires relaxed fuzzy matching or transposition support.
        
        var result = engine.Search(new Query("the ammtrix", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0, "Should find results");
        
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        string topTitle = firstDoc!.IndexedText;
        Console.WriteLine($"Top Result: {topTitle}");
        
        // Should find "The Matrix"
        bool foundMatrix = records.Any(r => engine.GetDocument(r.DocumentId)!.IndexedText == "The Matrix");
        Assert.IsTrue(foundMatrix, "Should find 'The Matrix' in top 10");
        
        Assert.IsTrue(topTitle.Contains("The Matrix"), "Top result should contain 'The Matrix'");
    }

    [TestMethod]
    public void RedemptionWshan_PrefersShawshank()
    {
        var engine = GetEngine();
        
        // "wshan" is a substring of "Shawshank".
        // If the user went to the effort of typing "wshan" (5 chars), 
        // they likely want the movie containing that specific sequence 
        // over generic "Redemption" matches.
        var result = engine.Search(new Query("redemption wshan", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0);
        
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        string topTitle = firstDoc!.IndexedText;
        Console.WriteLine($"Top Result: {topTitle}");
        
        Assert.IsTrue(topTitle.Contains("Shawshank", StringComparison.OrdinalIgnoreCase), 
            $"Expected Shawshank Redemption but got '{topTitle}'");
    }

    [TestMethod]
    public void Search_Star_VerifyGrouping()
    {
        var engine = GetEngine();
        var movies = GetMovies();
        var query = "star";
        
        var result = engine.Search(new Query(query, 500));
        var records = result.Records;
        
        Console.WriteLine($"Search results for '{query}' (Top 20 shown):");
        for (int i = 0; i < Math.Min(20, records.Length); i++)
        {
            var record = records[i];
            // Document IDs match the index in the movies list for this test setup
            var movie = movies[(int)record.DocumentId];
            Console.WriteLine($"{movie.Title} ({movie.Year}) - Score: {record.Score:F1}");
        }
        
        // Find representative movies for Group A (Exact matches)
        var starKid = records.FirstOrDefault(r => movies[(int)r.DocumentId].Title == "Star Kid");
        var starDust = records.FirstOrDefault(r => movies[(int)r.DocumentId].Title == "Star Dust");
        var starTrek = records.FirstOrDefault(r => movies[(int)r.DocumentId].Title == "Star Trek");
        
        // Find representative movies for Group B (Prefix matches)
        var stardom = records.FirstOrDefault(r => movies[(int)r.DocumentId].Title == "Stardom");
        var starlift = records.FirstOrDefault(r => movies[(int)r.DocumentId].Title == "Starlift");
        var stargirl = records.FirstOrDefault(r => movies[(int)r.DocumentId].Title == "Stargirl");
        var stardust = records.FirstOrDefault(r => movies[(int)r.DocumentId].Title == "Stardust");

        if (starKid == default(ScoreEntry)) Console.WriteLine("MISSING: Star Kid");
        if (stardom == default(ScoreEntry)) Console.WriteLine("MISSING: Stardom");

        // Verify we found the key documents
        // Note: ScoreEntry is a struct, so default is mostly zeros. We check DocumentId is not 0 (assuming 0 is not Star Kid/Stardom, which is safe given 40k movies)
        // Or safer: check if we found them by checking ID against list if we had map, but here we rely on FirstOrDefault logic
        // Better check:
        bool foundStarKid = starKid.Score > 0 || (starKid.DocumentId == 0 && movies[0].Title == "Star Kid");
        bool foundStardom = stardom.Score > 0;

        Assert.IsTrue(foundStarKid, "Should find 'Star Kid'");
        Assert.IsTrue(foundStardom, "Should find 'Stardom'");
        
        Console.WriteLine($"\nStar Kid Score: {starKid.Score}");
        Console.WriteLine($"Stardom Score: {stardom.Score}");

        // Verify Group A (Exact) > Group B (Prefix)
        Assert.IsTrue(starKid.Score > stardom.Score, 
            $"Group A (Exact, score={starKid.Score}) should score higher than Group B (Prefix, score={stardom.Score})");

        // Additional invariant for single-term query "star":
        // All titles whose text starts with the word "Star" (e.g. "Star Kid",
        // "Star Dust") must appear before any title that does not start with
        // "Star" (e.g. "The Star", "Lone Star", "Bar Starz").
        bool seenNonStartingStar = false;
        int limit = Math.Min(200, records.Length);
        for (int i = 0; i < limit; i++)
        {
            var movie = movies[(int)records[i].DocumentId];
            string title = movie.Title;

            // "Starts with Star" = first token is exactly "Star"
            bool startsWithStar =
                title.StartsWith("Star", StringComparison.OrdinalIgnoreCase) &&
                (title.Length == 4 || !char.IsLetter(title[4]));

            if (!startsWithStar)
            {
                seenNonStartingStar = true;
            }
            else if (seenNonStartingStar)
            {
                Assert.Fail($"Title '{title}' starting with 'Star' appears after a non-'Star' title in the results.");
            }
        }

        Console.WriteLine($"\nVerified: Group A Score ({starKid.Score}) > Group B Score ({stardom.Score})");
    }

    [TestMethod]
    public void Sap_PrefersPrefixAtTitleStart()
    {
        var engine = GetEngine();

        var query = "sap";
        var result = engine.Search(new Query(query, 200));
        var records = result.Records;

        Assert.IsTrue(records.Length > 0, "Should find results for 'sap'");

        Console.WriteLine($"Search results for '{query}' (Top 20 shown):");
        for (int i = 0; i < Math.Min(20, records.Length); i++)
        {
            var record = records[i];
            var doc = engine.GetDocument(record.DocumentId);
            Console.WriteLine($"{doc!.IndexedText} - Score: {record.Score:F1}");
        }

        // Invariant for single-term query "sap":
        // Titles whose first token starts with "sap" (e.g. "Sapoot", "Sapphire",
        // "Sappho 68", "Sappy Holiday") must appear before any title whose first
        // token does not start with "sap" (e.g. "Mae Martin SAP", "The Saphead").
        bool seenNonSapStart = false;
        int sapLimit = Math.Min(200, records.Length);
        for (int i = 0; i < sapLimit; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            string title = doc!.IndexedText;
            string lower = title.ToLowerInvariant();

            // First token starts with "sap" if the very beginning of the title
            // is "sap" and the next character, if any, is not a letter.
            bool startsWithSap =
                lower.StartsWith("sap", StringComparison.Ordinal) &&
                (lower.Length == 3 || !char.IsLetter(lower[3]));

            if (!startsWithSap)
            {
                seenNonSapStart = true;
            }
            else if (seenNonSapStart)
            {
                Assert.Fail($"Title '{title}' with 'sap' prefix at start appears after a non-'sap' starting title in the results.");
            }
        }
    }
    
    [TestMethod]
    public void TheHear_PrefersHearse()
    {
        var engine = GetEngine();
        
        // Multi-term query: "The Hearse" (Perfect Doc, Prefix) should beat "Did You Hear..." (Noisy, Strict).
        // This tests that Perfect Document Match is prioritized for multi-term queries.
        var result = engine.Search(new Query("the hear", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0, "Should find results for 'the hear'");
        
        var topDoc = engine.GetDocument(records[0].DocumentId);
        string topTitle = topDoc!.IndexedText;
        Console.WriteLine($"Top Result: {topTitle}");
        
        Assert.AreEqual("The Hearse", topTitle, "The Hearse should be the top result for 'the hear'");
    }
    
    [TestMethod]
    public void Shwashan_FindsShawshank()
    {
        var engine = GetEngine();
        
        // Single-term fuzzy query with multiple typos.
        // "shwashank" has distance 2 from "shawshank" (swap 'w' and 'a', substitute 'w' for second 'w').
        // Should find "The Shawshank Redemption" as top result.
        var result = engine.Search(new Query("shwashan", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0, "Should find results for 'shwashan'");
        
        var topDoc = engine.GetDocument(records[0].DocumentId);
        string topTitle = topDoc!.IndexedText;
        Console.WriteLine($"Top Result: {topTitle}");
        
        Assert.IsTrue(topTitle.Contains("Shawshank", StringComparison.OrdinalIgnoreCase), 
            $"Expected Shawshank Redemption but got '{topTitle}'");
    }

    [TestMethod]
    public void EatrixF_PrefersBeatrixFarrand()
    {
        var engine = GetEngine();

        string[] queries =
        [
            "eatrix f",
            "eatrix fe",
            "eatrix fea",
            "eatrix fer",
        ];

        foreach (var query in queries)
        {
            var result = engine.Search(new Query(query, 10));
            var records = result.Records;

            Assert.IsTrue(records.Length > 0, $"Should find results for '{query}'");

            var topDoc = engine.GetDocument(records[0].DocumentId);
            string topTitle = topDoc!.IndexedText;
            Console.WriteLine($"Top Result for '{query}': {topTitle}");

            // Extract last term to reason about how much of the second word has been typed.
            var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string lastTerm = parts[^1];

            // Principle: once the user has provided a reasonably specific suffix
            // for the second token (>= 3 characters), the intended bigram
            // "Beatrix Farrand..." should dominate.
            if (lastTerm.Length >= 3)
            {
                Assert.IsTrue(
                    topTitle.Contains("Beatrix", StringComparison.OrdinalIgnoreCase) &&
                    topTitle.Contains("Farrand", StringComparison.OrdinalIgnoreCase),
                    $"Expected Beatrix Farrand movie at top for '{query}' but got '{topTitle}'");
            }
        }
    }

    [TestMethod]
    public void De_PrefersPrefixAtTitleStart()
    {
        var engine = GetEngine();

        var query = "de";
        var result = engine.Search(new Query(query, 200));
        var records = result.Records;

        Assert.IsTrue(records.Length > 0, "Should find results for 'de'");

        Console.WriteLine($"Search results for '{query}' (Top 20 shown):");
        for (int i = 0; i < Math.Min(20, records.Length); i++)
        {
            var record = records[i];
            var doc = engine.GetDocument(record.DocumentId);
            Console.WriteLine($"{doc!.IndexedText} - Score: {record.Score:F1}");
        }

        // Principle for single-term 'de':
        // Titles whose FIRST token starts with "de" (e.g. "Dear Dead Delilah",
        // "De De Pyaar De", "Deadly Descent") must appear before any title whose
        // first token does not start with "de" (e.g. "Intent to Destroy ...").
        bool seenNonDeStart = false;
        int limit = Math.Min(200, records.Length);
        for (int i = 0; i < limit; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            string title = doc!.IndexedText;
            string lower = title.ToLowerInvariant();

            // First token starts with "de" if the very beginning of the title
            // is "de" (case-insensitive).
            bool startsWithDe = lower.StartsWith("de", StringComparison.Ordinal);

            if (!startsWithDe)
            {
                seenNonDeStart = true;
            }
            else if (seenNonDeStart)
            {
                Assert.Fail($"Title '{title}' with first token starting with 'de' appears after a non-'de' starting title in the results.");
            }
        }
    }


    [TestMethod]
    public void Search_SingleLetter_ReturnsResults()
    {
        var engine = GetEngine();
        
        // "a" should match Aladdin, After, Alita...
        // This tests 1-letter query support with immediate prefix matching
        var result = engine.Search(new Query("a", 10));
        
        Assert.IsTrue(result.Records.Length > 0, "Should return results for single letter 'a'");
        
        // Verify results start with 'a'
        foreach (var record in result.Records.Take(5))
        {
            var doc = engine.GetDocument(record.DocumentId);
            Assert.IsNotNull(doc);
            var title = doc!.IndexedText.ToLowerInvariant();
            Assert.IsTrue(title.StartsWith("a") || title.Contains(" a"), 
                $"Result '{doc.IndexedText}' should contain word starting with 'a'");
        }
    }

    [TestMethod]
    public void SingleLetter_X_PrefersExactTitle()
    {
        var engine = GetEngine();
        
        var result = engine.Search(new Query("x", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0, "Should return results for single letter 'x'");
        
        var topDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(topDoc);
        Assert.AreEqual("X", topDoc!.IndexedText, "Exact title 'X' should be the top result for query 'x'");
    }

    [TestMethod]
    public void Search_TwoLetters_ReturnsResults()
    {
        var engine = GetEngine();
        // "th" should match Thor, The Twilight Saga, The Matrix...
        var sw = Stopwatch.StartNew();
        var result = engine.Search(new Query("th", 10));
        sw.Stop();

        Console.WriteLine($"Search 'th' took {sw.ElapsedMilliseconds}ms. Results: {result.Records.Length}");
        
        Assert.IsTrue(result.Records.Length > 0, "Should return results for two letters 'th'");
    }
    
    [TestMethod]
    public void Io_PrefersExactTitleOverPrefixes()
    {
        var engine = GetEngine();
        
        var result = engine.Search(new Query("io", 10));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0, "Should return results for 'io'");
        
        var topDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(topDoc);
        Assert.AreEqual("IO", topDoc!.IndexedText, "Exact title 'IO' should be the top result for query 'io'");
    }
    
    [TestMethod]
    public void Search_MixedTerms_LongAndShort_ReturnsCorrectResults()
    {
        var engine = GetEngine();
        // "san a" MUST behave consistently with precedence rules:
        // - Position #1: "San Andreas"
        // - Positions #2-4: other "San Andreas ..." variants
        var result = engine.Search(new Query("san a", 10));
        
        Console.WriteLine($"Search 'san a' returned {result.Records.Length} results");
        for (int i = 0; i < Math.Min(10, result.Records.Length); i++)
        {
            var doc = engine.GetDocument(result.Records[i].DocumentId);
            Console.WriteLine($"  [{i + 1}] [{result.Records[i].Score}] {doc?.IndexedText}");
        }
        
        Assert.IsTrue(result.Records.Length >= 3, "Should return at least 3 results for 'san a'");
        
        // There are exactly 3 "San Andreas" titles in the dataset:
        // 1. "San Andreas"
        // 2. "San Andreas Quake"
        // 3. "San Andreas Mega Quake"
        
        // #1 MUST be "San Andreas" (exact match, no subtitle)
        var doc1 = engine.GetDocument(result.Records[0].DocumentId);
        Assert.IsNotNull(doc1);
        Assert.AreEqual("San Andreas", doc1!.IndexedText, "Position #1 MUST be 'San Andreas'");
        
        // #2-3 MUST be the other two "San Andreas ..." variants
        for (int i = 1; i <= 2; i++)
        {
            var doc = engine.GetDocument(result.Records[i].DocumentId);
            Assert.IsNotNull(doc, $"Document at position {i + 1} should not be null");
            Assert.IsTrue(doc!.IndexedText.StartsWith("San Andreas"),
                $"Position #{i + 1} MUST be a 'San Andreas ...' variant, but was '{doc.IndexedText}'");
        }
    }

    [TestMethod]
    public void FellowshipOfTheRing_PrefersCorrectLotrMovie()
    {
        var engine = GetEngine();

        var result = engine.Search(new Query("fellowship of the ring", 10));
        var records = result.Records;

        Assert.IsTrue(records.Length >= 2, "Expected at least two results for 'fellowship of the ring'.");

        var firstDoc = engine.GetDocument(records[0].DocumentId);
        var secondDoc = engine.GetDocument(records[1].DocumentId);

        Assert.IsNotNull(firstDoc, "First result document should not be null.");
        Assert.IsNotNull(secondDoc, "Second result document should not be null.");

        string firstTitle = firstDoc!.IndexedText;
        string secondTitle = secondDoc!.IndexedText;

        // Lock-in: the first movie in the Lord of the Rings trilogy must be top for this exact title query.
        Assert.AreEqual("The Lord of the Rings 1 - The Fellowship of the Ring", firstTitle);

        // And it must have a strictly higher score than the next-best result.
        Assert.IsTrue(records[0].Score > records[1].Score,
            $"Expected '{firstTitle}' to have a higher score than '{secondTitle}'.");
    }

    [TestMethod]
    public void TheMatri_FindsMatrixSequels()
    {
        var engine = GetEngine();
        
        // Query: "the matri"
        // Intent: "The Matrix" and its sequels.
        // "Matri" is a clean prefix for "Matrix".
        // "Martian", "Marine" should be lower because they are fuzzy/noisy matches or weaker prefix matches.
        
        var result = engine.Search(new Query("the matri", 20));
        var records = result.Records;
        
        Assert.IsTrue(records.Length > 0, "Should find results");
        
        // Check relative ordering
        int matrixIndex = -1;
        int reloadedIndex = -1;
        int revolutionsIndex = -1;
        int martianIndex = -1;
        int marineIndex = -1;
        
        for (int i = 0; i < records.Length; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            string title = doc!.IndexedText;
            
            if (title == "The Matrix") matrixIndex = i;
            else if (title == "The Matrix Reloaded") reloadedIndex = i;
            else if (title == "The Matrix Revolutions") revolutionsIndex = i;
            else if (title == "The Martian") martianIndex = i;
            else if (title == "The Marine") marineIndex = i;
        }
        
        // 1. The Matrix (Exact Prefix) should be #1 or very high
        Assert.IsTrue(matrixIndex >= 0, "The Matrix should be found");
        Assert.IsTrue(matrixIndex <= 2, $"The Matrix should be top ranked (found at {matrixIndex})");
        
        // 2. Matrix sequels (Clean Prefix "The Matri...") should beat Martian/Marine (Fuzzy/Noisy)
        // "the matri" -> "The Matrix..." is a Match Quality of (Exact, Prefix).
        // "the matri" -> "The Martian" is (Exact, Fuzzy?) or (Exact, None).
        
        if (martianIndex >= 0)
        {
            if (reloadedIndex >= 0)
                Assert.IsTrue(reloadedIndex < martianIndex, $"The Matrix Reloaded ({reloadedIndex}) should rank higher than The Martian ({martianIndex})");
            if (revolutionsIndex >= 0)
                Assert.IsTrue(revolutionsIndex < martianIndex, $"The Matrix Revolutions ({revolutionsIndex}) should rank higher than The Martian ({martianIndex})");
        }
        
        if (marineIndex >= 0)
        {
            if (reloadedIndex >= 0)
                Assert.IsTrue(reloadedIndex < marineIndex, $"The Matrix Reloaded ({reloadedIndex}) should rank higher than The Marine ({marineIndex})");
        }
    }

    [TestMethod]
    public void AsAm_PrefersAsIAm()
    {
        var engine = GetEngine();
        var movies = GetMovies();

        var result = engine.Search(new Query("as am", 20));
        var records = result.Records;

        Assert.IsTrue(records.Length > 0, "Expected at least one result for 'as am'.");

        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(firstDoc, "First result document should not be null.");

        string firstTitle = firstDoc!.IndexedText;
        Console.WriteLine("Results for 'as am':");
        for (int i = 0; i < records.Length && i < 10; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
        }

        // Lock-in: "As I Am" must be the top result for the short, two-word query "as am".
        Assert.AreEqual("As I Am", firstTitle,
            "Query 'as am' should prefer the title 'As I Am' that contains both query tokens as whole words.");
    }
}

/// <summary>
/// Tests for short query behavior with small ad-hoc datasets to verify
/// that even partial character matches return results.
/// </summary>
[TestClass]
public class ShortQueryAdHocTests
{
    [TestMethod]
    public void ShortQuery_TwoLetters_ReturnsPartialMatch()
    {
        // Create a minimal dataset
        var engine = SearchEngine.CreateDefault();
        var documents = new List<Document>
        {
            new Document(1, "cat"),
            new Document(2, "dog"),
            new Document(3, "ape")
        };
        
        engine.IndexDocuments(documents);
        
        // Search for "va" - should return results containing 'v' or 'a'
        // "ape" and "cat" both contain 'a', making them partial matches
        // "ape" should rank higher because 'a' appears at word start
        var result = engine.Search(new Query("va", 10));
        
        Console.WriteLine($"Search 'va' returned {result.Records.Length} results");
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Console.WriteLine($"  - {doc?.IndexedText} (score: {record.Score})");
        }
        
        Assert.IsTrue(result.Records.Length > 0, "Should return at least one result for 'va'");
        
        // Verify results contain documents with matching characters
        var topResult = result.Records[0];
        var topDoc = engine.GetDocument(topResult.DocumentId);
        Assert.IsTrue(topDoc?.IndexedText == "ape" || topDoc?.IndexedText == "cat", 
            "'ape' or 'cat' should be top result for query 'va' (both contain 'a')");
        
        // Verify "cat" has better score than other results (if any)
        if (result.Records.Length > 1)
        {
            for (int i = 1; i < result.Records.Length; i++)
            {
                Assert.IsTrue(topResult.Score >= result.Records[i].Score,
                    $"Top result 'cat' (score: {topResult.Score}) should have better or equal score than other results (score: {result.Records[i].Score})");
            }
        }
    }
    
    [TestMethod]
    public void ShortQuery_TwoLetters_MultiplePartialMatches()
    {
        var engine = SearchEngine.CreateDefault();
        var documents = new List<Document>
        {
            new Document(1, "apple"),
            new Document(2, "banana"),
            new Document(3, "cherry"),
            new Document(4, "grape"),
            new Document(5, "orange")
        };
        
        engine.IndexDocuments(documents);
        
        // Search for "ra" - should return "grape", "orange", "cherry" (all contain 'r' or 'a')
        var result = engine.Search(new Query("ra", 10));
        
        Console.WriteLine($"Search 'ra' returned {result.Records.Length} results");
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Console.WriteLine($"  - {doc?.IndexedText} (score: {record.Score})");
        }
        
        Assert.IsTrue(result.Records.Length > 0, "Should return results for 'ra'");
        
        // Should find words containing 'r' and/or 'a'
        HashSet<string> foundWords = new HashSet<string>();
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            if (doc != null)
            {
                foundWords.Add(doc.IndexedText);
            }
        }
        
        // At minimum, should find grape (contains both 'r' and 'a')
        Assert.IsTrue(foundWords.Contains("grape") || foundWords.Contains("orange") || foundWords.Contains("cherry"),
            "Should return words containing 'r' and/or 'a'");
    }
    
    [TestMethod]
    public void ShortQuery_SingleLetter_ReturnsAllMatches()
    {
        var engine = SearchEngine.CreateDefault();
        var documents = new List<Document>
        {
            new Document(1, "alpha"),
            new Document(2, "beta"),
            new Document(3, "gamma"),
            new Document(4, "delta")
        };
        
        engine.IndexDocuments(documents);
        
        // Search for "a" - should return "alpha", "gamma", "delta", "beta" (all contain 'a')
        var result = engine.Search(new Query("a", 10));
        
        Console.WriteLine($"Search 'a' returned {result.Records.Length} results");
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Console.WriteLine($"  - {doc?.IndexedText} (score: {record.Score})");
        }
        
        Assert.IsTrue(result.Records.Length > 0, "Should return results for single letter 'a'");
        
        // All words contain 'a', so should get all 4 results
        Assert.IsTrue(result.Records.Length >= 3, "Should return at least 3 words containing 'a'");
    }
    
    [TestMethod]
    public void ShortQuery_TwoLetters_NoExactMatch_ReturnsPartial()
    {
        var engine = SearchEngine.CreateDefault();
        var documents = new List<Document>
        {
            new Document(1, "table"),
            new Document(2, "chair"),
            new Document(3, "desk"),
            new Document(4, "lamp")
        };
        
        engine.IndexDocuments(documents);
        
        // Search for "ab" - exact "ab" doesn't appear, but "table" contains both 'a' and 'b'
        var result = engine.Search(new Query("ab", 10));
        
        Console.WriteLine($"Search 'ab' returned {result.Records.Length} results");
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            Console.WriteLine($"  - {doc?.IndexedText} (score: {record.Score})");
        }
        
        Assert.IsTrue(result.Records.Length > 0, "Should return results for 'ab' even without exact match");
        
        // Should ideally find "table" (contains 'ab' as substring)
        bool foundTable = false;
        foreach (var record in result.Records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            if (doc?.IndexedText == "table")
            {
                foundTable = true;
                break;
            }
        }
        
        Assert.IsTrue(foundTable, "Should return 'table' when searching for 'ab'");
    }
}

[TestClass]
public class MovieSearchParityTests : MovieSearchParityTestsBase
{
    private static SearchEngine? _movieEngine;
    private static List<MovieRecord> _movies = new();
    
    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // Build the movie index once for all tests in this class
        _movies = LoadMovies();
        _movieEngine = BuildMovieEngine(_movies);
    }
    
    protected override SearchEngine GetEngine() => _movieEngine!;
    protected override List<MovieRecord> GetMovies() => _movies;

    private static List<MovieRecord> LoadMovies()
    {
        // Use the same CSV as the benchmark/example projects
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.ToLower(),
            MissingFieldFound = null
        };

        string baseDir = AppContext.BaseDirectory;
        string directPath = Path.Combine(baseDir, "movies.csv");

        string filePath = directPath;

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);
        return csv.GetRecords<MovieRecord>().ToList();
    }
    
    private static SearchEngine BuildMovieEngine(List<MovieRecord> movies)
    {
        var engine = SearchEngine.CreateDefault();

        var documents = movies.Select((m, i) =>
            new Document((long)i, m.Title)).ToList();

        engine.IndexDocuments(documents);
        return engine;
    }
}
