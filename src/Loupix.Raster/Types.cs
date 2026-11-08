namespace Loupix.Raster;

public enum Axis { Horizontal, Vertical }

public enum Edge { Left, Top, Right, Bottom }

public enum StitchStatus
{
    /// <summary>The first frame was stored.</summary>
    First,
    /// <summary>New content was found and added.</summary>
    Added,
    /// <summary>The frame equals the previous one (end of page): the caller should stop.</summary>
    NoMovement,
    /// <summary>No reliable vertical shift was found; the frame was ignored.</summary>
    NoMatch,
    /// <summary>The result reached the maximum height; content was truncated and further frames are ignored.</summary>
    LimitReached,
}

/// <summary>A 1-D pixel interval; End is exclusive.</summary>
public readonly record struct Span(int Start, int End)
{
    public int Length => End - Start;
}
