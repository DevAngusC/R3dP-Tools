using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace PrintGear;

/// <summary>
/// Builds a support fin from a closed planar profile and one selected boundary
/// segment. Small teeth are distributed by World-Z height and extend toward
/// the outside of the closed profile.
/// </summary>
internal static class EdgeTeethSupportBuilder
{
    private const int MaximumToothCount = 512;
    private const int MonotonicitySamples = 64;
    private const double VerticalPlaneAngleToleranceRadians = Math.PI / 180.0;
    private const double FixedTaperAngleDegrees = 60.0;
    private const double FixedTaperSideSlope = 1.7320508075688772;

    private enum ToothBuildKind
    {
        None,
        FixedSlope,
        OutwardOnly
    }

    private sealed record ToothCandidate(
        double CenterZ,
        Curve ContactPiece,
        Brep? PreferredSolid,
        Brep? BodyCutter,
        ToothBuildKind PreferredKind,
        double InwardTaperLength);

    public static Brep? Create(
        Curve outline,
        Curve contactEdge,
        SupportParameters parameters,
        double tipLineWidth,
        double tolerance,
        out int contactCount,
        out EdgeTeethBuildReport buildReport,
        out string error)
    {
        contactCount = 0;
        buildReport = EdgeTeethBuildReport.Empty;

        if (!parameters.IsValid(tolerance, out error))
            return null;
        if (parameters.ContactClearance <= tolerance * 8.0)
        {
            error = "齒水平伸長XY必須大於文件絕對公差的八倍。";
            return null;
        }
        if (!RhinoMath.IsValidDouble(tipLineWidth) ||
            tipLineWidth <= tolerance * 4.0)
        {
            error = "齒尖線寬XY必須大於文件絕對公差的四倍。";
            return null;
        }
        if (tipLineWidth > parameters.Thickness + tolerance)
        {
            error = "齒尖線寬XY不可大於支撐厚度。";
            return null;
        }
        if (!outline.IsValid || !outline.IsClosed)
        {
            error = "支撐輪廓必須是單一、有效的閉合曲線；請先使用 Join 或 CloseCrv。";
            return null;
        }
        if (!outline.TryGetPlane(out var plane, tolerance))
        {
            error = "支撐輪廓必須位於單一平面內。";
            return null;
        }
        if (Math.Abs(Vector3d.Multiply(plane.Normal, Vector3d.ZAxis)) >
            Math.Sin(VerticalPlaneAngleToleranceRadians))
        {
            error =
                "輪廓平面必須近似垂直（平面法向須位於 World XY）；" +
                "否則 XY 齒尖線寬會改變齒高度Z。";
            return null;
        }

        var selfIntersections = Intersection.CurveSelf(outline, tolerance);
        if (selfIntersections is not null && selfIntersections.Count > 0)
        {
            error = "支撐輪廓不可自交。";
            return null;
        }

        if (!contactEdge.IsValid || contactEdge.IsClosed)
        {
            error =
                "沒有取得可用的獨立接觸邊段。" +
                "請在接觸區兩端 Split，再 Join 成含有獨立邊段的閉合曲線。";
            return null;
        }
        if (!IsOnOutlinePlaneAndBoundary(
                contactEdge,
                outline,
                plane,
                tolerance))
        {
            error = "選取的接觸邊段不屬於支撐輪廓。";
            return null;
        }
        if (!IsWorldZMonotonic(contactEdge, tolerance))
        {
            error =
                "所選接觸邊在 World Z 方向折返或高度變化不足。" +
                "請先分割成一段沿 Z 單調變化的斜邊或直邊。";
            return null;
        }
        if (!TryGetHorizontalOutwardDirection(
                contactEdge,
                outline,
                plane,
                parameters.ContactClearance,
                tolerance,
                out var horizontalOutward))
        {
            error =
                "無法在所選邊段找到一致的 World XY 水平外側方向；" +
                "請分割凹折邊段或縮短接觸區。";
            return null;
        }

        var bounds = contactEdge.GetBoundingBox(true);
        var minimumZ = bounds.Min.Z;
        var maximumZ = bounds.Max.Z;
        var toothHeight = parameters.ContactWidth;
        var verticalPitch = parameters.ContactSpacing;
        if (maximumZ - minimumZ < toothHeight - tolerance)
        {
            error = "所選接觸邊的 Z 高度不足以放置一個接觸齒。";
            return null;
        }
        if (verticalPitch - toothHeight <= tolerance * 4.0)
        {
            error =
                "接觸齒之間必須保留大於文件公差四倍的 Z 間隙；" +
                "請增加垂直齒距或減少齒高度Z。";
            return null;
        }

        var candidates = new List<ToothCandidate>();
        var requestedCount = 0;
        var halfHeight = toothHeight / 2.0;
        var endSafety = Math.Max(tolerance * 4.0, toothHeight * 0.005);
        for (var centerZ = minimumZ + halfHeight + endSafety;
             centerZ <= maximumZ - halfHeight - endSafety + tolerance;
             centerZ += verticalPitch)
        {
            if (requestedCount >= MaximumToothCount)
            {
                error =
                    $"接觸齒數量超過 {MaximumToothCount}；" +
                    "請增加垂直齒距或縮短接觸邊。";
                return null;
            }

            requestedCount++;
            if (!TryTrimAtWorldZBand(
                    contactEdge,
                    centerZ - halfHeight,
                    centerZ + halfHeight,
                    tolerance,
                    out var contactPiece))
            {
                continue;
            }

            Brep? preferredSolid = null;
            Brep? bodyCutter = null;
            var preferredKind = ToothBuildKind.None;
            var inwardTaperLength = 0.0;
            try
            {
                if (TryCreateFixedSlopeTooth(
                        outline,
                        contactPiece,
                        plane,
                        horizontalOutward,
                        parameters.ContactClearance,
                        parameters.Thickness,
                        tipLineWidth,
                        tolerance,
                        out var fixedSlopeSolid,
                        out bodyCutter,
                        out inwardTaperLength))
                {
                    preferredSolid = fixedSlopeSolid;
                    preferredKind = ToothBuildKind.FixedSlope;
                }
            }
            catch
            {
                preferredSolid = null;
                bodyCutter = null;
                preferredKind = ToothBuildKind.None;
                inwardTaperLength = 0.0;
            }

            if (preferredSolid is null)
            {
                try
                {
                    if (TryCreateOutwardOnlyTooth(
                            outline,
                            contactPiece,
                            plane,
                            horizontalOutward,
                            parameters.ContactClearance,
                            parameters.Thickness,
                            tipLineWidth,
                            tolerance,
                            out var outwardSolid))
                    {
                        preferredSolid = outwardSolid;
                        bodyCutter = null;
                        preferredKind = ToothBuildKind.OutwardOnly;
                        inwardTaperLength = 0.0;
                    }
                }
                catch
                {
                    preferredSolid = null;
                    preferredKind = ToothBuildKind.None;
                }
            }

            candidates.Add(new ToothCandidate(
                centerZ,
                contactPiece,
                preferredSolid,
                bodyCutter,
                preferredKind,
                inwardTaperLength));
        }

        if (requestedCount == 0)
        {
            error = "所選接觸邊沒有產生任何接觸齒。";
            return null;
        }

        var extrusionPlane = new Plane(
            plane.Origin - plane.Normal * (parameters.Thickness / 2.0),
            plane.XAxis,
            plane.YAxis);
        var extrusion = Extrusion.Create(
            outline,
            extrusionPlane,
            parameters.Thickness,
            true);
        if (extrusion is null)
        {
            error = "無法將支撐輪廓擠出為實體。";
            return null;
        }

        var mainBody = extrusion.ToBrep(true);
        if (mainBody is null || !mainBody.IsValid || !mainBody.IsSolid)
        {
            error = "無法建立封閉的支撐主體。";
            return null;
        }
        if (mainBody.SolidOrientation == BrepSolidOrientation.Inward)
            mainBody.Flip();

        var preferredCandidates = candidates
            .Where(candidate =>
                candidate.PreferredKind != ToothBuildKind.None &&
                candidate.PreferredSolid is not null)
            .ToList();
        if (preferredCandidates.Count > 0)
        {
            var cutters = preferredCandidates
                .Where(candidate => candidate.BodyCutter is not null)
                .Select(candidate => candidate.BodyCutter!)
                .ToList();
            if (TryCreateCarvedBody(
                    mainBody,
                    cutters,
                    tolerance,
                    out var preferredBody))
            {
                var unionInputs = new List<Brep> { preferredBody };
                unionInputs.AddRange(
                    preferredCandidates.Select(candidate => candidate.PreferredSolid!));
                if (TryCreateSingleSolidUnion(
                        unionInputs,
                        tolerance,
                        out var preferredSupport))
                {
                    var fixedSlopeCount = preferredCandidates.Count(candidate =>
                        candidate.PreferredKind == ToothBuildKind.FixedSlope);
                    var outwardOnlyCount =
                        preferredCandidates.Count - fixedSlopeCount;
                    contactCount = preferredCandidates.Count;
                    buildReport = new EdgeTeethBuildReport(
                        requestedCount,
                        fixedSlopeCount,
                        outwardOnlyCount,
                        requestedCount - contactCount,
                        fixedSlopeCount == contactCount
                            ? "FixedSlopePencil"
                            : fixedSlopeCount > 0
                                ? "Mixed"
                                : "OutwardOnly",
                        false,
                        false,
                        FixedTaperAngleDegrees,
                        preferredCandidates
                            .Where(candidate =>
                                candidate.PreferredKind ==
                                ToothBuildKind.FixedSlope)
                            .Select(candidate => candidate.InwardTaperLength)
                            .DefaultIfEmpty(0.0)
                            .Max());
                    error = string.Empty;
                    return preferredSupport;
                }
            }
        }

        // Any failed cutter or mixed Boolean invalidates the entire intermediate
        // result. Rebuild from the pristine body so no unfilled channel can leak
        // into the document.
        var outwardTeeth = new List<(double CenterZ, Brep Solid)>();
        foreach (var candidate in candidates.OrderBy(candidate => candidate.CenterZ))
        {
            try
            {
                if (TryCreateOutwardOnlyTooth(
                        outline,
                        candidate.ContactPiece,
                        plane,
                        horizontalOutward,
                        parameters.ContactClearance,
                        parameters.Thickness,
                        tipLineWidth,
                        tolerance,
                        out var outwardSolid))
                {
                    outwardTeeth.Add((candidate.CenterZ, outwardSolid));
                }
            }
            catch
            {
                // A local tooth failure is handled by skipping only that tooth.
            }
        }

        if (outwardTeeth.Count == 0)
        {
            error =
                "所有固定斜率與外側收尖接觸齒都無法形成有效實體；" +
                "未輸出只有主體、沒有接觸齒的不完整支撐。";
            return null;
        }

        var outwardInputs = new List<Brep> { mainBody.DuplicateBrep() };
        outwardInputs.AddRange(outwardTeeth.Select(tooth => tooth.Solid));
        if (TryCreateSingleSolidUnion(
                outwardInputs,
                tolerance,
                out var outwardSupport) &&
            HasAddedVolume(mainBody, outwardSupport, tolerance))
        {
            contactCount = outwardTeeth.Count;
            buildReport = new EdgeTeethBuildReport(
                requestedCount,
                0,
                contactCount,
                requestedCount - contactCount,
                "AllOutwardFallback",
                true,
                false,
                FixedTaperAngleDegrees,
                0.0);
            error = string.Empty;
            return outwardSupport;
        }

        var sequentialSupport = mainBody.DuplicateBrep();
        var sequentialCount = 0;
        foreach (var outwardTooth in outwardTeeth.OrderBy(tooth => tooth.CenterZ))
        {
            if (!TryCreateSingleSolidUnion(
                    new[] { sequentialSupport, outwardTooth.Solid },
                    tolerance,
                    out var candidateSupport) ||
                !HasAddedVolume(sequentialSupport, candidateSupport, tolerance))
            {
                continue;
            }

            sequentialSupport = candidateSupport;
            sequentialCount++;
        }

        if (sequentialCount == 0 ||
            !IsUsableSolid(sequentialSupport) ||
            !HasAddedVolume(mainBody, sequentialSupport, tolerance))
        {
            error =
                "接觸齒的批次與逐顆合併都失敗；" +
                "未輸出只有主體、沒有接觸齒的不完整支撐。";
            return null;
        }

        contactCount = sequentialCount;
        buildReport = new EdgeTeethBuildReport(
            requestedCount,
            0,
            contactCount,
            requestedCount - contactCount,
            "SequentialOutwardFallback",
            true,
            true,
            FixedTaperAngleDegrees,
            0.0);
        error = string.Empty;
        return sequentialSupport;
    }

