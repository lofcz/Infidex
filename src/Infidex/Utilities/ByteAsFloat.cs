namespace Infidex.Utilities;

/// <summary>
/// Provides conversion between float values [0.0, 1.0] and byte values [0, 255].
/// This achieves 4x memory savings by quantizing float weights to bytes.
/// Precision loss is minimal for ranking applications.
/// </summary>
internal static class ByteAsFloat
{
    /// <summary>
    /// Converts a float [0.0, 1.0] to a byte [0, 255]
    /// </summary>
    public static byte FloatToByte(float value)
    {
        return value switch
        {
            <= 0f => 0,
            >= 1f => 255,
            _ => (byte)Math.Min(Math.Round(value * 255f), 255)
        };
    }
    
    /// <summary>
    /// Converts a byte [0, 255] to a float [0.0, 1.0]
    /// </summary>
    public static float ByteToFloat(byte value)
    {
        return value / 255f;
    }
    
    /// <summary>
    /// Short alias for FloatToByte
    /// </summary>
    public static byte F2B(float value) => FloatToByte(value);
}


