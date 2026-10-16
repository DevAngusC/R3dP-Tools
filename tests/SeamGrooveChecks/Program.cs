using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PrintGear;
using Rhino;
using Rhino.Geometry;
using Rhino.FileIO;
using Rhino.DocObjects;
using Rhino.Runtime.InProcess;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    [STAThread]
    private static int Main(string[] args)
    {
        var system = System.Environment.GetEnvironmentVariable("RHINO_SYSTEM_DIR") ?? @"C:\Program Files\Rhino 8\System";
        SetDllDirectory(system);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var path = Path.Combine(system, "netcore", name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        using var core = new RhinoCore(new[] { "/NOSPLASH" }, WindowStyle.NoWindow);
        const double tol = 0.001;
        var p = new SeamGrooveParameters(0.6, 0.3, 0.2, 0, 0);
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/seam-groove-checks");
        Directory.CreateDirectory(output);
        using var model = new File3dm();
        model.Settings.ModelUnitSystem = UnitSystem.Millimeters;
        model.Settings.ModelAbsoluteTolerance = tol;
        var results = new List<string>();
        var failures = 0;
        void Test(string name, Action action)
        {
            try { action(); results.Add("PASS " + name); }
            catch (Exception ex) { failures++; results.Add("FAIL " + name + ": " + ex.Message); }
            Console.WriteLine(results[^1]);
            File.WriteAllLines(Path.Combine(output, "checks.txt"), results);
        }
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
        double Volume(Brep b) { using var m = VolumeMassProperties.Compute(b); return m?.Volume ?? 0; }
        void Reject(Action action)
        {
            try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
            throw new Exception("Invalid input was accepted");
        }
        void Add(Brep b, string name, int layer, double y)
        {
            using var copy = b.DuplicateBrep();
            copy.Translate(new Vector3d(0, y, 0));
            model.Objects.AddBrep(copy, new ObjectAttributes { Name = name, LayerIndex = layer });
        }
        var cutLayer = 0;
        var resultLayer = 1;
        model.AllLayers.Add(new Layer { Name = "01_Cutters", Color = System.Drawing.Color.DarkOrange });
        model.AllLayers.Add(new Layer { Name = "02_BooleanResults", Color = System.Drawing.Color.SteelBlue });
        using var line = new LineCurve(new Point3d(0, 0, 0), new Point3d(10, 0, 0));
        var rails = new Dictionary<string, Curve>
        {
            ["straight"] = line.DuplicateCurve(),
            ["arc"] = new ArcCurve(new Arc(new Point3d(0, 0, 0), new Point3d(5, 3, 0), new Point3d(10, 0, 0))),
            ["polyline_corner"] = new PolylineCurve(new[] { new Point3d(0, 0, 0), new Point3d(5, 0, 0), new Point3d(5, 5, 0) }),
            ["closed_circle"] = new Circle(new Point3d(5, 0, 0), 3).ToNurbsCurve(),
            ["closed_rectangle"] = new Rectangle3d(Plane.WorldXY, new Interval(0, 8), new Interval(-2, 2)).ToNurbsCurve(),
            ["spatial_spline"] = Curve.CreateInterpolatedCurve(new[] { new Point3d(0, 0, 0), new Point3d(3, 1, 1), new Point3d(6, -1, 2), new Point3d(10, 1, 1) }, 3)
        };
        var mixed = new PolyCurve();
        mixed.Append(new LineCurve(new Point3d(0, 0, 0), new Point3d(5, 0, 0)));
        mixed.Append(new ArcCurve(new Arc(new Point3d(5, 0, 0), Vector3d.XAxis, new Point3d(10, 5, 0))));
        rails["line_arc_polycurve"] = mixed;
        int row = 0;
        foreach (var pair in rails)
        {
            var currentRow = row++;
            Test(pair.Key + " solid + boolean", () =>
            {
                using var cutter = SeamGrooveBuilder.Build(pair.Value, -Vector3d.ZAxis, p, tol);
                Assert(cutter.IsValid && cutter.IsSolid && cutter.IsManifold, "Not a valid solid");
                using var block = new BoundingBox(new Point3d(-1, -4, -2), new Point3d(12, 7, 0)).ToBrep();
                var difference = Brep.CreateBooleanDifference(block, cutter, tol);
                Assert(difference is { Length: 1 } && difference[0].IsSolid && difference[0].IsValid, "Difference failed");
                var removed = Volume(block) - Volume(difference![0]);
                Assert(removed > tol, "No material removed");
                if (pair.Key == "straight")
                {
                    Assert(Math.Abs(removed - 0.5 * p.Width * p.Depth * 10) < tol, "Groove dimensions wrong");
                    Assert(Math.Abs(Volume(cutter) - 0.5 * p.Width / p.Depth * Math.Pow(p.Depth + p.Overcut, 2) * 10) < tol, "Cutter volume wrong");
                }
                Add(cutter, pair.Key + "_Cutter", cutLayer, currentRow * 14);
                Add(difference[0], pair.Key + "_GroovedBlock", resultLayer, currentRow * 14);
                foreach (var b in difference) b.Dispose();
            });
        }
        Test("reversed rail preserves solid volume", () =>
        {
            using var reversed = mixed.DuplicateCurve(); reversed.Reverse();
            using var a = SeamGrooveBuilder.Build(mixed, -Vector3d.ZAxis, p, tol);
            using var b = SeamGrooveBuilder.Build(reversed, -Vector3d.ZAxis, p, tol);
            Assert(Math.Abs(Volume(a) - Volume(b)) < tol, "Direction-dependent dimensions");
        });
        Test("radial groove around cylinder", () =>
        {
            using var circle = new Circle(new Point3d(0, 0, 2), 3).ToNurbsCurve();
            using var cutter = SeamGrooveBuilder.Build(circle, -Vector3d.XAxis, p, tol);
            using var cylinder = new Cylinder(new Circle(Plane.WorldXY, 3), 4).ToBrep(true, true);
            var difference = Brep.CreateBooleanDifference(cylinder, cutter, tol);
            Assert(difference is { Length: 1 } && difference[0].IsSolid && difference[0].IsValid, "Cylinder difference failed");
            Assert(Volume(cylinder) - Volume(difference![0]) > tol, "Radial direction did not cut cylinder");
            foreach (var b in difference) b.Dispose();
        });
        Test("vertical line and flip", () =>
        {
            using var vertical = new LineCurve(Point3d.Origin, new Point3d(0, 0, 10));
            using var a = SeamGrooveBuilder.Build(vertical, Vector3d.XAxis, p, tol);
            using var b = SeamGrooveBuilder.Build(vertical, -Vector3d.XAxis, p, tol);
            Assert(Math.Abs(a.GetBoundingBox(true).Max.X - p.Depth) < tol, "Wrong inward direction");
            Assert(Math.Abs(b.GetBoundingBox(true).Min.X + p.Depth) < tol, "Flip failed");
        });
        Test("rotation and end extension", () =>
        {
            using var cutter = SeamGrooveBuilder.Build(line, -Vector3d.ZAxis, p with { RotationDegrees = 90, EndExtension = 1 }, tol);
            var box = cutter.GetBoundingBox(true);
            Assert(Math.Abs(box.Min.X + 1) < tol && Math.Abs(box.Max.X - 11) < tol, "End extension failed");
            Assert(Math.Abs(box.Max.Y - p.Depth) < tol, "Rotation failed");
            Assert(line.PointAtStart == Point3d.Origin && line.PointAtEnd == new Point3d(10, 0, 0), "Source curve was modified");
        });
        Test("unordered connected segments join", () =>
        {
            using var first = new LineCurve(new Point3d(5, 0, 0), Point3d.Origin);
            using var second = new LineCurve(new Point3d(10, 0, 0), new Point3d(5, 0, 0));
            using var joined = SeamGrooveBuilder.JoinRails(new[] { second, first }, tol);
            using var cutter = SeamGrooveBuilder.Build(joined, -Vector3d.ZAxis, p, tol);
            Assert(Math.Abs(joined.GetLength() - 10) < tol && cutter.IsSolid, "Join failed");
        });
        Test("disconnected segments rejected", () =>
        {
            using var other = new LineCurve(new Point3d(20, 0, 0), new Point3d(30, 0, 0));
            Reject(() => SeamGrooveBuilder.JoinRails(new[] { line, other }, tol).Dispose());
        });
        Test("self crossing rejected", () =>
        {
            using var crossing = new PolylineCurve(new[] { new Point3d(0, 0, 0), new Point3d(5, 5, 0), new Point3d(0, 5, 0), new Point3d(5, 0, 0) });
            Reject(() => SeamGrooveBuilder.Build(crossing, -Vector3d.ZAxis, p, tol).Dispose());
        });
        Test("invalid dimensions and parallel direction rejected", () =>
        {
            Reject(() => SeamGrooveBuilder.Build(line, -Vector3d.ZAxis, p with { Depth = 0 }, tol).Dispose());
            Reject(() => SeamGrooveBuilder.Build(line, -Vector3d.ZAxis, p with { Width = double.NaN }, tol).Dispose());
            Reject(() => SeamGrooveBuilder.Build(line, Vector3d.XAxis, p, tol).Dispose());
        });
        Test("tight loop cutter self intersection rejected", () =>
        {
            using var tight = new Circle(Plane.WorldXY, 0.15).ToNurbsCurve();
            Reject(() => SeamGrooveBuilder.Build(tight, -Vector3d.ZAxis, p, tol).Dispose());
        });
        Test("inch unit equivalence", () =>
        {
            const double scale = 1.0 / 25.4;
            using var inches = line.DuplicateCurve(); inches.Transform(Transform.Scale(Point3d.Origin, scale));
            using var cutter = SeamGrooveBuilder.Build(inches, -Vector3d.ZAxis,
                new(p.Width * scale, p.Depth * scale, p.Overcut * scale, 0, 0), tol * scale);
            Assert(Math.Abs(Volume(cutter) / Math.Pow(scale, 3) - 2.5) < tol, "Unit conversion failed");
        });
        Test("surface normal planar path + boolean", () =>
        {
            using var surface = new PlaneSurface(Plane.WorldXY, new Interval(-1, 12), new Interval(-4, 4));
            using var faceBrep = surface.ToBrep();
            using var cutter = SeamGrooveBuilder.Build(line, Vector3d.ZAxis, p, tol, faceBrep.Faces.ToArray());
            Assert(Math.Abs(cutter.GetBoundingBox(true).Min.Z + p.Depth) < tol, "Surface inward direction incorrect");
            using var block = new BoundingBox(new Point3d(-1, -4, -2), new Point3d(12, 4, 0)).ToBrep();
            var result = Brep.CreateBooleanDifference(block, cutter, tol);
            Assert(result is { Length: 1 } && result[0].IsSolid, "Surface mode boolean failed");
            foreach (var b in result!) b.Dispose();
        });
        Test("surface normal closed cylinder path + boolean", () =>
        {
            using var cylinder = new Cylinder(new Circle(Plane.WorldXY, 3), 4).ToBrep(true, true);
            using var path = new Circle(new Point3d(0, 0, 2), 3).ToNurbsCurve();
            using var cutter = SeamGrooveBuilder.Build(path, Vector3d.ZAxis, p, tol, cylinder.Faces.ToArray());
            for (int i = 0; i < 25; i++)
            {
                double a = i * 2 * Math.PI / 25;
                var tip = new Point3d((3 - p.Depth) * Math.Cos(a), (3 - p.Depth) * Math.Sin(a), 2);
                Assert(cutter.ClosestPoint(tip).DistanceTo(tip) < tol * 5, "Cylinder tip does not follow inward normal");
            }
            var result = Brep.CreateBooleanDifference(cylinder, cutter, tol);
            Assert(result is { Length: 1 } && result[0].IsSolid, "Surface cylinder boolean failed");
            foreach (var b in result!) b.Dispose();
        });
        Test("twisting surface normals follow straight rail", () =>
        {
            using var surface = NurbsSurface.CreateFromCorners(new Point3d(0, -4, 0), new Point3d(12, -4, 0),
                new Point3d(12, 4, 5), new Point3d(0, 4, -5));
            using var reference = surface.ToBrep();
            using var full = surface.IsoCurve(0, surface.Domain(1).Mid);
            using var path = full.Trim(full.Domain.ParameterAt(0.1), full.Domain.ParameterAt(0.9));
            var faces = reference.Faces.ToArray();
            using var cutter = SeamGrooveBuilder.Build(path, Vector3d.ZAxis, p, tol, faces);
            using var flipped = SeamGrooveBuilder.Build(path, Vector3d.ZAxis, p, tol, faces, true);
            for (int i = 0; i <= 23; i++)
            {
                var frame = SeamGrooveSurfaceFrames.At(path, path.Domain.ParameterAt(i / 23.0), faces, tol);
                var expected = frame.Point + frame.Inward * p.Depth;
                var expectedFlip = frame.Point - frame.Inward * p.Depth;
                Assert(cutter.ClosestPoint(expected).DistanceTo(expected) < tol * 5, "Twisted surface tip deviation");
                Assert(flipped.ClosestPoint(expectedFlip).DistanceTo(expectedFlip) < tol * 5, "Surface normal flip failed");
            }
            Add(cutter, "SurfaceNormal_Twisting_Cutter", cutLayer, row * 14);
            Add(reference, "SurfaceNormal_Twisting_Reference_OpenSurface", resultLayer, row * 14);
        });
        Test("off face path rejected", () =>
        {
            using var plane = new PlaneSurface(Plane.WorldXY, new Interval(-1, 12), new Interval(-4, 4));
            using var reference = plane.ToBrep();
            using var off = line.DuplicateCurve(); off.Translate(new Vector3d(0, 0, 0.1));
            Reject(() => SeamGrooveBuilder.Build(off, Vector3d.ZAxis, p, tol, reference.Faces.ToArray()).Dispose());
        });
        Test("surface mode smooth PolyCurve and adjacent faces", () =>
        {
            using var left = new PlaneSurface(Plane.WorldXY, new Interval(-1, 5), new Interval(-4, 7));
            using var right = new PlaneSurface(Plane.WorldXY, new Interval(5, 12), new Interval(-4, 7));
            using var a = left.ToBrep(); using var b = right.ToBrep();
            using var cutter = SeamGrooveBuilder.Build(mixed, Vector3d.ZAxis, p, tol, a.Faces.Concat(b.Faces).ToArray());
            Assert(cutter.IsValid && cutter.IsSolid, "Smooth PolyCurve across adjacent faces failed");
        });
        Test("reversed face orientation respected", () =>
        {
            using var plane = new PlaneSurface(Plane.WorldXY, new Interval(-1, 12), new Interval(-4, 4));
            using var reference = plane.ToBrep(); reference.Flip();
            using var cutter = SeamGrooveBuilder.Build(line, Vector3d.ZAxis, p, tol, reference.Faces.ToArray());
            Assert(Math.Abs(cutter.GetBoundingBox(true).Max.Z - p.Depth) < tol, "Reversed face normal was ignored");
        });
        Test("trimmed face hole rejected", () =>
        {
            using var outer = new Rectangle3d(Plane.WorldXY, new Interval(-1, 12), new Interval(-4, 4)).ToNurbsCurve();
            using var hole = new Circle(new Point3d(5, 0, 0), 1).ToNurbsCurve();
            var refs = Brep.CreatePlanarBreps(new[] { outer, hole }, tol);
            try { Reject(() => SeamGrooveBuilder.Build(line, Vector3d.ZAxis, p, tol, refs[0].Faces.ToArray()).Dispose()); }
            finally { foreach (var b in refs) b.Dispose(); }
        });
        Test("ambiguous sharp reference faces rejected", () =>
        {
            using var xy = new PlaneSurface(Plane.WorldXY, new Interval(-1, 12), new Interval(0, 4));
            using var xz = new PlaneSurface(Plane.WorldZX, new Interval(-4, 0), new Interval(-1, 12));
            using var a = xy.ToBrep(); using var b = xz.ToBrep();
            Reject(() => SeamGrooveBuilder.Build(line, Vector3d.ZAxis, p, tol, a.Faces.Concat(b.Faces).ToArray()).Dispose());
        });
        Test("write review model", () => Assert(model.Write(Path.Combine(output, "SeamGroove-Examples.3dm"), 8), "3dm write failed"));
        Test("read review model and validate saved solids", () =>
        {
            using var saved = File3dm.Read(Path.Combine(output, "SeamGroove-Examples.3dm"));
            Assert(saved is not null && saved.Objects.Count == rails.Count * 2 + 2, "Saved objects missing");
            Assert(saved!.Objects.All(o => o.Geometry is Brep b && b.IsValid
                && (b.IsSolid || o.Attributes.Name.EndsWith("OpenSurface"))), "Invalid saved geometry");
        });
        foreach (var rail in rails.Values) rail.Dispose();
        results.Add($"TOTAL {results.Count} checks; {failures} failures");
        File.WriteAllLines(Path.Combine(output, "checks.txt"), results);
        Console.WriteLine(results[^1]);
        return failures == 0 ? 0 : 1;
    }
}