    private static bool TryCreateCarvedBody(
        Brep pristineBody,
        IReadOnlyList<Brep> cutters,
        double tolerance,
        out Brep carvedBody)
    {
        if (cutters.Count == 0)
        {
            carvedBody = pristineBody.DuplicateBrep();
            return IsUsableSolid(carvedBody);
        }

        try
        {
            var carved = Brep.CreateBooleanDifference(
                new[] { pristineBody.DuplicateBrep() },
                cutters,
                tolerance,
                true);
            if (carved is null ||
                carved.Length != 1 ||
                !IsUsableSolid(carved[0]))
            {
                carvedBody = null!;
                return false;
            }

            carvedBody = carved[0];
            if (carvedBody.SolidOrientation == BrepSolidOrientation.Inward)
                carvedBody.Flip();
            return true;
        }
        catch
        {
            carvedBody = null!;
            return false;
        }
    }

    private static bool TryCreateSingleSolidUnion(
        IEnumerable<Brep> inputs,
        double tolerance,
        out Brep support)
    {
        try
        {
            var union = Brep.CreateBooleanUnion(
                inputs.Select(input => input.DuplicateBrep()).ToList(),
                tolerance,
                true);
            if (union is null ||
                union.Length != 1 ||
                !IsUsableSolid(union[0]))
            {
                support = null!;
                return false;
            }

            support = union[0];
            if (support.SolidOrientation == BrepSolidOrientation.Inward)
                support.Flip();
            return true;
        }
        catch
        {
            support = null!;
            return false;
        }
    }

