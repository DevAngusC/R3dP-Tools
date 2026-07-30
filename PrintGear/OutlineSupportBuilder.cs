using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace PrintGear;

/// <summary>
/// Builds a light-touch support from a user-defined closed planar outline.
/// The target can be a whole model or a copied contact face. It is sectioned
/// in the outline plane and periodic contact teeth are left on the boundary.
/// </summary>
internal static class OutlineSupportBuilder
{
    private const int MaximumContactArcs = 64;

    public static Brep? Create(
        IReadOnlyList<GeometryBase> targets,
        Curve outline,
        SupportParameters parameters,
        double tolerance,
        out int contactCount,
        out string warning,
        out string error)
    {
        contactCount = 0;
        warning = string.Empty;

        if (!parameters.IsValid(tolerance, out error))
            return null;
        if (targets.Count == 0 || targets.All(target => target is null || !target.IsValid))
        {
            error = "沒有取得可用的目標模型。";
            return null;
        }

        if (!outline.IsValid || !outline.IsClosed)
        {
            error = "自訂支撐輪廓必須是有效的閉合曲線。";
            return null;
        }

        if (!outline.TryGetPlane(out var plane, tolerance))
        {
            error = "自訂支撐輪廓必須位於單一平面內。";
            return null;
        }

        var outlineIntersections = Intersection.CurveSelf(outline, tolerance);
        if (outlineIntersections is not null && outlineIntersections.Count > 0)
        {
            error = "自訂支撐輪廓不可自交。";
            return null;
        }

        using (var outlineArea = AreaMassProperties.Compute(outline, tolerance))
        {
            if (outlineArea is null || Math.Abs(outlineArea.Area) <= tolerance * tolerance)
            {
                error = "自訂支撐輪廓的面積太小。";
                return null;
            }
        }

        var sectionCurves = CreateSectionCurves(targets, plane, tolerance);
        if (sectionCurves.Count == 0)
        {
            error =
                "支撐輪廓所在平面沒有切到接觸面或目標模型。" +
                "請確認兩者實際相交，而不是只在透視畫面上看起來接觸。";
            return null;
        }

        var closedSectionLoops = sectionCurves
            .Where(section => section.IsClosed)
            .ToList();
        var contactSections = closedSectionLoops.Count > 0
            ? closedSectionLoops
            : sectionCurves;
        if (!TryCreateContactArcs(
                outline,
                contactSections,
                plane,
                tolerance,
                out var contactArcs,
                out error))
        {
            return null;
        }

        Curve supportRegion;
        var createdRegion = closedSectionLoops.Count > 0
            ? TryCreateFlushRegion(
                outline,
                closedSectionLoops,
                tolerance,
                out supportRegion,
                out warning,
                out error)
            : TryCreateFlushRegionFromContactFace(
                outline,
                sectionCurves,
                plane,
                tolerance,
                out supportRegion,
                out warning,
                out error);
        if (!createdRegion)
        {
            return null;
        }

        if (closedSectionLoops.Count == 0)
        {
            contactArcs = contactArcs
                .Where(contactArc =>
                    IsCurveOnBoundary(contactArc, supportRegion, tolerance))
                .ToList();
            if (contactArcs.Count == 0)
            {
                error =
                    "已切出接觸面，但無法判定哪一段屬於保留的支撐區域。" +
                    "請讓支撐輪廓只跨過一個接觸區，且穿入接觸面一小段。";
                return null;
            }
        }

        foreach (var contactArc in contactArcs)
        {
            var gaps = CreateClearanceGaps(
                contactArc,
                parameters.ContactWidth,
                parameters.ContactSpacing,
                tolerance,
                out var arcContactCount);
            contactCount += arcContactCount;

            foreach (var gap in gaps)
            {
                if (parameters.ContactClearance <= tolerance)
                    continue;

                if (!TryCreateClearanceStrip(
                        gap,
                        supportRegion,
                        plane,
                        parameters.ContactClearance,
                        tolerance,
                        out var strip,
                        out error))
                {
                    return null;
                }

                var difference = Curve.CreateBooleanDifference(
                    supportRegion,
                    strip,
                    tolerance);
                if (difference is null || difference.Length != 1)
                {
                    error =
                        "接觸退讓切開了支撐輪廓。" +
                        "請減少接觸退讓或加大手繪支撐造型。";
                    return null;
                }

                supportRegion = difference[0];
            }
        }

        if (!supportRegion.IsValid || !supportRegion.IsClosed)
        {
            error = "參數化接觸齒完成後，支撐輪廓無法封閉。";
            return null;
        }

        var finalIntersections = Intersection.CurveSelf(supportRegion, tolerance);
        if (finalIntersections is not null && finalIntersections.Count > 0)
        {
            error =
                "參數化接觸齒造成支撐輪廓自交。" +
                "請減少接觸退讓或增加接觸齒間距。";
            return null;
        }

        var extrusionPlane = new Plane(
            plane.Origin - plane.Normal * (parameters.Thickness / 2.0),
            plane.XAxis,
            plane.YAxis);
        var extrusion = Extrusion.Create(
            supportRegion,
            extrusionPlane,
            parameters.Thickness,
            true);
        if (extrusion is null)
        {
            error = "無法將自訂支撐輪廓擠出為實體。";
            return null;
        }

        var support = extrusion.ToBrep(true);
        if (support is null || !support.IsValid || !support.IsSolid)
        {
            error = "無法建立封閉且可列印的自訂支撐實體。";
            return null;
        }

        if (support.SolidOrientation == BrepSolidOrientation.Inward)
            support.Flip();

        error = string.Empty;
        return support;
    }

