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
public sealed class PrintSupportCommand : Command
{
    private static SupportInputMode lastInputMode = SupportInputMode.ProfileEdge;
    private static double lastThicknessMillimeters = 2.0;
    private static double lastClearanceMillimeters = 0.25;
    private static double lastContactWidthMillimeters = 0.4;
    private static double lastContactSpacingMillimeters = 5.0;
    private static double lastTipLineWidthMillimeters = 0.4;
    private static double lastFootExtensionMillimeters = 2.0;
    private static double lastBaseHeight;

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

        var thickness = new OptionDouble(
            lastThicknessMillimeters * millimetersToDocument,
            true,
            doc.ModelAbsoluteTolerance);
        var clearance = new OptionDouble(
            lastClearanceMillimeters * millimetersToDocument,
            true,
            0.0);
        var contactWidth = new OptionDouble(
            lastContactWidthMillimeters * millimetersToDocument,
            true,
            doc.ModelAbsoluteTolerance);
        var contactSpacing = new OptionDouble(
            lastContactSpacingMillimeters * millimetersToDocument,
            true,
            doc.ModelAbsoluteTolerance);
        var tipLineWidth = new OptionDouble(
            lastTipLineWidthMillimeters * millimetersToDocument,
            true,
            doc.ModelAbsoluteTolerance);
        var footExtension = new OptionDouble(
            lastFootExtensionMillimeters * millimetersToDocument,
            true,
            0.0);
        var baseHeight = new OptionDouble(lastBaseHeight);
        var inputModes = new[]
        {
            new LocalizeStringPair("ProfileEdge", "輪廓邊接觸齒"),
            new LocalizeStringPair("Outline", "自訂輪廓"),
            new LocalizeStringPair("Guide", "導引曲線")
        };

        var options = new GetOption();
        options.SetCommandPrompt(
            $"設定輕觸式支撐參數（目前文件單位：{doc.ModelUnitSystem}），完成後按 Enter");
        options.AcceptNothing(true);

        var inputMode = lastInputMode;
        while (true)
        {
            options.ClearCommandOptions();
            var modeOption = options.AddOptionList(
                new LocalizeStringPair("Mode", "建立模式"),
                inputModes,
                (int)inputMode);
            options.AddOptionDouble(
                new LocalizeStringPair("Thickness", "支撐厚度"),
                ref thickness);
            if (inputMode == SupportInputMode.ProfileEdge)
            {
                options.AddOptionDouble(
                    new LocalizeStringPair("TipLineWidth", "齒尖線寬XY"),
                    ref tipLineWidth);
            }
            var clearanceOption = inputMode == SupportInputMode.ProfileEdge
                ? new LocalizeStringPair("ToothDepth", "齒水平伸長XY")
                : new LocalizeStringPair("ContactClearance", "接觸退讓");
            var widthOption = inputMode == SupportInputMode.ProfileEdge
                ? new LocalizeStringPair("ToothHeightZ", "齒高度Z")
                : new LocalizeStringPair("ContactWidth", "接觸齒寬");
            var spacingOption = inputMode == SupportInputMode.ProfileEdge
                ? new LocalizeStringPair("ToothPitch", "沿線齒距")
                : new LocalizeStringPair("ContactSpacing", "接觸齒間距");
            options.AddOptionDouble(clearanceOption, ref clearance);
            options.AddOptionDouble(widthOption, ref contactWidth);
            options.AddOptionDouble(spacingOption, ref contactSpacing);
            if (inputMode == SupportInputMode.Guide)
            {
                options.AddOptionDouble(
                    new LocalizeStringPair("FootExtension", "底腳延伸"),
                    ref footExtension);
                options.AddOptionDouble(
                    new LocalizeStringPair("BaseHeight", "平台高度"),
                    ref baseHeight);
            }

            var getResult = options.Get();
            if (getResult == GetResult.Cancel)
                return Result.Cancel;
            if (getResult == GetResult.Nothing)
                break;
            if (getResult != GetResult.Option)
                return Result.Failure;

            if (options.OptionIndex() == modeOption)
                inputMode = (SupportInputMode)options.Option().CurrentListOptionIndex;
        }

