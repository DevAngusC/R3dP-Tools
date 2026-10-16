using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PrintGear;
using Rhino;
using Rhino.Geometry;
using Rhino.FileIO;
using Rhino.Runtime.InProcess;

internal static class Program
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [STAThread] private static int Main()
    {
        var system = Environment.GetEnvironmentVariable("RHINO_SYSTEM_DIR") ?? @"C:\Program Files\Rhino 8\System";
        SetDllDirectory(system);
        AppDomain.CurrentDomain.AssemblyResolve += (_,e) => {
            var file = Path.Combine(system,"netcore",new AssemblyName(e.Name).Name+".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        try { return Run(); } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static int Run()
    {
        using var core = new RhinoCore(new[] { "/NOSPLASH" },WindowStyle.NoWindow);
        var failures = 0;
        var results = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        foreach (var height in new[] { .2, .3 })
        foreach (var nozzle in new[] { .2, .4, .6, .8 })
        foreach (var curved in new[] { false, true })
        foreach (var reverse in new[] { false, true })
        foreach (var count in new[] { 2, 5, 9 })
        {
            var name = $"nozzle={nozzle} height={height} curved={curved} reverse={reverse} count={count}";
            try
            {
                using Curve edge = curved
                    ? new ArcCurve(new Arc(new Point3d(20,0,0),new Point3d(24,0,10),new Point3d(20,0,20)))
                    : new LineCurve(new Point3d(20,0,0),new Point3d(30,0,20));
                using var outline = new PolyCurve();
                outline.Append(edge.DuplicateCurve());
                outline.Append(new LineCurve(edge.PointAtEnd,new Point3d(0,0,20)));
                outline.Append(new LineCurve(new Point3d(0,0,20),new Point3d(0,0,0)));
                outline.Append(new LineCurve(new Point3d(0,0,0),edge.PointAtStart));
                if (reverse) edge.Reverse();
                using var solid = ContinuousChamferSupportBuilder.Create(outline,edge,
                    new SupportParameters(2,.4,height),nozzle,count,.001,
                    out var actual,out var report,out var error);
                Check(solid is not null && solid.IsValid && solid.IsSolid,error);
                Check(actual==count && report.SkippedCount==0,"Wrong count");
                // Independently recover tooth bands by intersecting a probe line outside the body.
                // At y=0, a point 0.125 mm beyond the original boundary must be inside each tooth.
                using var ascending = edge.DuplicateCurve();
                if (ascending.PointAtStart.Z > ascending.PointAtEnd.Z) ascending.Reverse();
                var start = Rhino.Geometry.Intersect.Intersection.CurvePlane(ascending,
                    new Plane(new Point3d(0,0,height/2+.004),Vector3d.ZAxis),.001)[0].ParameterA;
                var end = Rhino.Geometry.Intersect.Intersection.CurvePlane(ascending,
                    new Plane(new Point3d(0,0,20-height/2-.004),Vector3d.ZAxis),.001)[0].ParameterA;
                var s0=ascending.GetLength(new Interval(ascending.Domain.Min,start));
                var s1=ascending.GetLength(new Interval(ascending.Domain.Min,end));
                for(var i=0;i<count;i++) {
                    var length=s0+(s1-s0)*i/(count-1);
                    Check(ascending.LengthParameter(length,out var t),"Length lookup");
                    var pt=ascending.PointAt(t)+new Vector3d(.125,0,0);
                    Check(solid!.IsPointInside(pt,.001,true),$"Missing tooth {i}");
                    Check(solid.IsPointInside(pt+new Vector3d(0,nozzle/2-.01,0),.001,true),"Tooth too narrow");
                    Check(!solid.IsPointInside(pt+new Vector3d(0,nozzle/2+.01,0),.001,true),"Tooth too wide");
                    if(i<count-1) {
                        ascending.LengthParameter(length+(s1-s0)/(count-1)/2,out t);
                        Check(!solid.IsPointInside(ascending.PointAt(t)+new Vector3d(.125,0,0),.001,true),"Gap filled");
                    }
                }
                results.Add("PASS "+name);
            }
            catch(Exception e) { failures++;results.Add("FAIL "+name+": "+e.Message); }
            Console.WriteLine(results[^1]);
        }
        foreach(var count in new[]{1,513,100}) {
            using var edge=new LineCurve(new Point3d(20,0,0),new Point3d(30,0,20));
            using var outline=new PolylineCurve(new[]{edge.PointAtStart,edge.PointAtEnd,new Point3d(0,0,20),new Point3d(0,0,0),edge.PointAtStart});
            using var solid=ContinuousChamferSupportBuilder.Create(outline,edge,new SupportParameters(2,.25,.4),.4,count,.001,out _,out _,out var error);
            var ok=solid is null && !string.IsNullOrEmpty(error);
            if(!ok)failures++;
            results.Add((ok?"PASS ":"FAIL ")+"reject count "+count);
            Console.WriteLine(results[^1]);
        }
        Directory.CreateDirectory("artifacts/support-checks");
        File.WriteAllLines("artifacts/support-checks/checks.txt",results);
        return failures==0?0:1;
    }
}
