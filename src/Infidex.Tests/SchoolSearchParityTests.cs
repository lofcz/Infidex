using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Infidex.Api;
using Infidex.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Infidex.Tests;

/// <summary>
/// Shared helpers for school search parity tests.
/// Mirrors the movie parity setup but indexes school names from a JSON fixture.
/// </summary>
[TestClass]
public class SchoolSearchParityTests
{
    private static SearchEngine? _schoolEngine;
    private static List<string> _schoolNames = new();

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _schoolNames = LoadSchoolNames();
        _schoolEngine = BuildSchoolEngine(_schoolNames);
    }

    private static List<string> LoadSchoolNames()
    {
        // Expect schools.json to be copied next to the test binaries (same pattern as movies.csv).
        string baseDir = AppContext.BaseDirectory;
        string filePath = Path.Combine(baseDir, "schools.json");

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Could not find schools.json at '{filePath}'");

        string json = File.ReadAllText(filePath);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var records = JsonSerializer.Deserialize<List<SchoolRecord>>(json, options) ?? new List<SchoolRecord>();

        return records
            .Select(r => r.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    private sealed class SchoolRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Cin { get; set; } = string.Empty;
        public string Sin { get; set; } = string.Empty;
    }

    private static SearchEngine BuildSchoolEngine(List<string> schoolNames)
    {
        var engine = SearchEngine.CreateDefault();

        var documents = schoolNames
            .Select((name, i) => new Document((long)i, name))
            .ToList();

        engine.IndexDocuments(documents);
        return engine;
    }

    private static SearchEngine GetEngine() => _schoolEngine!;

    /// <summary>
    /// "sciozlí" should give ScioŠkola Zlín a HIGHER score than ScioŠkola Kolín.
    /// The query suffix "zlí" strongly matches "Zlín" but only weakly matches "Kolín" (just "lí").
    /// </summary>
    [TestMethod]
    public void Sciozli_ZlinScoresHigherThanKolin()
    {
        var engine = GetEngine();

        var result = engine.Search(new Query("sciozlí", 20));
        var records = result.Records;

        Console.WriteLine($"Search results for 'sciozlí' ({records.Length} results):");
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
        }

        Assert.IsTrue(records.Length >= 1, "Should return at least 1 school for 'sciozlí'.");

        // Requirement 1: First place must be ScioŠkola Zlín
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsTrue(firstDoc!.IndexedText.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase),
            $"First result should be ScioŠkola Zlín, but was: {firstDoc.IndexedText}");

        // Find scores for both schools
        int zlinScore = -1;
        int kolinScore = -1;

        foreach (var record in records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            string title = doc!.IndexedText;

            if (title.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase))
                zlinScore = record.Score;
            else if (title.Contains("ScioŠkola Kolín", StringComparison.OrdinalIgnoreCase))
                kolinScore = record.Score;
        }

        Assert.IsTrue(zlinScore > 0, "Should find ScioŠkola Zlín");

        // Requirement 2: If ScioŠkola Kolín appears, it must have a lesser score
        if (kolinScore > 0)
        {
            Assert.IsTrue(zlinScore > kolinScore,
                $"ScioŠkola Zlín (score={zlinScore}) should score HIGHER than ScioŠkola Kolín (score={kolinScore}) for 'sciozlí'. " +
                $"The 'zlí' suffix strongly matches 'Zlín' but only weakly matches 'Kolín'.");
        }
    }

    /// <summary>
    /// "scio škola ve zlíně" should rank ScioŠkola Zlín first.
    /// Even though "Církevní... ve Zlíně" matches more common terms (škola, ve, zlíně),
    /// the rare term "scio" should strongly favor ScioŠkola.
    /// This tests proper IDF-style term weighting where rare terms matter more.
    /// </summary>
    [TestMethod]
    public void ScioSkolaVeZline_PrefersScioSkola()
    {
        var engine = GetEngine();

        var result = engine.Search(new Query("scio škola ve zlíně", 20));
        var records = result.Records;

        Console.WriteLine($"Search results for 'scio škola ve zlíně' ({records.Length} results):");
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
        }

        Assert.IsTrue(records.Length >= 1, "Should return at least 1 school.");

        // Requirement: ScioŠkola Zlín should be first because "scio" is a rare/unique term
        // that strongly indicates user intent, even if other docs match more common terms
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsTrue(firstDoc!.IndexedText.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase),
            $"First result should be ScioŠkola Zlín (rare term 'scio' should dominate), but was: {firstDoc.IndexedText}");
    }

    /// <summary>
    /// "sciozlínskáškola" contains "zlín" as a substring, so ScioŠkola Zlín should rank first.
    /// ScioŠkola Kolín should score lower (if it appears) since "kolín" is not in the query.
    /// </summary>
    [TestMethod]
    public void Sciozlinskaskola_ZlinRanksFirst()
    {
        var engine = GetEngine();

        var result = engine.Search(new Query("sciozlínskáškola", 20));
        var records = result.Records;

        Console.WriteLine($"Search results for 'sciozlínskáškola' ({records.Length} results):");
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
        }

        Assert.IsTrue(records.Length >= 1, "Should return at least 1 school for 'sciozlínskáškola'.");

        // Requirement 1: First place must be ScioŠkola Zlín (query contains "zlín")
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsTrue(firstDoc!.IndexedText.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase),
            $"First result should be ScioŠkola Zlín (query contains 'zlín'), but was: {firstDoc.IndexedText}");

        // Find scores for both schools
        int zlinScore = -1;
        int kolinScore = -1;

        foreach (var record in records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            string title = doc!.IndexedText;

            if (title.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase))
                zlinScore = record.Score;
            else if (title.Contains("ScioŠkola Kolín", StringComparison.OrdinalIgnoreCase))
                kolinScore = record.Score;
        }

        Assert.IsTrue(zlinScore > 0, "Should find ScioŠkola Zlín");

        // Requirement 2: If ScioŠkola Kolín appears, it must have a lesser score
        if (kolinScore > 0)
        {
            Assert.IsTrue(zlinScore > kolinScore,
                $"ScioŠkola Zlín (score={zlinScore}) should score HIGHER than ScioŠkola Kolín (score={kolinScore}) for 'sciozlínskáškola'. " +
                $"The query contains 'zlín' but not 'kolín'.");
        }
    }

    /// <summary>
    /// "sciozlín" is a concatenation of "scio" + "zlín", so ScioŠkola Zlín should rank first.
    /// The query clearly contains both components.
    /// </summary>
    [TestMethod]
    public void Sciozlin_Query_ReturnsSchool()
    {
        var engine = GetEngine();

        var result = engine.Search(new Query("sciozlín", 20));
        var records = result.Records;

        Console.WriteLine($"Search results for 'sciozlín' ({records.Length} results):");
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
        }

        Assert.IsTrue(records.Length > 0, "Should return some schools for 'sciozlín'.");

        // ScioŠkola Zlín must be the TOP result - "sciozlín" = "scio" + "zlín"
        var topDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(topDoc);
        Assert.IsTrue(topDoc!.IndexedText.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase),
            $"First result should be ScioŠkola Zlín for 'sciozlín', but was: {topDoc.IndexedText}");

        // If Kolín appears, it must have a lower score
        int zlinScore = records[0].Score;
        int kolinScore = -1;

        foreach (var record in records)
        {
            var doc = engine.GetDocument(record.DocumentId);
            if (doc!.IndexedText.Contains("ScioŠkola Kolín", StringComparison.OrdinalIgnoreCase))
            {
                kolinScore = record.Score;
                break;
            }
        }

        if (kolinScore >= 0)
        {
            Assert.IsTrue(zlinScore > kolinScore,
                $"ScioŠkola Zlín (score={zlinScore}) should score higher than ScioŠkola Kolín (score={kolinScore}).");
        }
    }

    [TestMethod]
    public void TyrsovkaCeskaLipa_PrefersCeskaLipaSchool()
    {
        var engine = GetEngine();

        var query = "tyršovka česká lípa";
        var result = engine.Search(new Query(query, 20));
        var records = result.Records;

        Assert.IsTrue(records.Length > 0, $"Should return some schools for '{query}'.");

        Console.WriteLine($"Search results for '{query}' (Top 10 shown):");
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"{doc!.IndexedText} - Score: {records[i].Score}");
        }

        // Target: Tyrš primary school in Česká Lípa
        const string targetName = "Základní škola Dr. Miroslava Tyrše, Česká Lípa, Mánesova 1526, příspěvková organizace";

        int targetIndex = -1;
        int targetScore = -1;

        for (int i = 0; i < records.Length; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            string title = doc!.IndexedText;

            if (title.Contains(targetName, StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = i;
                targetScore = records[i].Score;
                break;
            }
        }

        Assert.IsTrue(targetIndex >= 0, $"Should find '{targetName}' for '{query}'.");

        // Requirement 1: Desired Česká Lípa Tyrš primary school must be first.
        Assert.AreEqual(0, targetIndex,
            $"'{targetName}' must be the TOP result for '{query}', but appeared at index {targetIndex}.");

        // Requirement 2: It must have a strictly higher score than anything else.
        for (int i = 1; i < records.Length; i++)
        {
            Assert.IsTrue(targetScore > records[i].Score,
                $"'{targetName}' (score={targetScore}) should score higher than '{engine.GetDocument(records[i].DocumentId)!.IndexedText}' (score={records[i].Score}) for '{query}'.");
        }
    }
}

