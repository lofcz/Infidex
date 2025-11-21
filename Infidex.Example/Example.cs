using Infidex.Core;
using Infidex.Api;

namespace Infidex.Example;

/// <summary>
/// Example usage of the Infidex search engine with multi-field support.
/// </summary>
public static class Example
{
    public static void RunBasicExample()
    {
        Console.WriteLine("=== Infidex Search Engine - Basic Example (Single Field) ===\n");
        
        // 1. Create the search engine
        var engine = SearchEngine.CreateDefault();
        
        // 2. Prepare some documents - simple single-field approach
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
        var result = engine.Search(new Query(query, 5));
        
        if (result.Records.Length == 0)
        {
            Console.WriteLine("  No results found.");
        }
        else
        {
            foreach (var entry in result.Records)
            {
                var doc = engine.GetDocument(entry.DocumentId);
                if (doc != null)
                {
                    var preview = doc.IndexedText.Length > 60 
                        ? doc.IndexedText.Substring(0, 60) + "..." 
                        : doc.IndexedText;
                    Console.WriteLine($"  [{entry.Score:D3}] Doc {entry.DocumentId}: {preview}");
                }
            }
        }
        Console.WriteLine();
    }
    
    public static void RunMultiFieldExample()
    {
        Console.WriteLine("=== Multi-Field Search Example ===\n");
        Console.WriteLine("Demonstrates field weights: High (1.5x), Med (1.25x), Low (1.0x)\n");
        
        var engine = SearchEngine.CreateDefault();
        
        // Create documents with multiple weighted fields
        var products = new[]
        {
            CreateProduct(1L, "iPhone 15 Pro", "Latest flagship smartphone with A17 chip", "Electronics", "$999"),
            CreateProduct(2L, "Samsung Galaxy S24", "Premium Android phone with AI features", "Electronics", "$899"),
            CreateProduct(3L, "MacBook Pro", "Professional laptop for developers and creators", "Computers", "$2499"),
            CreateProduct(4L, "Dell XPS 15", "High-performance laptop with stunning display", "Computers", "$1799"),
            CreateProduct(5L, "Sony WH-1000XM5", "Noise-cancelling wireless headphones", "Audio", "$399")
        };
        
        engine.IndexDocuments(products);
        
        Console.WriteLine("Sample Products:");
        foreach (var p in products)
        {
            var fields = p.Fields.GetFieldList();
            var title = fields.First(f => f.Name == "title").Value;
            var desc = fields.First(f => f.Name == "description").Value;
            Console.WriteLine($"  {p.DocumentKey}. {title} - {desc}");
        }
        Console.WriteLine();
        
        // Search demonstrates field weight influence
        RunSearch(engine, products, "phone");       // Should rank phones higher due to title match
        RunSearch(engine, products, "laptop");      // Should find laptops
        RunSearch(engine, products, "professional"); // Description match
        RunSearch(engine, products, "electronics");  // Category (low weight) match
    }
    
    private static Document CreateProduct(long id, string title, string description, string category, string price)
    {
        var fields = new DocumentFields();
        fields.AddField("title", title, Weight.High, indexable: true);           // Most important
        fields.AddField("description", description, Weight.Med, indexable: true); // Medium importance
        fields.AddField("category", category, Weight.Low, indexable: true);       // Less important
        fields.AddField("price", price, Weight.Low, indexable: false);            // Not searchable
        
        return new Document(id, fields);
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
        
        // Index technical documents with metadata using multi-field approach
        var docs = new[]
        {
            CreateTechDoc(1L, "Machine learning algorithms require large datasets for training", "category:AI, author:John Doe"),
            CreateTechDoc(2L, "Deep learning neural networks use backpropagation", "category:AI, author:Jane Smith"),
            CreateTechDoc(3L, "Natural language processing enables text understanding", "category:NLP, author:John Doe"),
            CreateTechDoc(4L, "Computer vision algorithms detect objects in images", "category:CV, author:Bob Johnson"),
            CreateTechDoc(5L, "Reinforcement learning agents learn from experience", "category:RL, author:Alice Williams")
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
            var results = engine.Search(new Query(query, 3));
            
            if (results.Records.Length > 0)
            {
                foreach (var result in results.Records)
                {
                    var doc = engine.GetDocument(result.DocumentId);
                    if (doc != null)
                    {
                        Console.WriteLine($"  [{result.Score:D3}] {doc.IndexedText}");
                        Console.WriteLine($"       Metadata: {doc.DocumentClientInformation}");
                    }
                }
            }
            else
            {
                Console.WriteLine("  No matches found.");
            }
            Console.WriteLine();
        }
    }
    
    private static Document CreateTechDoc(long id, string content, string metadata)
    {
        var fields = new DocumentFields();
        fields.AddField("content", content, Weight.Med, indexable: true);
        return new Document(id, 0, fields, metadata);
    }
    
    public static void RunSegmentedDocumentExample()
    {
        Console.WriteLine("=== Segmented Document Example ===\n");
        
        var engine = SearchEngine.CreateDefault();
        
        // Simulate a long document that's been segmented
        long bookId = 100L;
        var segments = new[]
        {
            CreateSegment(bookId, 0, "Chapter 1: The hero begins his journey through the dark forest.", "book:Epic Tale, chapter:1"),
            CreateSegment(bookId, 1, "The forest was full of dangers and mysterious creatures lurking in shadows.", "book:Epic Tale, chapter:1"),
            CreateSegment(bookId, 2, "Chapter 2: The hero discovers an ancient temple hidden in the mountains.", "book:Epic Tale, chapter:2"),
            CreateSegment(bookId, 3, "Inside the temple, ancient scrolls revealed the secret of the prophecy.", "book:Epic Tale, chapter:2")
        };
        
        engine.IndexDocuments(segments);
        
        Console.WriteLine("Searching in segmented book...\n");
        
        var result = engine.Search(new Query("ancient temple", 5));
        
        foreach (var entry in result.Records)
        {
            var doc = engine.GetDocument(entry.DocumentId);
            if (doc != null)
            {
                Console.WriteLine($"[{entry.Score:D3}] Book {doc.DocumentKey}, Segment {doc.SegmentNumber}:");
                Console.WriteLine($"      {doc.IndexedText}");
                Console.WriteLine($"      Info: {doc.DocumentClientInformation}\n");
            }
        }
    }
    
    private static Document CreateSegment(long id, int segmentNumber, string content, string metadata)
    {
        var fields = new DocumentFields();
        fields.AddField("content", content, Weight.Med, indexable: true);
        return new Document(id, segmentNumber, fields, metadata);
    }
}
