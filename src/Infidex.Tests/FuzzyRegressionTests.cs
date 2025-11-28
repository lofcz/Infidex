using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Core;
using Infidex.Api;
using System.Linq;
using System;

namespace Infidex.Tests;

[TestClass]
public class FuzzyRegressionTests
{
    private SearchEngine _engine = null!;
    
    [TestInitialize]
    public void Setup()
    {
        _engine = SearchEngine.CreateDefault();
        
        var docs = new[]
        {
            new Document(1L, "The Mat"),
            new Document(2L, "The Matrix"),
            new Document(3L, "The Matriarx"),
            new Document(4L, "The Match"),
            new Document(5L, "The Meatrix")
        };
        
        _engine.IndexDocuments(docs);
    }
    
    [TestMethod]
    public void Search_TheMatrx_RanksMatrixAboveMat()
    {
        // Query: "the matrx"
        // Target: "The Matrix" (Doc 2) should be higher than "The Mat" (Doc 1)
        // "matrx" is a typo for "matrix".
        // "The Matriarx" (Doc 3) contains "matrx" exactly? If tokenizer splits it? 
        // "Matriarx" -> "matriarx". "matrx" is not "matriarx".
        // Unless "matrx" is in "Matriarx" as n-gram? Yes.
        // But "The Matrix" should beat "The Mat" because "matrx" -> "matrix" (fuzzy) is a whole-word match
        // whereas "The Mat" is only a partial n-gram match (which should be suppressed!).
        
        var result = _engine.Search(new Query("the matrx", 10));
        
        Console.WriteLine("Results for 'the matrx':");
        foreach (var r in result.Records)
        {
            Console.WriteLine($"[{r.Score:F1}] Doc {r.DocumentId}");
        }

        var doc1 = result.Records.FirstOrDefault(r => r.DocumentId == 1L); // The Mat
        var doc2 = result.Records.FirstOrDefault(r => r.DocumentId == 2L); // The Matrix
        
        Assert.IsNotNull(doc2, "The Matrix should be found");
     
        // The Matrix should score higher than The Mat
        Assert.IsTrue(doc2.Score > doc1.Score, 
            $"The Matrix ({doc2.Score}) should rank higher than The Mat ({doc1.Score})");
    }
}
