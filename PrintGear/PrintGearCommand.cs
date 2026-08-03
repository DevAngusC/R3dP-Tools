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
    private enum GearSizingMode
    {
        TeethAndModule,
        TargetOutsideDiameter,
        TargetOutsideDiameterKeepToothSize
    }

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
    private static GearSizingMode lastSizingMode = GearSizingMode.TeethAndModule;
    private static double lastOutsideDiameter = 52.0;

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
        var outsideDiameter = new OptionDouble(lastOutsideDiameter, true, RhinoMath.ZeroTolerance);
        var sizingModes = new[]
        {
            new LocalizeStringPair("TeethAndModule", "齒數與模數"),
            new LocalizeStringPair("TargetOutsideDiameter", "目標含齒外徑"),
            new LocalizeStringPair("TargetOutsideDiameterKeepToothSize", "目標外徑（保持齒尺寸）")
        };
        var sizingMode = lastSizingMode;

        var options = new GetOption();
        options.SetCommandPrompt("設定可 3D 列印齒輪參數，完成後按 Enter");
        options.AcceptNothing(true);

        while (true)
        {
            options.ClearCommandOptions();
            var sizingOption = options.AddOptionList(
                new LocalizeStringPair("SizingMode", "尺寸模式"),
                sizingModes,
                (int)sizingMode);
            if (sizingMode == GearSizingMode.TeethAndModule)
            {
                options.AddOptionInteger(new LocalizeStringPair("Teeth", "齒數"), ref teeth);
                options.AddOptionDouble(new LocalizeStringPair("Module", "模數"), ref module);
            }
            else if (sizingMode == GearSizingMode.TargetOutsideDiameter)
            {
                options.AddOptionInteger(new LocalizeStringPair("Teeth", "齒數"), ref teeth);
                options.AddOptionDouble(
                    new LocalizeStringPair("OutsideDiameter", "含齒外徑"),
                    ref outsideDiameter);
            }
            else
            {
                options.AddOptionDouble(
                    new LocalizeStringPair("ToothModule", "齒尺寸模數"),
                    ref module);
                options.AddOptionDouble(
                    new LocalizeStringPair("OutsideDiameter", "目標含齒外徑"),
                    ref outsideDiameter);
            }
            options.AddOptionDouble(new LocalizeStringPair("PressureAngle", "壓力角"), ref pressureAngle);
            options.AddOptionDouble(new LocalizeStringPair("Thickness", "齒輪厚度"), ref thickness);
            options.AddOptionDouble(new LocalizeStringPair("BoreDiameter", "軸孔直徑"), ref bore);
            options.AddOptionDouble(new LocalizeStringPair("Backlash", "背隙"), ref backlash);
            options.AddOptionDouble(new LocalizeStringPair("FlankClearance", "齒側間隙"), ref flankClearance);
            options.AddOptionDouble(new LocalizeStringPair("BoreCompensation", "孔徑補償"), ref boreCompensation);
            options.AddOptionDouble(new LocalizeStringPair("HubDiameter", "輪轂直徑"), ref hubDiameter);
            options.AddOptionDouble(new LocalizeStringPair("HubThickness", "輪轂厚度"), ref hubThickness);

            var getResult = options.Get();
            if (getResult == GetResult.Cancel)
                return Result.Cancel;
            if (getResult == GetResult.Nothing)
                break;
            if (getResult != GetResult.Option)
                return Result.Failure;

            if (options.OptionIndex() == sizingOption)
                sizingMode = (GearSizingMode)options.Option().CurrentListOptionIndex;
        }

        var selectedTeeth = teeth.CurrentValue;
        var selectedModule = module.CurrentValue;
        double? requestedOutsideDiameter = null;
        var toothSizePreserved = false;
        if (sizingMode == GearSizingMode.TargetOutsideDiameter)
        {
            requestedOutsideDiameter = outsideDiameter.CurrentValue;
            selectedModule = requestedOutsideDiameter.Value / (selectedTeeth + 2.0);
            if (!RhinoMath.IsValidDouble(selectedModule) || selectedModule <= RhinoMath.ZeroTolerance)
            {
                RhinoApp.WriteLine("PrintGear：目標含齒外徑必須大於零。");
                return Result.Failure;
            }
        }
        else if (sizingMode == GearSizingMode.TargetOutsideDiameterKeepToothSize)
        {
            requestedOutsideDiameter = outsideDiameter.CurrentValue;
            selectedModule = module.CurrentValue;
            if (!RhinoMath.IsValidDouble(selectedModule) || selectedModule <= RhinoMath.ZeroTolerance)
            {
                RhinoApp.WriteLine("PrintGear：保持齒尺寸時，齒尺寸模數必須大於零。");
                return Result.Failure;
            }

            var targetTeeth = requestedOutsideDiameter.Value / selectedModule - 2.0;
            var minimumOutsideDiameter = selectedModule * (6.0 + 2.0);
            var maximumOutsideDiameter = selectedModule * (500.0 + 2.0);
            if (!RhinoMath.IsValidDouble(targetTeeth) ||
                requestedOutsideDiameter.Value < minimumOutsideDiameter - RhinoMath.ZeroTolerance ||
                requestedOutsideDiameter.Value > maximumOutsideDiameter + RhinoMath.ZeroTolerance)
            {
                RhinoApp.WriteLine(
                    $"PrintGear：保持齒尺寸時，目標外徑必須介於 {minimumOutsideDiameter:0.###} " +
                    $"與 {maximumOutsideDiameter:0.###} 之間。");
                return Result.Failure;
            }

            selectedTeeth = (int)Math.Round(targetTeeth, MidpointRounding.AwayFromZero);
            if (selectedTeeth is < 6 or > 500)
            {
                RhinoApp.WriteLine("PrintGear：保持齒尺寸時，自動換算的齒數必須介於 6 與 500 之間。");
                return Result.Failure;
            }

            toothSizePreserved = true;
        }

        var parameters = new GearParameters(
            selectedTeeth,
            selectedModule,
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
        attributes.SetUserString("PrintGear.OutsideDiameter", parameters.OutsideDiameter.ToString("G"));
        attributes.SetUserString("PrintGear.SizingMode", sizingMode.ToString());
        attributes.SetUserString("PrintGear.ToothSizePreserved", toothSizePreserved.ToString());
        if (requestedOutsideDiameter.HasValue)
            attributes.SetUserString("PrintGear.RequestedOutsideDiameter", requestedOutsideDiameter.Value.ToString("G"));
        attributes.SetUserString("PrintGear.PressureAngle", parameters.PressureAngleDegrees.ToString("G"));
        attributes.SetUserString("PrintGear.Backlash", parameters.Backlash.ToString("G"));
        attributes.SetUserString("PrintGear.FlankClearance", parameters.FlankClearance.ToString("G"));
        attributes.SetUserString("PrintGear.BoreCompensation", parameters.BoreCompensation.ToString("G"));

        doc.Objects.AddBrep(gear, attributes);
        doc.Views.Redraw();
        lastSizingMode = sizingMode;
        lastOutsideDiameter = parameters.OutsideDiameter;
        RhinoApp.WriteLine(
            $"列印齒輪：已建立 {parameters.Teeth} 齒，" +
            $"節圓直徑 {parameters.PitchRadius * 2.0:0.###}，" +
            $"外徑 {parameters.OutsideDiameter:0.###}。");
        if (toothSizePreserved && requestedOutsideDiameter.HasValue)
        {
            RhinoApp.WriteLine(
                $"PrintGear：已保持模數 {parameters.Module:0.###}；目標外徑 " +
                $"{requestedOutsideDiameter.Value:0.###} 對應為 {parameters.Teeth} 齒，" +
                $"實際外徑 {parameters.OutsideDiameter:0.###}。");
        }
        return Result.Success;
    }
}
