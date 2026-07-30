using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace PrintGear;

[System.Runtime.InteropServices.Guid("B641CE73-2815-4E4F-834A-8A56D688DA9F")]
public sealed class PrintThreadCommand : Command
{
    private static ThreadParameters lastParameters = new(
        ThreadKind.Pair,
        NominalDiameter: 20.0,
        Pitch: 2.0,
        Length: 16.0,
        RadialClearance: 0.20,
        WallThickness: 3.0,
        LeftHanded: false);

    public override string EnglishName => "PrintThread";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var diameter = new OptionDouble(lastParameters.NominalDiameter, true, RhinoMath.ZeroTolerance);
        var pitch = new OptionDouble(lastParameters.Pitch, true, RhinoMath.ZeroTolerance);
        var length = new OptionDouble(lastParameters.Length, true, RhinoMath.ZeroTolerance);
        var clearance = new OptionDouble(lastParameters.RadialClearance, true, 0.0);
        var wall = new OptionDouble(lastParameters.WallThickness, true, RhinoMath.ZeroTolerance);
        var leftHanded = new OptionToggle(
            lastParameters.LeftHanded,
            new LocalizeStringPair("Right", "右牙"),
            new LocalizeStringPair("Left", "左牙"));
        var types = new[]
        {
            new LocalizeStringPair("Male", "公螺紋"),
            new LocalizeStringPair("Female", "母螺紋"),
            new LocalizeStringPair("Pair", "公母成對")
        };

        var options = new GetOption();
        options.SetCommandPrompt("設定可 3D 列印螺紋參數，完成後按 Enter");
        options.AcceptNothing(true);
        var typeOption = options.AddOptionList(new LocalizeStringPair("Type", "類型"), types, (int)lastParameters.Kind);
        options.AddOptionDouble(new LocalizeStringPair("Diameter", "公稱直徑"), ref diameter);
        options.AddOptionDouble(new LocalizeStringPair("Pitch", "牙距"), ref pitch);
        options.AddOptionDouble(new LocalizeStringPair("Length", "螺紋長度"), ref length);
        options.AddOptionDouble(new LocalizeStringPair("RadialClearance", "單側間隙"), ref clearance);
        options.AddOptionDouble(new LocalizeStringPair("WallThickness", "母件壁厚"), ref wall);
        options.AddOptionToggle(new LocalizeStringPair("Hand", "旋向"), ref leftHanded);

        var kind = lastParameters.Kind;
        while (true)
        {
            var getResult = options.Get();
            if (getResult == GetResult.Cancel)
                return Result.Cancel;
            if (getResult == GetResult.Nothing)
                break;
            if (getResult != GetResult.Option)
                return Result.Failure;

            if (options.OptionIndex() == typeOption)
                kind = (ThreadKind)options.Option().CurrentListOptionIndex;
        }

        var parameters = new ThreadParameters(
            kind,
            diameter.CurrentValue,
            pitch.CurrentValue,
            length.CurrentValue,
            clearance.CurrentValue,
            wall.CurrentValue,
            leftHanded.CurrentValue);
        if (!parameters.IsValid(out var validationError))
        {
            RhinoApp.WriteLine($"列印螺紋：{validationError}");
            return Result.Failure;
        }

        var pointGetter = new GetPoint();
        pointGetter.SetCommandPrompt("選擇螺紋底部的中心點");
        pointGetter.Get();
        if (pointGetter.CommandResult() != Result.Success)
            return pointGetter.CommandResult();

        var origin = pointGetter.Point();
        var objectsAdded = 0;
        if (kind is ThreadKind.Male or ThreadKind.Pair)
        {
            var male = ThreadMeshBuilder.CreateMale(parameters, out var error);
            if (male is null)
            {
                RhinoApp.WriteLine($"列印螺紋：{error}");
                return Result.Failure;
            }

            male.Translate(origin - Point3d.Origin);
            AddThreadObject(doc, male, parameters, "公螺紋");
            objectsAdded++;
        }

        if (kind is ThreadKind.Female or ThreadKind.Pair)
        {
            var female = ThreadMeshBuilder.CreateFemale(parameters, out var error);
            if (female is null)
            {
                RhinoApp.WriteLine($"列印螺紋：{error}");
                return Result.Failure;
            }

            var femaleOrigin = origin;
            if (kind == ThreadKind.Pair)
            {
                var offset = parameters.NominalDiameter / 2.0 + parameters.FemaleOuterRadius + parameters.SuggestedPairGap;
                femaleOrigin += Vector3d.XAxis * offset;
            }

            female.Translate(femaleOrigin - Point3d.Origin);
            AddThreadObject(doc, female, parameters, "母螺紋");
            objectsAdded++;
        }

        lastParameters = parameters;
        doc.Views.Redraw();
        RhinoApp.WriteLine(
            $"列印螺紋：已建立 {objectsAdded} 個封閉網格。" +
            "單側間隙建議先以 FDM 0.15–0.30 mm 為起點。");
        return Result.Success;
    }

    private static void AddThreadObject(RhinoDoc doc, Mesh mesh, ThreadParameters parameters, string type)
    {
        var attributes = new ObjectAttributes
        {
            Name = $"列印螺紋_{type}_直徑{parameters.NominalDiameter:0.###}_牙距{parameters.Pitch:0.###}"
        };
        attributes.SetUserString("PrintThread.Type", type);
        attributes.SetUserString("PrintThread.NominalDiameter", parameters.NominalDiameter.ToString("G"));
        attributes.SetUserString("PrintThread.Pitch", parameters.Pitch.ToString("G"));
        attributes.SetUserString("PrintThread.Length", parameters.Length.ToString("G"));
        attributes.SetUserString("PrintThread.RadialClearance", parameters.RadialClearance.ToString("G"));
        attributes.SetUserString("PrintThread.WallThickness", parameters.WallThickness.ToString("G"));
        attributes.SetUserString("PrintThread.Hand", parameters.LeftHanded ? "左牙" : "右牙");
        doc.Objects.AddMesh(mesh, attributes);
    }
}
