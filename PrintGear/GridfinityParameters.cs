namespace PrintGear;

// All dimensions are millimetres, independently of Rhino document units.
internal enum GridfinityBaseStyle { Solid, Lightweight }

internal sealed record GridfinityParameters(int Columns = 2, int Rows = 2,
    double Clearance = 0.25, double MagnetDiameterAllowance = 0.5,
    double MagnetDepthAllowance = 0.4, GridfinityBaseStyle BaseStyle = GridfinityBaseStyle.Solid)
{
    public const double Pitch = 42;
    public const double Floor = 3.2;
    public const double SocketHeight = 4.4; // Truncate the standard 4.65 mm socket before its zero-width rim.
    public const double FootHeight = 4.75;
    public const double DeckThickness = 2.25;
    public bool HasFloor => BaseStyle == GridfinityBaseStyle.Solid;
    public double FloorHeight => HasFloor ? Floor : 0;
    public int FemaleHoleCount => HasFloor ? 4 * Columns * Rows : 0;
    public double Width => Columns * Pitch;
    public double Length => Rows * Pitch;
    public double HoleDiameter => 6 + MagnetDiameterAllowance;
    public double HoleDepth => 2 + MagnetDepthAllowance;

    public void Validate()
    {
        if (!Enum.IsDefined(BaseStyle)) throw new ArgumentException("無效的母座類型。");
        if (Columns < 1 || Rows < 1 || Columns > 20 || Rows > 20 || Columns * Rows > 100)
            throw new ArgumentException("每邊需為 1–20 格，總格數不得超過 100。");
        if (!double.IsFinite(Clearance) || Clearance < 0.1 || Clearance > 0.4)
            throw new ArgumentException("單側間隙需為 0.10–0.40 mm。");
        if (!double.IsFinite(MagnetDiameterAllowance) || MagnetDiameterAllowance < 0 || MagnetDiameterAllowance > 0.8
            || !double.IsFinite(MagnetDepthAllowance) || MagnetDepthAllowance < 0 || MagnetDepthAllowance > 0.6)
            throw new ArgumentException("磁鐵孔直徑加大量需為 0–0.8 mm，深度加大量需為 0–0.6 mm。");
    }
}
