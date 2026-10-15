namespace Ww.ExiledData.Core.Identity;

public readonly record struct ScreenPoint(int X, int Y)
{
    /// <summary>True when both axes differ by at most <paramref name="tolerance"/> pixels.</summary>
    public bool IsNear(ScreenPoint other, int tolerance) =>
        Math.Abs(X - other.X) <= tolerance && Math.Abs(Y - other.Y) <= tolerance;

    public override string ToString() => $"({X}, {Y})";
}
