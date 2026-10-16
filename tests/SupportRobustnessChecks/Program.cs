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
        var results=new List<string>();
        foreach(var tolerance in new[]{.001,.01})
        foreach(var dx in new[]{0.0,10.0,40.0,100.0})
        foreach(var triangle in new[]{false,true})
        {
            var name=$"dx={dx} triangle={triangle} tolerance={tolerance}";
            try {
                using var edge=new LineCurve(new Point3d(20,0,0),new Point3d(20+dx,0,20));
                using var outline=triangle
                    ?new PolylineCurve(new[]{edge.PointAtStart,edge.PointAtEnd,new Point3d(0,0,0),edge.PointAtStart})
                    :new PolylineCurve(new[]{edge.PointAtStart,edge.PointAtEnd,new Point3d(0,0,20),new Point3d(0,0,0),edge.PointAtStart});
                using var solid=ContinuousChamferSupportBuilder.Create(outline,edge,new(2,.4,.2),.4,5,tolerance,out var count,out var report,out var error);
                if(solid is null||!solid.IsSolid||!solid.IsValid||count!=5)throw new Exception(error);
                using var probe=edge.DuplicateCurve();probe.Translate(.125,0,0);
                if(!Rhino.Geometry.Intersect.Intersection.CurveBrep(probe,solid,.0001,out var overlap,out var points))throw new Exception("Cannot inspect tooth bands");
                foreach(var curve in overlap)curve.Dispose();
                var heights=points.Select(pt=>pt.Z).OrderBy(z=>z).ToArray();
                if(heights.Length!=10)throw new Exception($"Expected five physical tooth bands, got {heights.Length} crossings");
                var centers=new List<double>();
                for(var i=0;i<10;i+=2)
                {
                    if(Math.Abs(heights[i+1]-heights[i]-.2)>.002)throw new Exception("Tooth layer height changed");
                    centers.Add((heights[i+1]+heights[i])/2);
                }
                for(var i=1;i<4;i++)if(Math.Abs((centers[i+1]-centers[i])-(centers[1]-centers[0]))>.002)throw new Exception("Teeth not equally spaced");
                if(heights[0]>.25 || 20-heights[^1]>.25)throw new Exception("End tooth moved excessively far from endpoint");
                results.Add("PASS "+name);
            }catch(Exception e){results.Add("FAIL "+name+": "+e.Message);}
            Console.WriteLine(results[^1]);
            Directory.CreateDirectory("artifacts/support-robustness-checks");
            File.WriteAllLines("artifacts/support-robustness-checks/checks.txt",results);
        }
        return results.Any(r=>r.StartsWith("FAIL"))?1:0;
    }
}
