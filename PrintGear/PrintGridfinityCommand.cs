using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace PrintGear;

[System.Runtime.InteropServices.Guid("C2E995B4-A3A4-46C1-8858-057944592BC4")]
public sealed class PrintGridfinityCommand : Command
{
    private static GridfinityParameters last = new();
    private enum OutputKind { Female, Male, Pair }
    private static OutputKind lastOutput = OutputKind.Pair;
    public override string EnglishName => "PrintGridfinity";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits)
        {
            RhinoApp.WriteLine("請先將文件設定為具體長度單位（例如毫米）。");
            return Result.Failure;
        }
        var columns = new OptionInteger(last.Columns, 1, 20);
        var rows = new OptionInteger(last.Rows, 1, 20);
        var clearance = new OptionDouble(last.Clearance, .1, .4);
        var diameter = new OptionDouble(last.MagnetDiameterAllowance, 0, .8);
        var depth = new OptionDouble(last.MagnetDepthAllowance, 0, .6);
        using var options = new GetOption();
        options.SetCommandPrompt("Gridfinity 公母座方陣：尺寸一律以 mm 輸入，完成後按 Enter");
        options.AcceptNothing(true);
        var output = lastOutput;
        var outputOption = options.AddOptionList(new LocalizeStringPair("Output", "生成內容"),
            new[] { new LocalizeStringPair("Female", "只生成母座"),
                new LocalizeStringPair("Male", "只生成公座"),
                new LocalizeStringPair("Pair", "公母成對") }, (int)output);
        var baseStyle = last.BaseStyle;
        var styleOption = options.AddOptionList(new LocalizeStringPair("BaseStyle", "母座類型"),
            new[] { new LocalizeStringPair("Solid", "實體磁吸母座"),
                new LocalizeStringPair("Lightweight", "鏤空輕量母座") }, (int)baseStyle);
        options.AddOptionInteger(new LocalizeStringPair("Columns", "X格數"), ref columns);
        options.AddOptionInteger(new LocalizeStringPair("Rows", "Y格數"), ref rows);
        options.AddOptionDouble(new LocalizeStringPair("Clearance", "單側間隙mm"), ref clearance);
        options.AddOptionDouble(new LocalizeStringPair("MagnetDiameterAllowance", "磁鐵孔直徑加大量mm"), ref diameter);
        options.AddOptionDouble(new LocalizeStringPair("MagnetDepthAllowance", "磁鐵孔深度加大量mm"), ref depth);
        while (true)
        {
            var result = options.Get();
            if (result == GetResult.Nothing) break;
            if (result != GetResult.Option) return options.CommandResult();
            if (options.OptionIndex() == styleOption)
                baseStyle = (GridfinityBaseStyle)options.Option().CurrentListOptionIndex;
            if (options.OptionIndex() == outputOption)
                output = (OutputKind)options.Option().CurrentListOptionIndex;
        }
        var p = new GridfinityParameters(columns.CurrentValue, rows.CurrentValue, clearance.CurrentValue,
            diameter.CurrentValue, depth.CurrentValue, baseStyle);
        try { p.Validate(); }
        catch (ArgumentException e) { RhinoApp.WriteLine(e.Message); return Result.Failure; }
        using var point = new GetPoint();
        point.SetCommandPrompt(output == OutputKind.Pair
            ? "選擇母座左下角底部位置（World XY，公座並排於右側）"
            : "選擇底座左下角底部位置（World XY）");
        point.Get();
        if (point.CommandResult() != Result.Success) return point.CommandResult();
        var ids = new List<Guid>();
        var cancelled = false;
        void OnEscape(object? sender, EventArgs args) => cancelled = true;
        RhinoApp.EscapeKeyPressed += OnEscape;
        try
        {
            RhinoApp.WriteLine($"建立 {p.Columns} × {p.Rows} Gridfinity 公母座，Esc 可取消…");
            var pair = GridfinityBuilder.Build(p, () => {
                RhinoApp.Wait();
                if (cancelled) throw new OperationCanceledException();
            });
            using var female = pair.Female;
            using var male = pair.Male;
            RhinoApp.Wait();
            if (cancelled) throw new OperationCanceledException();
            if (output == OutputKind.Pair) male.Translate(p.Width+10, 0, 0);
            // The male's standard grid envelope is inset 0.25 mm; anchor its actual corner for single output.
            if (output == OutputKind.Male) male.Translate(-.25, -.25, 0);
            var scale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
            foreach (var (brep, role) in new[] { (female,"Female"), (male,"Male") })
            {
                if ((output == OutputKind.Female && role == "Male")
                    || (output == OutputKind.Male && role == "Female")) continue;
                if (!brep.Transform(Transform.Scale(Point3d.Origin,scale))
                    || !brep.Transform(Transform.Translation(point.Point()-Point3d.Origin)))
                    throw new InvalidOperationException("單位或位置轉換失敗。");
                using var attributes = new ObjectAttributes { Name = $"Gridfinity_{role}_{p.Columns}x{p.Rows}" };
                attributes.SetUserString("PrintGridfinity.Version", "3");
                attributes.SetUserString("PrintGridfinity.Output", output.ToString());
                attributes.SetUserString("PrintGridfinity.BaseStyle", p.BaseStyle.ToString());
                attributes.SetUserString("PrintGridfinity.Role", role);
                attributes.SetUserString("PrintGridfinity.Units", "mm");
                foreach (var (key,value) in new[] { ("Columns",(double)p.Columns), ("Rows",(double)p.Rows),
                    ("Pitch",42.0), ("Clearance",p.Clearance), ("MagnetHoleDiameter",role == "Female" && !p.HasFloor ? 0 : p.HoleDiameter),
                    ("MagnetHoleDepth",role == "Female" && !p.HasFloor ? 0 : p.HoleDepth),
                    ("MagnetHoleCount",role == "Female" ? p.FemaleHoleCount : 4.0*p.Columns*p.Rows),
                    ("AssemblyLift",p.FloorHeight) })
                    attributes.SetUserString("PrintGridfinity."+key,value.ToString("G",CultureInfo.InvariantCulture));
                var id = doc.Objects.AddBrep(brep,attributes);
                if (id == Guid.Empty) throw new InvalidOperationException("新增物件失敗。");
                ids.Add(id);
            }
            last = p;
            lastOutput = output;
            doc.Views.Redraw();
            var description = output == OutputKind.Male ? "只生成公座" :
                (output == OutputKind.Female ? "只生成母座" : "公母成對") +
                $"；母座：{(p.HasFloor ? "實體磁吸" : "鏤空輕量（無底板、無磁鐵孔）")}";
            RhinoApp.WriteLine($"已建立 {ids.Count} 個封閉 Brep（{description}）。單側間隙 {p.Clearance:0.##} mm；建議先試印 1×1 校正配合。");
            return Result.Success;
        }
        catch (Exception e)
        {
            foreach (var id in ids) doc.Objects.Delete(id,true);
            doc.Views.Redraw();
            RhinoApp.WriteLine("Gridfinity："+(e is OperationCanceledException ? "已取消。" : e.Message));
            return e is OperationCanceledException ? Result.Cancel : Result.Failure;
        }
        finally { RhinoApp.EscapeKeyPressed -= OnEscape; }
    }
}
