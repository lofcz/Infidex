using Infidex.Core;

namespace Infidex.Tests;

[TestClass]
public class ScoreArrayTests
{
    [TestMethod]
    public void Add_SingleEntry_StoresCorrectly()
    {
        var scoreArray = new ScoreArray();
        scoreArray.Add(1, 100);
        
        var results = scoreArray.GetAll();
        Assert.AreEqual(1, results.Length);
        Assert.AreEqual(1L, results[0].DocumentId);
        Assert.AreEqual(100, results[0].Score);
    }
    
    [TestMethod]
    public void GetTopK_ReturnsHighestScoresFirst()
    {
        var scoreArray = new ScoreArray();
        scoreArray.Add(1, 50);
        scoreArray.Add(2, 200);
        scoreArray.Add(3, 100);
        scoreArray.Add(4, 250);
        
        var topTwo = scoreArray.GetTopK(2);
        
        Assert.AreEqual(2, topTwo.Length);
        Assert.AreEqual(4L, topTwo[0].DocumentId); // Score 250
        Assert.AreEqual(2L, topTwo[1].DocumentId); // Score 200
    }
    
    [TestMethod]
    public void GetTopK_WithSameScores_ReturnsAll()
    {
        var scoreArray = new ScoreArray();
        scoreArray.Add(1, 100);
        scoreArray.Add(2, 100);
        scoreArray.Add(3, 100);
        
        var results = scoreArray.GetTopK(2);
        
        // Should get 2 results even though all have same score
        Assert.AreEqual(2, results.Length);
        Assert.AreEqual(100, results[0].Score);
        Assert.AreEqual(100, results[1].Score);
    }
    
    [TestMethod]
    public void Clear_RemovesAllEntries()
    {
        var scoreArray = new ScoreArray();
        scoreArray.Add(1, 100);
        scoreArray.Add(2, 200);
        
        scoreArray.Clear();
        
        Assert.AreEqual(0, scoreArray.Count);
        Assert.AreEqual(0, scoreArray.GetAll().Length);
    }
}


