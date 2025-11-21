using Infidex.Coverage;
using Infidex.Tokenization;

namespace Infidex.Tests;

[TestClass]
public class CoverageEngineTests
{
    private CoverageEngine CreateEngine()
    {
        var tokenizer = new Tokenizer(
            new[] { 3 },
            tokenizerSetup: TokenizerSetup.CreateDefault());
        return new CoverageEngine(tokenizer, CoverageSetup.CreateDefault());
    }
    
    [TestMethod]
    public void CalculateCoverageScore_ExactMatch_ReturnsHighScore()
    {
        var engine = CreateEngine();
        
        byte score = engine.CalculateCoverageScore(
            "hello world",
            "this is hello world text",
            0.0,
            out int wordHits);
        
        Assert.IsTrue(score > 200);
        Assert.AreEqual(2, wordHits);
    }
    
    [TestMethod]
    public void CalculateCoverageScore_NoMatch_ReturnsLowScore()
    {
        var engine = CreateEngine();
        
        byte score = engine.CalculateCoverageScore(
            "xyz abc",
            "hello world test",
            0.0,
            out int wordHits);
        
        Assert.IsTrue(score < 100);
    }
    
    [TestMethod]
    public void CalculateCoverageScore_PartialMatch_ReturnsModerateScore()
    {
        var engine = CreateEngine();
        
        byte score = engine.CalculateCoverageScore(
            "hello world test",
            "hello world",
            0.0,
            out int wordHits);
        
        Assert.IsTrue(score > 100);
        Assert.AreEqual(2, wordHits);
    }
    
    [TestMethod]
    public void CalculateCoverageScore_FuzzyMatch_FindsTypo()
    {
        var engine = CreateEngine();
        
        byte score = engine.CalculateCoverageScore(
            "batmam",  // typo
            "batman is a superhero",
            0.0,
            out int wordHits);
        
        Assert.IsTrue(score > 150);
        Assert.IsTrue(wordHits > 0);
    }
    
    [TestMethod]
    public void CalculateCoverageScore_JoinedWords_DetectsCompound()
    {
        var engine = CreateEngine();
        
        byte score = engine.CalculateCoverageScore(
            "new york",
            "I live in newyork city",
            0.0,
            out int wordHits);
        
        // Should detect "newyork" matches "new york"
        Assert.IsTrue(score > 100);
    }
    
    [TestMethod]
    public void CalculateCoverageScore_PrefixMatch_FindsPartialWord()
    {
        var engine = CreateEngine();
        
        byte score = engine.CalculateCoverageScore(
            "bat",
            "batman is a superhero",
            0.0,
            out int wordHits);
        
        // Should detect "bat" is prefix of "batman"
        Assert.IsTrue(score > 50);
    }
    
    [TestMethod]
    public void CalculateCoverageScore_EmptyQuery_ReturnsZero()
    {
        var engine = CreateEngine();
        
        byte score = engine.CalculateCoverageScore(
            "",
            "hello world",
            0.0,
            out int wordHits);
        
        Assert.AreEqual(0, score);
        Assert.AreEqual(0, wordHits);
    }
}