    private static List<Curve> CreateSectionCurves(
        IReadOnlyList<GeometryBase> targets,
        Plane plane,
        double tolerance)
    {
        var sections = new List<Curve>();
        foreach (var target in targets)
        {
            if (target is null || !target.IsValid)
                continue;

            if (target is Mesh mesh)
            {
                var meshSections = Intersection.MeshPlane(mesh, plane);
                if (meshSections is not null)
                {
                    sections.AddRange(
                        meshSections
                            .Where(polyline => polyline.IsValid && polyline.Count >= 3)
                            .Select(polyline => new PolylineCurve(polyline)));
                }
                continue;
            }

            var brep = target switch
            {
                Brep targetBrep => targetBrep,
                Extrusion extrusion => extrusion.ToBrep(true),
                SubD subD => subD.ToBrep(),
                Surface surface => surface.ToBrep(),
                _ => Brep.TryConvertBrep(target)
            };
            if (brep is null || !brep.IsValid)
                continue;

            if (Intersection.BrepPlane(
                    brep,
                    plane,
                    tolerance,
                    true,
                    out var targetSections,
                    out _))
            {
                sections.AddRange(targetSections.Where(section => section.IsValid));
            }
        }

        if (sections.Count == 0)
            return [];

        var joined = Curve.JoinCurves(sections, tolerance, false);
        return joined
            .Where(curve =>
                curve.IsValid &&
                curve.GetLength() > tolerance * 4.0)
            .ToList();
    }

