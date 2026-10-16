using System.Drawing;
using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace PrintGear;

[System.Runtime.InteropServices.Guid("9D5E15F9-26D2-4AB7-82E6-B7745E0F3EB1")]
public sealed class PrintSeamGrooveCommand : Command
{
    private static SeamGrooveParameters lastMillimeters = new(0.6, 0.3, 0.2, 0.0, 0.0);
    public override string EnglishName => "PrintSeamGroove";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        using var selection = new GetObject();
        selection.SetCommandPrompt("選取溝槽開口中心線：曲線、多重曲線或可相接的線段／邊緣，完成後 Enter");
        selection.GeometryFilter = ObjectType.Curve;
        selection.SubObjectSelect = true;
        selection.GetMultiple(1, 0);
        if (selection.CommandResult() != Result.Success) return selection.CommandResult();

        Curve rail;
        try { rail = SeamGrooveBuilder.JoinRails(selection.Objects().Select(o => o.Curve()), doc.ModelAbsoluteTolerance); }
        catch (Exception ex) { RhinoApp.WriteLine(ex.Message); return Result.Failure; }
        using (rail)
        {
            using var target = new GetObject();
            target.SetCommandPrompt("選取要開槽的參考面（可 Ctrl+Shift 選子面，可多選）；Enter 改用手動方向");
            target.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
            target.SubObjectSelect = true;
            target.EnablePreSelect(false, true);
            target.AcceptNothing(true);
            var targetResult = target.GetMultiple(1, 0);
            if (targetResult == GetResult.Cancel) return Result.Cancel;
            if (targetResult != GetResult.Object && targetResult != GetResult.Nothing) return Result.Failure;
            var faces = new List<BrepFace>();
            if (targetResult == GetResult.Object)
            {
                foreach (var item in target.Objects())
                {
                    var face = item.Face();
                    if (face is not null) faces.Add(face);
                    else if (item.Brep() is { } brep) faces.AddRange(brep.Faces);
                }
                if (faces.Count == 0) { RhinoApp.WriteLine("沒有取得有效參考面。"); return Result.Failure; }
            }
            var surfaceMode = faces.Count > 0;
            var flipNormals = false;
            var inward = Vector3d.ZAxis;
            if (surfaceMode)
                RhinoApp.WriteLine("貼面模式：尖端沿參考面的反法線切入；開放曲面請檢查預覽並視需要翻向。");
            else
            {
                using var direction = new GetPoint();
                direction.SetCommandPrompt("由路徑起點，點選三角尖端切入材料的方向（方向會投影至起點垂直截面）");
                direction.SetBasePoint(rail.PointAtStart, true);
                direction.DrawLineFromPoint(rail.PointAtStart, true);
                direction.Get();
                if (direction.CommandResult() != Result.Success) return direction.CommandResult();
                inward = direction.Point() - rail.PointAtStart;
                var tangent = rail.TangentAtStart;
                tangent.Unitize();
                inward -= tangent * (inward * tangent);
                if (!inward.Unitize())
                {
                    RhinoApp.WriteLine("尖端方向不可平行於路徑切線或與起點重合，請重新執行。");
                    return Result.Failure;
                }
            }

            var scale = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);
            if (!RhinoMath.IsValidDouble(scale) || scale <= 0) return Result.Failure;
            var width = new OptionDouble(lastMillimeters.Width * scale, true, 0);
            var depth = new OptionDouble(lastMillimeters.Depth * scale, true, 0);
            var overcut = new OptionDouble(lastMillimeters.Overcut * scale, true, 0);
            var extension = new OptionDouble(lastMillimeters.EndExtension * scale, true, 0);
            var rotation = new OptionDouble(0);
            using var options = new GetOption();
            options.SetCommandPrompt($"預覽接縫溝槽刀具（{doc.ModelUnitSystem}）；調整參數，Enter 建立，Esc 取消");
            options.AcceptNothing(true);
            options.AddOptionDouble(new LocalizeStringPair("Width", "槽口寬度"), ref width);
            options.AddOptionDouble(new LocalizeStringPair("Depth", "槽深"), ref depth);
            options.AddOptionDouble(new LocalizeStringPair("Overcut", "外側延伸"), ref overcut);
            if (!rail.IsClosed && !surfaceMode)
                options.AddOptionDouble(new LocalizeStringPair("EndExtension", "端部延長"), ref extension);
            if (!surfaceMode)
                options.AddOptionDouble(new LocalizeStringPair("Rotation", "截面旋轉角度"), ref rotation);
            var flip = options.AddOption(new LocalizeStringPair("Flip", "尖端翻向"));
            var preview = new SeamGroovePreview();
            try
            {
                while (true)
                {
                    preview.Solid?.Dispose();
                    preview.Solid = null;
                    var p = new SeamGrooveParameters(width.CurrentValue, depth.CurrentValue, overcut.CurrentValue,
                        rail.IsClosed || surfaceMode ? 0 : extension.CurrentValue, surfaceMode ? 0 : rotation.CurrentValue);
                    try { preview.Solid = SeamGrooveBuilder.Build(rail, inward, p, doc.ModelAbsoluteTolerance, faces, flipNormals); }
                    catch (Exception ex) { RhinoApp.WriteLine($"無有效預覽：{ex.Message} 可調整參數後重試。"); }
                    preview.Enabled = preview.Solid is not null;
                    doc.Views.Redraw();
                    var result = options.Get();
                    if (result == GetResult.Cancel) return Result.Cancel;
                    if (result == GetResult.Option)
                    {
                        if (options.OptionIndex() == flip)
                        {
                            if (surfaceMode) flipNormals = !flipNormals;
                            else inward = -inward;
                        }
                        continue;
                    }
                    if (result != GetResult.Nothing) return Result.Failure;
                    if (preview.Solid is null) continue;
                    var attributes = new ObjectAttributes
                    {
                        Name = "PrintSeamGroove_Cutter", LayerIndex = doc.Layers.CurrentLayerIndex,
                        ColorSource = ObjectColorSource.ColorFromObject, ObjectColor = Color.DarkOrange
                    };
                    void Put(string key, double value) => attributes.SetUserString("PrintSeamGroove." + key,
                        value.ToString("R", CultureInfo.InvariantCulture));
                    Put("Width", p.Width); Put("Depth", p.Depth); Put("Overcut", p.Overcut);
                    Put("EndExtension", p.EndExtension); Put("RotationDegrees", p.RotationDegrees);
                    Put("Tolerance", doc.ModelAbsoluteTolerance);
                    attributes.SetUserString("PrintSeamGroove.Units", doc.ModelUnitSystem.ToString());
                    attributes.SetUserString("PrintSeamGroove.RailRole", "GrooveMouthCenter");
                    attributes.SetUserString("PrintSeamGroove.OrientationMode", surfaceMode ? "SurfaceNormal" : "Manual");
                    attributes.SetUserString("PrintSeamGroove.NormalFlipped", flipNormals.ToString());
                    if (surfaceMode)
                        attributes.SetUserString("PrintSeamGroove.ReferenceFaces", string.Join(";",
                            target.Objects().Select(o => $"{o.ObjectId}:{o.GeometryComponentIndex.Index}")));
                    attributes.SetUserString("PrintSeamGroove.SourceObjects", string.Join(";", selection.Objects().Select(o => o.ObjectId)));
                    if (!surfaceMode) attributes.SetUserString("PrintSeamGroove.StartInward", inward.ToString());
                    var id = doc.Objects.AddBrep(preview.Solid, attributes);
                    if (id == Guid.Empty) return Result.Failure;
                    lastMillimeters = new(p.Width / scale, p.Depth / scale, p.Overcut / scale, p.EndExtension / scale, 0);
                    doc.Objects.Select(id);
                    RhinoApp.WriteLine($"已建立封閉接縫溝槽刀具：槽寬 {p.Width:g}、槽深 {p.Depth:g}。使用 BooleanDifference，先選構件，再選橘色刀具。");
                    return Result.Success;
                }
            }
            finally
            {
                preview.Enabled = false;
                preview.Solid?.Dispose();
                doc.Views.Redraw();
            }
        }
    }

    private sealed class SeamGroovePreview : DisplayConduit
    {
        public Brep? Solid { get; set; }
        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (Solid is not null) e.IncludeBoundingBox(Solid.GetBoundingBox(false));
        }
        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (Solid is null) return;
            using var material = new DisplayMaterial(Color.DarkOrange, 0.35);
            e.Display.DrawBrepShaded(Solid, material);
            e.Display.DrawBrepWires(Solid, Color.OrangeRed, 2);
        }
    }
}
