using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace PrintGear;

[System.Runtime.InteropServices.Guid("3D270D7A-69E2-43A4-952A-ED1C3530477C")]
public sealed partial class PrintSupportCommand : Command
{
    private static int lastSupportType;
    private static double lastThicknessMillimeters = 2.0;
    private static double lastToothLengthMillimeters = 0.4;
    private static int lastToothCount = 5;
    private static double lastNozzleSizeMillimeters = 0.4;
    private static double lastPrintThicknessMillimeters = 0.2;

    public override string EnglishName => "PrintSupport";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var millimetersToDocument = RhinoMath.UnitScale(
            UnitSystem.Millimeters,
            doc.ModelUnitSystem);
        if (!RhinoMath.IsValidDouble(millimetersToDocument) ||
            millimetersToDocument <= 0.0)
        {
            RhinoApp.WriteLine("無法將毫米換算為目前文件單位。");
            return Result.Failure;
        }

        var printThickness = new OptionDouble(lastPrintThicknessMillimeters * millimetersToDocument, true, doc.ModelAbsoluteTolerance);
        var nozzleSize = new OptionDouble(lastNozzleSizeMillimeters * millimetersToDocument, true, doc.ModelAbsoluteTolerance);
        using (var printOptions = new GetOption())
        {
            printOptions.SetCommandPrompt($"第 1 層：設定列印尺寸（目前文件單位：{doc.ModelUnitSystem}），Enter 選擇支撐類型");
            printOptions.AcceptNothing(true);
            printOptions.AddOptionDouble(new LocalizeStringPair("NozzleSize", "列印噴頭尺寸"), ref nozzleSize);
            printOptions.AddOptionDouble(new LocalizeStringPair("PrintThickness", "列印層高尺寸"), ref printThickness);
            while (true)
            {
                var getResult = printOptions.Get();
                if (getResult == GetResult.Cancel) return Result.Cancel;
                if (getResult == GetResult.Nothing) break;
                if (getResult != GetResult.Option) return Result.Failure;
            }
        }

        var supportType = lastSupportType;
        using (var typeOptions = new GetOption())
        {
            typeOptions.SetCommandPrompt("第 2 層：選擇支撐類型");
            typeOptions.SetCommandPromptDefault(supportType == 1 ? "桁架式支撐" : "輪廓邊接觸齒");
            typeOptions.AcceptNothing(true);
            var trussOption = typeOptions.AddOption(new LocalizeStringPair("Truss", "桁架式支撐"));
            typeOptions.AddOption(new LocalizeStringPair("ProfileEdge", "輪廓邊接觸齒"));
            var getResult = typeOptions.Get();
            if (getResult == GetResult.Cancel) return Result.Cancel;
            if (getResult == GetResult.Option) supportType = typeOptions.OptionIndex() == trussOption ? 1 : 0;
            else if (getResult != GetResult.Nothing) return Result.Failure;
        }
        if (supportType == 1)
        {
            var trussResult = RunTrussMode(doc, nozzleSize.CurrentValue, printThickness.CurrentValue, millimetersToDocument);
            if (trussResult == Result.Success)
            {
                lastSupportType = 1;
                lastNozzleSizeMillimeters = nozzleSize.CurrentValue / millimetersToDocument;
                lastPrintThicknessMillimeters = printThickness.CurrentValue / millimetersToDocument;
            }
            return trussResult;
        }

