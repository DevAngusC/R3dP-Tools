using Rhino.PlugIns;

namespace PrintGear;

/// <summary>Entry point for the Rhino3DPrintSupport Rhino plug-in.</summary>
public sealed class PrintGearPlugin : PlugIn
{
    public PrintGearPlugin()
    {
        Instance = this;
    }

    public static PrintGearPlugin? Instance { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;
}
