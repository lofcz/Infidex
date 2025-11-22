using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Query = Infidex.Api.Query;

namespace Infidex.Benchmark;

[MemoryDiagnoser]
[RankColumn]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class QueryBenchmarks
{
    private Infidex.SearchEngine _infidexEngine = null!;
    private Indx.Api.SearchEngine _indxEngine = null!;
    private Lucene.Net.Store.Directory _luceneDirectory = null!;
    private StandardAnalyzer _luceneAnalyzer = null!;
    private IndexSearcher _luceneSearcher = null!;
    private Lucene.Net.Store.Directory _luceneNGramDirectory = null!;
    private Analyzer _luceneNGramAnalyzer = null!;
    private IndexSearcher _luceneNGramSearcher = null!;
    
    [GlobalSetup]
    public void Setup()
    {
        var movies = MovieData.LoadMovies();
        Console.WriteLine($"Setting up query benchmarks with {movies.Count} movies");
        
        // Setup Infidex
        _infidexEngine = Infidex.SearchEngine.CreateDefault();
        var infidexDocuments = movies.Select((movie, i) => 
            new Infidex.Core.Document((long)i, movie.Title)).ToList();
        _infidexEngine.IndexDocuments(infidexDocuments);
        
        // Setup Indx
        var jsonStream = new MemoryStream();
        var writer = new StreamWriter(jsonStream, Encoding.UTF8);
        int id = 0;
        foreach (var movie in movies)
        {
            var docObj = new { id = id++, title = movie.Title };
            writer.WriteLine(JsonSerializer.Serialize(docObj));
        }
        writer.Flush();
        jsonStream.Position = 0;

        _indxEngine = new Indx.Api.SearchEngine("", null, 400);
        _indxEngine.Init(jsonStream, "id");
        
        var titleField = _indxEngine.GetField("title");
        if (titleField != null)
        {
            titleField.Indexable = true;
        }
        
        jsonStream.Position = 0;
        _indxEngine.Load(jsonStream);
        
        while (_indxEngine.Status.SystemState == Indx.Api.SystemState.Loading)
        {
            System.Threading.Thread.Sleep(10);
        }
        
        _indxEngine.Index();
        
        while (_indxEngine.Status.SystemState == Indx.Api.SystemState.Indexing)
        {
            System.Threading.Thread.Sleep(10);
        }
        
        // Setup Lucene
        const LuceneVersion luceneVersion = LuceneVersion.LUCENE_48;
        _luceneDirectory = new RAMDirectory();
        _luceneAnalyzer = new StandardAnalyzer(luceneVersion);
        var indexConfig = new IndexWriterConfig(luceneVersion, _luceneAnalyzer);
        
        using (var iw = new IndexWriter(_luceneDirectory, indexConfig))
        {
            foreach (var movie in movies)
            {
                var doc = new Lucene.Net.Documents.Document();
                doc.Add(new TextField("title", movie.Title ?? string.Empty, Lucene.Net.Documents.Field.Store.YES));
                iw.AddDocument(doc);
            }
            iw.Commit();
        }
        
        var reader = DirectoryReader.Open(_luceneDirectory);
        _luceneSearcher = new IndexSearcher(reader);
        
        // Setup Lucene with NGram (fair comparison)
        _luceneNGramDirectory = new RAMDirectory();
        _luceneNGramAnalyzer = new NGramAnalyzer(luceneVersion);
        var ngramIndexConfig = new IndexWriterConfig(luceneVersion, _luceneNGramAnalyzer);
        
        using (var ngramWriter = new IndexWriter(_luceneNGramDirectory, ngramIndexConfig))
        {
            foreach (var movie in movies)
            {
                var doc = new Lucene.Net.Documents.Document();
                doc.Add(new TextField("title", movie.Title ?? string.Empty, Lucene.Net.Documents.Field.Store.YES));
                ngramWriter.AddDocument(doc);
            }
            ngramWriter.Commit();
        }
        
        var ngramReader = DirectoryReader.Open(_luceneNGramDirectory);
        _luceneNGramSearcher = new IndexSearcher(ngramReader);
        
        Console.WriteLine("Setup complete");
    }
    
    [GlobalCleanup]
    public void Cleanup()
    {
        _indxEngine?.Dispose();
        _luceneAnalyzer?.Dispose();
        _luceneDirectory?.Dispose();
        _luceneNGramAnalyzer?.Dispose();
        _luceneNGramDirectory?.Dispose();
    }
    
    // Infidex Queries
    
    [Benchmark]
    public void Infidex_Query_Shawshank()
    {
        var result = _infidexEngine.Search(new Infidex.Api.Query("Shawshank", 10));
    }
    
    [Benchmark]
    public void Infidex_Query_ShaaawshankTypo()
    {
        var result = _infidexEngine.Search(new Infidex.Api.Query("Shaaawshank", 10));
    }
    
    [Benchmark]
    public void Infidex_Query_ShaaAwashankSpaces()
    {
        var result = _infidexEngine.Search(new Infidex.Api.Query("Shaa awashank", 10));
    }
    
    [Benchmark]
    public void Infidex_Query_RedemptionShank()
    {
        var result = _infidexEngine.Search(new Infidex.Api.Query("redemption shank", 10));
    }
    
    // Indx Queries
    
    [Benchmark]
    public void Indx_Query_Shawshank()
    {
        var query = new Indx.Api.Query("Shawshank", maxNumberOfRecordsToReturn: 10);
        var result = _indxEngine.Search(query);
    }
    
    [Benchmark]
    public void Indx_Query_ShaaawshankTypo()
    {
        var query = new Indx.Api.Query("Shaaawshank", maxNumberOfRecordsToReturn: 10);
        var result = _indxEngine.Search(query);
    }
    
    [Benchmark]
    public void Indx_Query_ShaaAwashankSpaces()
    {
        var query = new Indx.Api.Query("Shaa awashank", maxNumberOfRecordsToReturn: 10);
        var result = _indxEngine.Search(query);
    }
    
    [Benchmark]
    public void Indx_Query_RedemptionShank()
    {
        var query = new Indx.Api.Query("redemption shank", maxNumberOfRecordsToReturn: 10);
        var result = _indxEngine.Search(query);
    }
    
    // Lucene Queries
    
    [Benchmark(Baseline = true)]
    public void Lucene_Query_Shawshank()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneAnalyzer);
        var query = parser.Parse("Shawshank");
        var hits = _luceneSearcher.Search(query, 10);
    }
    
    [Benchmark]
    public void Lucene_Query_ShaaawshankTypo()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneAnalyzer);
        // Lucene with fuzzy search using ~
        var query = parser.Parse("Shaaawshank~");
        var hits = _luceneSearcher.Search(query, 10);
    }
    
    [Benchmark]
    public void Lucene_Query_ShaaAwashankSpaces()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneAnalyzer);
        var query = parser.Parse("\"Shaa awashank\"~10");
        var hits = _luceneSearcher.Search(query, 10);
    }
    
    [Benchmark]
    public void Lucene_Query_RedemptionShank()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneAnalyzer);
        var query = parser.Parse("redemption shank");
        var hits = _luceneSearcher.Search(query, 10);
    }
    
    // Lucene NGram Queries (Fair Comparison)
    
    [Benchmark]
    public void LuceneNGram_Query_Shawshank()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneNGramAnalyzer);
        var query = parser.Parse("Shawshank");
        var hits = _luceneNGramSearcher.Search(query, 10);
    }
    
    [Benchmark]
    public void LuceneNGram_Query_ShaaawshankTypo()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneNGramAnalyzer);
        var query = parser.Parse("Shaaawshank");
        var hits = _luceneNGramSearcher.Search(query, 10);
    }
    
    [Benchmark]
    public void LuceneNGram_Query_ShaaAwashankSpaces()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneNGramAnalyzer);
        var query = parser.Parse("Shaa awashank");
        var hits = _luceneNGramSearcher.Search(query, 10);
    }
    
    [Benchmark]
    public void LuceneNGram_Query_RedemptionShank()
    {
        var parser = new QueryParser(LuceneVersion.LUCENE_48, "title", _luceneNGramAnalyzer);
        var query = parser.Parse("redemption shank");
        var hits = _luceneNGramSearcher.Search(query, 10);
    }
}

