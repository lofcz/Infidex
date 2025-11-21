using Infidex.Api;
using Infidex.Filtering;

namespace Infidex.Tests;

[TestClass]
public class FilterTests
{
    [TestMethod]
    public void ValueFilter_ExactMatch_ReturnsTrue()
    {
        var filter = new ValueFilter("status", "active");
        
        Assert.IsTrue(filter.Matches("active"));
        Assert.IsFalse(filter.Matches("inactive"));
    }
    
    [TestMethod]
    public void RangeFilter_WithinRange_ReturnsTrue()
    {
        var filter = new RangeFilter("price", 10, 100);
        
        Assert.IsTrue(filter.Matches(50));
        Assert.IsTrue(filter.Matches(10)); // Inclusive by default
        Assert.IsTrue(filter.Matches(100)); // Inclusive by default
        Assert.IsFalse(filter.Matches(5));
        Assert.IsFalse(filter.Matches(150));
    }
    
    [TestMethod]
    public void FilterMask_AndOperation_CombinesCorrectly()
    {
        var mask1 = new FilterMask(10);
        mask1.Add(1);
        mask1.Add(2);
        mask1.Add(3);
        
        var mask2 = new FilterMask(10);
        mask2.Add(2);
        mask2.Add(3);
        mask2.Add(4);
        
        var result = mask1.And(mask2);
        
        Assert.IsTrue(result.IsInFilter(2));
        Assert.IsTrue(result.IsInFilter(3));
        Assert.IsFalse(result.IsInFilter(1));
        Assert.IsFalse(result.IsInFilter(4));
    }
    
    [TestMethod]
    public void FilterMask_OrOperation_CombinesCorrectly()
    {
        var mask1 = new FilterMask(10);
        mask1.Add(1);
        mask1.Add(2);
        
        var mask2 = new FilterMask(10);
        mask2.Add(3);
        mask2.Add(4);
        
        var result = mask1.Or(mask2);
        
        Assert.IsTrue(result.IsInFilter(1));
        Assert.IsTrue(result.IsInFilter(2));
        Assert.IsTrue(result.IsInFilter(3));
        Assert.IsTrue(result.IsInFilter(4));
    }
    
    [TestMethod]
    public void FilterMask_NotOperation_InvertsCorrectly()
    {
        var mask = new FilterMask(5);
        mask.Add(1);
        mask.Add(3);
        
        var result = mask.Not();
        
        Assert.IsFalse(result.IsInFilter(1));
        Assert.IsTrue(result.IsInFilter(0));
        Assert.IsTrue(result.IsInFilter(2));
        Assert.IsFalse(result.IsInFilter(3));
        Assert.IsTrue(result.IsInFilter(4));
    }
}


