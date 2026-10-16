using Rhino.Geometry;

namespace PrintGear;

internal static class GridfinityBuilder
{
    internal const double Tolerance = 0.001;

    // A paired baseplate and connected multi-cell male adapter, both resting on Z=0.
    public static (Brep Female, Brep Male) Build(GridfinityParameters p, Action? checkCancel = null)
    {
        p.Validate();
        var owned = new List<Brep>();
        Brep Own(Brep b) { owned.Add(b); return b; }
        try
        {
            var female = Own(Block(p.Width, p.Length, 0, p.FloorHeight + GridfinityParameters.SocketHeight));
            var male = Own(RoundedSolid(p.Width - .5, p.Length - .5, 3.75,
                GridfinityParameters.FootHeight, GridfinityParameters.FootHeight + GridfinityParameters.DeckThickness));
            male.Translate(.25, .25, 0);
            for (var x = 0; x < p.Columns; x++)
            for (var y = 0; y < p.Rows; y++)
            {
                checkCancel?.Invoke();
                var cx = 21 + x * 42;
                var cy = 21 + y * 42;
                // Keep the standard male geometry fixed. Clearance changes only the socket.
                var foot = Own(Profile(cx, cy, 0,
                    [(0, 35.6, .8), (.8, 37.2, 1.6), (2.6, 37.2, 1.6), (4.75, 41.5, 3.75)]));
                male = Own(Single(Brep.CreateBooleanUnion(new[] { male, foot }, Tolerance), "公座聯集"));
                var d = p.Clearance - .25;
                // Extend below the base only for through-open lightweight sockets.
                var levels = new List<(double Z, double Width, double Radius)> {
                    (0, 36.3 + 2*d, 1.15+d), (.7, 37.7+2*d, 1.85+d),
                    (2.5, 37.7+2*d, 1.85+d), (4.65, 42+2*d, 4+d), (4.85, 42+2*d, 4+d) };
                // Continue the 45-degree lower chamfer through the underside, avoiding
                // an extra coincident loft seam at the cutting plane.
                if (!p.HasFloor) levels[0] = (-.1, 36.1+2*d, 1.05+d);
                var socket = Own(Profile(cx, cy, p.FloorHeight, levels.ToArray()));
                female = Own(Single(Brep.CreateBooleanDifference(female, socket, Tolerance), "母座凹槽"));
                var femaleHoles = new List<Brep>();
                var maleHoles = new List<Brep>();
                foreach (var dx in new[] { -13.0, 13.0 })
                foreach (var dy in new[] { -13.0, 13.0 })
                {
                    if (p.HasFloor) femaleHoles.Add(Own(Hole(cx+dx, cy+dy, p.FloorHeight-p.HoleDepth,
                        p.FloorHeight+.1, p.HoleDiameter)));
                    maleHoles.Add(Own(Hole(cx+dx, cy+dy, -.1, p.HoleDepth, p.HoleDiameter)));
                }
                if (p.HasFloor) female = Own(Single(Brep.CreateBooleanDifference(new[] { female }, femaleHoles, Tolerance), "母座磁鐵孔"));
                male = Own(Single(Brep.CreateBooleanDifference(new[] { male }, maleHoles, Tolerance), "公座磁鐵孔"));
            }
            owned.Remove(female);
            owned.Remove(male);
            return (female, male);
        }
        finally { foreach (var b in owned) b.Dispose(); }
    }

    private static Brep Single(Brep[]? parts, string stage)
    {
        if (parts is { Length: 1 } && parts[0] is not null && parts[0].IsValid && parts[0].IsSolid)
        {
            if (parts[0].SolidOrientation == BrepSolidOrientation.Inward) parts[0].Flip();
            return parts[0];
        }
        if (parts != null) foreach (var b in parts) b?.Dispose();
        throw new InvalidOperationException($"{stage}未產生單一有效封閉實體，未新增任何物件。");
    }

    private static Brep Block(double w, double h, double z0, double z1) =>
        new BoundingBox(new Point3d(0, 0, z0), new Point3d(w, h, z1)).ToBrep();

    private static Curve Rounded(double cx, double cy, double z, double w, double h, double r)
    {
        using var rectangle = new Rectangle3d(new Plane(new Point3d(cx,cy,z), Vector3d.ZAxis),
            new Interval(-w/2,w/2),new Interval(-h/2,h/2)).ToNurbsCurve();
        return Curve.CreateFilletCornersCurve(rectangle, r, Tolerance, .001)
            ?? throw new InvalidOperationException("無法建立圓角輪廓。");
    }

    private static Brep RoundedSolid(double w, double h, double r, double z0, double z1)
    {
        using var curve = Rounded(w/2,h/2,z0,w,h,r);
        using var extrusion = Extrusion.Create(curve, z1-z0, true);
        return extrusion.ToBrep();
    }

    private static Brep Profile(double cx, double cy, double z0, (double Z,double Width,double Radius)[] levels)
    {
        var curves = levels.Select(l => Rounded(cx,cy,z0+l.Z,l.Width,l.Width,l.Radius)).ToArray();
        try
        {
            var lofts = Brep.CreateFromLoft(curves, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
            if (lofts == null || lofts.Length != 1)
            {
                if (lofts != null) foreach (var b in lofts) b.Dispose();
                throw new InvalidOperationException("無法建立 Gridfinity 剖面。");
            }
            using var loft = lofts[0];
            return Single(new[] { loft.CapPlanarHoles(Tolerance) }, "剖面封口");
        }
        finally { foreach (var c in curves) c.Dispose(); }
    }

    private static Brep Hole(double x,double y,double z0,double z1,double diameter) =>
        new Cylinder(new Circle(new Plane(new Point3d(x,y,z0),Vector3d.ZAxis),diameter/2),z1-z0).ToBrep(true,true);
}
