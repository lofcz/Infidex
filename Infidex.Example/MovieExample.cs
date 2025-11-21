using System.Globalization;
using System.Diagnostics;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using Infidex.Core;

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
    public static void Run()
    {
        Console.WriteLine("Loading movies from CSV...");
        
        var records = new List<MovieRecord>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.ToLower(),
            MissingFieldFound = null // Ignore missing fields if any
        };

        string filePath = Path.Combine(AppContext.BaseDirectory, "movies.csv");
        Console.WriteLine($"Reading from: {filePath}");
        using (var reader = new StreamReader(filePath))
        using (var csv = new CsvReader(reader, config))
        {
            records = csv.GetRecords<MovieRecord>().ToList();
        }

        Console.WriteLine($"Loaded {records.Count} movies.");

        // Create Search Engine
        var engine = SearchEngine.CreateDefault();
        
        // Index Documents
        Console.WriteLine("Indexing movies...");
        var documents = records.Select((r, i) => new Document(i, r.Title)).ToList();
        
        var sw = Stopwatch.StartNew();
        // Use the new IndexDocuments method
        engine.IndexDocuments(documents);
        sw.Stop();
        
        // Explicitly calculate weights (IndexDocuments does this, but good practice to be sure if batched, 
        // though the current implementation does it at the end of IndexDocuments)
        // engine.CalculateWeights(); 

        Console.WriteLine($"Indexing complete in {sw.ElapsedMilliseconds}ms.");

        // Perform Queries
        SearchAndPrint(engine, "Shawshank");
        SearchAndPrint(engine, "Shaaawshank");
        SearchAndPrint(engine, "Shaa awashank");
        SearchAndPrint(engine, "Shaa awa shank");
    }

    private static void SearchAndPrint(SearchEngine engine, string query)
    {
        Console.WriteLine($"\nSearching for: '{query}'");
        var sw = Stopwatch.StartNew();
        var result = engine.Search(query);
        sw.Stop();
        Console.WriteLine($"Search took {sw.ElapsedMilliseconds}ms");
        
        if (result.Results.Length == 0)
        {
            Console.WriteLine("No results found.");
        }
        else
        {
            foreach (var hit in result.Results)
            {
                var doc = engine.GetDocument(hit.DocumentId);
                Console.WriteLine($"[{hit.Score}] {doc?.IndexedText}");
            }
        }
    }
}
