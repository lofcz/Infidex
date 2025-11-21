using Infidex.Core;

namespace Infidex.Example;

/// <summary>
/// Example usage of the Infidex search engine.
/// </summary>
public static class Example
{
    public static void RunBasicExample()
    {
        Console.WriteLine("=== Infidex Search Engine - Basic Example ===\n");
        
        // 1. Create the search engine
        var engine = SearchEngine.CreateDefault();
        engine.EnableDebugLogging = true;
        
        // 2. Prepare some documents using the rich Document structure
        var documents = new[]
        {
            new Document(1L, "The quick brown fox jumps over the lazy dog"),
            new Document(2L, "A journey of a thousand miles begins with a single step"),
            new Document(3L, "To be or not to be, that is the question"),
            new Document(4L, "All that glitters is not gold"),
            new Document(5L, "The fox was quick and clever in the forest"),
            new Document(6L, "Batman and Robin fight crime in Gotham City"),
            new Document(7L, "Superman flies faster than a speeding bullet"),
            new Document(8L, "Spider-Man swings through New York City"),
            new Document(9L, "Wonder Woman protects the innocent"),
            new Document(10L, "The Flash runs at incredible speeds")
        };
        
        // 3. Index the documents
        Console.WriteLine("Indexing documents...");
        engine.IndexDocuments(documents);
        
        var stats = engine.GetStatistics();
        Console.WriteLine($"Indexed: {stats}\n");
        
        // 4. Run some searches
        RunSearch(engine, documents, "qick fux");
        RunSearch(engine, documents, "batman");
        RunSearch(engine, documents, "battamam"); // Typo - should still find batman
        RunSearch(engine, documents, "new york");
        RunSearch(engine, documents, "speeding");
    }
    
    private static void RunSearch(SearchEngine engine, Document[] documents, string query)
    {
        Console.WriteLine($"Search: \"{query}\"");
        var result = engine.Search(query, maxResults: 5);
        
        if (result.Results.Length == 0)
        {
            Console.WriteLine("  No results found.");
        }
        else
        {
            foreach (var entry in result.Results)
            {
                var doc = documents.First(d => d.DocumentKey == entry.DocumentId);
                var preview = doc.IndexedText.Length > 60 
                    ? doc.IndexedText.Substring(0, 60) + "..." 
                    : doc.IndexedText;
                Console.WriteLine($"  [{entry.Score:D3}] Doc {entry.DocumentId}: {preview}");
            }
        }
        Console.WriteLine();
    }
    
    public static void RunAdvancedExample()
    {
        Console.WriteLine("=== Infidex Search Engine - Advanced Example ===\n");
        
        // Create engine with custom configuration
        var engine = new SearchEngine(
            indexSizes: new[] { 2, 3 },       // Both 2-grams and 3-grams
            startPadSize: 2,
            stopPadSize: 0,
            enableCoverage: true,
            stopTermLimit: 1_000_000
        );
        
        // Index technical documents with metadata
        var docs = new[]
        {
            new Document(1L, 0, "Machine learning algorithms require large datasets for training", 
                "category:AI, author:John Doe"),
            new Document(2L, 0, "Deep learning neural networks use backpropagation", 
                "category:AI, author:Jane Smith"),
            new Document(3L, 0, "Natural language processing enables text understanding", 
                "category:NLP, author:John Doe"),
            new Document(4L, 0, "Computer vision algorithms detect objects in images", 
                "category:CV, author:Bob Johnson"),
            new Document(5L, 0, "Reinforcement learning agents learn from experience", 
                "category:RL, author:Alice Williams")
        };
        
        engine.IndexDocuments(docs);
        
        Console.WriteLine("Searching technical documents...\n");
        
        // Test various queries
        var queries = new[]
        {
            "machine learning",
            "neural network",
            "lerning",  // Typo - coverage engine should help
            "algorithms"
        };
        
        foreach (var query in queries)
        {
            Console.WriteLine($"Query: \"{query}\"");
            var results = engine.Search(query, maxResults: 3);
            
            if (results.Results.Length > 0)
            {
                foreach (var result in results.Results)
                {
                    var doc = docs.First(d => d.DocumentKey == result.DocumentId);
                    Console.WriteLine($"  [{result.Score:D3}] {doc.IndexedText}");
                    Console.WriteLine($"       Metadata: {doc.DocumentClientInformation}");
                }
            }
            else
            {
                Console.WriteLine("  No matches found.");
            }
            Console.WriteLine();
        }
    }
    
    public static void RunSegmentedDocumentExample()
    {
        Console.WriteLine("=== Segmented Document Example ===\n");
        
        var engine = SearchEngine.CreateDefault();
        
        // Simulate a long document that's been segmented
        long bookId = 100L;
        var segments = new[]
        {
            new Document(bookId, 0, 
                "Chapter 1: The hero begins his journey through the dark forest.",
                "book:Epic Tale, chapter:1"),
            new Document(bookId, 1, 
                "The forest was full of dangers and mysterious creatures lurking in shadows.",
                "book:Epic Tale, chapter:1"),
            new Document(bookId, 2, 
                "Chapter 2: The hero discovers an ancient temple hidden in the mountains.",
                "book:Epic Tale, chapter:2"),
            new Document(bookId, 3, 
                "Inside the temple, ancient scrolls revealed the secret of the prophecy.",
                "book:Epic Tale, chapter:2")
        };
        
        engine.IndexDocuments(segments);
        
        Console.WriteLine("Searching in segmented book...\n");
        
        var result = engine.Search("ancient temple", maxResults: 5);
        
        foreach (var entry in result.Results)
        {
            var doc = segments.First(d => d.Id == entry.DocumentId);
            Console.WriteLine($"[{entry.Score:D3}] Book {doc.DocumentKey}, Segment {doc.SegmentNumber}:");
            Console.WriteLine($"      {doc.IndexedText}");
            Console.WriteLine($"      Info: {doc.DocumentClientInformation}\n");
        }
    }
}