        var parameters = new SupportParameters(
            thickness.CurrentValue,
            clearance.CurrentValue,
            contactWidth.CurrentValue,
            contactSpacing.CurrentValue,
            footExtension.CurrentValue,
            baseHeight.CurrentValue);
        if (!parameters.IsValid(doc.ModelAbsoluteTolerance, out var validationError))
        {
            RhinoApp.WriteLine($"無法建立支撐：{validationError}");
            return Result.Failure;
        }

        var result = inputMode switch
        {
            SupportInputMode.ProfileEdge =>
                RunProfileEdgeMode(
                    doc,
                    parameters,
                    tipLineWidth.CurrentValue,
                    millimetersToDocument),
            SupportInputMode.Outline =>
                RunOutlineMode(doc, parameters, millimetersToDocument),
            _ =>
                RunGuideMode(doc, parameters, millimetersToDocument)
        };
        if (result != Result.Success)
            return result;

        lastInputMode = inputMode;
        lastThicknessMillimeters = thickness.CurrentValue / millimetersToDocument;
        lastClearanceMillimeters = clearance.CurrentValue / millimetersToDocument;
        lastContactWidthMillimeters = contactWidth.CurrentValue / millimetersToDocument;
        lastContactSpacingMillimeters = contactSpacing.CurrentValue / millimetersToDocument;
        if (inputMode == SupportInputMode.ProfileEdge)
        {
            lastTipLineWidthMillimeters =
                tipLineWidth.CurrentValue / millimetersToDocument;
        }
        lastFootExtensionMillimeters = footExtension.CurrentValue / millimetersToDocument;
        lastBaseHeight = baseHeight.CurrentValue;
        return Result.Success;
    }

    private static Result RunProfileEdgeMode(
        RhinoDoc doc,
        SupportParameters parameters,
        double tipLineWidth,
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
            millimetersToDocument,
            "ProfileEdge",
            null);
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
            "PrintSupport.ToothPitchMm",
            (parameters.ContactSpacing / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.TipLineWidthMm",
            (tipLineWidth / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ToothDirection",
            "WorldXYHorizontal");
        attributes.SetUserString(
            "PrintSupport.ToothDistribution",
            "AlongContactEdge");
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
                "失敗齒已略過，其他有效接觸齒保留。");
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
            $"沿線齒距 {(parameters.ContactSpacing / millimetersToDocument):G} mm。");
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

    private static Result RunOutlineMode(
        RhinoDoc doc,
        SupportParameters parameters,
        double millimetersToDocument)
    {
        using var targetGetter = new GetObject();
        targetGetter.SetCommandPrompt(
            "選取由 ExtractSrf（Copy=Yes）複製出的接觸面；也可選完整模型，按 Enter 完成");
        targetGetter.GeometryFilter =
            ObjectType.Surface |
            ObjectType.Brep |
            ObjectType.Extrusion |
            ObjectType.Mesh |
            ObjectType.SubD |
            ObjectType.InstanceReference;
        targetGetter.GroupSelect = true;
        targetGetter.SubObjectSelect = false;
        targetGetter.GetMultiple(1, 0);
        if (targetGetter.CommandResult() != Result.Success)
            return targetGetter.CommandResult();

        var targets = new List<GeometryBase>();
        var targetIds = new List<Guid>();
        var rejectedTypes = new List<string>();
        for (var i = 0; i < targetGetter.ObjectCount; i++)
        {
            var objectReference = targetGetter.Object(i);
            var addedTargetCount = AppendTargetGeometry(objectReference, targets);
            if (addedTargetCount == 0)
            {
                rejectedTypes.Add(
                    objectReference.Object()?.ObjectType.ToString() ??
                    objectReference.Geometry()?.GetType().Name ??
                    "Unknown");
                continue;
            }

            targetIds.Add(objectReference.ObjectId);
        }

        if (targets.Count == 0)
        {
            var selectedTypes = rejectedTypes.Count > 0
                ? $" 所選類型：{string.Join(", ", rejectedTypes.Distinct())}。"
                : string.Empty;
            RhinoApp.WriteLine(
                "沒有取得可用的接觸面或目標幾何；" +
                "建議先用 ExtractSrf（Copy=Yes）複製單一接觸面，" +
                "也支援多重曲面、擠出、Mesh、SubD 與 Block。" +
                selectedTypes);
            return Result.Failure;
        }

        using var outlineGetter = new GetObject();
        outlineGetter.SetCommandPrompt(
            "選取閉合平面支撐輪廓；讓輪廓完整跨過接觸面剖線並稍微穿入，按 Enter 完成");
        outlineGetter.GeometryFilter = ObjectType.Curve;
        outlineGetter.GroupSelect = true;
        outlineGetter.SubObjectSelect = false;
        outlineGetter.GetMultiple(1, 0);
        if (outlineGetter.CommandResult() != Result.Success)
            return outlineGetter.CommandResult();

        var inputCurves = new List<Curve>();
        for (var i = 0; i < outlineGetter.ObjectCount; i++)
        {
            var curve = outlineGetter.Object(i).Curve();
            if (curve is not null)
                inputCurves.Add(curve.DuplicateCurve());
        }

        if (inputCurves.Count == 0)
        {
            RhinoApp.WriteLine("沒有取得可用的自訂支撐輪廓。");
            return Result.Failure;
        }

        var joinedOutlines = Curve.JoinCurves(
            inputCurves,
            doc.ModelAbsoluteTolerance,
            false);
        var added = 0;
        var failures = new List<string>();
        var warnings = new List<string>();
        for (var i = 0; i < joinedOutlines.Length; i++)
        {
            var support = OutlineSupportBuilder.Create(
                targets,
                joinedOutlines[i],
                parameters,
                doc.ModelAbsoluteTolerance,
                out var contactCount,
                out var warning,
                out var error);
            if (support is null)
            {
                failures.Add($"輪廓 {i + 1}: {error}");
                continue;
            }

            var attributes = CreateAttributes(
                added + 1,
                parameters,
                contactCount,
                millimetersToDocument,
                "Outline",
                targetIds);
            if (doc.Objects.AddBrep(support, attributes) == Guid.Empty)
            {
                failures.Add($"輪廓 {i + 1}: 無法把支撐加入文件。");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(warning))
                warnings.Add($"輪廓 {i + 1}: {warning}");
            added++;
        }

        if (added == 0)
        {
            foreach (var failure in failures)
                RhinoApp.WriteLine(failure);
            return Result.Failure;
        }

        doc.Views.Redraw();
        RhinoApp.WriteLine(
            $"已建立 {added} 個自訂輪廓輕觸式支撐；" +
            "原模型與手繪輪廓均保留，請勿與模型做布林聯集。");
        foreach (var warning in warnings)
            RhinoApp.WriteLine($"注意：{warning}");
        foreach (var failure in failures)
            RhinoApp.WriteLine($"略過：{failure}");
        return Result.Success;
    }

    private static Result RunGuideMode(
        RhinoDoc doc,
        SupportParameters parameters,
        double millimetersToDocument)
    {
        using var curveGetter = new GetObject();
        curveGetter.SetCommandPrompt(
            "選取位於模型接觸邊界的開放平面曲線（可選取多段剖面曲線）");
        curveGetter.GeometryFilter = ObjectType.Curve;
        curveGetter.GroupSelect = true;
        curveGetter.SubObjectSelect = false;
        curveGetter.GetMultiple(1, 0);
        if (curveGetter.CommandResult() != Result.Success)
            return curveGetter.CommandResult();

        var inputCurves = new List<Curve>();
        for (var i = 0; i < curveGetter.ObjectCount; i++)
        {
            var curve = curveGetter.Object(i).Curve();
            if (curve is not null)
                inputCurves.Add(curve.DuplicateCurve());
        }

        if (inputCurves.Count == 0)
        {
            RhinoApp.WriteLine("沒有取得可用的導引曲線。");
            return Result.Failure;
        }

        var joinedCurves = Curve.JoinCurves(
            inputCurves,
            doc.ModelAbsoluteTolerance,
            false);
        var added = 0;
        var failures = new List<string>();
        for (var i = 0; i < joinedCurves.Length; i++)
        {
            var support = SupportStructureBuilder.Create(
                joinedCurves[i],
                parameters,
                doc.ModelAbsoluteTolerance,
                out var contactCount,
                out var error);
            if (support is null)
            {
                failures.Add($"曲線 {i + 1}: {error}");
                continue;
            }

            var attributes = CreateAttributes(
                added + 1,
                parameters,
                contactCount,
                millimetersToDocument,
                "Guide",
                null);
            if (doc.Objects.AddBrep(support, attributes) == Guid.Empty)
            {
                failures.Add($"曲線 {i + 1}: 無法把支撐加入文件。");
                continue;
            }

            added++;
        }

        if (added == 0)
        {
            foreach (var failure in failures)
                RhinoApp.WriteLine(failure);
            return Result.Failure;
        }

        doc.Views.Redraw();
        RhinoApp.WriteLine(
            $"已建立 {added} 個導引曲線輕觸式支撐；" +
            $"主肋退讓 {parameters.ContactClearance / millimetersToDocument:0.###} mm，" +
            "請勿與模型做布林聯集。");
        foreach (var failure in failures)
            RhinoApp.WriteLine($"略過：{failure}");
        return Result.Success;
    }

    private static int AppendTargetGeometry(
        ObjRef objectReference,
        ICollection<GeometryBase> targets)
    {
        if (objectReference.Object() is InstanceObject instanceObject)
        {
            instanceObject.Explode(
                true,
                out var pieces,
                out _,
                out var transforms);
            if (pieces is null || transforms is null)
                return 0;

            var added = 0;
            for (var i = 0; i < Math.Min(pieces.Length, transforms.Length); i++)
            {
                added += AppendTargetGeometry(
                    pieces[i].Geometry,
                    transforms[i],
                    targets);
            }
            return added;
        }

        return AppendTargetGeometry(
            objectReference.Geometry(),
            Transform.Identity,
            targets);
    }

    private static int AppendTargetGeometry(
        GeometryBase? geometry,
        Transform transform,
        ICollection<GeometryBase> targets)
    {
        if (geometry is null)
            return 0;

        var duplicate = geometry.Duplicate();
        if (duplicate is null)
            return 0;

        if (!transform.IsValid ||
            (!transform.IsIdentity && !duplicate.Transform(transform)))
            return 0;

        if (!duplicate.IsValid)
            return 0;

        switch (duplicate)
        {
            case Brep:
            case Extrusion:
            case Mesh:
            case SubD:
            case Surface:
                targets.Add(duplicate);
                return 1;
            default:
                {
                    var converted = Brep.TryConvertBrep(duplicate);
                    if (converted is null || !converted.IsValid)
                        return 0;

                    targets.Add(converted.DuplicateBrep());
                    return 1;
                }
        }
    }

    private static ObjectAttributes CreateAttributes(
        int index,
        SupportParameters parameters,
        int contactCount,
        double millimetersToDocument,
        string inputMode,
        IReadOnlyList<Guid>? targetIds)
    {
        var attributes = new ObjectAttributes
        {
            Name = $"PrintSupport_LightTouch_{index:00}"
        };
        attributes.SetUserString("PrintSupport.Type", "LightTouchFin");
        attributes.SetUserString("PrintSupport.Mode", inputMode);
        attributes.SetUserString(
            "PrintSupport.ThicknessMm",
            (parameters.Thickness / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ContactClearanceMm",
            (parameters.ContactClearance / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ContactWidthMm",
            (parameters.ContactWidth / millimetersToDocument).ToString("G"));
        attributes.SetUserString(
            "PrintSupport.ContactSpacingMm",
            (parameters.ContactSpacing / millimetersToDocument).ToString("G"));
        attributes.SetUserString("PrintSupport.ContactCount", contactCount.ToString());
        attributes.SetUserString(
            "PrintSupport.FootExtensionMm",
            (parameters.FootExtension / millimetersToDocument).ToString("G"));
        attributes.SetUserString("PrintSupport.BaseHeight", parameters.BaseHeight.ToString("G"));
        if (targetIds is not null && targetIds.Count > 0)
        {
            attributes.SetUserString(
                "PrintSupport.TargetIds",
                string.Join(",", targetIds));
        }
        return attributes;
    }

    private enum SupportInputMode
    {
        ProfileEdge,
        Outline,
        Guide
    }
}
