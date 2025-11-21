namespace Infidex.Core;

/// <summary>
/// Represents a tokenized segment of text (n-gram or word).
/// </summary>
public class Shingle
{
    /// <summary>
    /// The actual text content of the shingle
    /// </summary>
    public string Text { get; set; }
    
    /// <summary>
    /// Number of occurrences of this shingle in the source text
    /// </summary>
    public int Occurrences { get; set; }
    
    /// <summary>
    /// Position where this shingle appears in the source text
    /// </summary>
    public int Position { get; set; }
    
    public Shingle(string text, int occurrences, int position)
    {
        Text = text;
        Occurrences = occurrences;
        Position = position;
    }
    
    public override string ToString() => $"{Text} (x{Occurrences})";
    
    public override bool Equals(object? obj)
    {
        if (obj is Shingle other)
            return Text == other.Text;
        return false;
    }
    
    public override int GetHashCode() => Text.GetHashCode();
}


