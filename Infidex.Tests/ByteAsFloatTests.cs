using Infidex.Utilities;

namespace Infidex.Tests;

[TestClass]
public class ByteAsFloatTests
{
    [TestMethod]
    public void FloatToByte_ZeroReturnsZero()
    {
        Assert.AreEqual(0, ByteAsFloat.FloatToByte(0f));
    }
    
    [TestMethod]
    public void FloatToByte_OneReturns255()
    {
        Assert.AreEqual(255, ByteAsFloat.FloatToByte(1f));
    }
    
    [TestMethod]
    public void FloatToByte_HalfReturns128()
    {
        byte result = ByteAsFloat.FloatToByte(0.5f);
        Assert.IsTrue(result >= 127 && result <= 128);
    }
    
    [TestMethod]
    public void ByteToFloat_ZeroReturnsZero()
    {
        Assert.AreEqual(0f, ByteAsFloat.ByteToFloat(0));
    }
    
    [TestMethod]
    public void ByteToFloat_255ReturnsOne()
    {
        Assert.AreEqual(1f, ByteAsFloat.ByteToFloat(255));
    }
    
    [TestMethod]
    public void RoundTrip_PreservesApproximateValue()
    {
        float original = 0.75f;
        byte quantized = ByteAsFloat.FloatToByte(original);
        float restored = ByteAsFloat.ByteToFloat(quantized);
        
        // Should be within 1/255 tolerance
        Assert.IsTrue(Math.Abs(original - restored) < 0.01f);
    }
}


