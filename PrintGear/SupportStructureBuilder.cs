using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace PrintGear;

/// <summary>
/// Builds a closed, planar light-touch support fin from an open guide curve.
/// The body is backed away from the guide while narrow teeth still reach it.
/// </summary>
internal static class SupportStructureBuilder
{
    private const int MinimumSegments = 4;
    private const int MaximumSegments = 6_000;

    public static Brep? Create(
        Curve guide,
        SupportParameters parameters,
        double tolerance,
        out int contactCount,
        out string error)
    {
        contactCount = 0;
        if (!parameters.IsValid(tolerance, out error))
            return null;

        if (!guide.IsValid)
        {
            error = "導引曲線無效。";
            return null;
        }

        if (guide.IsClosed)
        {
            error = "目前版本需要開放曲線；請先修剪封閉剖面，只保留要支撐的區段。";
            return null;
        }

        var length = guide.GetLength();
        if (!RhinoMath.IsValidDouble(length) || length <= tolerance * 4.0)
        {
            error = "導引曲線太短。";
            return null;
        }

        var segmentCount = Math.Clamp(
            (int)Math.Ceiling(length / parameters.SamplingStep(tolerance)),
            MinimumSegments,
            MaximumSegments);
        var curveParameters = guide.DivideByCount(segmentCount, true);
        if (curveParameters is null || curveParameters.Length < 3)
        {
            error = "無法沿導引曲線建立足夠的取樣點。";
            return null;
        }

        var samples = curveParameters.Select(guide.PointAt).ToArray();
        if (!TryGetSupportFrame(guide, samples, tolerance, parameters.BaseHeight, out var frame, out error))
            return null;

        var localGuide = new Point2d[samples.Length];
        var planarityTolerance = Math.Max(tolerance * 10.0, parameters.Thickness * 0.02);
        var minimumWorldZ = double.PositiveInfinity;
        for (var i = 0; i < samples.Length; i++)
        {
            var delta = samples[i] - frame.Origin;
            var deviation = Math.Abs(Vector3d.Multiply(delta, frame.Normal));
            if (deviation > planarityTolerance)
            {
                error =
                    $"導引曲線不是垂直共平面曲線（偏差 {deviation:0.###}）。" +
                    "請使用線段或由垂直剖面取得的曲線。";
                return null;
            }

            localGuide[i] = new Point2d(
                Vector3d.Multiply(delta, frame.Horizontal),
                Vector3d.Multiply(delta, Vector3d.ZAxis));
            minimumWorldZ = Math.Min(minimumWorldZ, samples[i].Z);
        }

        if (minimumWorldZ < parameters.BaseHeight - tolerance)
        {
            error =
                $"導引曲線最低 World Z={minimumWorldZ:0.###}，" +
                $"低於平台 Z={parameters.BaseHeight:0.###}。" +
                "若要使用手繪三角支撐造型，請改用建立模式=自訂輪廓。";
            return null;
        }

        if (!IsHorizontallyMonotonic(localGuide, tolerance))
        {
            error =
                "導引曲線沿列印平台方向反折，會形成自交支撐。" +
                "請將曲線在反折處分段後再執行。";
            return null;
        }

        var cumulativeLengths = CumulativeLengths(samples);
        var topProfile = new Point2d[localGuide.Length];
        var wasContact = false;
        for (var i = 0; i < localGuide.Length; i++)
        {
            var isContact = IsContactSample(
                cumulativeLengths[i],
                cumulativeLengths[^1],
                parameters.ContactWidth,
                parameters.ContactSpacing,
                tolerance);

            if (isContact && !wasContact)
                contactCount++;
            wasContact = isContact;

            if (isContact || parameters.ContactClearance <= tolerance)
            {
                topProfile[i] = localGuide[i];
                continue;
            }

            var tangent = LocalTangent(localGuide, i);
            if (!tangent.Unitize())
            {
                error = "導引曲線含有無法判定方向的重複取樣點。";
                return null;
            }

            // Rotate the tangent in the support plane and choose the direction
            // that points toward the build plate.
            var setbackDirection = new Vector2d(tangent.Y, -tangent.X);
            if (setbackDirection.Y > 0.0)
                setbackDirection *= -1.0;
            if (!setbackDirection.Unitize())
            {
                error = "無法計算接觸退讓方向。";
                return null;
            }

            var backedOff = localGuide[i] + setbackDirection * parameters.ContactClearance;
            if (backedOff.Y < 0.0)
                backedOff.Y = 0.0;
            topProfile[i] = backedOff;
        }

        var profile = CreateClosedProfile(topProfile, frame, parameters.FootExtension, tolerance);
        if (profile is null || !profile.IsValid || !profile.IsClosed)
        {
            error = "無法由導引曲線建立封閉的支撐輪廓。";
            return null;
        }

        var selfIntersections = Intersection.CurveSelf(profile, tolerance);
        if (selfIntersections is not null && selfIntersections.Count > 0)
        {
            error =
                "退讓後的支撐輪廓發生自交。" +
                "請減少接觸退讓、底腳延伸，或將導引曲線分段。";
            return null;
        }

        var extrusionPlane = new Plane(
            frame.Origin - frame.Normal * (parameters.Thickness / 2.0),
            frame.Horizontal,
            Vector3d.ZAxis);
        var extrusion = Extrusion.Create(profile, extrusionPlane, parameters.Thickness, true);
        if (extrusion is null)
        {
            error = "支撐輪廓自交或退讓量過大，無法建立實體。";
            return null;
        }

        var brep = extrusion.ToBrep(true);
        if (brep is null || !brep.IsValid || !brep.IsSolid)
        {
            error = "無法建立封閉且可列印的支撐實體。";
            return null;
        }

        if (brep.SolidOrientation == BrepSolidOrientation.Inward)
            brep.Flip();

        error = string.Empty;
        return brep;
    }

