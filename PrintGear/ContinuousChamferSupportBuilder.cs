using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace PrintGear;

/// <summary>
/// Reproduces the manual support workflow: extrude the complete support body,
/// chamfer both longitudinal edges beside the selected contact segment, then
/// array constant-size contact-band teeth along that segment.
/// </summary>
internal static class ContinuousChamferSupportBuilder
{
    private const int MaximumToothCount = 512;
    private const int MonotonicitySamples = 64;
    private const double VerticalPlaneAngleToleranceRadians = Math.PI / 180.0;

    public static Brep? Create(
        Curve outline,
        Curve contactEdge,
        SupportParameters parameters,
        double tipLineWidth,
        int toothCount,
        double tolerance,
        out int contactCount,
        out ContinuousChamferBuildReport buildReport,
        out string error)
    {
        contactCount = 0;
        buildReport = ContinuousChamferBuildReport.Empty;

        if (!parameters.IsValid(tolerance, out error))
            return null;
        if (parameters.ContactClearance <= tolerance * 8.0)
        {
            error = "齒尖長必須大於文件絕對公差的八倍。";
            return null;
        }
        if (!RhinoMath.IsValidDouble(tipLineWidth) ||
            tipLineWidth <= tolerance * 4.0)
        {
            error = "噴嘴尺寸必須大於文件絕對公差的四倍。";
            return null;
        }
        if (tipLineWidth > parameters.Thickness + tolerance)
        {
            error = "噴嘴尺寸不可大於支撐厚度。";
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
                "否則無法同時保持方塊齒的 World Z 高度與 XY 水平伸長。";
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
        if (toothCount < 2 || toothCount > MaximumToothCount)
        {
            error = $"沿線齒尖數量均分必須介於 2 至 {MaximumToothCount} 顆（包含頭尾）。";
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
        if (mainBody is null || !IsUsableSolid(mainBody))
        {
            error = "無法建立封閉的支撐主體。";
            return null;
        }
        if (mainBody.SolidOrientation == BrepSolidOrientation.Inward)
            mainBody.Flip();

        if (!TryCreateContinuousChamfer(
                mainBody,
                contactEdge,
                plane,
                parameters.Thickness,
                tipLineWidth,
                tolerance,
                out var chamferedBody,
                out var requestedChamferDistance,
                out var actualChamferDistance,
                out var usedAdjustedChamfer,
                out error))
        {
            return null;
        }

        var distributionEdge = contactEdge.DuplicateCurve();
        if (distributionEdge.PointAtStart.Z > distributionEdge.PointAtEnd.Z)
            distributionEdge.Reverse();

        var halfHeight = toothHeight / 2.0;
        var endSafety = Math.Max(tolerance * 4.0, toothHeight * 0.005);
        var firstCenterZ = minimumZ + halfHeight + endSafety;
        var lastCenterZ = maximumZ - halfHeight - endSafety;
        // At a triangular apex the available root width tends to zero. Locate the
        // nearest complete tooth at each end before distributing the requested count.
        bool CanPlaceEnd(double centerZ)
        {
            if(!TryTrimAtWorldZBand(distributionEdge,centerZ-halfHeight,centerZ+halfHeight,tolerance,out var piece))
                return false;
            using(piece)
            {
                if(!TryCreateContactBandTooth(piece,outline,plane,horizontalOutward,
                    parameters.ContactClearance,toothHeight,tipLineWidth,tolerance,out var trial))return false;
                trial.Dispose();return true;
            }
        }
        bool FindUsableEnd(double start,double limit,out double usable)
        {
            usable=start;
            if(CanPlaceEnd(start))return true;
            var sign=Math.Sign(limit-start);
            var step=Math.Max(tolerance*4,toothHeight*.05);
            var bad=start;
            while(Math.Abs(bad-limit)>tolerance)
            {
                var good=start+sign*Math.Min(step,Math.Abs(limit-start));
                if(CanPlaceEnd(good))
                {
                    for(var iteration=0;iteration<24 && Math.Abs(good-bad)>tolerance*.1;iteration++)
                    {
                        var mid=(bad+good)/2;
                        if(CanPlaceEnd(mid))good=mid;else bad=mid;
                    }
                    usable=good+sign*Math.Min(tolerance*2,Math.Abs(limit-good));
                    return CanPlaceEnd(usable);
                }
                bad=good;step*=2;
            }
            return false;
        }
        var middleZ=(firstCenterZ+lastCenterZ)/2;
        if(firstCenterZ>=lastCenterZ ||
            !FindUsableEnd(firstCenterZ,middleZ,out firstCenterZ) ||
            !FindUsableEnd(lastCenterZ,middleZ,out lastCenterZ))
        {
            error="輪廓端部沒有足夠空間容納完整齒根；請加寬輪廓或減小齒尺寸。";
            return null;
        }
        if (firstCenterZ >= lastCenterZ - tolerance ||
            !TryParameterAtWorldZ(
                distributionEdge,
                firstCenterZ,
                tolerance,
                out var firstCenterParameter) ||
            !TryParameterAtWorldZ(distributionEdge, lastCenterZ, tolerance, out var lastCenterParameter))
        {
            error = "接觸邊端點附近沒有足夠空間放置完整高度的方塊齒。";
            return null;
        }

        var firstCenterLength = distributionEdge.GetLength(
            new Interval(
                distributionEdge.Domain.Min,
                firstCenterParameter));
        var lastCenterLength = distributionEdge.GetLength(
            new Interval(distributionEdge.Domain.Min, lastCenterParameter));
        var totalLength = distributionEdge.GetLength();
        if (!RhinoMath.IsValidDouble(firstCenterLength) ||
            !RhinoMath.IsValidDouble(totalLength) ||
            totalLength <= tolerance * 4.0)
        {
            error = "無法量測所選接觸邊的沿線長度。";
            return null;
        }

        var requestedCount = toothCount;
        var teeth = new List<(double AlongEdgeDistance, Brep Solid)>();
        var previousCenterZ = double.NegativeInfinity;
        for (var index = 0; index < toothCount; index++)
        {
            var alongEdgeDistance = firstCenterLength +
                (lastCenterLength - firstCenterLength) * index / (toothCount - 1);
            if (!distributionEdge.LengthParameter(alongEdgeDistance, out var centerParameter))
            {
                error = "無法計算均分齒尖位置，未輸出支撐。";
                return null;
            }
            var contactPoint = distributionEdge.PointAt(centerParameter);
            if (contactPoint.Z - previousCenterZ <= toothHeight + tolerance * 4.0)
            {
                error = "均分齒尖過密，完整齒形之間沒有足夠間隙；請減少齒尖數量或列印厚度。";
                return null;
            }
            previousCenterZ = contactPoint.Z;
            try
            {
                if (TryTrimAtWorldZBand(
                        distributionEdge,
                        contactPoint.Z - halfHeight,
                        contactPoint.Z + halfHeight,
                        tolerance,
                        out var contactPiece) &&
                    TryCreateContactBandTooth(
                        contactPiece,
                        outline,
                        plane,
                        horizontalOutward,
                        parameters.ContactClearance,
                        toothHeight,
                        tipLineWidth,
                        tolerance,
                        out var tooth))
                {
                    teeth.Add((alongEdgeDistance, tooth));
                }
            }
            catch
            {
                // Reject incomplete tooth sets below, including either missing end tooth.
            }
        }

        if (teeth.Count != requestedCount)
        {
            error =
                "無法建立全部指定齒尖（包含頭尾）；請調整輪廓或齒尺寸，未輸出不完整支撐。";
            return null;
        }

        var unionInputs = new List<Brep> { chamferedBody };
        unionInputs.AddRange(teeth.Select(tooth => tooth.Solid));
        if (TryCreateSingleSolidUnion(
                unionInputs,
                tolerance,
                out var support) &&
            HasAddedVolume(chamferedBody, support, tolerance))
        {
            contactCount = teeth.Count;
            buildReport = CreateReport(
                requestedCount,
                contactCount,
                requestedChamferDistance,
                actualChamferDistance,
                usedAdjustedChamfer,
                false);
            error = string.Empty;
            return support;
        }

        var sequentialSupport = chamferedBody.DuplicateBrep();
        var sequentialCount = 0;
        foreach (var tooth in teeth.OrderBy(tooth => tooth.AlongEdgeDistance))
        {
            if (!TryCreateSingleSolidUnion(
                    new[] { sequentialSupport, tooth.Solid },
                    tolerance,
                    out var candidateSupport) ||
                !HasAddedVolume(sequentialSupport, candidateSupport, tolerance))
            {
                continue;
            }

            sequentialSupport = candidateSupport;
            sequentialCount++;
        }

        if (sequentialCount != requestedCount ||
            !IsUsableSolid(sequentialSupport) ||
            !HasAddedVolume(chamferedBody, sequentialSupport, tolerance))
        {
            error =
                "無法合併全部指定齒尖（包含頭尾）；未輸出缺齒支撐。";
            return null;
        }

        contactCount = sequentialCount;
        buildReport = CreateReport(
            requestedCount,
            contactCount,
            requestedChamferDistance,
            actualChamferDistance,
            usedAdjustedChamfer,
            true);
        error = string.Empty;
        return sequentialSupport;
    }

    private static ContinuousChamferBuildReport CreateReport(
        int requestedCount,
        int contactCount,
        double requestedChamferDistance,
        double actualChamferDistance,
        bool usedAdjustedChamfer,
        bool usedSequentialUnion)
    {
        var chamferApplied = actualChamferDistance > RhinoMath.ZeroTolerance;
        var buildMode = chamferApplied
            ? usedAdjustedChamfer
                ? "ContinuousChamferAdjusted"
                : "ContinuousChamferContactBandTeeth"
            : "ContactBandTeethNoChamferNeeded";
        if (usedSequentialUnion)
            buildMode += "Sequential";

        return new ContinuousChamferBuildReport(
            requestedCount,
            contactCount,
            requestedCount - contactCount,
            buildMode,
            chamferApplied,
            usedAdjustedChamfer,
            usedSequentialUnion,
            requestedChamferDistance,
            actualChamferDistance);
    }

    private static bool TryCreateContinuousChamfer(
        Brep mainBody,
        Curve contactEdge,
        Plane plane,
        double thickness,
        double tipLineWidth,
        double tolerance,
        out Brep chamferedBody,
        out double requestedDistance,
        out double actualDistance,
        out bool usedAdjustedDistance,
        out string error)
    {
        requestedDistance = Math.Max(0.0, (thickness - tipLineWidth) / 2.0);
        actualDistance = 0.0;
        usedAdjustedDistance = false;

        if (requestedDistance <= tolerance * 2.0)
        {
            chamferedBody = mainBody.DuplicateBrep();
            error = string.Empty;
            return IsUsableSolid(chamferedBody);
        }

        if (!TryFindContactChamferEdges(
                mainBody,
                contactEdge,
                plane,
                thickness,
                tolerance,
                out var edgeIndices))
        {
            chamferedBody = null!;
            error =
                "無法在擠出主體上辨識接觸線前後兩條 ChamferEdge。" +
                "請確認接觸區是閉合 PolyCurve 中的獨立邊段。";
            return false;
        }

        var attempts = new List<(double Distance, double Tolerance, bool Adjusted)>
        {
            (requestedDistance, tolerance, false),
            (requestedDistance, tolerance * 2.0, false)
        };
        var adjustedDistance = requestedDistance - tolerance * 2.0;
        if (adjustedDistance > tolerance * 4.0)
            attempts.Add((adjustedDistance, tolerance * 2.0, true));

        foreach (var attempt in attempts)
        {
            try
            {
                var distances = Enumerable
                    .Repeat(attempt.Distance, edgeIndices.Length)
                    .ToArray();
                var results = Brep.CreateFilletEdges(
                    mainBody.DuplicateBrep(),
                    edgeIndices,
                    distances,
                    distances,
                    BlendType.Chamfer,
                    RailType.DistanceFromEdge,
                    attempt.Tolerance);
                if (results is null)
                    continue;

                var validResults = results
                    .Where(IsUsableSolid)
                    .ToList();
                if (validResults.Count != 1)
                    continue;

                chamferedBody = validResults[0];
                if (chamferedBody.SolidOrientation ==
                    BrepSolidOrientation.Inward)
                {
                    chamferedBody.Flip();
                }

                requestedDistance = Math.Max(
                    0.0,
                    (thickness - tipLineWidth) / 2.0);
                actualDistance = attempt.Distance;
                usedAdjustedDistance = attempt.Adjusted;
                error = string.Empty;
                return true;
            }
            catch
            {
                // Try the next numerically safer ChamferEdge attempt.
            }
        }

        chamferedBody = null!;
        error =
            $"無法沿整條接觸邊建立 {requestedDistance:0.###} 的連續 Chamfer。" +
            "請縮短接觸邊段、加寬支撐輪廓內側空間，或增加齒尖線寬XY。";
        return false;
    }

    private static bool TryFindContactChamferEdges(
        Brep mainBody,
        Curve contactEdge,
        Plane plane,
        double thickness,
        double tolerance,
        out int[] edgeIndices)
    {
        var normal = plane.Normal;
        if (!normal.Unitize())
        {
            edgeIndices = Array.Empty<int>();
            return false;
        }

        var matchTolerance = Math.Max(
            tolerance * 20.0,
            thickness * 1e-6);
        var found = new List<int>(2);
        foreach (var offset in new[] { -thickness / 2.0, thickness / 2.0 })
        {
            var expected = contactEdge.DuplicateCurve();
            if (!expected.Translate(normal * offset))
            {
                edgeIndices = Array.Empty<int>();
                return false;
            }

            var matches = mainBody.Edges
                .Where(edge => edge.Valence == EdgeAdjacency.Interior)
                .Where(edge => AreCoincidentCurves(
                    edge,
                    expected,
                    matchTolerance))
                .Select(edge => edge.EdgeIndex)
                .Distinct()
                .ToList();
            if (matches.Count != 1)
            {
                edgeIndices = Array.Empty<int>();
                return false;
            }

            found.Add(matches[0]);
        }

        if (found.Count != 2 || found[0] == found[1])
        {
            edgeIndices = Array.Empty<int>();
            return false;
        }

        edgeIndices = found.ToArray();
        return true;
    }

    private static bool AreCoincidentCurves(
        Curve candidate,
        Curve expected,
        double tolerance)
    {
        var candidateLength = candidate.GetLength();
        var expectedLength = expected.GetLength();
        var lengthTolerance = Math.Max(
            tolerance,
            expectedLength * 1e-6);
        if (Math.Abs(candidateLength - expectedLength) > lengthTolerance)
            return false;

        foreach (var normalizedLength in
                 new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var expectedPoint =
                expected.PointAtNormalizedLength(normalizedLength);
            if (!candidate.ClosestPoint(
                    expectedPoint,
                    out var candidateParameter) ||
                candidate.PointAt(candidateParameter)
                    .DistanceTo(expectedPoint) > tolerance)
            {
                return false;
            }

            var candidatePoint =
                candidate.PointAtNormalizedLength(normalizedLength);
            if (!expected.ClosestPoint(
                    candidatePoint,
                    out var expectedParameter) ||
                expected.PointAt(expectedParameter)
                    .DistanceTo(candidatePoint) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateContactBandTooth(
        Curve contactPiece,
        Curve outline,
        Plane outlinePlane,
        Vector3d horizontalOutward,
        double depth,
        double toothHeight,
        double toothWidth,
        double tolerance,
        out Brep tooth)
    {
        tooth = null!;
        var supportNormal = outlinePlane.Normal;
        if (!IsUsableToothRail(contactPiece, tolerance) ||
            !supportNormal.Unitize() ||
            !horizontalOutward.Unitize() ||
            depth <= tolerance * 8.0 ||
            toothHeight <= tolerance * 4.0 ||
            toothWidth <= tolerance * 4.0 ||
            Math.Abs(horizontalOutward.Z) >
                RhinoMath.ZeroTolerance * 10.0 ||
            Math.Abs(Vector3d.Multiply(
                horizontalOutward,
                supportNormal)) > 1e-8)
        {
            return false;
        }

        var actualHeight = Math.Abs(
            contactPiece.PointAtEnd.Z -
            contactPiece.PointAtStart.Z);
        if (Math.Abs(actualHeight - toothHeight) >
            Math.Max(tolerance * 2.0, toothHeight * 1e-6))
        {
            return false;
        }

        var joinOverlap = Math.Min(
            depth * 0.25,
            Math.Max(
                tolerance * 8.0,
                Math.Min(depth * 0.05, toothWidth * 0.05)));
        if (joinOverlap <= tolerance)
            return false;

        if (!IsToothCorridorValid(
                contactPiece,
                outline,
                outlinePlane,
                horizontalOutward,
                joinOverlap,
                depth,
                tolerance))
        {
            return false;
        }

        var innerRail = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            -joinOverlap);
        var outerRail = CreateTranslatedRail(
            contactPiece,
            horizontalOutward,
            depth);
        if (innerRail is null ||
            outerRail is null ||
            !IsUsableToothRail(innerRail, tolerance) ||
            !IsUsableToothRail(outerRail, tolerance))
        {
            return false;
        }

        AlignDirection(innerRail, outerRail);
        var innerStart = innerRail.PointAtStart;
        var innerEnd = innerRail.PointAtEnd;
        var outerStart = outerRail.PointAtStart;
        var outerEnd = outerRail.PointAtEnd;
        if (Math.Abs(innerStart.Z - outerStart.Z) > tolerance ||
            Math.Abs(innerEnd.Z - outerEnd.Z) > tolerance)
        {
            return false;
        }

        outerRail.Reverse();
        var footprint = new PolyCurve();
        if (!footprint.Append(innerRail) ||
            !footprint.Append(new LineCurve(innerEnd, outerEnd)) ||
            !footprint.Append(outerRail) ||
            !footprint.Append(new LineCurve(outerStart, innerStart)) ||
            !footprint.IsValid ||
            !footprint.IsClosed)
        {
            return false;
        }

        var selfIntersections = Intersection.CurveSelf(
            footprint,
            tolerance);
        if (selfIntersections is not null && selfIntersections.Count > 0)
            return false;

        var toothPlane = new Plane(
            outlinePlane.Origin - supportNormal * (toothWidth / 2.0),
            outlinePlane.XAxis,
            outlinePlane.YAxis);
        var extrusion = Extrusion.Create(
            footprint,
            toothPlane,
            toothWidth,
            true);
        var toothBrep = extrusion?.ToBrep(true);
        if (toothBrep is null || !IsUsableSolid(toothBrep))
            return false;

        if (toothBrep.SolidOrientation == BrepSolidOrientation.Inward)
            toothBrep.Flip();
        tooth = toothBrep;
        return true;
    }

    private static bool TryTrimAtWorldZBand(
        Curve edge,
        double lowerZ,
        double upperZ,
        double tolerance,
        out Curve contactPiece)
    {
        if (!TryParameterAtWorldZ(
                edge,
                lowerZ,
                tolerance,
                out var lowerParameter) ||
            !TryParameterAtWorldZ(
                edge,
                upperZ,
                tolerance,
                out var upperParameter))
        {
            contactPiece = null!;
            return false;
        }

        var interval = new Interval(
            Math.Min(lowerParameter, upperParameter),
            Math.Max(lowerParameter, upperParameter));
        var trimmed = edge.Trim(interval);
        if (trimmed is null ||
            !IsUsableToothRail(trimmed, tolerance))
        {
            contactPiece = null!;
            return false;
        }

        contactPiece = trimmed;
        return true;
    }

    private static Curve? CreateTranslatedRail(
        Curve source,
        Vector3d direction,
        double distance)
    {
        var translated = source.DuplicateCurve();
        if (!translated.Translate(direction * distance))
            return null;

        return translated.IsValid ? translated : null;
    }

    private static bool IsUsableToothRail(
        Curve rail,
        double tolerance)
    {
        if (!rail.IsValid ||
            rail.IsClosed ||
            rail.GetLength() <= tolerance * 4.0)
        {
            return false;
        }

        var selfIntersections = Intersection.CurveSelf(
            rail,
            tolerance);
        return selfIntersections is null || selfIntersections.Count == 0;
    }

    private static bool IsToothCorridorValid(
        Curve contactPiece,
        Curve outline,
        Plane plane,
        Vector3d horizontalOutward,
        double joinOverlap,
        double depth,
        double tolerance)
    {
        foreach (var normalizedLength in
                 new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var boundaryPoint =
                contactPiece.PointAtNormalizedLength(normalizedLength);
            if (outline.Contains(
                    boundaryPoint -
                    horizontalOutward * joinOverlap,
                    plane,
                    tolerance) != PointContainment.Inside)
            {
                return false;
            }

            foreach (var fraction in
                     new[] { 0.25, 0.5, 0.75, 1.0 })
            {
                if (outline.Contains(
                        boundaryPoint +
                        horizontalOutward * (depth * fraction),
                        plane,
                        tolerance) != PointContainment.Outside)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void AlignDirection(
        Curve reference,
        Curve candidate)
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

    private static bool IsOnOutlinePlaneAndBoundary(
        Curve edge,
        Curve outline,
        Plane plane,
        double tolerance)
    {
        var distanceTolerance = Math.Max(
            tolerance * 10.0,
            RhinoMath.ZeroTolerance);
        for (var index = 0; index <= 8; index++)
        {
            var point = edge.PointAtNormalizedLength(index / 8.0);
            if (Math.Abs(plane.DistanceTo(point)) > distanceTolerance)
                return false;
            if (!outline.ClosestPoint(point, out var outlineParameter))
                return false;
            if (outline.PointAt(outlineParameter).DistanceTo(point) >
                distanceTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWorldZMonotonic(
        Curve edge,
        double tolerance)
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
            return afterVolume.Volume >
                   beforeVolume.Volume + minimumIncrease;
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

    private static void RemoveNearDuplicateParameters(
        Curve curve,
        List<double> parameters,
        double tolerance)
    {
        parameters.Sort();
        for (var index = parameters.Count - 1; index > 0; index--)
        {
            if (curve.PointAt(parameters[index])
                    .DistanceTo(curve.PointAt(parameters[index - 1])) <=
                tolerance)
            {
                parameters.RemoveAt(index);
            }
        }
    }
}

internal readonly record struct ContinuousChamferBuildReport(
    int RequestedCount,
    int RectangularToothCount,
    int SkippedCount,
    string BuildMode,
    bool ChamferApplied,
    bool UsedAdjustedChamfer,
    bool UsedSequentialUnion,
    double RequestedChamferDistance,
    double ActualChamferDistance)
{
    public static ContinuousChamferBuildReport Empty =>
        new(0, 0, 0, "None", false, false, false, 0.0, 0.0);
}
