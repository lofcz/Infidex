using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Core;
using Infidex.Api;

namespace Infidex.Tests;

/// <summary>
/// Tests to ensure our implementation matches the reference output exactly.
/// </summary>
[TestClass]
public class ReferenceMatchingTests
{
    private SearchEngine _engine = null!;
    private Document[] _documents = null!;
    
    [TestInitialize]
    public void Setup()
    {
        _engine = SearchEngine.CreateDefault();
        
        // Same documents as the example
        _documents = new[]
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
        
        _engine.IndexDocuments(_documents);
    }
    
    [TestMethod]
    public void Search_Batman_ReturnsCorrectDocuments()
    {
        var result = _engine.Search(new Query("batman", 10));
        
        // Expected: 1 result, Doc 6 at position 0
        Assert.AreEqual(1, result.Records.Length, "Should return exactly 1 result");
        Assert.AreEqual(6L, result.Records[0].DocumentId, "First result should be Doc 6");
    }
    
    [TestMethod]
    public void Search_QickFux_ReturnsCorrectDocuments()
    {
        var result = _engine.Search(new Query("qick fux", 10));
        
        // Expected: 2 results
        // Position 0: Doc 5
        // Position 1: Doc 1
        Assert.AreEqual(2, result.Records.Length, "Should return exactly 2 results");
        Assert.AreEqual(5L, result.Records[0].DocumentId, "First result should be Doc 5");
        Assert.AreEqual(1L, result.Records[1].DocumentId, "Second result should be Doc 1");
    }
    
    [TestMethod]
    public void Search_Battamam_ReturnsCorrectDocuments()
    {
        var result = _engine.Search(new Query("battamam", 10));
        
        // Debug: Print all results
        Console.WriteLine($"battamam returned {result.Records.Length} results:");
        foreach (var r in result.Records)
        {
            Console.WriteLine($"  [{r.Score:D3}] Doc {r.DocumentId}");
        }
        
        // Expected: 1 result, Doc 6 at position 0
        Assert.AreEqual(1, result.Records.Length, "Should return exactly 1 result");
        Assert.AreEqual(6L, result.Records[0].DocumentId, "First result should be Doc 6");
    }
    
    [TestMethod]
    public void Search_NewYork_ReturnsCorrectDocuments()
    {
        var result = _engine.Search(new Query("new york", 10));
        
        // Expected: 1 result, Doc 8 at position 0
        Assert.AreEqual(1, result.Records.Length, "Should return exactly 1 result");
        Assert.AreEqual(8L, result.Records[0].DocumentId, "First result should be Doc 8");
    }
    
    [TestMethod]
    public void Search_Speeding_ReturnsCorrectDocuments()
    {
        var result = _engine.Search(new Query("speeding", 10));
        
        // Expected: 1 result, Doc 7 at position 0
        Assert.AreEqual(1, result.Records.Length, "Should return exactly 1 result");
        Assert.AreEqual(7L, result.Records[0].DocumentId, "First result should be Doc 7");
    }
}

