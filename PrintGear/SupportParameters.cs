using Rhino;

namespace PrintGear;

/// <summary>Light-touch support dimensions in the active Rhino document's units.</summary>
internal readonly record struct SupportParameters(
    double Thickness,
    double ContactClearance,
    double ContactWidth)
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

        error = string.Empty;
        return true;
    }

}
