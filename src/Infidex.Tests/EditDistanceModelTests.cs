using Infidex.Metrics;

namespace Infidex.Tests;

[TestClass]
public class EditDistanceModelTests
{
    [TestMethod]
    public void MaxEdits_IncreasesMonotonically_WithLength()
    {
        int prev = 0;
        for (int length = 1; length <= 64; length++)
        {
            int current = EditDistanceModel.GetMaxEditsForLength(length);
            
            Assert.IsTrue(current >= prev);
            Assert.IsTrue(current <= length);
            prev = current;
        }
    }

    [TestMethod]
    public void MaxEdits_MatchesExpectedThresholds_ForShortWords()
    {
        // These expectations are derived from the Binomial(L, p) model
        // with p = 0.04 and alpha = 0.01, i.e. the smallest d such that:
        //   Pr[D <= d] >= 0.99
        //
        // Values are chosen where the margin to the 0.99 cutoff is
        // comfortably larger than floating point noise.
        Assert.AreEqual(1, EditDistanceModel.GetMaxEditsForLength(1));  // ~100% mass at <=1
        Assert.AreEqual(1, EditDistanceModel.GetMaxEditsForLength(2));
        Assert.AreEqual(1, EditDistanceModel.GetMaxEditsForLength(3));
        Assert.AreEqual(1, EditDistanceModel.GetMaxEditsForLength(4));
        Assert.AreEqual(2, EditDistanceModel.GetMaxEditsForLength(5));
        Assert.AreEqual(2, EditDistanceModel.GetMaxEditsForLength(6));
        Assert.AreEqual(2, EditDistanceModel.GetMaxEditsForLength(8));
        Assert.AreEqual(2, EditDistanceModel.GetMaxEditsForLength(10));
    }
}


