using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace PrintGear;

internal readonly record struct SeamGrooveParameters(
    double Width, double Depth, double Overcut, double EndExtension, double RotationDegrees);

internal static class SeamGrooveBuilder
{
    // The rail is the mouth centre. Positive depth points into the material.
    // Continue the two flanks above the mouth so the cutter crosses the surface.
    internal static PolylineCurve CreateProfile(Point3d origin, Vector3d tangent,
        Vector3d inward, SeamGrooveParameters p)
    {
        tangent.Unitize();
        inward -= tangent * (inward * tangent);
        if (!inward.Unitize())
            throw new ArgumentException("尖端方向不可平行於路徑起點切線。");
        inward.Transform(Transform.Rotation(RhinoMath.ToRadians(p.RotationDegrees), tangent, Point3d.Origin));
        var across = Vector3d.CrossProduct(tangent, inward);
        across.Unitize();
        var tip = origin + inward * p.Depth;
        var back = origin - inward * p.Overcut;
        var halfBase = p.Width * (1.0 + p.Overcut / p.Depth) / 2.0;
        return new PolylineCurve(new[] { tip, back + across * halfBase, back - across * halfBase, tip });
    }

    internal static Curve JoinRails(IEnumerable<Curve> inputs, double tolerance)
    {
        var copies = inputs.Select(c => c.DuplicateCurve()).ToArray();
        try
        {
            if (copies.Length == 0 || copies.Any(c => !c.IsValid || c.GetLength() <= tolerance * 10))
                throw new ArgumentException("請選取有效且長於文件容差十倍的曲線。");
            var joined = Curve.JoinCurves(copies, tolerance);
            if (joined is null || joined.Length != 1)
            {
                if (joined is not null) foreach (var c in joined) c.Dispose();
                throw new ArgumentException("選取的線段必須能接成一條連續路徑；請檢查斷點、分岔或重複線。");
            }
            return joined[0];
        }
        finally { foreach (var c in copies) c.Dispose(); }
    }

    internal static Brep Build(Curve source, Vector3d inward, SeamGrooveParameters p,
        double tolerance, IReadOnlyList<BrepFace>? referenceFaces = null, bool flipNormals = false)
    {
        if (!RhinoMath.IsValidDouble(tolerance) || tolerance <= 0)
            throw new ArgumentException("文件容差無效。");
        if (new[] { p.Width, p.Depth, p.Overcut }.Any(v => !RhinoMath.IsValidDouble(v) || v <= tolerance * 4)
            || !RhinoMath.IsValidDouble(p.EndExtension) || p.EndExtension < 0
            || !RhinoMath.IsValidDouble(p.RotationDegrees))
            throw new ArgumentException("槽寬、槽深、外側延伸須大於文件容差四倍；端部延長不可為負值。");
        if (!source.IsValid || source.GetLength() <= tolerance * 10 || !inward.IsValid)
            throw new ArgumentException("路徑或尖端方向無效。");
        var onSurface = referenceFaces is { Count: > 0 };
        if (onSurface && (p.EndExtension != 0 || p.RotationDegrees != 0))
            throw new ArgumentException("貼面模式以面法線定向，不提供截面旋轉或離面端部延長。");

        using var rail = !source.IsClosed && p.EndExtension > 0
            ? source.Extend(CurveEnd.Both, p.EndExtension, CurveExtensionStyle.Line)
                ?? throw new InvalidOperationException("無法延長路徑端部。")
            : source.DuplicateCurve();
        var intersections = Intersection.CurveSelf(rail, tolerance);
        if (intersections is not null && intersections.Count > 0)
            throw new ArgumentException("路徑不可自交或重疊，請先修整曲線。");

        var profiles = onSurface
            ? SeamGrooveSurfaceFrames.CreateProfiles(rail, referenceFaces!, flipNormals, p, tolerance)
            : new List<Curve> { CreateProfile(rail.PointAtStart, rail.TangentAtStart, inward, p) };
        // Planar rails keep a stable plane normal; spatial rails use freeform transport.
        var planar = rail.TryGetPlane(out var plane, tolerance) && !rail.IsLinear(tolerance);
        Brep[] pieces;
        try
        {
            pieces = Brep.CreateFromSweep(rail, profiles, Point3d.Unset, Point3d.Unset,
                planar && !onSurface ? SweepFrame.Roadlike : SweepFrame.Freeform,
                planar && !onSurface ? plane.Normal : Vector3d.Unset, rail.IsClosed,
                SweepBlend.Local, SweepMiter.Trimmed, tolerance, SweepRebuild.None, 0, 0);
        }
        finally { foreach (var c in profiles) c.Dispose(); }
        if (pieces is null || pieces.Length == 0)
            throw new InvalidOperationException("掃掠失敗；請縮小截面或修整過急的轉角。");
        try
        {
            var joined = Brep.JoinBreps(pieces, tolerance);
            if (joined is null || joined.Length != 1)
            {
                if (joined is not null) foreach (var b in joined) b.Dispose();
                throw new InvalidOperationException("掃掠未接成單一多重曲面，沒有輸出零碎物件。");
            }
            using var shell = joined[0];
            var solid = shell.IsSolid ? shell.DuplicateBrep() : shell.CapPlanarHoles(tolerance);
            if (solid is null || !solid.IsValid || !solid.IsSolid || !solid.IsManifold)
            {
                solid?.Dispose();
                throw new InvalidOperationException("無法形成有效封閉實體；請檢查折角、閉合接點或截面尺寸。");
            }
            if (solid.SolidOrientation == BrepSolidOrientation.Inward) solid.Flip();
            using var mass = VolumeMassProperties.Compute(solid);
            if (mass is null || !RhinoMath.IsValidDouble(mass.Volume) || mass.Volume <= Math.Pow(tolerance, 3))
            {
                solid.Dispose();
                throw new InvalidOperationException("實體體積無效。");
            }
            try { CheckSelfIntersections(solid, tolerance); }
            catch { solid.Dispose(); throw; }
            return solid;
        }
        finally { foreach (var b in pieces) b.Dispose(); }
    }

    private static void CheckSelfIntersections(Brep solid, double tolerance)
    {
        // Brep.IsValid checks topology, not global self-intersections. Add a
        // tolerance-controlled mesh check to catch cutters folding into themselves.
        using var settings = new MeshingParameters
        {
            Tolerance = tolerance * 0.5, MinimumEdgeLength = tolerance * 0.1,
            RefineGrid = true, JaggedSeams = false
        };
        var parts = Mesh.CreateFromBrep(solid, settings);
        if (parts is null || parts.Length == 0)
            throw new InvalidOperationException("無法建立實體自交檢查網格。");
        using var mesh = new Mesh();
        foreach (var part in parts) { mesh.Append(part); part.Dispose(); }
        mesh.Vertices.CombineIdentical(true, true);
        mesh.Faces.CullDegenerateFaces();
        mesh.Weld(Math.PI);
        var ok = mesh.GetSelfIntersections(Math.Min(tolerance * 0.01, 1e-7),
            out var crossings, true, out var overlaps, false, out var overlapMesh,
            null, CancellationToken.None, null);
        overlapMesh?.Dispose();
        if (!ok || (crossings?.Any(c => c.Length > tolerance) ?? false)
            || (overlaps?.Any(c => c.Length > tolerance) ?? false))
            throw new InvalidOperationException("刀具可能自交或重疊；請縮小截面、增加彎曲半徑或分開製作。");
    }
}
