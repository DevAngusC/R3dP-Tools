using Rhino;

namespace PrintGear;

/// <summary>Dimensions used to generate one spur gear, in the active Rhino document's units.</summary>
public readonly record struct GearParameters(
    int Teeth,
    double Module,
    double PressureAngleDegrees,
    double Thickness,
    double BoreDiameter,
    double Backlash,
    double FlankClearance,
    double BoreCompensation,
    double HubDiameter,
    double HubThickness)
{
    public double PitchRadius => Module * Teeth / 2.0;
    public double OutsideRadius => PitchRadius + Module;
    public double OutsideDiameter => OutsideRadius * 2.0;
    public double RootRadius => Math.Max(Module * (Teeth / 2.0 - 1.25), Module * 0.2);
    public double EffectiveBoreDiameter => Math.Max(0.0, BoreDiameter + BoreCompensation);
    public double EffectiveHubDiameter => Math.Max(HubDiameter, EffectiveBoreDiameter);

    public bool IsValid(out string error)
    {
        if (Teeth is < 6 or > 500)
        {
            error = "齒數必須介於 6 與 500 之間。";
            return false;
        }

        if (Module <= RhinoMath.ZeroTolerance)
        {
            error = "模數必須大於零。";
            return false;
        }

        if (PressureAngleDegrees is < 14.5 or > 35.0)
        {
            error = "壓力角必須介於 14.5° 與 35° 之間。";
            return false;
        }

        if (Thickness <= RhinoMath.ZeroTolerance)
        {
            error = "齒輪厚度必須大於零。";
            return false;
        }

        if (BoreDiameter < 0.0 || BoreCompensation < 0.0 || Backlash < 0.0 || FlankClearance < 0.0)
        {
            error = "軸孔、補償、背隙與齒側間隙不可為負值。";
            return false;
        }

        if (EffectiveBoreDiameter >= RootRadius * 2.0)
        {
            error = "套用補償後的軸孔直徑過大，會超過齒根直徑。";
            return false;
        }

        if (HubDiameter < 0.0 || HubThickness < 0.0 || HubThickness > Thickness)
        {
            error = "輪轂尺寸無效；厚度必須介於 0 與齒輪厚度之間。";
            return false;
        }

        if (EffectiveHubDiameter > RootRadius * 2.0)
        {
            error = "輪轂直徑不可超過齒根直徑。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
