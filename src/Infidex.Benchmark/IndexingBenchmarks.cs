using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Indx.Api;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Infidex.Benchmark;

[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class IndexingBenchmarks
{
    private List<MovieRecord> _movies = null!;
    
    [GlobalSetup]
    public void Setup()
    {
        _movies = MovieData.LoadMovies();
        Console.WriteLine($"Loaded {_movies.Count} movies for indexing benchmark");
    }
    
    [Benchmark]
    public void Infidex_Index40kMovies()
    {
        var engine = Infidex.SearchEngine.CreateDefault();
        
        var documents = _movies.Select((movie, i) => 
            new Infidex.Core.Document((long)i, movie.Title)).ToList();
        
        engine.IndexDocuments(documents);
    }
    
    [Benchmark]
    public void Indx_Index40kMovies()
    {
        // Convert to JSON stream for Indx
        var jsonStream = new MemoryStream();
        var writer = new StreamWriter(jsonStream, Encoding.UTF8);
        int id = 0;
        foreach (var movie in _movies)
        {
            var docObj = new { id = id++, title = movie.Title };
            writer.WriteLine(JsonSerializer.Serialize(docObj));
        }
        writer.Flush();
        jsonStream.Position = 0;

        // Create Search Engine (400 = Default config)
        var engine = new Indx.Api.SearchEngine("", null, 400);
        
        // Init parses the stream to find fields and structure
        engine.Init(jsonStream, "id");

        // Ensure 'title' is indexable
        var titleField = engine.GetField("title");
        if (titleField != null)
        {
            titleField.Indexable = true;
        }
        
        // Reset stream position for Load
        jsonStream.Position = 0;
        engine.Load(jsonStream);
        
        // Wait for loading to complete
        while (engine.Status.SystemState == SystemState.Loading)
        {
            System.Threading.Thread.Sleep(10);
        }
        
        engine.Index();
        
        // Wait for indexing to complete
        while (engine.Status.SystemState == SystemState.Indexing)
        {
            System.Threading.Thread.Sleep(10);
        }
        
        engine.Dispose();
    }

    [Benchmark]
    public void Lucene_Index40kMovies_NGram()
    {
        const LuceneVersion luceneVersion = LuceneVersion.LUCENE_48;
        
        using var directory = new RAMDirectory();
        using var analyzer = new NGramAnalyzer(luceneVersion);
        var indexConfig = new IndexWriterConfig(luceneVersion, analyzer);
        
        using var writer = new IndexWriter(directory, indexConfig);
        
        foreach (var movie in _movies)
        {
            var doc = new Lucene.Net.Documents.Document();
            doc.Add(new TextField("title", movie.Title ?? string.Empty, Lucene.Net.Documents.Field.Store.YES));
            writer.AddDocument(doc);
        }
        
        writer.Commit();
        writer.Flush(triggerMerge: false, applyAllDeletes: false);
    }
}

/// <summary>
/// Custom analyzer that uses NGram tokenization (2-3 grams) to match Infidex's approach
/// </summary>
public sealed class NGramAnalyzer : Analyzer
{
    private readonly LuceneVersion _version;
    
    public NGramAnalyzer(LuceneVersion version)
    {
        _version = version;
    }
    
    protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
    {
        var tokenizer = new KeywordTokenizer(reader);
        TokenStream filter = new LowerCaseFilter(_version, tokenizer);
        filter = new NGramTokenFilter(_version, filter, 2, 3); // 2-3 grams like Infidex
        return new TokenStreamComponents(tokenizer, filter);
    }
}

