using System.Text;
using System.Text.Json;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Infidex.Benchmark;

public static class ResultComparison
{
    public static void Run()
    {
        var movies = MovieData.LoadMovies();
        Console.WriteLine($"Loaded {movies.Count} movies\n");
        
        // Setup Infidex
        var infidexEngine = Infidex.SearchEngine.CreateDefault();
        var infidexDocuments = movies.Select((movie, i) => 
            new Infidex.Core.Document((long)i, movie.Title)).ToList();
        infidexEngine.IndexDocuments(infidexDocuments);
        
        // Disable debug logging for cleaner output
        // infidexEngine.EnableDebugLogging = true;
        
        // Setup Indx (reference)
        var jsonStream = new MemoryStream();
        var streamWriter = new StreamWriter(jsonStream, Encoding.UTF8);
        int id = 0;
        foreach (var movie in movies)
        {
            var docObj = new { id = id++, title = movie.Title };
            streamWriter.WriteLine(JsonSerializer.Serialize(docObj));
        }
        streamWriter.Flush();
        jsonStream.Position = 0;

        var indxEngine = new Indx.Api.SearchEngine("", null, 400);
        indxEngine.Init(jsonStream, "id");
        
        var titleField = indxEngine.GetField("title");
        if (titleField != null)
        {
            titleField.Indexable = true;
        }
        
        jsonStream.Position = 0;
        indxEngine.Load(jsonStream);
        
        while (indxEngine.Status.SystemState == Indx.Api.SystemState.Loading)
        {
            System.Threading.Thread.Sleep(10);
        }
        
        indxEngine.Index();
        
        while (indxEngine.Status.SystemState == Indx.Api.SystemState.Indexing)
        {
            System.Threading.Thread.Sleep(10);
        }
        
        // Setup Lucene NGram
        const LuceneVersion luceneVersion = LuceneVersion.LUCENE_48;
        var luceneDirectory = new RAMDirectory();
        var luceneAnalyzer = new NGramAnalyzer(luceneVersion);
        var indexConfig = new IndexWriterConfig(luceneVersion, luceneAnalyzer);
        
        using (var writer = new IndexWriter(luceneDirectory, indexConfig))
        {
            for (int i = 0; i < movies.Count; i++)
            {
                var doc = new Document();
                doc.Add(new StringField("id", i.ToString(), Field.Store.YES));
                doc.Add(new TextField("title", movies[i].Title ?? string.Empty, Field.Store.YES));
                writer.AddDocument(doc);
            }
            writer.Commit();
        }
        
        var reader = DirectoryReader.Open(luceneDirectory);
        var searcher = new IndexSearcher(reader);
        
        // Test queries
        string[] testQueries = 
        {
            "redeption sh"
        };
        
        foreach (var queryText in testQueries)
        {
            Console.WriteLine($"========================================");
            Console.WriteLine($"Query: \"{queryText}\"");
            Console.WriteLine($"========================================\n");
            
            // Infidex results
            Console.WriteLine("--- INFIDEX RESULTS (Top 5) ---");
            var infidexResults = infidexEngine.Search(new Infidex.Api.Query(queryText, 5));
            foreach (var result in infidexResults.Records.Take(5))
            {
                var doc = infidexEngine.GetDocument(result.DocumentId);
                var title = doc?.IndexedText ?? "Unknown";
                Console.WriteLine($"  [{result.Score:000}] {title}");
            }
            
            Console.WriteLine();
            
            // Indx results
            Console.WriteLine("--- INDX RESULTS (Top 5) ---");
            var indxQuery = new Indx.Api.Query(queryText, maxNumberOfRecordsToReturn: 5);
            var indxResults = indxEngine.Search(indxQuery);
            
            foreach (var result in indxResults.Records.Take(5))
            {
                string jsonData = indxEngine.GetJsonDataOfKey(result.DocumentKey);
                string title = "Unknown";
                try 
                {
                    using (var jsonDoc = JsonDocument.Parse(jsonData))
                    {
                        if (jsonDoc.RootElement.TryGetProperty("title", out var prop))
                        {
                            title = prop.GetString() ?? "";
                        }
                    }
                }
                catch {}
                
                Console.WriteLine($"  [{result.Score:000}] {title}");
            }
            
            Console.WriteLine();
            
            // Lucene NGram results
            Console.WriteLine("--- LUCENE NGRAM RESULTS (Top 5) ---");
            var parser = new QueryParser(luceneVersion, "title", luceneAnalyzer);
            var luceneQuery = parser.Parse(queryText);
            var luceneHits = searcher.Search(luceneQuery, 5);
            
            foreach (var hit in luceneHits.ScoreDocs)
            {
                var doc = searcher.Doc(hit.Doc);
                var title = doc.Get("title");
                var score = hit.Score;
                Console.WriteLine($"  [{score:F2}] {title}");
            }
            
            Console.WriteLine($"\nTotal: Infidex={infidexResults.Records.Length}, Indx={indxResults.Records?.Length ?? 0}, Lucene={luceneHits.TotalHits}\n\n");
        }
        
        indxEngine.Dispose();
        luceneAnalyzer.Dispose();
        luceneDirectory.Dispose();
    }
}

