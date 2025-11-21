using Infidex.Tokenization;

namespace Infidex.Tests;

[TestClass]
public class TokenizerTests
{
    [TestMethod]
    public void TokenizeForIndexing_SimpleText_GeneratesNGrams()
    {
        var tokenizer = new Tokenizer(new[] { 2, 3 }, startPadSize: 2, stopPadSize: 0);
        var shingles = tokenizer.TokenizeForIndexing("bat");
        
        // Should generate 2-grams and 3-grams with padding
        Assert.IsTrue(shingles.Count > 0);
        Assert.IsTrue(shingles.Any(s => s.Text.Contains("ba")));
        Assert.IsTrue(shingles.Any(s => s.Text.Contains("at")));
    }
    
    [TestMethod]
    public void TokenizeForSearch_ExtractsWords()
    {
        var tokenizerSetup = TokenizerSetup.CreateDefault();
        var tokenizer = new Tokenizer(
            new[] { 3 }, 
            startPadSize: 2, 
            tokenizerSetup: tokenizerSetup);
        
        var shingles = tokenizer.TokenizeForSearch("hello world", out var dict, false);
        
        Assert.IsTrue(shingles.Any(s => s.Text == "hello"));
        Assert.IsTrue(shingles.Any(s => s.Text == "world"));
    }
    
    [TestMethod]
    public void GetWordTokensForCoverage_SplitsCorrectly()
    {
        var tokenizerSetup = TokenizerSetup.CreateDefault();
        var tokenizer = new Tokenizer(new[] { 3 }, tokenizerSetup: tokenizerSetup);
        
        var words = tokenizer.GetWordTokensForCoverage("hello world test", minWordSize: 2);
        
        Assert.AreEqual(3, words.Count);
        Assert.IsTrue(words.Contains("hello"));
        Assert.IsTrue(words.Contains("world"));
        Assert.IsTrue(words.Contains("test"));
    }
    
    [TestMethod]
    public void TokenizeForIndexing_WithNormalizer_AppliesNormalization()
    {
        var normalizer = new TextNormalizer(
            new Dictionary<string, string> { { "test", "best" } },
            new Dictionary<char, char>());
        
        var tokenizer = new Tokenizer(
            new[] { 4 }, 
            textNormalizer: normalizer);
        
        var shingles = tokenizer.TokenizeForIndexing("test");
        
        // Should contain "best" instead of "test"
        Assert.IsTrue(shingles.Any(s => s.Text.Contains("best")));
    }
}


