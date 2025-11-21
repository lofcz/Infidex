using Microsoft.VisualStudio.TestTools.UnitTesting;
using Infidex.Core;
using Infidex.Api;
using System.IO;

namespace Infidex.Tests;

[TestClass]
public class PersistenceTests
{
    [TestMethod]
    public void SaveAndLoadIndex_PreservesData()
    {
        string filePath = "test_index.bin";
        try
        {
            // 1. Create and index
            var engine = SearchEngine.CreateDefault();
            var documents = new[]
            {
                new Document(1L, "The quick brown fox"),
                new Document(2L, "jumps over the lazy dog")
            };
            engine.IndexDocuments(documents);
            
            // Verify search before save
            var resultsBefore = engine.Search(new Query("fox", 10));
            Assert.AreEqual(1, resultsBefore.Records.Length);
            Assert.AreEqual(1L, resultsBefore.Records[0].DocumentId);
            
            // 2. Save
            engine.Save(filePath);
            
            // 3. Load
            // Use default config for loading (must match what was used for indexing usually)
            var config = ConfigurationParameters.GetConfig(400);
            var loadedEngine = SearchEngine.Load(
                filePath,
                config.IndexSizes,
                config.StartPadSize,
                config.StopPadSize,
                true,
                config.TextNormalizer,
                config.TokenizerSetup,
                null,
                config.StopTermLimit,
                config.WordMatcherSetup
            );
            
            // 4. Verify search after load
            var resultsAfter = loadedEngine.Search(new Query("fox", 10));
            Assert.AreEqual(1, resultsAfter.Records.Length);
            Assert.AreEqual(1L, resultsAfter.Records[0].DocumentId);
            
            // Verify another term
            var resultsDog = loadedEngine.Search(new Query("dog", 10));
            Assert.AreEqual(1, resultsDog.Records.Length);
            Assert.AreEqual(2L, resultsDog.Records[0].DocumentId);
            
            // Verify statistics
            var statsBefore = engine.GetStatistics();
            var statsAfter = loadedEngine.GetStatistics();
            Assert.AreEqual(statsBefore.DocumentCount, statsAfter.DocumentCount);
            Assert.AreEqual(statsBefore.VocabularySize, statsAfter.VocabularySize);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
}
