using Infidex.Metrics;

namespace Infidex.Tests;

[TestClass]
public class LevenshteinDistanceTests
{
    [TestMethod]
    public void Calculate_IdenticalStrings_ReturnsZero()
    {
        Assert.AreEqual(0, LevenshteinDistance.Calculate("hello", "hello"));
    }
    
    [TestMethod]
    public void Calculate_OneCharDifference_ReturnsOne()
    {
        Assert.AreEqual(1, LevenshteinDistance.Calculate("hello", "hallo"));
    }
    
    [TestMethod]
    public void Calculate_Insertion_ReturnsOne()
    {
        Assert.AreEqual(1, LevenshteinDistance.Calculate("bat", "brat"));
    }
    
    [TestMethod]
    public void Calculate_Deletion_ReturnsOne()
    {
        Assert.AreEqual(1, LevenshteinDistance.Calculate("batman", "batma"));
    }
    
    [TestMethod]
    public void Calculate_CompletelyDifferent_ReturnsMaxLength()
    {
        int distance = LevenshteinDistance.Calculate("abc", "xyz");
        Assert.AreEqual(3, distance);
    }
    
    [TestMethod]
    public void Calculate_EmptyStrings_ReturnsCorrectly()
    {
        Assert.AreEqual(0, LevenshteinDistance.Calculate("", ""));
        Assert.AreEqual(5, LevenshteinDistance.Calculate("hello", ""));
        Assert.AreEqual(5, LevenshteinDistance.Calculate("", "hello"));
    }
    
    [TestMethod]
    public void IsWithinDistance_OneEditAway_ReturnsTrue()
    {
        Assert.IsTrue(LevenshteinDistance.IsWithinDistance("batman", "batmam", 1));
    }
    
    [TestMethod]
    public void IsWithinDistance_TwoEditsAway_ReturnsFalse()
    {
        // "batman" -> "ratmin" requires 2 edits: b→r and a→i
        Assert.IsFalse(LevenshteinDistance.IsWithinDistance("batman", "ratmin", 1));
    }
    
    [TestMethod]
    public void Calculate_LongStrings_UsesFastenshtein()
    {
        // Strings longer than 64 chars use Fastenshtein algorithm
        string longString1 = new string('a', 70) + "test";
        string longString2 = new string('a', 70) + "best";
        
        int distance = LevenshteinDistance.Calculate(longString1, longString2);
        Assert.AreEqual(1, distance); // Only one character different
    }
    
    [TestMethod]
    public void Calculate_Fastenshtein_HandlesEdgeCases()
    {
        // Test various edge cases with reliable Fastenshtein
        Assert.AreEqual(0, LevenshteinDistance.Calculate("", ""));
        Assert.AreEqual(5, LevenshteinDistance.Calculate("hello", ""));
        Assert.AreEqual(5, LevenshteinDistance.Calculate("", "hello"));
        Assert.AreEqual(3, LevenshteinDistance.Calculate("kitten", "sitting"));
        Assert.AreEqual(3, LevenshteinDistance.Calculate("saturday", "sunday"));
    }
}