    private static bool TryCreateContactArcs(
        Curve outline,
        IReadOnlyList<Curve> sectionLoops,
        Plane plane,
        double tolerance,
        out List<Curve> contactArcs,
        out string error)
    {
        contactArcs = [];
        foreach (var section in sectionLoops)
        {
            var intersections = Intersection.CurveCurve(
                section,
                outline,
                tolerance,
                tolerance);
            if (intersections is null || intersections.Count == 0)
                continue;

            if (intersections.Any(intersection => intersection.IsOverlap))
            {
                error =
                    "支撐輪廓有一段與模型剖面完全重合。" +
                    "請把輪廓稍微延伸進模型，讓接觸線形成明確的兩個交點。";
                return false;
            }

            var splitParameters = intersections
                .Where(intersection => intersection.IsPoint)
                .Select(intersection => intersection.ParameterA)
                .Where(parameter =>
                    parameter > section.Domain.Min + RhinoMath.ZeroTolerance &&
                    parameter < section.Domain.Max - RhinoMath.ZeroTolerance)
                .OrderBy(parameter => parameter)
                .ToList();
            RemoveNearDuplicates(splitParameters, tolerance);
            if (splitParameters.Count < 2)
                continue;

            var pieces = section.Split(splitParameters);
            if (pieces is null)
                continue;

            foreach (var piece in pieces)
            {
                if (!piece.IsValid || piece.GetLength() <= tolerance * 4.0)
                    continue;

                var midpoint = piece.PointAtNormalizedLength(0.5);
                var containment = outline.Contains(midpoint, plane, tolerance);
                if (containment == PointContainment.Inside)
                    contactArcs.Add(piece);
            }
        }

        if (contactArcs.Count == 0)
        {
            error =
                "找不到支撐輪廓穿過目標模型所形成的接觸線。" +
                "請確認輪廓是閉合平面曲線，並讓模型側的邊穿入實體一小段。";
            return false;
        }

        if (contactArcs.Count > MaximumContactArcs)
        {
            error =
                $"偵測到 {contactArcs.Count} 段接觸線，造型過於複雜。" +
                "請簡化支撐輪廓或分開建立。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryCreateFlushRegion(
        Curve outline,
        IReadOnlyList<Curve> sectionLoops,
        double tolerance,
        out Curve supportRegion,
        out string warning,
        out string error)
    {
        var difference = Curve.CreateBooleanDifference(
            outline,
            sectionLoops,
            tolerance);
        if (difference is null || difference.Length == 0)
        {
            supportRegion = null!;
            warning = string.Empty;
            error =
                "模型剖面完全吃掉了手繪支撐造型，或無法進行平面裁切。" +
                "請把支撐輪廓向模型外側加大。";
            return false;
        }

        var largestRegion = LargestAreaCurve(difference, tolerance);
        if (largestRegion is null)
        {
            supportRegion = null!;
            warning = string.Empty;
            error = "模型裁切後沒有取得有效的支撐區域。";
            return false;
        }
        supportRegion = largestRegion;

        warning = difference.Length > 1
            ? $"模型把支撐輪廓分成 {difference.Length} 塊；目前只保留面積最大的一塊。"
            : string.Empty;
        error = string.Empty;
        return true;
    }

    private static bool TryCreateFlushRegionFromContactFace(
        Curve outline,
        IReadOnlyList<Curve> sectionCurves,
        Plane plane,
        double tolerance,
        out Curve supportRegion,
        out string warning,
        out string error)
    {
        var booleanInputs = new List<Curve> { outline };
        booleanInputs.AddRange(sectionCurves);
        using var regions = Curve.CreateBooleanRegions(
            booleanInputs,
            plane,
            false,
            tolerance);
        if (regions is null || regions.RegionCount == 0)
        {
            supportRegion = null!;
            warning = string.Empty;
            error =
                "接觸面的剖線無法分割支撐輪廓。" +
                "請讓封閉輪廓完整跨過接觸面剖線，而不是只靠近或相切。";
            return false;
        }

        var candidates = new List<Curve>();
        for (var regionIndex = 0; regionIndex < regions.RegionCount; regionIndex++)
        {
            var boundaries = regions.RegionCurves(regionIndex);
            if (boundaries is null || boundaries.Length == 0)
                continue;

            var outerBoundary = boundaries[0];
            if (!outerBoundary.IsValid || !outerBoundary.IsClosed)
                continue;

            using var properties = AreaMassProperties.Compute(
                outerBoundary,
                tolerance);
            if (properties is null ||
                outline.Contains(
                    properties.Centroid,
                    plane,
                    tolerance) == PointContainment.Outside)
            {
                continue;
            }

            candidates.Add(outerBoundary.DuplicateCurve());
        }

        var largestRegion = LargestAreaCurve(candidates, tolerance);
        if (largestRegion is null)
        {
            supportRegion = null!;
            warning = string.Empty;
            error =
                "接觸面剖線沒有在支撐輪廓內形成有效區域。" +
                "請讓輪廓穿過接觸面兩次，並讓大部分輪廓位於支撐側。";
            return false;
        }

        supportRegion = largestRegion;
        warning = candidates.Count > 2
            ? $"接觸面把支撐輪廓分成 {candidates.Count} 塊；目前保留面積最大的一塊。"
            : string.Empty;
        error = string.Empty;
        return true;
    }

    private static bool IsCurveOnBoundary(
        Curve curve,
        Curve boundary,
        double tolerance)
    {
        var distanceTolerance = Math.Max(tolerance * 10.0, RhinoMath.ZeroTolerance);
        foreach (var normalizedParameter in new[] { 0.2, 0.5, 0.8 })
        {
            var point = curve.PointAtNormalizedLength(normalizedParameter);
            if (!boundary.ClosestPoint(point, out var boundaryParameter))
                return false;
            if (boundary.PointAt(boundaryParameter).DistanceTo(point) > distanceTolerance)
                return false;
        }

        return true;
    }

    private static List<Curve> CreateClearanceGaps(
        Curve contactArc,
        double contactWidth,
        double contactSpacing,
        double tolerance,
        out int contactCount)
    {
        contactCount = 0;
        var length = contactArc.GetLength();
        if (length <= tolerance * 4.0)
            return [];

        var halfWidth = contactWidth / 2.0;
        var intervals = new List<(double Start, double End)>();
        AddContactInterval(intervals, 0.0, halfWidth, length);
        for (var center = contactSpacing; center < length; center += contactSpacing)
            AddContactInterval(intervals, center - halfWidth, center + halfWidth, length);
        AddContactInterval(intervals, length - halfWidth, length, length);

        intervals.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<(double Start, double End)>();
        foreach (var interval in intervals)
        {
            if (merged.Count == 0 || interval.Start > merged[^1].End + tolerance)
            {
                merged.Add(interval);
                continue;
            }

            var previous = merged[^1];
            merged[^1] = (previous.Start, Math.Max(previous.End, interval.End));
        }

        contactCount = merged.Count;
        var gaps = new List<Curve>();
        var cursor = 0.0;
        foreach (var interval in merged)
        {
            if (interval.Start - cursor > tolerance * 4.0)
            {
                var gap = TrimByLength(contactArc, cursor, interval.Start);
                if (gap is not null)
                    gaps.Add(gap);
            }
            cursor = Math.Max(cursor, interval.End);
        }

        if (length - cursor > tolerance * 4.0)
        {
            var gap = TrimByLength(contactArc, cursor, length);
            if (gap is not null)
                gaps.Add(gap);
        }

        return gaps;
    }

    private static void AddContactInterval(
        List<(double Start, double End)> intervals,
        double start,
        double end,
        double totalLength)
    {
        var clippedStart = Math.Clamp(start, 0.0, totalLength);
        var clippedEnd = Math.Clamp(end, 0.0, totalLength);
        if (clippedEnd > clippedStart)
            intervals.Add((clippedStart, clippedEnd));
    }

    private static Curve? TrimByLength(Curve curve, double start, double end)
    {
        if (!curve.LengthParameter(start, out var startParameter) ||
            !curve.LengthParameter(end, out var endParameter))
        {
            return null;
        }

        return curve.Trim(startParameter, endParameter);
    }

    private static bool TryCreateClearanceStrip(
        Curve gap,
        Curve supportRegion,
        Plane plane,
        double clearance,
        double tolerance,
        out Curve strip,
        out string error)
    {
        var innerGap = SelectOffset(
            gap,
            supportRegion,
            plane,
            clearance,
            tolerance,
            true);
        if (innerGap is null)
        {
            strip = null!;
            error =
                "無法判定接觸退讓應朝支撐造型的哪一側。" +
                "請加大支撐輪廓或減少接觸退讓。";
            return false;
        }

        var overlapDistance = Math.Max(tolerance * 2.0, clearance * 0.02);
        var outerGap = SelectOffset(
                gap,
                supportRegion,
                plane,
                overlapDistance,
                tolerance,
                false)
            ?? gap.DuplicateCurve();

        AlignDirection(gap, innerGap);
        AlignDirection(gap, outerGap);

        var innerReversed = innerGap.DuplicateCurve();
        innerReversed.Reverse();
        var pieces = new Curve[]
        {
            outerGap,
            new LineCurve(outerGap.PointAtEnd, innerGap.PointAtEnd),
            innerReversed,
            new LineCurve(innerGap.PointAtStart, outerGap.PointAtStart)
        };
        var joined = Curve.JoinCurves(pieces, tolerance, true);
        if (joined.Length != 1 || !joined[0].IsClosed)
        {
            strip = null!;
            error = "無法建立封閉的接觸退讓區域。";
            return false;
        }

        strip = joined[0];
        error = string.Empty;
        return true;
    }

    private static Curve? SelectOffset(
        Curve source,
        Curve supportRegion,
        Plane plane,
        double distance,
        double tolerance,
        bool wantInside)
    {
        foreach (var signedDistance in new[] { distance, -distance })
        {
            var candidates = source.Offset(
                plane,
                signedDistance,
                tolerance,
                CurveOffsetCornerStyle.Sharp);
            if (candidates is null)
                continue;

            foreach (var candidate in candidates)
            {
                if (!candidate.IsValid)
                    continue;

                var midpoint = candidate.PointAtNormalizedLength(0.5);
                var containment = supportRegion.Contains(midpoint, plane, tolerance);
                var isInside = containment == PointContainment.Inside;
                if (isInside == wantInside)
                    return candidate;
            }
        }

        return null;
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

    private static Curve? LargestAreaCurve(
        IEnumerable<Curve> curves,
        double tolerance)
    {
        Curve? largest = null;
        var largestArea = 0.0;
        foreach (var curve in curves)
        {
            if (!curve.IsValid || !curve.IsClosed)
                continue;

            using var properties = AreaMassProperties.Compute(curve, tolerance);
            if (properties is null)
                continue;

            var area = Math.Abs(properties.Area);
            if (area <= largestArea)
                continue;

            largestArea = area;
            largest = curve;
        }

        return largest;
    }

    private static void RemoveNearDuplicates(
        List<double> parameters,
        double tolerance)
    {
        for (var i = parameters.Count - 1; i > 0; i--)
        {
            if (Math.Abs(parameters[i] - parameters[i - 1]) <= tolerance)
                parameters.RemoveAt(i);
        }
    }
}
