using Rhino;
using Rhino.Geometry;

namespace PrintGear;

/// <summary>Adaptive cross sections anchored to trimmed reference faces.</summary>
internal static class SeamGrooveSurfaceFrames
{
    internal readonly record struct Frame(double Parameter, Point3d Point, Vector3d Tangent, Vector3d Inward);

    internal static Frame At(Curve rail, double parameter, IReadOnlyList<BrepFace> faces, double tolerance)
    {
        var point = rail.PointAt(parameter);
        var tangent = rail.TangentAt(parameter);
        if (!tangent.Unitize()) throw new ArgumentException("路徑含無效切線。");
        var candidates = new List<Vector3d>();
        foreach (var face in faces)
        {
            if (!face.ClosestPoint(point, out var u, out var v)
                || face.PointAt(u, v).DistanceTo(point) > tolerance * 2
                || face.IsPointOnFace(u, v, tolerance) == PointFaceRelation.Exterior) continue;
            var normal = face.NormalAt(u, v);
            // BrepFace.NormalAt already includes face orientation in RhinoCommon.
            // Applying OrientationIsReversed again would undo a user's Flip.
            if (!normal.Unitize()) continue;
            if (Math.Abs(normal * tangent) > Math.Sin(RhinoMath.ToRadians(5)))
                throw new ArgumentException("路徑切線未沿參考面，請先 Pull／Project 並檢查曲線。");
            normal -= tangent * (normal * tangent);
            normal.Unitize();
            candidates.Add(-normal);
        }
        if (candidates.Count == 0)
            throw new ArgumentException("路徑未貼在選取面的有效範圍內（允許偏差為文件容差兩倍）；請先 Pull／Project 或補選相鄰面。");
        if (candidates.Any(n => Vector3d.VectorAngle(n, candidates[0]) > RhinoMath.ToRadians(5)))
            throw new ArgumentException("參考面在路徑上有不同法線或銳角；請分段並各自選面。");
        return new Frame(parameter, point, tangent, candidates[0]);
    }

    internal static List<Curve> CreateProfiles(Curve rail, IReadOnlyList<BrepFace> faces,
        bool flip, SeamGrooveParameters p, double tolerance)
    {
        if (rail.GetNextDiscontinuity(Continuity.G1_continuous, rail.Domain.Min, rail.Domain.Max, out _))
            throw new ArgumentException("貼面模式需要切線連續的路徑；請將折角分段或圓角化，亦可改用手動方向模式。");
        var frames = new List<Frame>();
        var first = At(rail, rail.Domain.Min, faces, tolerance);
        frames.Add(first);
        void Refine(Frame a, Frame b, int level)
        {
            var middle = At(rail, (a.Parameter + b.Parameter) * 0.5, faces, tolerance);
            var angleLimit = RhinoMath.ToRadians(4);
            var middleNormal = a.Inward + b.Inward;
            var normalGood = middleNormal.Unitize()
                && Vector3d.VectorAngle(middleNormal, middle.Inward) <= RhinoMath.ToRadians(0.5);
            var turnGood = Vector3d.VectorAngle(a.Inward, b.Inward) <= angleLimit
                && Vector3d.VectorAngle(a.Tangent, b.Tangent) <= angleLimit;
            if (level >= 2 && normalGood && turnGood)
            {
                frames.Add(b);
                return;
            }
            if (level >= 12 || frames.Count >= 512)
                throw new ArgumentException("表面法線變化過急或路徑過於複雜；請分段處理。");
            Refine(a, middle, level + 1);
            Refine(middle, b, level + 1);
        }
        // Seed several intervals so endpoints alone cannot hide a full turn.
        for (var i = 1; i <= 8; i++)
        {
            var next = At(rail, rail.Domain.ParameterAt(i / 8.0), faces, tolerance);
            Refine(frames[^1], next, 0);
        }
        if (rail.IsClosed && (Vector3d.VectorAngle(first.Inward, frames[^1].Inward) > RhinoMath.ToRadians(0.5)
            || Vector3d.VectorAngle(first.Tangent, frames[^1].Tangent) > RhinoMath.ToRadians(0.5)))
            throw new ArgumentException("閉合接點的法線或切線不連續，無法建立貼面刀具。");
        // Closed sweep wraps the final distinct station to the first profile.
        // A duplicate profile at the periodic endpoint can make Sweep1 fail.
        if (rail.IsClosed) frames.RemoveAt(frames.Count - 1);
        var profiles = new List<Curve>();
        try
        {
            foreach (var frame in frames)
                profiles.Add(SeamGrooveBuilder.CreateProfile(frame.Point, frame.Tangent,
                    flip ? -frame.Inward : frame.Inward, p));
            return profiles;
        }
        catch { foreach (var c in profiles) c.Dispose(); throw; }
    }
}