        var thickness = new OptionDouble(lastThicknessMillimeters * millimetersToDocument, true, doc.ModelAbsoluteTolerance);
        var toothLength = new OptionDouble(lastToothLengthMillimeters * millimetersToDocument, true, 0.0);
        var toothCount = new OptionInteger(lastToothCount, 2, 512);
        using (var profileOptions = new GetOption())
        {
            profileOptions.SetCommandPrompt("第 3 層：設定輪廓邊接觸齒參數；尺寸採文件單位，Enter 選取輪廓");
            profileOptions.AcceptNothing(true);
            profileOptions.AddOptionDouble(new LocalizeStringPair("Thickness", "支撐厚度"), ref thickness);
            profileOptions.AddOptionDouble(new LocalizeStringPair("ToothDepth", "齒尖長"), ref toothLength);
            profileOptions.AddOptionInteger(new LocalizeStringPair("ToothCount", "沿線齒尖數量均分"), ref toothCount);
            while (true)
            {
                var getResult = profileOptions.Get();
                if (getResult == GetResult.Cancel) return Result.Cancel;
                if (getResult == GetResult.Nothing) break;
                if (getResult != GetResult.Option) return Result.Failure;
            }
        }

        var parameters = new SupportParameters(
            thickness.CurrentValue, toothLength.CurrentValue, printThickness.CurrentValue);
        if (!parameters.IsValid(doc.ModelAbsoluteTolerance, out var validationError))
        {
            RhinoApp.WriteLine($"無法建立支撐：{validationError}");
            return Result.Failure;
        }

