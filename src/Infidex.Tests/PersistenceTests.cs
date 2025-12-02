using CsvHelper;
using CsvHelper.Configuration;
using Infidex.Api;
using Infidex.Core;
using System.Globalization;

namespace Infidex.Tests;

[TestClass]
public class PersistenceTests
{
    [TestMethod]
    public void SaveAndLoadIndex_PreservesData()
    {
        string filePath = "test_index.bin";
        try
        {
            // 1. Create and index
            var engine = SearchEngine.CreateDefault();
            var documents = new[]
            {
                new Document(1L, "The quick brown fox"),
                new Document(2L, "jumps over the lazy dog")
            };
            engine.IndexDocuments(documents);

            // Verify search before save
            var resultsBefore = engine.Search(new Query("fox", 10));
            Assert.AreEqual(1, resultsBefore.Records.Length);
            Assert.AreEqual(1L, resultsBefore.Records[0].DocumentId);

            // 2. Save
            engine.Save(filePath);

            // 3. Load
            // Use default config for loading (must match what was used for indexing usually)
            var config = ConfigurationParameters.GetConfig(400);
            var loadedEngine = SearchEngine.Load(
                filePath,
                config.IndexSizes,
                config.StartPadSize,
                config.StopPadSize,
                true,
                config.TextNormalizer,
                config.TokenizerSetup,
                null,
                config.StopTermLimit,
                config.WordMatcherSetup
            );

            // 4. Verify search after load
            var resultsAfter = loadedEngine.Search(new Query("fox", 10));
            Assert.AreEqual(1, resultsAfter.Records.Length);
            Assert.AreEqual(1L, resultsAfter.Records[0].DocumentId);

            // Verify another term
            var resultsDog = loadedEngine.Search(new Query("dog", 10));
            Assert.AreEqual(1, resultsDog.Records.Length);
            Assert.AreEqual(2L, resultsDog.Records[0].DocumentId);

            // Verify statistics
            var statsBefore = engine.GetStatistics();
            var statsAfter = loadedEngine.GetStatistics();
            Assert.AreEqual(statsBefore.DocumentCount, statsAfter.DocumentCount);
            Assert.AreEqual(statsBefore.VocabularySize, statsAfter.VocabularySize);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [TestMethod]
    public void SaveAndLoad40kMovies_MeasureIndexSize()
    {
        string filePath = "movies_40k_index.bin";
        try
        {
            // Load movies from CSV
            var movies = LoadMovies();
            Console.WriteLine($"Loaded {movies.Count} movies from CSV");

            // Create and index
            var engine = SearchEngine.CreateDefault();
            var documents = movies.Select((m, i) =>
                new Document((long)i, m.Title)).ToList();

            Console.WriteLine($"Indexing {documents.Count} movie titles...");
            engine.IndexDocuments(documents);

            var stats = engine.GetStatistics();
            Console.WriteLine($"Index stats: {stats.DocumentCount} documents, {stats.VocabularySize} unique terms");

            // Test search before save
            var testResults = engine.Search(new Query("redemption", 5));
            Console.WriteLine($"Test search found {testResults.Records.Length} results");

            // Save index
            Console.WriteLine("Saving index to disk...");
            engine.Save(filePath);

            // Measure file size
            var fileInfo = new FileInfo(filePath);
            long fileSizeBytes = fileInfo.Length;
            double fileSizeKB = fileSizeBytes / 1024.0;
            double fileSizeMB = fileSizeKB / 1024.0;

            Console.WriteLine($"\n=== INDEX FILE SIZE METRICS ===");
            Console.WriteLine($"Documents indexed: {documents.Count:N0}");
            Console.WriteLine($"Unique terms: {stats.VocabularySize:N0}");
            Console.WriteLine($"File size: {fileSizeBytes:N0} bytes");
            Console.WriteLine($"File size: {fileSizeKB:N2} KB");
            Console.WriteLine($"File size: {fileSizeMB:N2} MB");
            Console.WriteLine($"Bytes per document: {fileSizeBytes / (double)documents.Count:N2}");
            Console.WriteLine($"================================\n");

            // Load back and verify
            Console.WriteLine("Loading index from disk...");
            var config = ConfigurationParameters.GetConfig(400);
            var loadedEngine = SearchEngine.Load(
                filePath,
                config.IndexSizes,
                config.StartPadSize,
                config.StopPadSize,
                true,
                config.TextNormalizer,
                config.TokenizerSetup,
                null,
                config.StopTermLimit,
                config.WordMatcherSetup
            );

            // Verify loaded index works
            var loadedStats = loadedEngine.GetStatistics();
            Assert.AreEqual(stats.DocumentCount, loadedStats.DocumentCount);
            Assert.AreEqual(stats.VocabularySize, loadedStats.VocabularySize);

            // Verify search results match
            var loadedResults = loadedEngine.Search(new Query("redemption", 5));
            Assert.AreEqual(testResults.Records.Length, loadedResults.Records.Length);
            Console.WriteLine($"Loaded index verified: search returned {loadedResults.Records.Length} results");

            // Additional searches to verify quality
            var searchTerms = new[] { "batman", "matrix", "star wars", "love", "action" };
            foreach (var term in searchTerms)
            {
                var results = loadedEngine.Search(new Query(term, 3));
                Console.WriteLine($"Search '{term}': {results.Records.Length} results");
            }
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [TestMethod]
    public void SaveAndLoadIndex_UnicodeSurrogateCharacters()
    {
        string filePath = "surrogates_index.bin";
        try
        {
            // 1. Create and index
            var engine = SearchEngine.CreateDefault();
            var documents = new[]
            {
                new Document(1L, "\uD83D\uDD0D")
            };
            engine.IndexDocuments(documents);

            var resultsBefore = engine.Search(new Query("\uD83D\uDD0D", 10));
            Assert.HasCount(1, resultsBefore.Records);
            Assert.AreEqual(1L, resultsBefore.Records[0].DocumentId);

            // 2. Save
            engine.Save(filePath);

            // 3. Load
            var config = ConfigurationParameters.GetConfig(400);
            var loadedEngine = SearchEngine.Load(
                filePath,
                config.IndexSizes,
                config.StartPadSize,
                config.StopPadSize,
                true,
                config.TextNormalizer,
                config.TokenizerSetup,
                null,
                config.StopTermLimit,
                config.WordMatcherSetup
            );

            // 4. Verify search after load
            var resultsAfter = loadedEngine.Search(new Query("\uD83D\uDD0D", 10));
            Assert.HasCount(1, resultsAfter.Records);
            Assert.AreEqual(1L, resultsAfter.Records[0].DocumentId);

            // Verify statistics
            var statsBefore = engine.GetStatistics();
            var statsAfter = loadedEngine.GetStatistics();
            Assert.AreEqual(statsBefore.DocumentCount, statsAfter.DocumentCount);
            Assert.AreEqual(statsBefore.VocabularySize, statsAfter.VocabularySize);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    private static List<MovieRecord> LoadMovies()
    {
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
