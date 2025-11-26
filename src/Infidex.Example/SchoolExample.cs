using System.Diagnostics;
using System.Text.Json;
using Infidex.Core;
using Infidex.Api;

namespace Infidex.Example;

public class SchoolRecord
{
    public string Name { get; set; } = string.Empty;
    public string Cin { get; set; } = string.Empty;
    public string Sin { get; set; } = string.Empty;
}

public class SchoolExample
{
    public static void Run(ExampleMode mode = ExampleMode.Repl)
    {
        Console.WriteLine("Loading schools from JSON...");
        
        string filePath = Path.Combine(AppContext.BaseDirectory, "schools.json");
        Console.WriteLine($"Reading from: {filePath}");
        
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Error: schools.json not found at {filePath}");
            return;
        }
        
        string json = File.ReadAllText(filePath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        
        var records = JsonSerializer.Deserialize<List<SchoolRecord>>(json, options) ?? new List<SchoolRecord>();
        var schoolNames = records
            .Select(r => r.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        Console.WriteLine($"Loaded {schoolNames.Count} schools.");

        // Create Search Engine
        var engine = SearchEngine.CreateDefault();
        
        Console.WriteLine("Indexing schools...");
        var documents = schoolNames
            .Select((name, i) => new Document((long)i, name))
            .ToList();
        
        var sw = Stopwatch.StartNew();
        engine.IndexDocuments(documents);
        sw.Stop();

        long elapsedMs = sw.ElapsedMilliseconds;
        double elapsedSec = sw.Elapsed.TotalSeconds;
        double docsPerSec = elapsedSec > 0 ? schoolNames.Count / elapsedSec : 0;

        Console.WriteLine(
            $"Indexing complete: {schoolNames.Count:N0} schools in {elapsedMs:N0} ms (~{docsPerSec:N0} docs/s).");
        
        // Index-only mode: stop after building the index.
        if (mode == ExampleMode.Index)
            return;

        // Perform predefined example queries in Test/Repl modes.
        if (mode is ExampleMode.Test or ExampleMode.Repl)
        {
            SearchAndPrint(engine, new Query("sciozlín"));
            SearchAndPrint(engine, new Query("základní škola"));
            SearchAndPrint(engine, new Query("mate"));
        }

        // Interactive REPL only in Repl mode.
        if (mode == ExampleMode.Repl)
        {
            while (true)
            {
                Console.Write($"> ");
                string? input = Console.ReadLine();

                if (input is "q" or "!q" or "quit" or "exit")
                {
                    break;
                }
                
                if (!string.IsNullOrWhiteSpace(input))
                {
                    SearchAndPrint(engine, new Query(input));
                }
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
            foreach (var hit in result.Records.Take(10))
            {
                var doc = engine.GetDocument(hit.DocumentId);
                if (doc != null)
                {
                    Console.WriteLine($"[{hit.Score}] {doc.IndexedText}");
                }
            }
        }
    }
}

