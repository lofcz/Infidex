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

[TestClass]
public class MovieSearchParityTests
{
    private static SearchEngine? _movieEngine;
    
    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // Build the movie index once for all tests in this class
        _movieEngine = BuildMovieEngine();
    }
    
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
    
    private static SearchEngine BuildMovieEngine()
    {
        var engine = SearchEngine.CreateDefault();
        var movies = LoadMovies();

        var documents = movies.Select((m, i) =>
            new Document((long)i, m.Title)).ToList();

        engine.IndexDocuments(documents);
        return engine;
    }

    [TestMethod]
    public void RedemptionSh_PrefersShawshankOverOtherRedemptionTitles()
    {
        var engine = _movieEngine!;
        
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
        var engine = _movieEngine!;
        
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
        var engine = _movieEngine!;
        
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
        var engine = _movieEngine!;
        
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
        var engine = _movieEngine!;
        
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
}