    private static bool HasAddedVolume(
        Brep before,
        Brep after,
        double tolerance)
    {
        try
        {
            using var beforeVolume = VolumeMassProperties.Compute(before);
            using var afterVolume = VolumeMassProperties.Compute(after);
            if (beforeVolume is null || afterVolume is null)
                return false;

            var minimumIncrease = Math.Max(
                tolerance * tolerance * tolerance * 8.0,
                Math.Abs(beforeVolume.Volume) * 1e-10);
            return afterVolume.Volume > beforeVolume.Volume + minimumIncrease;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsableSolid(Brep brep)
    {
        return brep.IsValid &&
               brep.IsSolid &&
               brep.IsManifold;
    }

    private static bool IsOnOutlinePlaneAndBoundary(
        Curve edge,
        Curve outline,
        Plane plane,
        double tolerance)
    {
        var distanceTolerance = Math.Max(tolerance * 10.0, RhinoMath.ZeroTolerance);
        for (var index = 0; index <= 8; index++)
        {
            var point = edge.PointAtNormalizedLength(index / 8.0);
            if (Math.Abs(plane.DistanceTo(point)) > distanceTolerance)
                return false;
            if (!outline.ClosestPoint(point, out var outlineParameter))
                return false;
            if (outline.PointAt(outlineParameter).DistanceTo(point) > distanceTolerance)
                return false;
        }

        return true;
    }

    private static bool IsWorldZMonotonic(Curve edge, double tolerance)
    {
        var startZ = edge.PointAtStart.Z;
        var endZ = edge.PointAtEnd.Z;
        var totalChange = endZ - startZ;
        if (Math.Abs(totalChange) <= tolerance * 10.0)
            return false;

        var direction = Math.Sign(totalChange);
        var previousZ = startZ;
        for (var index = 1; index <= MonotonicitySamples; index++)
        {
            var currentZ = edge.PointAtNormalizedLength(
                index / (double)MonotonicitySamples).Z;
            if ((currentZ - previousZ) * direction < -tolerance)
                return false;
            previousZ = currentZ;
        }

        return true;
    }

    private static bool TryTrimAtWorldZBand(
        Curve edge,
        double lowerZ,
        double upperZ,
        double tolerance,
        out Curve contactPiece)
    {
        if (!TryParameterAtWorldZ(edge, lowerZ, tolerance, out var lowerParameter) ||
            !TryParameterAtWorldZ(edge, upperZ, tolerance, out var upperParameter))
        {
            contactPiece = null!;
            return false;
        }

        var interval = new Interval(
            Math.Min(lowerParameter, upperParameter),
            Math.Max(lowerParameter, upperParameter));
        var trimmed = edge.Trim(interval);
        if (trimmed is null || !trimmed.IsValid ||
            trimmed.GetLength() <= tolerance * 4.0)
        {
            contactPiece = null!;
            return false;
        }

        contactPiece = trimmed;
        return true;
    }

    private static bool TryParameterAtWorldZ(
        Curve edge,
        double worldZ,
        double tolerance,
        out double parameter)
    {
        if (Math.Abs(edge.PointAtStart.Z - worldZ) <= tolerance)
        {
            parameter = edge.Domain.Min;
            return true;
        }
        if (Math.Abs(edge.PointAtEnd.Z - worldZ) <= tolerance)
        {
            parameter = edge.Domain.Max;
            return true;
        }

        var horizontalPlane = new Plane(
            new Point3d(0.0, 0.0, worldZ),
            Vector3d.ZAxis);
        var intersections = Intersection.CurvePlane(
            edge,
            horizontalPlane,
            tolerance);
        if (intersections is null)
        {
            parameter = RhinoMath.UnsetValue;
            return false;
        }

        var parameters = intersections
            .Where(intersection => intersection.IsPoint)
            .Select(intersection => intersection.ParameterA)
            .ToList();
        RemoveNearDuplicateParameters(edge, parameters, tolerance);
        if (parameters.Count != 1)
        {
            parameter = RhinoMath.UnsetValue;
            return false;
        }

        parameter = parameters[0];
        return true;
    }

    private static bool TryCreateFixedSlopeTooth(
        Curve outline,
        Curve contactPiece,
        Plane plane,
        Vector3d horizontalOutward,
        double depth,
        double rootThickness,
        double tipLineWidth,
        double tolerance,
        out Brep toothSolid,
        out Brep? bodyCutter,
        out double inwardTaperLength)
    {
        toothSolid = null!;
        bodyCutter = null;
        inwardTaperLength = 0.0;

        var supportNormal = plane.Normal;
        if (!supportNormal.Unitize() ||
            rootThickness <= tolerance * 8.0 ||
            tipLineWidth <= tolerance * 4.0 ||
            tipLineWidth > rootThickness + tolerance)
        {
            return false;
        }

        var effectiveTipWidth =
            rootThickness - tipLineWidth <= tolerance * 4.0
                ? rootThickness
                : tipLineWidth;
        if (!horizontalOutward.Unitize() ||
            Math.Abs(horizontalOutward.Z) > RhinoMath.ZeroTolerance * 10.0)
        {
            return false;
        }

        var joinInset = Math.Max(tolerance * 2.0, rootThickness * 0.01);
        var hiddenJoinWidth = rootThickness - joinInset * 2.0;
        if (hiddenJoinWidth <= tolerance * 4.0)
            return false;

        var totalTaperRun =
            (rootThickness - effectiveTipWidth) /
            (2.0 * FixedTaperSideSlope);
        if (totalTaperRun <= tolerance * 2.0)
        {
            return TryCreateOutwardOnlyTooth(
                outline,
                contactPiece,
                plane,
                horizontalOutward,
                depth,
                rootThickness,
                effectiveTipWidth,
                tolerance,
                out toothSolid);
        }

        var taperStartOffset = depth - totalTaperRun;
        inwardTaperLength = Math.Max(0.0, -taperStartOffset);
        var overlap = Math.Max(tolerance * 12.0, depth * 0.02);
        var hiddenOffset = Math.Min(0.0, taperStartOffset) - overlap;
        var taperStartPiece = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            taperStartOffset);
        var hiddenPiece = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            hiddenOffset);
        var tipPiece = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            depth);
        var cutterOverrun = Math.Min(
            depth,
            Math.Max(tolerance * 8.0, depth * 0.02));
        var cutterOuterPiece = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            cutterOverrun);
        if (taperStartPiece is null ||
            hiddenPiece is null ||
            tipPiece is null ||
            cutterOuterPiece is null ||
            !IsUsableToothRail(contactPiece, tolerance) ||
            !IsUsableToothRail(taperStartPiece, tolerance) ||
            !IsUsableToothRail(hiddenPiece, tolerance) ||
            !IsUsableToothRail(tipPiece, tolerance) ||
            !IsUsableToothRail(cutterOuterPiece, tolerance))
        {
            return false;
        }

        if (!IsHorizontalCorridorValid(
                contactPiece,
                outline,
                plane,
                -horizontalOutward,
                -hiddenOffset,
                tolerance,
                PointContainment.Inside) ||
            !IsHorizontalCorridorValid(
                contactPiece,
                outline,
                plane,
                horizontalOutward,
                depth,
                tolerance,
                PointContainment.Outside) ||
            !HasDesiredContainment(
                hiddenPiece,
                outline,
                plane,
                tolerance,
                PointContainment.Inside))
        {
            return false;
        }

        var pencilSolid = CreateThreeRailSolid(
            hiddenPiece,
            hiddenJoinWidth,
            taperStartPiece,
            rootThickness,
            tipPiece,
            effectiveTipWidth,
            supportNormal,
            tolerance);
        if (pencilSolid is null)
            return false;

        if (taperStartOffset < -tolerance * 2.0)
        {
            bodyCutter = CreateRibbonCutter(
                taperStartPiece,
                cutterOuterPiece,
                plane,
                supportNormal,
                rootThickness,
                tolerance);
            if (bodyCutter is null)
                return false;
        }

        toothSolid = pencilSolid;
        return true;
    }

    private static bool TryCreateOutwardOnlyTooth(
        Curve outline,
        Curve contactPiece,
        Plane plane,
        Vector3d horizontalOutward,
        double depth,
        double rootThickness,
        double tipLineWidth,
        double tolerance,
        out Brep toothSolid)
    {
        toothSolid = null!;

        var supportNormal = plane.Normal;
        if (!supportNormal.Unitize() ||
            rootThickness <= tolerance * 8.0 ||
            tipLineWidth <= tolerance * 4.0 ||
            tipLineWidth > rootThickness + tolerance ||
            !horizontalOutward.Unitize() ||
            Math.Abs(horizontalOutward.Z) >
            RhinoMath.ZeroTolerance * 10.0)
        {
            return false;
        }

        var effectiveTipWidth =
            rootThickness - tipLineWidth <= tolerance * 4.0
                ? rootThickness
                : tipLineWidth;
        var joinInset = Math.Max(tolerance * 2.0, rootThickness * 0.01);
        var hiddenJoinWidth = rootThickness - joinInset * 2.0;
        if (hiddenJoinWidth <= tolerance * 4.0)
            return false;

        var overlap = Math.Max(tolerance * 12.0, depth * 0.02);
        var hiddenPiece = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            -overlap);
        var tipPiece = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            depth);
        if (hiddenPiece is null ||
            tipPiece is null ||
            !IsUsableToothRail(contactPiece, tolerance) ||
            !IsUsableToothRail(hiddenPiece, tolerance) ||
            !IsUsableToothRail(tipPiece, tolerance) ||
            !IsHorizontalCorridorValid(
                contactPiece,
                outline,
                plane,
                -horizontalOutward,
                overlap,
                tolerance,
                PointContainment.Inside) ||
            !IsHorizontalCorridorValid(
                contactPiece,
                outline,
                plane,
                horizontalOutward,
                depth,
                tolerance,
                PointContainment.Outside))
        {
            return false;
        }

        var outwardSolid = CreateThreeRailSolid(
            hiddenPiece,
            hiddenJoinWidth,
            contactPiece,
            rootThickness,
            tipPiece,
            effectiveTipWidth,
            supportNormal,
            tolerance);
        if (outwardSolid is null)
            return false;

        toothSolid = outwardSolid;
        return true;
    }

    private static Brep? CreateThreeRailSolid(
        Curve innerPiece,
        double innerWidth,
        Curve middlePiece,
        double middleWidth,
        Curve outerPiece,
        double outerWidth,
        Vector3d supportNormal,
        double tolerance)
    {
        AlignDirection(middlePiece, innerPiece);
        AlignDirection(middlePiece, outerPiece);
        var faces = new List<Brep>(10);
        if (!TryAddExtrudedRailFace(
                innerPiece,
                supportNormal,
                innerWidth,
                faces) ||
            !TryAddExtrudedRailFace(
                outerPiece,
                supportNormal,
                outerWidth,
                faces) ||
            !TryAddLoftSide(
                innerPiece,
                innerWidth,
                middlePiece,
                middleWidth,
                supportNormal,
                1.0,
                tolerance,
                faces) ||
            !TryAddLoftSide(
                middlePiece,
                middleWidth,
                outerPiece,
                outerWidth,
                supportNormal,
                1.0,
                tolerance,
                faces) ||
            !TryAddLoftSide(
                innerPiece,
                innerWidth,
                middlePiece,
                middleWidth,
                supportNormal,
                -1.0,
                tolerance,
                faces) ||
            !TryAddLoftSide(
                middlePiece,
                middleWidth,
                outerPiece,
                outerWidth,
                supportNormal,
                -1.0,
                tolerance,
                faces) ||
            !TryAddEndFace(
                innerPiece,
                innerWidth,
                middlePiece,
                middleWidth,
                supportNormal,
                true,
                tolerance,
                faces) ||
            !TryAddEndFace(
                middlePiece,
                middleWidth,
                outerPiece,
                outerWidth,
                supportNormal,
                true,
                tolerance,
                faces) ||
            !TryAddEndFace(
                innerPiece,
                innerWidth,
                middlePiece,
                middleWidth,
                supportNormal,
                false,
                tolerance,
                faces) ||
            !TryAddEndFace(
                middlePiece,
                middleWidth,
                outerPiece,
                outerWidth,
                supportNormal,
                false,
                tolerance,
                faces))
        {
            return null;
        }

        var joined = Brep.JoinBreps(faces, tolerance);
        if (joined is null || joined.Length != 1)
            return null;

        var tooth = joined[0];
        if (!tooth.IsValid || !tooth.IsSolid || !tooth.IsManifold)
            return null;

        if (tooth.SolidOrientation == BrepSolidOrientation.Inward)
            tooth.Flip();

        return tooth;
    }

    private static Brep? CreateRibbonCutter(
        Curve innerRail,
        Curve outerRail,
        Plane plane,
        Vector3d supportNormal,
        double bodyThickness,
        double tolerance)
    {
        var inner = innerRail.DuplicateCurve();
        var outer = outerRail.DuplicateCurve();
        AlignDirection(inner, outer);

        var innerStart = inner.PointAtStart;
        var innerEnd = inner.PointAtEnd;
        var outerStart = outer.PointAtStart;
        var outerEnd = outer.PointAtEnd;
        outer.Reverse();

        var loop = new PolyCurve();
        if (!loop.Append(inner) ||
            !loop.Append(new LineCurve(innerEnd, outerEnd)) ||
            !loop.Append(outer) ||
            !loop.Append(new LineCurve(outerStart, innerStart)) ||
            !loop.IsClosed ||
            !loop.IsValid)
        {
            return null;
        }

        var selfIntersections = Intersection.CurveSelf(loop, tolerance);
        if (selfIntersections is not null && selfIntersections.Count > 0)
            return null;

        var cutMargin = Math.Max(tolerance * 4.0, bodyThickness * 0.01);
        var cutWidth = bodyThickness + cutMargin * 2.0;
        var cutterPlane = new Plane(
            plane.Origin - supportNormal * (cutWidth / 2.0),
            plane.XAxis,
            plane.YAxis);
        var extrusion = Extrusion.Create(
            loop,
            cutterPlane,
            cutWidth,
            true);
        var cutter = extrusion?.ToBrep(true);
        if (cutter is null ||
            !cutter.IsValid ||
            !cutter.IsSolid ||
            !cutter.IsManifold)
        {
            return null;
        }

        if (cutter.SolidOrientation == BrepSolidOrientation.Inward)
            cutter.Flip();

        return cutter;
    }

    private static bool IsUsableToothRail(
        Curve rail,
        double tolerance)
    {
        if (!rail.IsValid || rail.IsClosed ||
            rail.GetLength() <= tolerance * 4.0)
        {
            return false;
        }

        var intersections = Intersection.CurveSelf(rail, tolerance);
        return intersections is null || intersections.Count == 0;
    }

    private static bool TryAddExtrudedRailFace(
        Curve rail,
        Vector3d supportNormal,
        double width,
        ICollection<Brep> faces)
    {
        if (width <= RhinoMath.ZeroTolerance)
            return false;

        var halfWidth = width / 2.0;
        var baseRail = CreateTranslatedRail(
            rail,
            supportNormal,
            -halfWidth);
        if (baseRail is null)
            return false;

        var surface = Surface.CreateExtrusion(
            baseRail,
            supportNormal * width);
        if (surface is null)
            return false;

        var face = Brep.CreateFromSurface(surface);
        if (face is null || !face.IsValid)
            return false;

        faces.Add(face);
        return true;
    }

    private static bool TryAddLoftSide(
        Curve railA,
        double widthA,
        Curve railB,
        double widthB,
        Vector3d supportNormal,
        double side,
        double tolerance,
        ICollection<Brep> faces)
    {
        var curveA = CreateTranslatedRail(
            railA,
            supportNormal,
            side * widthA / 2.0);
        var curveB = CreateTranslatedRail(
            railB,
            supportNormal,
            side * widthB / 2.0);
        if (curveA is null || curveB is null)
            return false;

        AlignDirection(curveA, curveB);
        curveA.Domain = new Interval(0.0, 1.0);
        curveB.Domain = new Interval(0.0, 1.0);

        var loft = Brep.CreateFromLoft(
            new Curve[] { curveA, curveB },
            Point3d.Unset,
            Point3d.Unset,
            LoftType.Straight,
            false,
            tolerance);
        if (loft is null || loft.Length != 1 || !loft[0].IsValid)
            return false;

        faces.Add(loft[0]);
        return true;
    }

    private static bool TryAddEndFace(
        Curve railA,
        double widthA,
        Curve railB,
        double widthB,
        Vector3d supportNormal,
        bool atStart,
        double tolerance,
        ICollection<Brep> faces)
    {
        var pointA = atStart ? railA.PointAtStart : railA.PointAtEnd;
        var pointB = atStart ? railB.PointAtStart : railB.PointAtEnd;
        if (pointA.DistanceTo(pointB) <= tolerance * 2.0)
            return false;

        var aMinus = pointA - supportNormal * (widthA / 2.0);
        var bMinus = pointB - supportNormal * (widthB / 2.0);
        var bPlus = pointB + supportNormal * (widthB / 2.0);
        var aPlus = pointA + supportNormal * (widthA / 2.0);
        var face = Brep.CreateFromCornerPoints(
            aMinus,
            bMinus,
            bPlus,
            aPlus,
            tolerance);
        if (face is null || !face.IsValid)
            return false;

        faces.Add(face);
        return true;
    }

    private static Curve? CreateTranslatedRail(
        Curve source,
        Vector3d supportNormal,
        double distance)
    {
        var copy = source.DuplicateCurve();
        if (!copy.Translate(supportNormal * distance))
            return null;

        return copy.IsValid ? copy : null;
    }

    private static bool TryGetHorizontalOutwardDirection(
        Curve contactEdge,
        Curve outline,
        Plane plane,
        double depth,
        double tolerance,
        out Vector3d horizontalOutward)
    {
        horizontalOutward = Vector3d.Unset;
        var horizontalAxis = Vector3d.CrossProduct(
            Vector3d.ZAxis,
            plane.Normal);
        if (!horizontalAxis.Unitize())
            return false;

        var probeDistance = Math.Min(
            depth,
            Math.Max(tolerance * 6.0, depth * 0.05));
        if (probeDistance <= tolerance)
            return false;

        var candidates = new List<Vector3d>();
        foreach (var directionSign in new[] { 1.0, -1.0 })
        {
            var candidate = horizontalAxis * directionSign;
            if (IsConsistentHorizontalBoundaryDirection(
                    contactEdge,
                    outline,
                    plane,
                    candidate,
                    probeDistance,
                    tolerance))
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count != 1)
            return false;

        horizontalOutward = candidates[0];
        return true;
    }

    private static bool IsConsistentHorizontalBoundaryDirection(
        Curve contactEdge,
        Curve outline,
        Plane plane,
        Vector3d candidateOutward,
        double probeDistance,
        double tolerance)
    {
        foreach (var normalizedLength in
                 new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
        {
            var point = contactEdge.PointAtNormalizedLength(normalizedLength);
            if (outline.Contains(
                    point + candidateOutward * probeDistance,
                    plane,
                    tolerance) != PointContainment.Outside ||
                outline.Contains(
                    point - candidateOutward * probeDistance,
                    plane,
                    tolerance) != PointContainment.Inside)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHorizontalCorridorValid(
        Curve source,
        Curve outline,
        Plane plane,
        Vector3d direction,
        double distance,
        double tolerance,
        PointContainment desiredContainment)
    {
        foreach (var fraction in new[] { 0.5, 1.0 })
        {
            if (distance * fraction <= tolerance * 1.5)
                continue;

            var translated = CreateTranslatedRail(
                source,
                direction,
                distance * fraction);
            if (translated is null ||
                !HasDesiredContainment(
                    translated,
                    outline,
                    plane,
                    tolerance,
                    desiredContainment))
            {
                return false;
            }

            var intersections = Intersection.CurveCurve(
                translated,
                outline,
                tolerance,
                tolerance);
            if (intersections is not null && intersections.Count > 0)
                return false;
        }

        return true;
    }

    private static bool HasDesiredContainment(
        Curve curve,
        Curve outline,
        Plane plane,
        double tolerance,
        PointContainment desiredContainment)
    {
        foreach (var normalizedLength in
                 new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
        {
            var point = curve.PointAtNormalizedLength(normalizedLength);
            if (outline.Contains(point, plane, tolerance) !=
                desiredContainment)
            {
                return false;
            }
        }

        return true;
    }

    private static void AlignDirection(Curve reference, Curve candidate)
    {
        var alignedDistance =
            reference.PointAtStart.DistanceTo(candidate.PointAtStart) +
            reference.PointAtEnd.DistanceTo(candidate.PointAtEnd);
        var reversedDistance =
            reference.PointAtStart.DistanceTo(candidate.PointAtEnd) +
            reference.PointAtEnd.DistanceTo(candidate.PointAtStart);
        if (reversedDistance < alignedDistance)
            candidate.Reverse();
    }

    private static void RemoveNearDuplicateParameters(
        Curve curve,
        List<double> parameters,
        double tolerance)
    {
        parameters.Sort();
        for (var index = parameters.Count - 1; index > 0; index--)
        {
            if (curve.PointAt(parameters[index])
                    .DistanceTo(curve.PointAt(parameters[index - 1])) <= tolerance)
            {
                parameters.RemoveAt(index);
            }
        }
    }
}

internal readonly record struct EdgeTeethBuildReport(
    int RequestedCount,
    int FixedSlopeCount,
    int OutwardOnlyCount,
    int SkippedCount,
    string BuildMode,
    bool UsedGlobalFallback,
    bool UsedSequentialUnion,
    double TaperAngleDegrees,
    double MaximumInwardTaperLength)
{
    public static EdgeTeethBuildReport Empty =>
        new(0, 0, 0, 0, "None", false, false, 0.0, 0.0);
}
