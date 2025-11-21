using Infidex.Core;
using Infidex.WordMatcher;

namespace Infidex.Tests;

[TestClass]
public class WordMatcherTests
{
    [TestMethod]
    public void Lookup_ExactMatch_FindsDocument()
    {
        var setup = new WordMatcherSetup(
            MaximumWordSizeExact: 10,
            MinimumWordSizeExact: 2,
            SupportLD1: false,
            SupportAffix: false);
        
        var delimiters = new[] { ' ', ',' };
        var matcher = new WordMatcher.WordMatcher(setup, delimiters);
        
        matcher.Load("hello world test", 0);
        matcher.Load("goodbye world", 1);
        
        var results = matcher.Lookup("world");
        
        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Contains(0));
        Assert.IsTrue(results.Contains(1));
    }
    
    [TestMethod]
    public void Lookup_LD1Support_FindsFuzzyMatches()
    {
        var setup = new WordMatcherSetup(
            MaximumWordSizeLD1: 10,
            MinimumWordSizeLD1: 3,
            SupportLD1: true,
            SupportAffix: false);
        
        var delimiters = new[] { ' ' };
        var matcher = new WordMatcher.WordMatcher(setup, delimiters);
        
        matcher.Load("batman is here", 0);
        
        // "batmam" is 1 edit away from "batman"
        var results = matcher.Lookup("batmam");
        
        Assert.IsTrue(results.Count > 0);
        Assert.IsTrue(results.Contains(0));
    }
    
    [TestMethod]
    public void LookupAffix_FindsPrefixMatches()
    {
        var setup = new WordMatcherSetup(
            MaximumWordSizeExact: 10,
            MinimumWordSizeExact: 2,
            SupportAffix: true);
        
        var delimiters = new[] { ' ' };
        var matcher = new WordMatcher.WordMatcher(setup, delimiters);
        
        matcher.Load("batman superman spiderman", 0);
        
        // "bat" is a prefix of "batman"
        var results = matcher.LookupAffix("bat");
        
        Assert.IsTrue(results.Count > 0);
        Assert.IsTrue(results.Contains(0));
    }
}


