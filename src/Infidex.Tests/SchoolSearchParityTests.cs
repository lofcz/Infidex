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
    /// First school parity test: fuzzy / prefix behavior for a Czech-like name.
    /// </summary>
    [TestMethod]
    public void Sciozlin_Query_ReturnsSchool()
    {
        var engine = GetEngine();

        var result = engine.Search(new Query("sciozlín", 20));
        var records = result.Records;

        Assert.IsTrue(records.Length > 0, "Should return some schools for 'sciozlín'.");

        var topDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsNotNull(topDoc);

        string topTitle = topDoc!.IndexedText;
        Console.WriteLine($"Top Result for 'sciozlín': {topTitle}");

        // Ensure that the ScioŠkola Zlín school is ranked above the ScioŠkola Kolín school when
        // searching for \"sciozlín\". Both names share the ScioŠkola prefix,
        // but the query's suffix \"zlín\" should clearly favor the Zlín school.
        int zlinIndex = -1;
        int kolinIndex = -1;

        for (int i = 0; i < records.Length; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            string title = doc!.IndexedText;

            if (title.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase))
                zlinIndex = i;
            else if (title.Contains("ScioŠkola Kolín", StringComparison.OrdinalIgnoreCase))
                kolinIndex = i;
        }

        Assert.IsTrue(zlinIndex >= 0, "Should find ScioŠkola Zlín");

        // If Kolín appears at all, it must not outrank Zlín.
        if (kolinIndex >= 0)
        {
            Assert.IsTrue(zlinIndex < kolinIndex,
                $"ScioŠkola Zlín should rank above ScioŠkola Kolín for 'sciozlín' (Zlín at {zlinIndex}, Kolín at {kolinIndex}).");
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

        int ceskaLipaIndex = -1;
        int ceskyBrodIndex = -1;

        for (int i = 0; i < records.Length; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            string title = doc!.IndexedText;

            if (title.Contains("Česká Lípa", StringComparison.OrdinalIgnoreCase))
                ceskaLipaIndex = i;
            else if (title.Contains("Český Brod", StringComparison.OrdinalIgnoreCase))
                ceskyBrodIndex = i;
        }

        Assert.IsTrue(ceskaLipaIndex >= 0, "Should find the Česká Lípa school");

        // If the Český Brod school appears at all, it must not outrank Česká Lípa.
        if (ceskyBrodIndex >= 0)
        {
            Assert.IsTrue(ceskaLipaIndex < ceskyBrodIndex,
                $"Česká Lípa school should rank above Český Brod for '{query}' (Česká Lípa at {ceskaLipaIndex}, Český Brod at {ceskyBrodIndex}).");
        }
    }
}

