using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Infidex;
using Infidex.Api;
using Infidex.Core;

namespace Infidex.Tests;

[TestClass]
public class MovieSearchPersistenceTests : MovieSearchParityTestsBase
{
    private static SearchEngine? _originalEngine;
    private static SearchEngine? _loadedEngine;
    private static List<MovieRecord> _movies = new();
    private static string _tempIndexPath = string.Empty;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        // 1. Load movies
        _movies = LoadMovies();

        // 2. Build initial engine
        _originalEngine = SearchEngine.CreateDefault();
        var documents = _movies.Select((m, i) => new Document((long)i, m.Title)).ToList();
        _originalEngine.IndexDocuments(documents);

        // 3. Save to disk
        _tempIndexPath = Path.Combine(Path.GetTempPath(), $"movie_index_{Guid.NewGuid()}.idx");
        _originalEngine.Save(_tempIndexPath);
        // Do NOT dispose original engine yet, we need it for parity comparison

        // 4. Load back
        // Configuration must match CreateDefault
        var config = ConfigurationParameters.GetConfig(400);
        _loadedEngine = SearchEngine.Load(
            _tempIndexPath,
            config.IndexSizes,
            config.StartPadSize,
            config.StopPadSize,
            true, // enableCoverage
            config.TextNormalizer,
            config.TokenizerSetup,
            null, // coverageSetup (default)
            config.StopTermLimit,
            config.WordMatcherSetup,
            config.FieldWeights
        );
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _originalEngine?.Dispose();
        _loadedEngine?.Dispose();
        if (File.Exists(_tempIndexPath))
        {
            try { File.Delete(_tempIndexPath); } catch { }
        }
    }

    protected override SearchEngine GetEngine() => _loadedEngine!;
    protected override List<MovieRecord> GetMovies() => _movies;

    [TestMethod]
    public void VerifyExactParityWithOriginalIndex()
    {
        var queries = new[] 
        { 
            "star", "redemption", "shawshank", "batman", "love", "matrix", "action",
            "redemption sh", "star wars", "the"
        };

        foreach (var q in queries)
        {
            Console.WriteLine("ORIGINAL ENGINE --------------------");
            var originalResult = _originalEngine!.Search(new Query(q, 50));
            Console.WriteLine("LOADED ENGINE --------------------");
            var loadedResult = _loadedEngine!.Search(new Query(q, 50));

            Assert.AreEqual(originalResult.Records.Length, loadedResult.Records.Length, 
                $"Result count mismatch for query '{q}'");

            for (int i = 0; i < originalResult.Records.Length; i++)
            {
                var orig = originalResult.Records[i];
                var load = loadedResult.Records[i];

                Assert.AreEqual(orig.DocumentId, load.DocumentId, 
                    $"Document ID mismatch at index {i} for query '{q}'");
                
                Assert.AreEqual(orig.Score, load.Score, 
                    $"Score mismatch at index {i} for query '{q}'. DocId: {orig.DocumentId}");
            }
        }
    }

    private static List<MovieRecord> LoadMovies()
    {
        // Duplicate logic from ParityTests, or we could make LoadMovies public static in a helper
        // For now, duplicating to keep tests self-contained
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.ToLower(),
            MissingFieldFound = null
        };

        string baseDir = AppContext.BaseDirectory;
        string filePath = Path.Combine(baseDir, "movies.csv");

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);
        return csv.GetRecords<MovieRecord>().ToList();
    }
}