    private static bool TryGetSupportFrame(
        Curve guide,
        IReadOnlyList<Point3d> samples,
        double tolerance,
        double baseHeight,
        out SupportFrame frame,
        out string error)
    {
        var horizontal = Vector3d.Unset;
        if (guide.TryGetPlane(out var guidePlane, tolerance))
        {
            var projectedNormal = guidePlane.Normal -
                Vector3d.ZAxis * Vector3d.Multiply(guidePlane.Normal, Vector3d.ZAxis);
            if (projectedNormal.Unitize())
            {
                horizontal = Vector3d.CrossProduct(Vector3d.ZAxis, projectedNormal);
                horizontal.Unitize();
            }
        }

        if (!horizontal.IsValid || horizontal.IsTiny(tolerance))
        {
            var bestLength = 0.0;
            for (var i = 1; i < samples.Count; i++)
            {
                var candidate = samples[i] - samples[i - 1];
                candidate -= Vector3d.ZAxis * Vector3d.Multiply(candidate, Vector3d.ZAxis);
                if (candidate.SquareLength <= bestLength)
                    continue;

                bestLength = candidate.SquareLength;
                horizontal = candidate;
            }

            if (!horizontal.Unitize())
                horizontal = Vector3d.XAxis;
        }

        var normal = Vector3d.CrossProduct(horizontal, Vector3d.ZAxis);
        if (!normal.Unitize())
        {
            frame = default;
            error = "無法建立垂直支撐平面。";
            return false;
        }

        var origin = new Point3d(samples[0].X, samples[0].Y, baseHeight);
        frame = new SupportFrame(origin, horizontal, normal);
        error = string.Empty;
        return true;
    }

    private static bool IsHorizontallyMonotonic(IReadOnlyList<Point2d> points, double tolerance)
    {
        var direction = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var delta = points[i].X - points[i - 1].X;
            if (Math.Abs(delta) <= tolerance)
                continue;

            var current = Math.Sign(delta);
            if (direction == 0)
                direction = current;
            else if (current != direction)
                return false;
        }

        return true;
    }

    private static double[] CumulativeLengths(IReadOnlyList<Point3d> points)
    {
        var lengths = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
            lengths[i] = lengths[i - 1] + points[i].DistanceTo(points[i - 1]);
        return lengths;
    }

    private static bool IsContactSample(
        double distance,
        double totalLength,
        double width,
        double spacing,
        double tolerance)
    {
        var halfWidth = width / 2.0 + tolerance;
        if (distance <= halfWidth || totalLength - distance <= halfWidth)
            return true;

        var nearestIndex = Math.Round(distance / spacing);
        return Math.Abs(distance - nearestIndex * spacing) <= halfWidth;
    }

    private static Vector2d LocalTangent(IReadOnlyList<Point2d> points, int index)
    {
        if (index == 0)
            return points[1] - points[0];
        if (index == points.Count - 1)
            return points[^1] - points[^2];
        return points[index + 1] - points[index - 1];
    }

    private static PolylineCurve? CreateClosedProfile(
        IReadOnlyList<Point2d> topProfile,
        SupportFrame frame,
        double footExtension,
        double tolerance)
    {
        var horizontalDirection = Math.Sign(topProfile[^1].X - topProfile[0].X);
        if (horizontalDirection == 0)
            horizontalDirection = 1;

        var polyline = new Polyline(topProfile.Count + 3);
        foreach (var point in topProfile)
            AddIfDistinct(polyline, frame.ToWorld(point), tolerance);

        var endFoot = new Point2d(
            topProfile[^1].X + horizontalDirection * footExtension,
            0.0);
        var startFoot = new Point2d(
            topProfile[0].X - horizontalDirection * footExtension,
            0.0);
        AddIfDistinct(polyline, frame.ToWorld(endFoot), tolerance);
        AddIfDistinct(polyline, frame.ToWorld(startFoot), tolerance);

        if (polyline.Count < 3)
            return null;

        AddIfDistinct(polyline, polyline[0], tolerance);
        if (!polyline.IsClosed)
            polyline.Add(polyline[0]);
        return new PolylineCurve(polyline);
    }

    private static void AddIfDistinct(Polyline polyline, Point3d point, double tolerance)
    {
        if (polyline.Count == 0 || polyline[^1].DistanceTo(point) > tolerance)
            polyline.Add(point);
    }

    private readonly record struct SupportFrame(
        Point3d Origin,
        Vector3d Horizontal,
        Vector3d Normal)
    {
        public Point3d ToWorld(Point2d point) =>
            Origin + Horizontal * point.X + Vector3d.ZAxis * point.Y;
    }
}
