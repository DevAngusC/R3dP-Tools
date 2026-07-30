using Rhino;

namespace PrintGear;

/// <summary>Thread dimensions in the active Rhino document's units.</summary>
internal readonly record struct ThreadParameters(
    ThreadKind Kind,
    double NominalDiameter,
    double Pitch,
    double Length,
    double RadialClearance,
    double WallThickness,
    bool LeftHanded)
{
    // ISO metric 60-degree threads have a basic radial depth of 0.61343 x pitch.
    // A slightly reduced value makes the generated profile more forgiving for FDM.
    public double ThreadDepth => Pitch * 0.60;
    public double MinorRadius => NominalDiameter / 2.0 - ThreadDepth;
    public double FemaleInnerRadiusAtCrest => NominalDiameter / 2.0 + RadialClearance;
    public double FemaleOuterRadius => FemaleInnerRadiusAtCrest + WallThickness;
    public double SuggestedPairGap => Math.Max(Pitch, RadialClearance * 4.0);

    public bool IsValid(out string error)
    {
        if (NominalDiameter <= RhinoMath.ZeroTolerance)
        {
            error = "公稱直徑必須大於零。";
            return false;
        }

        if (Pitch <= RhinoMath.ZeroTolerance)
        {
            error = "牙距必須大於零。";
            return false;
        }

        if (Length < Pitch * 0.75)
        {
            error = "螺紋長度至少須為牙距的 0.75 倍。";
            return false;
        }

        if (RadialClearance < 0.0)
        {
            error = "單側間隙不可為負值。";
            return false;
        }

        if (MinorRadius <= Math.Max(RhinoMath.ZeroTolerance * 10.0, Pitch * 0.08))
        {
            error = "此牙距對應的公稱直徑過小。";
            return false;
        }

        if (Kind is ThreadKind.Female or ThreadKind.Pair && WallThickness < Pitch * 0.45)
        {
            error = "母螺紋壁厚至少須為牙距的 0.45 倍。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

internal enum ThreadKind
{
    Male,
    Female,
    Pair
}
