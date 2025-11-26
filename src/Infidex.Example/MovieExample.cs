using System.Diagnostics;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using Infidex.Core;
using Infidex.Api;

namespace Infidex.Example;

public class MovieRecord
{
    [Name("title")]
    public string Title { get; set; }
    
    [Name("description")]
    public string Description { get; set; }
    
    [Name("genre")]
    public string Genre { get; set; }
    
    [Name("year")]
    public string Year { get; set; }
}

public class MovieExample
{
    public static void Run(bool useLargeDataset = false, ExampleMode mode = ExampleMode.Repl)
    {
        Console.WriteLine(useLargeDataset
            ? "Loading 1M movies from CSV..."
            : "Loading movies from CSV...");
        
        var records = new List<MovieRecord>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.ToLower(),
            MissingFieldFound = null, // Ignore missing fields if any
            HeaderValidated = null    // Allow schemas without all mapped headers (e.g. movies1M.csv)
        };

        string filePath = useLargeDataset
            ? GetLargeMoviesPath()
            : Path.Combine(AppContext.BaseDirectory, "movies.csv");

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Error: movies CSV not found at {filePath}");
            return;
        }

        Console.WriteLine($"Reading from: {filePath}");
        using (var reader = new StreamReader(filePath))
        using (var csv = new CsvReader(reader, config))
        {
            records = csv.GetRecords<MovieRecord>().ToList();
        }

        Console.WriteLine($"Loaded {records.Count} movies.");

        // Create Search Engine
        var engine = SearchEngine.CreateDefault();
        
        // Index Documents with multi-field support
        Console.WriteLine("Indexing movies with weighted fields (Title=High, Description=Med, Genre=Low)...");
        var documents = records.Select((r, i) => CreateMovieDocument(i, r)).ToList();
        
        var sw = Stopwatch.StartNew();
        // Use the new IndexDocuments method
        engine.IndexDocuments(documents);
        sw.Stop();
        
        // Explicitly calculate weights (IndexDocuments does this, but good practice to be sure if batched, 
        // though the current implementation does it at the end of IndexDocuments)
        // engine.CalculateWeights(); 

        long elapsedMs = sw.ElapsedMilliseconds;
        double elapsedSec = sw.Elapsed.TotalSeconds;
        double docsPerSec = elapsedSec > 0 ? records.Count / elapsedSec : 0;

        Console.WriteLine(
            $"Indexing complete: {records.Count:N0} movies in {elapsedMs:N0} ms (~{docsPerSec:N0} docs/s).");
        
        // Index-only mode: stop after building the index.
        if (mode == ExampleMode.Index)
            return;

        // Perform predefined queries in Test/Repl modes.
        if (mode is ExampleMode.Test or ExampleMode.Repl)
        {
            SearchAndPrint(engine, new Query("redemption shank"));
            SearchAndPrint(engine, new Query("Shaaawshank"));
            SearchAndPrint(engine, new Query("Shaa awashank"));
            SearchAndPrint(engine, new Query("Shaa awa shank"));
        }

        // Interactive REPL only in Repl mode.
        if (mode == ExampleMode.Repl)
        {
            while (true)
            {
                Console.Write($"> ");
                string input = Console.ReadLine();

                if (input is "q" or "!q" or "quit" or "exit")
                {
                    break;
                }
                
                SearchAndPrint(engine, new Query(input));
            }
        }
    }

    private static void SearchAndPrint(SearchEngine engine, Query query)
    {
        Console.WriteLine($"\nSearching for: '{query.Text}'");
        var sw = Stopwatch.StartNew();
        var result = engine.Search(query);
        sw.Stop();
        Console.WriteLine($"Search took {sw.ElapsedMilliseconds}ms");
        
        if (result.Records.Length == 0)
        {
            Console.WriteLine("No results found.");
        }
        else
        {
            foreach (var hit in result.Records)
            {
                var doc = engine.GetDocument(hit.DocumentId);
                if (doc != null)
                {
                    var titleField = doc.Fields.GetField("title");
                    var title = titleField?.Value?.ToString() ?? doc.IndexedText;
                    Console.WriteLine($"[{hit.Score}] {title}");
                }
            }
        }
    }
    
    private static Document CreateMovieDocument(long id, MovieRecord movie)
    {
        return new Document(id, movie.Title);
    }

    private static string GetLargeMoviesPath()
    {
        // When running from Infidex.Example, the base directory is typically:
        //   src/Infidex.Example/bin/{Configuration}/{TargetFramework}/
        // We want to reach:
        //   src/Infidex.Tests/movies1M.csv
        //
        // So we go up four levels to src/, then into Infidex.Tests.
        string baseDir = AppContext.BaseDirectory;
        string srcDir = Path.GetFullPath(
            Path.Combine(baseDir, "..", "..", "..", ".."));

        return Path.Combine(srcDir, "Infidex.Tests", "movies1M.csv");
    }
}
