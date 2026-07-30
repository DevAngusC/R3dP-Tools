using Rhino;

namespace PrintGear;

/// <summary>Light-touch support dimensions in the active Rhino document's units.</summary>
internal readonly record struct SupportParameters(
    double Thickness,
    double ContactClearance,
    double ContactWidth,
    double ContactSpacing,
    double FootExtension,
    double BaseHeight)
{
    public bool IsValid(double tolerance, out string error)
    {
        if (Thickness <= tolerance)
        {
            error = "支撐厚度必須大於文件絕對公差。";
            return false;
        }

        if (ContactClearance < 0.0)
        {
            error = "接觸退讓量不可為負值。";
            return false;
        }

        if (ContactWidth <= tolerance)
        {
            error = "接觸齒寬必須大於文件絕對公差。";
            return false;
        }

        if (ContactSpacing < ContactWidth)
        {
            error = "接觸齒間距不可小於接觸齒寬。";
            return false;
        }

        if (FootExtension < 0.0)
        {
            error = "底腳延伸量不可為負值。";
            return false;
        }

        if (!RhinoMath.IsValidDouble(BaseHeight))
        {
            error = "列印平台高度不是有效數值。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public double SamplingStep(double tolerance)
    {
        var featureStep = Math.Min(ContactWidth / 3.0, ContactSpacing / 12.0);
        return Math.Max(tolerance * 2.0, featureStep);
    }
}
