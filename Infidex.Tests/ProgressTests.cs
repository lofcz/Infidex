using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Core;
using System.Collections.Generic;

namespace Infidex.Tests;

[TestClass]
public class ProgressTests
{
    [TestMethod]
    public void IndexDocuments_ReportsProgress()
    {
        var engine = SearchEngine.CreateDefault();
        var documents = new List<Document>();
        for (int i = 0; i < 100; i++)
        {
            documents.Add(new Document(i, $"Document {i} content"));
        }
        
        var progressValues = new List<int>();
        engine.ProgressChanged += (sender, progress) => 
        {
            progressValues.Add(progress);
        };
        
        engine.IndexDocuments(documents);
        
        Assert.IsTrue(progressValues.Count > 0);
        Assert.IsTrue(progressValues[0] >= 0);
        Assert.IsTrue(progressValues[^1] == 100);
        
        // Check if we got updates from both phases
        // Phase 1: 0-50
        // Phase 2: 50-100
        bool hasPhase1 = false;
        bool hasPhase2 = false;
        foreach (var p in progressValues)
        {
            if (p > 0 && p < 50) hasPhase1 = true;
            if (p > 50 && p < 100) hasPhase2 = true;
        }
        
        Assert.IsTrue(hasPhase1, "Should have progress from adding documents");
        Assert.IsTrue(hasPhase2, "Should have progress from calculating weights");
    }
}
