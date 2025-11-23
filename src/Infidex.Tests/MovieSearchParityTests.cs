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
            
        // Optional: Verify internal consistency
        if (starDust.Score > 0)
             Assert.AreEqual(starKid.Score, starDust.Score, "Star Kid and Star Dust should have same score");
             
        if (starlift.Score > 0)
             Assert.AreEqual(stardom.Score, starlift.Score, "Stardom and Starlift should have same score");

        Console.WriteLine($"\nVerified: Group A Score ({starKid.Score}) > Group B Score ({stardom.Score})");
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
