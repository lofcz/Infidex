namespace Infidex.Internalized.Roaring;

internal abstract class Container : IEquatable<Container>
{
    public const int MaxSize = 4096; // everything <= is an ArrayContainer
    public const int MaxCapacity = 1 << 16;

    protected internal abstract int Cardinality { get; }

    public abstract int ArraySizeInBytes { get; }

    public bool Equals(Container? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return !ReferenceEquals(null, other) && EqualsInternal(other);
    }

    protected abstract bool EqualsInternal(Container other);

    public abstract void EnumerateFill(List<int> list, int key);

    public static Container operator |(Container x, Container y)
    {
        ArrayContainer? xArrayContainer = x as ArrayContainer;
        ArrayContainer? yArrayContainer = y as ArrayContainer;
        if (xArrayContainer != null && yArrayContainer != null)
        {
            return xArrayContainer | yArrayContainer;
        }
        if (xArrayContainer != null)
        {
            return xArrayContainer | (BitmapContainer)y;
        }
        if (yArrayContainer != null)
        {
            return (BitmapContainer) x | yArrayContainer;
        }
        return (BitmapContainer) x | (BitmapContainer)y;
    }

    public static Container operator &(Container x, Container y)
    {
        ArrayContainer? xArrayContainer = x as ArrayContainer;
        ArrayContainer? yArrayContainer = y as ArrayContainer;
        if (xArrayContainer != null && yArrayContainer != null)
        {
            return xArrayContainer & yArrayContainer;
        }
        if (xArrayContainer != null)
        {
            return xArrayContainer & (BitmapContainer) y;
        }
        if (yArrayContainer != null)
        {
            return (BitmapContainer) x & yArrayContainer;
        }
        return (BitmapContainer) x & (BitmapContainer) y;
    }

    public static Container operator ^(Container x, Container y)
    {
        ArrayContainer? xArrayContainer = x as ArrayContainer;
        ArrayContainer? yArrayContainer = y as ArrayContainer;
        if (xArrayContainer != null && yArrayContainer != null)
        {
            return xArrayContainer ^ yArrayContainer;
        }
        if (xArrayContainer != null)
        {
            return xArrayContainer ^ (BitmapContainer)y;
        }
        if (yArrayContainer != null)
        {
            return (BitmapContainer) x ^ yArrayContainer;
        }
        return (BitmapContainer) x ^ (BitmapContainer)y;
    }

    public static Container operator ~(Container x)
    {
        return x is ArrayContainer xArrayContainer ? ~xArrayContainer : ~(BitmapContainer) x;
    }

    public static Container AndNot(Container x, Container y)
    {
        ArrayContainer? xArrayContainer = x as ArrayContainer;
        ArrayContainer? yArrayContainer = y as ArrayContainer;
        if (xArrayContainer != null && yArrayContainer != null)
        {
            return ArrayContainer.AndNot(xArrayContainer, yArrayContainer);
        }
        if (xArrayContainer != null)
        {
            return ArrayContainer.AndNot(xArrayContainer, (BitmapContainer)y);
        }
        return yArrayContainer != null ? BitmapContainer.AndNot((BitmapContainer) x, yArrayContainer) : BitmapContainer.AndNot((BitmapContainer) x, (BitmapContainer)y);
    }
}
