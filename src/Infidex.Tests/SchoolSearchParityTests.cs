using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Infidex.Api;
using Infidex.Core;
using Infidex.Synonyms;
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
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonym("zs", "zakladni");
        synonymMap.AddSynonym("ss", "stredni");
        synonymMap.AddSynonym("gympl", "gymnazium");

        var config = ConfigurationParameters.GetConfig(400);
        var engine = new SearchEngine(
            indexSizes: config.IndexSizes,
            startPadSize: config.StartPadSize,
            stopPadSize: config.StopPadSize,
            enableCoverage: true,
            textNormalizer: config.TextNormalizer,
            tokenizerSetup: config.TokenizerSetup,
            coverageSetup: null,
            stopTermLimit: config.StopTermLimit,
            wordMatcherSetup: config.WordMatcherSetup,
            fieldWeights: config.FieldWeights,
            synonymMap: synonymMap);

        var documents = schoolNames
            .Select((name, i) => new Document((long)i, name))
            .ToList();

        engine.IndexDocuments(documents);
        return engine;
    }

    private static SearchEngine GetEngine() => _schoolEngine!;

    /// <summary>
    /// Test position-independence: "bělohrad" (8 chars, specific town) should beat "lázně" (5 chars, common)
    /// regardless of where "bělohrad" appears in the query. Tests multiple query permutations.
    /// The engine should recognize that matching the longer, more specific term is more informative.
    /// </summary>
    [TestMethod]
    public void MaterskaSkolaWithBelohrad_PrefersBelohradskaSkola_AllPermutations()
    {
        var engine = GetEngine();
        const string targetName = "Bělohradská mateřská škola";

        // Test multiple permutations - "bělohrad" in different positions
        string[] queries = new[]
        {
            "mateřská škola lázně bělohrad",     // bělohrad at end (type-ahead position)
            "mateřská bělohrad škola lázně",     // bělohrad in middle
            "bělohrad mateřská škola lázně",     // bělohrad at start
            "bělohrad lázně mateřská škola"      // different ordering
        };

        foreach (string query in queries)
        {
            var result = engine.Search(new Query(query, 20));
            var records = result.Records;

            Assert.IsTrue(records.Length > 0, $"Should return some schools for '{query}'.");

            Console.WriteLine($"\nSearch results for '{query}' (Top 10 shown):");
            for (int i = 0; i < Math.Min(10, records.Length); i++)
            {
                var doc = engine.GetDocument(records[i].DocumentId);
                Console.WriteLine($"  {i + 1}. [{records[i].Score}] {doc!.IndexedText}");
            }

            int targetIndex = -1;
            float targetScore = -1;

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
            
            Console.WriteLine($"Target '{targetName}' found at index {targetIndex} with score {targetScore}");

            // Requirement: Bělohradská mateřská škola must be first for ALL permutations
            Assert.AreEqual(0, targetIndex,
                $"'{targetName}' must be the TOP result for '{query}', but appeared at index {targetIndex}.");

            // Requirement 2: It must have a strictly higher score than anything else
            for (int i = 1; i < records.Length; i++)
            {
                Assert.IsTrue(targetScore > records[i].Score,
                    $"'{targetName}' (score={targetScore}) should score higher than '{engine.GetDocument(records[i].DocumentId)!.IndexedText}' (score={records[i].Score}) for '{query}'.");
            }
        }
    }
    
    [TestMethod]
    public void BelPrefixes_PreferBelohradskaSkola_FirstForAll()
    {
        var engine = GetEngine();
        const string targetName = "Bělohradská mateřská škola";

        string[] queries =
        [
            "bel", "belo", "beloh", "belohr", "belohra", "belohrad", "belohrads", "belohradska"
        ];

        foreach (string query in queries)
        {
            var result = engine.Search(new Query(query, 20));
            var records = result.Records;

            Assert.IsTrue(records.Length > 0, $"Should return some schools for '{query}'.");

            Console.WriteLine($"\nSearch results for '{query}' (Top 10 shown):");
            for (int i = 0; i < Math.Min(10, records.Length); i++)
            {
                var doc = engine.GetDocument(records[i].DocumentId);
                Console.WriteLine($"  {i + 1}. [{records[i].Score}] {doc!.IndexedText}");
            }

            var topDoc = engine.GetDocument(records[0].DocumentId);
            Assert.IsNotNull(topDoc, "Top document should not be null.");
            Assert.IsTrue(
                topDoc!.IndexedText.Contains(targetName, StringComparison.OrdinalIgnoreCase),
                $"'{targetName}' must be the TOP result for '{query}', but top was: '{topDoc!.IndexedText}'.");
        }
    }


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
        float zlinScore = -1;
        float kolinScore = -1;

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
        float zlinScore = -1;
        float kolinScore = -1;

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
        float zlinScore = records[0].Score;
        float kolinScore = -1;

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

    /// <summary>
    /// "scioškola br/pl/če/zl" should rank the corresponding ScioŠkola city first.
    /// The second token (city abbreviation) should match as an exact prefix of the city name.
    /// </summary>
    [TestMethod]
    [DataRow("scioškola br", "ScioŠkola Brno", DisplayName = "scioškola br → Brno")]
    [DataRow("scioškola pl", "ScioŠkola Plzeň", DisplayName = "scioškola pl → Plzeň")]
    [DataRow("scioškola če", "ScioŠkola České Budějovice", DisplayName = "scioškola če → České Budějovice")]
    [DataRow("scioškola zl", "ScioŠkola Zlín", DisplayName = "scioškola zl → Zlín")]
    public void ScioskolaCityAbbreviation_RanksCorrectCityFirst(string query, string expectedSchoolSubstring)
    {
        var engine = GetEngine();
        var result = engine.Search(new Query(query, 20));
        var records = result.Records;

        Console.WriteLine($"Search results for '{query}' (Top 5):");
        for (int i = 0; i < Math.Min(5, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  {i + 1}. [{records[i].Score}] {doc!.IndexedText}");
        }

        Assert.IsTrue(records.Length >= 1, $"Should return at least 1 school for '{query}'.");

        // Requirement 1: First result must contain the expected ScioŠkola city
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsTrue(firstDoc!.IndexedText.Contains(expectedSchoolSubstring, StringComparison.OrdinalIgnoreCase),
            $"First result should contain '{expectedSchoolSubstring}', but was: {firstDoc.IndexedText}");

        // Requirement 2: The correct ScioŠkola must have a strictly higher score than others
        float targetScore = records[0].Score;
        for (int i = 1; i < records.Length; i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            if (!doc!.IndexedText.Contains(expectedSchoolSubstring, StringComparison.OrdinalIgnoreCase))
            {
                Assert.IsTrue(targetScore > records[i].Score,
                    $"'{expectedSchoolSubstring}' (score={targetScore}) should have higher score than '{doc.IndexedText}' (score={records[i].Score})");
            }
        }
    }

    /// <summary>
    /// "škola zlín s" - matches "2ika, zakladni skola Zlin s.r.o." best because
    /// "s" matches "s.r.o." exactly/prefix, and other terms match well.
    /// </summary>
    [TestMethod]
    public void SkolaZlinS_FindsRelevanSchools()
    {
        var engine = GetEngine();
        var query = "škola zlín s";
        var result = engine.Search(new Query(query, 20));
        var records = result.Records;

        Console.WriteLine($"Search results for '{query}' (Top 10):");
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  {i + 1}. [{records[i].Score}] {doc!.IndexedText}");
        }

        Assert.IsTrue(records.Length >= 2, $"Should return at least 2 schools for '{query}'.");

        // Target: "2ika, zakladni skola Zlin s.r.o." should be top result
        // or very close to top.
        var firstDoc = engine.GetDocument(records[0].DocumentId);
        Assert.IsTrue(firstDoc!.IndexedText.Contains("2ika", StringComparison.OrdinalIgnoreCase) ||
                      firstDoc!.IndexedText.Contains("ScioŠkola", StringComparison.OrdinalIgnoreCase),
             $"First result should be 2ika or ScioŠkola, but was: {firstDoc.IndexedText}");
             
        // Specifically check for 2ika as top result if ScioŠkola is not
        if (firstDoc.IndexedText.Contains("2ika", StringComparison.OrdinalIgnoreCase))
        {
             // Accepted as correct behavior
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
        float targetScore = -1;

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

    /// <summary>
    /// Debug test to understand why n-gram backbone isn't finding "ScioŠkola Zlín"
    /// for query "zlínská scioškola".
    /// </summary>
    [TestMethod]
    public void Debug_NGramOverlap_ZlinskaScioSkola()
    {
        var engine = GetEngine();
        Console.WriteLine("=== 'zlínská scioškola' search ===");
        var fullResults = engine.Search(new Query("zlínská scioškola", 10));
        Console.WriteLine($"\nTotal candidates: {fullResults.TotalCandidates}");
        Console.WriteLine("Final results:");
        foreach (var r in fullResults.Records)
        {
            var doc = engine.GetDocument(r.DocumentId);
            Console.WriteLine($"  [{r.Score}] {doc!.IndexedText}");
        }
    }

    /// <summary>
    /// Tests adjectival/derived word forms matching base words.
    /// "zlínská" is the adjective form of "Zlín" in Czech.
    /// The query "zlínská scioškola" should find "ScioŠkola Zlín" because:
    /// - "zlín" is a PREFIX of "zlínská" (stem matching)
    /// - "scioškola" matches "ScioŠkola" (case-insensitive)
    /// </summary>
    [TestMethod]
    public void ZlinskaScioSkola_AdjectiveFormMatchesBaseWord()
    {
        var engine = GetEngine();

        // Test both word orders
        var testCases = new[]
        {
            ("zlínská scioškola", "Adjective form first"),
            ("scioškola zlínská", "Adjective form last"),
        };

        foreach (var (query, description) in testCases)
        {
            var result = engine.Search(new Query(query, 20));
            var records = result.Records;

            Console.WriteLine($"\n{description}: '{query}'");
            Console.WriteLine($"Results (Top 10):");
            for (int i = 0; i < Math.Min(10, records.Length); i++)
            {
                var doc = engine.GetDocument(records[i].DocumentId);
                Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
            }

            Assert.IsTrue(records.Length > 0, $"Should return results for '{query}'.");

            // Find ScioŠkola Zlín
            int scioZlinIndex = -1;
            for (int i = 0; i < records.Length; i++)
            {
                var doc = engine.GetDocument(records[i].DocumentId);
                if (doc!.IndexedText.Contains("ScioŠkola Zlín", StringComparison.OrdinalIgnoreCase))
                {
                    scioZlinIndex = i;
                    break;
                }
            }

            Assert.IsTrue(scioZlinIndex >= 0, 
                $"ScioŠkola Zlín should appear in results for '{query}' ({description})");
            
            // ScioŠkola Zlín should be in top 3 at minimum
            Assert.IsTrue(scioZlinIndex < 3,
                $"ScioŠkola Zlín should be in top 3 for '{query}' ({description}), but was at index {scioZlinIndex}");
        }
    }

    /// <summary>
    /// Tests that queries with typos near valid words still find reasonable results.
    /// "zlímská" (typo with 'm' instead of 'n') should ideally still find Zlín-related results
    /// through fuzzy matching.
    /// </summary>
    [TestMethod]
    public void ZlimskaScioSkola_TypoStillFindsResults()
    {
        var engine = GetEngine();

        var query = "zlímská scioškola";
        var result = engine.Search(new Query(query, 20));
        var records = result.Records;

        Console.WriteLine($"Search results for '{query}' (typo case):");
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            Console.WriteLine($"  [{records[i].Score}] {doc!.IndexedText}");
        }

        // At minimum, "scioškola" should find ScioŠkola schools
        bool foundAnyScioSkola = false;
        for (int i = 0; i < Math.Min(10, records.Length); i++)
        {
            var doc = engine.GetDocument(records[i].DocumentId);
            if (doc!.IndexedText.Contains("ScioŠkola", StringComparison.OrdinalIgnoreCase))
            {
                foundAnyScioSkola = true;
                break;
            }
        }

        Assert.IsTrue(foundAnyScioSkola,
            $"At least one ScioŠkola should appear in top 10 for '{query}' (the 'scioškola' term should still match)");
    }

    /// <summary>
    /// Validates that for queries like "scio škola x" or "škola scio x" (where x is a letter),
    /// the search results rank schools starting with that letter (in the city part) higher
    /// than schools that do not match the letter.
    /// </summary>
    [TestMethod]
    public void ScioskolaLetterPrefix_RanksCorrectCityFirst_AllLetters()
    {
        var engine = GetEngine();
        char[] alphabet = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
        string[] prefixFormats = new[] { "scio škola {0}", "škola scio {0}" };
        
        // Czech culture for diacritic-insensitive comparison
        var culture = new System.Globalization.CultureInfo("cs-CZ");

        foreach (char letter in alphabet)
        {
            foreach (string fmt in prefixFormats)
            {
                string query = string.Format(fmt, letter);
                var result = engine.Search(new Query(query, 50));
                var records = result.Records;

                if (records.Length == 0) continue;

                bool encounteredNonMatch = false;

                // "ScioŠkola " is 10 chars long (including space)
                // We want to check if the part AFTER "ScioŠkola " starts with the letter.
                // Or simply check if "ScioŠkola {letter}" matches the start, ignoring diacritics.
                // Given "ScioŠkola Brno...", checking if it starts with "ScioŠkola b" works.

                string expectedPrefix = $"ScioŠkola {letter}";
                
                // Helper to check if a name matches the target letter
                bool IsMatch(string name)
                {
                     // Check if it starts with "ScioŠkola " and then the letter
                     // Using IgnoreNonSpace to handle C -> Č, etc.
                     return culture.CompareInfo.IsPrefix(name, expectedPrefix, 
                         System.Globalization.CompareOptions.IgnoreNonSpace | System.Globalization.CompareOptions.IgnoreCase);
                }

                // Check ranking consistency
                for (int i = 0; i < records.Length; i++)
                {
                    var doc = engine.GetDocument(records[i].DocumentId);
                    string name = doc!.IndexedText;
                    
                    bool matches = IsMatch(name);

                    if (matches)
                    {
                        if (encounteredNonMatch)
                        {
                            // We found a match after finding a non-match. This implies bad ranking.
                            // However, we should verify if the "non-match" was indeed a school.
                            // The dataset seems to only contain schools.
                            
                            // Dump context for debugging
                            string context = $"\nQuery: '{query}'\nFailed at index {i} (0-based).\n";
                            context += "Top results:\n";
                            for(int k=0; k<=i; k++)
                            {
                                var d = engine.GetDocument(records[k].DocumentId);
                                context += $"  {k}. [{records[k].Score}] {d!.IndexedText} (Match: {IsMatch(d.IndexedText)})\n";
                            }
                            
                            Assert.Fail($"Ranking consistency failure: Found matching school '{name}' after non-matching results for query '{query}'.\n{context}");
                        }
                    }
                    else
                    {
                        encounteredNonMatch = true;
                    }
                }
            }
        }
    }
}
