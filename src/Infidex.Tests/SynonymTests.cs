using Infidex.Api;
using Infidex.Core;
using Infidex.Synonyms;

namespace Infidex.Tests;

[TestClass]
public class SynonymTests
{
    [TestMethod]
    public void AddSynonym_CreatesBidirectionalMapping()
    {
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonym("car", "automobile");
        
        var carSynonyms = synonymMap.GetSynonyms("car");
        var autoSynonyms = synonymMap.GetSynonyms("automobile");
        
        Assert.IsTrue(carSynonyms.Contains("automobile"));
        Assert.IsTrue(autoSynonyms.Contains("car"));
    }
    
    [TestMethod]
    public void AddSynonymGroup_CreatesFullMesh()
    {
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonymGroup("car", "automobile", "vehicle");
        
        var carSynonyms = synonymMap.GetSynonyms("car");
        Assert.IsTrue(carSynonyms.Contains("automobile"));
        Assert.IsTrue(carSynonyms.Contains("vehicle"));
        
        var autoSynonyms = synonymMap.GetSynonyms("automobile");
        Assert.IsTrue(autoSynonyms.Contains("car"));
        Assert.IsTrue(autoSynonyms.Contains("vehicle"));
        
        var vehicleSynonyms = synonymMap.GetSynonyms("vehicle");
        Assert.IsTrue(vehicleSynonyms.Contains("car"));
        Assert.IsTrue(vehicleSynonyms.Contains("automobile"));
    }
    
    [TestMethod]
    public void SynonymMap_IsCaseInsensitive()
    {
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonym("Car", "Automobile");
        
        var carSynonyms = synonymMap.GetSynonyms("CAR");
        Assert.IsTrue(carSynonyms.Contains("automobile"));
        Assert.IsTrue(carSynonyms.Contains("Automobile"));
    }
    
    [TestMethod]
    public void GetSynonyms_ReturnsEmptySetForUnknownTerm()
    {
        var synonymMap = new SynonymMap();
        var synonyms = synonymMap.GetSynonyms("unknown");
        
        Assert.IsNotNull(synonyms);
        Assert.AreEqual(0, synonyms.Count);
    }
    
    [TestMethod]
    public void Clear_RemovesAllSynonyms()
    {
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonym("car", "automobile");
        
        Assert.AreEqual(2, synonymMap.Count);
        
        synonymMap.Clear();
        
        Assert.AreEqual(0, synonymMap.Count);
        Assert.IsFalse(synonymMap.HasSynonyms("car"));
    }
    
    [TestMethod]
    public void SearchEngine_WithSynonyms_IsAccessible()
    {
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonym("car", "automobile");
        
        var engine = new SearchEngine(
            indexSizes: [4, 5, 6],
            synonymMap: synonymMap);
        
        Assert.IsNotNull(engine.SynonymMap);
        Assert.AreSame(synonymMap, engine.SynonymMap);
    }
    
    [TestMethod]
    public void Search_WithSynonyms_FindsBothTerms()
    {
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonym("car", "automobile");
        
        var engine = new SearchEngine(
            indexSizes: [4, 5, 6],
            synonymMap: synonymMap);
        
        // Index documents
        engine.IndexDocuments([
            new Document(1L,"I drive a car to work" ),
            new Document(2L, "This automobile is fast"),
            new Document(3L, "The truck is big")
        ]);
        
        // Search for "car" should find both car and automobile docs
        var results = engine.Search(new Query("car", 10));
        
        Assert.IsTrue(results.Records.Length >= 2, "Should find at least 2 documents (car and automobile)");
        
        var foundIds = new HashSet<long>(results.Records.Select(r => r.DocumentId));
        Assert.IsTrue(foundIds.Contains(1), "Should find document with 'car'");
        Assert.IsTrue(foundIds.Contains(2), "Should find document with 'automobile' (synonym)");
    }
    
    [TestMethod]
    public void Search_WithSynonyms_WorksBothDirections()
    {
        var synonymMap = new SynonymMap();
        synonymMap.AddSynonym("car", "automobile");
        
        var engine = new SearchEngine(
            indexSizes: [4, 5, 6],
            synonymMap: synonymMap);
        
        engine.IndexDocuments([
            new Document(1L,"I drive a car to work" ),
            new Document(2L, "This automobile is fast"),
        ]);
        
        // Search for "automobile" should also find "car" docs
        var results = engine.Search(new Query("automobile", 10));
        
        Assert.IsTrue(results.Records.Length >= 2, "Should find both documents");
        
        var foundIds = new HashSet<long>(results.Records.Select(r => r.DocumentId));
        Assert.IsTrue(foundIds.Contains(1), "Should find document with 'car' (synonym)");
        Assert.IsTrue(foundIds.Contains(2), "Should find document with 'automobile'");
    }
}

