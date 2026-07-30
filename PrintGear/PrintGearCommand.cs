using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace PrintGear;

[System.Runtime.InteropServices.Guid("0AD7E1FE-9D54-44D1-9490-B29FB01EDC03")]
public sealed class PrintGearCommand : Command
{
    private static GearParameters lastParameters = new(
        Teeth: 24,
        Module: 2.0,
        PressureAngleDegrees: 20.0,
        Thickness: 8.0,
        BoreDiameter: 5.0,
        Backlash: 0.15,
        FlankClearance: 0.05,
        BoreCompensation: 0.20,
        HubDiameter: 0.0,
        HubThickness: 0.0);

    public override string EnglishName => "PrintGear";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var teeth = new OptionInteger(lastParameters.Teeth, 6, 500);
        var module = new OptionDouble(lastParameters.Module, true, RhinoMath.ZeroTolerance);
        var pressureAngle = new OptionDouble(lastParameters.PressureAngleDegrees, 14.5, 35.0);
        var thickness = new OptionDouble(lastParameters.Thickness, true, RhinoMath.ZeroTolerance);
        var bore = new OptionDouble(lastParameters.BoreDiameter, true, 0.0);
        var backlash = new OptionDouble(lastParameters.Backlash, true, 0.0);
        var flankClearance = new OptionDouble(lastParameters.FlankClearance, true, 0.0);
        var boreCompensation = new OptionDouble(lastParameters.BoreCompensation, true, 0.0);
        var hubDiameter = new OptionDouble(lastParameters.HubDiameter, true, 0.0);
        var hubThickness = new OptionDouble(lastParameters.HubThickness, true, 0.0);

        var options = new GetOption();
        options.SetCommandPrompt("設定可 3D 列印齒輪參數，完成後按 Enter");
        options.AcceptNothing(true);
        options.AddOptionInteger(new LocalizeStringPair("Teeth", "齒數"), ref teeth);
        options.AddOptionDouble(new LocalizeStringPair("Module", "模數"), ref module);
        options.AddOptionDouble(new LocalizeStringPair("PressureAngle", "壓力角"), ref pressureAngle);
        options.AddOptionDouble(new LocalizeStringPair("Thickness", "齒輪厚度"), ref thickness);
        options.AddOptionDouble(new LocalizeStringPair("BoreDiameter", "軸孔直徑"), ref bore);
        options.AddOptionDouble(new LocalizeStringPair("Backlash", "背隙"), ref backlash);
        options.AddOptionDouble(new LocalizeStringPair("FlankClearance", "齒側間隙"), ref flankClearance);
        options.AddOptionDouble(new LocalizeStringPair("BoreCompensation", "孔徑補償"), ref boreCompensation);
        options.AddOptionDouble(new LocalizeStringPair("HubDiameter", "輪轂直徑"), ref hubDiameter);
        options.AddOptionDouble(new LocalizeStringPair("HubThickness", "輪轂厚度"), ref hubThickness);

        while (true)
        {
            var getResult = options.Get();
            if (getResult == GetResult.Cancel)
                return Result.Cancel;
            if (getResult == GetResult.Nothing)
                break;
            if (getResult != GetResult.Option)
                return Result.Failure;
        }

        var parameters = new GearParameters(
            teeth.CurrentValue,
            module.CurrentValue,
            pressureAngle.CurrentValue,
            thickness.CurrentValue,
            bore.CurrentValue,
            backlash.CurrentValue,
            flankClearance.CurrentValue,
            boreCompensation.CurrentValue,
            hubDiameter.CurrentValue,
            hubThickness.CurrentValue);

        var pointGetter = new GetPoint();
        pointGetter.SetCommandPrompt("選擇齒輪底部的中心點");
        pointGetter.Get();
        if (pointGetter.CommandResult() != Result.Success)
            return pointGetter.CommandResult();

        var gear = InvoluteGearBuilder.Create(parameters, doc.ModelAbsoluteTolerance, out var error);
        if (gear is null)
        {
            RhinoApp.WriteLine($"列印齒輪：{error}");
            return Result.Failure;
        }

        gear.Translate(pointGetter.Point() - Point3d.Origin);
        lastParameters = parameters;

        var attributes = new ObjectAttributes
        {
            Name = $"列印齒輪_齒數{parameters.Teeth}_模數{parameters.Module:0.###}"
        };
        attributes.SetUserString("PrintGear.Teeth", parameters.Teeth.ToString());
        attributes.SetUserString("PrintGear.Module", parameters.Module.ToString("G"));
        attributes.SetUserString("PrintGear.PressureAngle", parameters.PressureAngleDegrees.ToString("G"));
        attributes.SetUserString("PrintGear.Backlash", parameters.Backlash.ToString("G"));
        attributes.SetUserString("PrintGear.FlankClearance", parameters.FlankClearance.ToString("G"));
        attributes.SetUserString("PrintGear.BoreCompensation", parameters.BoreCompensation.ToString("G"));

        doc.Objects.AddBrep(gear, attributes);
        doc.Views.Redraw();
        RhinoApp.WriteLine(
            $"列印齒輪：已建立 {parameters.Teeth} 齒，" +
            $"節圓直徑 {parameters.PitchRadius * 2.0:0.###}，" +
            $"外徑 {parameters.OutsideRadius * 2.0:0.###}。");
        return Result.Success;
    }
}
