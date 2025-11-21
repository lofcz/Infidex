using Infidex;
using Infidex.Core;

namespace Infidex.Tests;

[TestClass]
public class SearchEngineTests
{
    [TestMethod]
    public void IndexAndSearch_SimpleDocuments_FindsMatches()
    {
        var engine = SearchEngine.CreateDefault();
        
        // Index some documents
        var docs = new[]
        {
            new Document(1L, "The quick brown fox jumps over the lazy dog"),
            new Document(2L, "A journey of a thousand miles begins with a single step"),
            new Document(3L, "To be or not to be that is the question"),
            new Document(4L, "The fox was quick and clever")
        };
        
        engine.IndexDocuments(docs);
        
        // Search for "fox"
        var result = engine.Search("fox", maxResults: 10);
        
        Assert.IsTrue(result.Results.Length > 0);
        
        // Documents 1 and 4 should be in results (they contain "fox")
        var docIds = result.Results.Select(r => r.DocumentId).ToArray();
        Assert.IsTrue(docIds.Contains(1L));
        Assert.IsTrue(docIds.Contains(4L));
    }
    
    [TestMethod]
    public void Search_ExactMatch_ReturnsHighScore()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            new Document(1L, "hello world"),
            new Document(2L, "goodbye world"),
            new Document(3L, "hello there")
        });
        
        var result = engine.Search("hello world", maxResults: 10);
        
        Assert.IsTrue(result.Results.Length > 0);
        Assert.AreEqual(1L, result.Results[0].DocumentId);
        Assert.IsTrue(result.Results[0].Score > 200); // Should be high
    }
    
    [TestMethod]
    public void Search_FuzzyMatch_FindsSimilar()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            new Document(1L, "batman and robin"),
            new Document(2L, "superman flies high"),
            new Document(3L, "spiderman swings")
        });
        
        // Search with typo "batmam" should still find "batman"
        var result = engine.Search("batmam", maxResults: 10);
        
        Assert.IsTrue(result.Results.Length > 0);
        // Document 1 should be first (contains "batman" which is close to "batmam")
        Assert.AreEqual(1L, result.Results[0].DocumentId);
    }
    
    [TestMethod]
    public void Search_EmptyQuery_ReturnsNoResults()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            new Document(1L, "hello world")
        });
        
        var result = engine.Search("", maxResults: 10);
        
        Assert.AreEqual(0, result.Results.Length);
    }
    
    [TestMethod]
    public void Search_NoMatches_ReturnsEmptyResults()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            new Document(1L, "hello world"),
            new Document(2L, "goodbye world")
        });
        
        var result = engine.Search("xyzabc", maxResults: 10);
        
        // Should return no results or very low scores
        Assert.IsTrue(result.Results.Length == 0 || result.Results[0].Score < 50);
    }
    
    [TestMethod]
    public void Search_MultiWordQuery_RanksRelevance()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            new Document(1L, "the quick brown fox"),
            new Document(2L, "the lazy brown dog"),
            new Document(3L, "a quick decision"),
            new Document(4L, "quick brown")
        });
        
        var result = engine.Search("quick brown", maxResults: 10);
        
        Assert.IsTrue(result.Results.Length > 0);
        
        // Document 4 should rank highest (exact phrase match)
        // Document 1 should also rank high (both words present)
        var top = result.Results[0];
        Assert.IsTrue(top.DocumentId == 4L || top.DocumentId == 1L);
    }
    
    [TestMethod]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        var engine = SearchEngine.CreateDefault();
        
        engine.IndexDocuments(new[]
        {
            new Document(1L, "hello"),
            new Document(2L, "world"),
            new Document(3L, "test")
        });
        
        var stats = engine.GetStatistics();
        
        Assert.AreEqual(3, stats.DocumentCount);
        Assert.IsTrue(stats.VocabularySize > 0);
    }
    
    [TestMethod]
    public void MinimalEngine_WorksWithoutCoverage()
    {
        var engine = SearchEngine.CreateMinimal();
        
        engine.IndexDocuments(new[]
        {
            new Document(1L, "hello world"),
            new Document(2L, "goodbye world")
        });
        
        var result = engine.Search("hello", maxResults: 10);
        
        Assert.IsTrue(result.Results.Length > 0);
        Assert.AreEqual(1L, result.Results[0].DocumentId);
    }
}

