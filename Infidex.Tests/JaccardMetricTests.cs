using Infidex.Metrics;

namespace Infidex.Tests;

[TestClass]
public class JaccardMetricTests
{
    [TestMethod]
    public void JaccardOfAllChars_IdenticalStrings_ReturnsOne()
    {
        var jaccard = new JaccardMetric();
        double similarity = jaccard.JaccardOfAllChars("hello", "hello");
        
        Assert.AreEqual(1.0, similarity, 0.0001);
    }
    
    [TestMethod]
    public void JaccardOfAllChars_CompletelyDifferent_ReturnsZero()
    {
        var jaccard = new JaccardMetric();
        double similarity = jaccard.JaccardOfAllChars("abc", "xyz");
        
        Assert.AreEqual(0.0, similarity, 0.0001);
    }
    
    [TestMethod]
    public void JaccardOfAllChars_PartialOverlap_ReturnsCorrectValue()
    {
        var jaccard = new JaccardMetric();
        // "hello" and "hallo"
        // Frequencies: h:1,e:1,l:2,o:1 vs h:1,a:1,l:2,o:1
        // Intersection: h:1, l:2, o:1 = 4
        // Union: 5 + 5 - 4 = 6
        // Jaccard: 4/6 = 0.666...
        double similarity = jaccard.JaccardOfAllChars("hello", "hallo");
        
        Assert.IsTrue(similarity > 0.6 && similarity < 0.7);
    }
    
    [TestMethod]
    public void JaccardOfCharSet_IdenticalStrings_ReturnsOne()
    {
        var jaccard = new JaccardMetric();
        double similarity = jaccard.JaccardOfCharSet("hello", "hello");
        
        Assert.AreEqual(1.0, similarity, 0.0001);
    }
    
    [TestMethod]
    public void JaccardOfCharSet_CompletelyDifferent_ReturnsZero()
    {
        var jaccard = new JaccardMetric();
        double similarity = jaccard.JaccardOfCharSet("abc", "xyz");
        
        Assert.AreEqual(0.0, similarity, 0.0001);
    }
    
    [TestMethod]
    public void JaccardOfCharSet_WithRepeatedChars_IgnoresFrequency()
    {
        var jaccard = new JaccardMetric();
        // "aaa" has set {a}, "aab" has set {a, b}
        // Intersection: {a} = 1
        // Union: {a, b} = 2
        // Jaccard: 1/2 = 0.5
        double similarity = jaccard.JaccardOfCharSet("aaa", "aab");
        
        Assert.AreEqual(0.5, similarity, 0.0001);
    }
    
    [TestMethod]
    public void JaccardOfAllChars_EmptyStrings_HandlesGracefully()
    {
        var jaccard = new JaccardMetric();
        jaccard.SoughtText = "test".ToCharArray();
        
        // Should not throw, returns 0 for empty document
        double similarity = jaccard.JaccardOfAllChars(Array.Empty<char>());
        Assert.AreEqual(0.0, similarity);
    }
    
    [TestMethod]
    public void ThreadSafety_MultipleAccesses_NoExceptions()
    {
        var jaccard = new JaccardMetric();
        var exceptions = new List<Exception>();
        
        // Run multiple operations in parallel
        Parallel.For(0, 100, i =>
        {
            try
            {
                jaccard.SoughtText = $"query{i}".ToCharArray();
                jaccard.JaccardOfAllChars($"document{i}".ToCharArray());
                jaccard.JaccardOfCharSet($"document{i}".ToCharArray());
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        });
        
        Assert.AreEqual(0, exceptions.Count, "Should not throw exceptions during parallel access");
    }
}