        var result = RunProfileEdgeMode(doc, parameters, nozzleSize.CurrentValue,
            toothCount.CurrentValue, millimetersToDocument);
        if (result != Result.Success) return result;
        lastSupportType = 0;
        lastThicknessMillimeters = thickness.CurrentValue / millimetersToDocument;
        lastPrintThicknessMillimeters = printThickness.CurrentValue / millimetersToDocument;
        lastToothLengthMillimeters = toothLength.CurrentValue / millimetersToDocument;
        lastNozzleSizeMillimeters = nozzleSize.CurrentValue / millimetersToDocument;
        lastToothCount = toothCount.CurrentValue;
        return Result.Success;
    }

    private static Result RunProfileEdgeMode(
        RhinoDoc doc,
        SupportParameters parameters,
        double tipLineWidth,
        int toothCount,
        double millimetersToDocument)
    {
        using var outlineGetter = new GetObject();
        outlineGetter.SetCommandPrompt("選取一條閉合、平面的支撐輪廓");
        outlineGetter.GeometryFilter = ObjectType.Curve;
        outlineGetter.GroupSelect = false;
        outlineGetter.SubObjectSelect = false;
        outlineGetter.Get();
        if (outlineGetter.CommandResult() != Result.Success)
            return outlineGetter.CommandResult();

        var outlineReference = outlineGetter.Object(0);
        var sourceOutline = outlineReference.Curve();
        if (sourceOutline is null)
        {
            RhinoApp.WriteLine("沒有取得可用的支撐輪廓。");
            return Result.Failure;
        }

        var outline = sourceOutline.DuplicateCurve();
        if (!outline.IsValid || !outline.IsClosed)
        {
            RhinoApp.WriteLine(
                "支撐輪廓必須是單一閉合曲線；請先使用 Join 或 CloseCrv。");
            return Result.Failure;
        }
        if (!outline.TryGetPlane(out _, doc.ModelAbsoluteTolerance))
        {
            RhinoApp.WriteLine("支撐輪廓必須位於單一平面內。");
            return Result.Failure;
        }

        var segments = outline.DuplicateSegments();
        if (segments is null || segments.Length < 2)
        {
            RhinoApp.WriteLine(
                "輪廓沒有可辨識的獨立邊段。" +
                "請在接觸區兩端 Split，再 Join 成 PolyCurve。");
            return Result.Failure;
        }

        var segmentResult = PickContactSegment(
            outline,
            segments,
            doc.ModelAbsoluteTolerance,
            out var contactSegment,
            out var contactSegmentIndex);
        if (segmentResult != Result.Success)
            return segmentResult;

        var support = ContinuousChamferSupportBuilder.Create(
            outline,
            contactSegment,
            parameters,
            tipLineWidth,
            toothCount,
            doc.ModelAbsoluteTolerance,
            out var contactCount,
            out var buildReport,
            out var error);
        if (support is null)
        {
            RhinoApp.WriteLine($"無法建立輪廓邊接觸齒支撐：{error}");
            return Result.Failure;
        }

        var attributes = CreateAttributes(
            1,
            parameters,
            contactCount,
            millimetersToDocument);
        attributes.SetUserString(
            "PrintSupport.SourceCurveId",
            outlineReference.ObjectId.ToString());
        attributes.SetUserString(
            "PrintSupport.ContactSegmentIndex",
            contactSegmentIndex.ToString());
        attributes.SetUserString(
            "PrintSupport.ToothDepthMm",
            (parameters.ContactClearance / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ToothHeightZMm",
            (parameters.ContactWidth / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ToothCount",
            toothCount.ToString());
        attributes.SetUserString("PrintSupport.PrintThicknessMm",
            (parameters.ContactWidth / millimetersToDocument).ToString("G"));
        attributes.SetUserString("PrintSupport.NozzleSizeMm",
            (tipLineWidth / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.TipLineWidthMm",
            (tipLineWidth / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ToothDirection",
            "WorldXYHorizontal");
        attributes.SetUserString(
            "PrintSupport.ToothDistribution",
            "EqualArcLengthIncludingEnds");
        attributes.SetUserString(
            "PrintSupport.ChamferDistanceMm",
            (buildReport.ActualChamferDistance / millimetersToDocument)
            .ToString("G"));
        attributes.SetUserString(
            "PrintSupport.TaperMode",
            buildReport.BuildMode);
        attributes.SetUserString(
            "PrintSupport.TaperModeRequested",
            "ContinuousChamfer");
        attributes.SetUserString(
            "PrintSupport.ChamferAngleDegrees",
            "45");
        attributes.SetUserString(
            "PrintSupport.ToothShape",
            "ContactBandPrism");
        attributes.SetUserString(
            "PrintSupport.RequestedToothCount",
            buildReport.RequestedCount.ToString());
        attributes.SetUserString(
            "PrintSupport.RectangularToothCount",
            buildReport.RectangularToothCount.ToString());
        attributes.SetUserString(
            "PrintSupport.SkippedToothCount",
            buildReport.SkippedCount.ToString());
        attributes.SetUserString(
            "PrintSupport.BuildStatus",
            buildReport.SkippedCount > 0 ||
            buildReport.UsedAdjustedChamfer
                ? "Degraded"
                : "Complete");

        if (doc.Objects.AddBrep(support, attributes) == Guid.Empty)
        {
            RhinoApp.WriteLine("無法把輪廓邊接觸齒支撐加入文件。");
            return Result.Failure;
        }

        doc.Views.Redraw();
        if (buildReport.UsedAdjustedChamfer)
        {
            RhinoApp.WriteLine(
                $"注意：為通過 Rhino 幾何容差，Chamfer 距離由 " +
                $"{buildReport.RequestedChamferDistance / millimetersToDocument:G} mm " +
                $"微調為 {buildReport.ActualChamferDistance / millimetersToDocument:G} mm。");
        }
        if (buildReport.UsedSequentialUnion)
        {
            RhinoApp.WriteLine(
                "注意：貼邊方塊齒的批次合併失敗，已改為逐顆合併；" +
                "全部指定接觸齒均已保留。");
        }
        if (buildReport.SkippedCount > 0)
        {
            RhinoApp.WriteLine(
                $"建立摘要：規劃 {buildReport.RequestedCount} 顆；" +
                $"貼邊方塊齒 {buildReport.RectangularToothCount} 顆、" +
                $"略過 {buildReport.SkippedCount} 顆。");
        }
        RhinoApp.WriteLine(
            $"已建立 1 個連續 Chamfer 貼邊方塊齒支撐，共 {contactCount} 個接觸齒；" +
            $"主體厚度 {(parameters.Thickness / millimetersToDocument):G} mm、" +
            $"Chamfer {(buildReport.ActualChamferDistance / millimetersToDocument):G} mm、" +
            $"貼邊方塊齒 W×H×D = {(tipLineWidth / millimetersToDocument):G} × " +
            $"{(parameters.ContactWidth / millimetersToDocument):G} × " +
            $"{(parameters.ContactClearance / millimetersToDocument):G} mm、" +
            $"沿線齒尖數量均分 {toothCount} 顆（包含頭尾）。");
        return Result.Success;
    }

    private static Result PickContactSegment(
        Curve outline,
        IReadOnlyList<Curve> segments,
        double tolerance,
        out Curve contactSegment,
        out int contactSegmentIndex)
    {
        while (true)
        {
            using var segmentPicker = new GetPoint();
            segmentPicker.SetCommandPrompt(
                "在輪廓上點選要產生細小接觸齒的邊段（紅色高亮）");
            if (!segmentPicker.Constrain(outline, false))
            {
                contactSegment = null!;
                contactSegmentIndex = -1;
                RhinoApp.WriteLine("無法將點選限制在支撐輪廓上。");
                return Result.Failure;
            }

            segmentPicker.DynamicDraw += (sender, eventArgs) =>
            {
                var highlightedIndex = ClosestSegmentIndex(
                    segments,
                    eventArgs.CurrentPoint,
                    out _,
                    out _);
                if (highlightedIndex >= 0)
                {
                    eventArgs.Display.DrawCurve(
                        segments[highlightedIndex],
                        Color.Red,
                        4);
                }
            };

            var getResult = segmentPicker.Get();
            if (getResult == GetResult.Cancel)
            {
                contactSegment = null!;
                contactSegmentIndex = -1;
                return Result.Cancel;
            }
            if (getResult != GetResult.Point)
            {
                contactSegment = null!;
                contactSegmentIndex = -1;
                return Result.Failure;
            }

            var selectedIndex = ClosestSegmentIndex(
                segments,
                segmentPicker.Point(),
                out var closestDistance,
                out var secondClosestDistance);
            if (selectedIndex < 0 || closestDistance > tolerance * 10.0)
            {
                RhinoApp.WriteLine("沒有在輪廓上取得可用的邊段，請重新點選。");
                continue;
            }
            if (secondClosestDistance <= tolerance * 10.0)
            {
                RhinoApp.WriteLine(
                    "點選位置太靠近輪廓轉角；請在要選取的邊段中央再點一次。");
                continue;
            }

            contactSegment = segments[selectedIndex].DuplicateCurve();
            contactSegmentIndex = selectedIndex;
            return Result.Success;
        }
    }

    private static int ClosestSegmentIndex(
        IReadOnlyList<Curve> segments,
        Point3d point,
        out double closestDistance,
        out double secondClosestDistance)
    {
        var closestIndex = -1;
        closestDistance = double.PositiveInfinity;
        secondClosestDistance = double.PositiveInfinity;
        for (var index = 0; index < segments.Count; index++)
        {
            if (!segments[index].ClosestPoint(point, out var parameter))
                continue;

            var distance = segments[index].PointAt(parameter).DistanceTo(point);
            if (distance < closestDistance)
            {
                secondClosestDistance = closestDistance;
                closestDistance = distance;
                closestIndex = index;
            }
            else if (distance < secondClosestDistance)
            {
                secondClosestDistance = distance;
            }
        }

        return closestIndex;
    }

    private static ObjectAttributes CreateAttributes(
        int index,
        SupportParameters parameters,
        int contactCount,
        double millimetersToDocument)
    {
        var attributes = new ObjectAttributes
        {
            Name = $"PrintSupport_LightTouch_{index:00}"
        };
        attributes.SetUserString("PrintSupport.Type", "LightTouchFin");
        attributes.SetUserString("PrintSupport.Mode", "ProfileEdge");
        attributes.SetUserString(
            "PrintSupport.ThicknessMm",
            (parameters.Thickness / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ContactClearanceMm",
            (parameters.ContactClearance / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ContactWidthMm",
            (parameters.ContactWidth / millimetersToDocument).ToString("G"));
        attributes.SetUserString("PrintSupport.ContactCount", contactCount.ToString());
        return attributes;
    }
}
