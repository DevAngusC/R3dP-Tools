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
        foreach(var height in Environment.GetEnvironmentVariable("TRUSS_STRESS_ONLY")=="1" ? Array.Empty<double>() : (Environment.GetEnvironmentVariable("TRUSS_FAIL_ONLY")=="1" ? new[]{78.489} : new[]{12.0,40.0,78.489,80.0}))
        foreach(var baseWidth in Environment.GetEnvironmentVariable("TRUSS_FAIL_ONLY")=="1" ? new[]{8.0} : new[]{0.0,8.0})
        foreach(var tolerance in Environment.GetEnvironmentVariable("TRUSS_FAIL_ONLY")=="1" ? new[]{.01} : new[]{.001,.01})
        {
            var name=$"height={height} baseWidth={baseWidth} tolerance={tolerance}";
            try {
                using var leg=Brep.CreateFromBox(new BoundingBox(-20,-8,0,-16,8,height+2));
                using var slab=Brep.CreateFromBox(new BoundingBox(-20,-8,height,20,8,height+4));
                using var target=Brep.CreateBooleanUnion(new[]{leg,slab},.001)[0];
                using var result=TrussSupportBuilder.Build(target,new(0,0,height),-Vector3d.ZAxis,new(.4,.2,3,4,.8,baseWidth),tolerance);
                if(!result.IsSolid || !result.IsValid)throw new Exception("Invalid output");
                results.Add("PASS "+name);
            }catch(Exception e){results.Add("FAIL "+name+": "+e.Message);}
            Console.WriteLine(results[^1]);
            Directory.CreateDirectory("artifacts/truss-robustness-checks");
            File.WriteAllLines("artifacts/truss-robustness-checks/checks.txt",results);
        }
        if(Environment.GetEnvironmentVariable("TRUSS_FAIL_ONLY")!="1")
        foreach(var config in new[]{(Width:3.0,Rod:.6),(Width:6.0,Rod:1.2)})
        foreach(var factor in new[]{1.0,3.0})
        foreach(var side in new[]{false,true})
        {
            if(Environment.GetEnvironmentVariable("TRUSS_EDGE_ONLY")=="1" && (config.Width!=6 || factor!=3 || !side))continue;
            var name=$"78.489 mm width={config.Width} rod={config.Rod} baseFactor={factor} side={side}";
            try {
                const double h=78.489;
                using var leg=Brep.CreateFromBox(new BoundingBox(-30,-12,0,-26,12,h+2));
                using var slab=Brep.CreateFromBox(new BoundingBox(-30,-12,h,30,12,h+4));
                using var target=Brep.CreateBooleanUnion(new[]{leg,slab},.001)[0];
                var point=side?new Point3d(30,0,h+2):new Point3d(0,0,h);
                target.Translate(10000,-20000,-30);point+=new Vector3d(10000,-20000,-30);
                using var result=TrussSupportBuilder.Build(target,point,side?Vector3d.XAxis:-Vector3d.ZAxis,
                    new(.4,.2,3,config.Width,config.Rod,config.Width*factor),.01);
                if(!result.IsValid || !result.IsSolid || Math.Abs(result.GetBoundingBox(true).Min.Z+30)>.02)throw new Exception("Invalid translated output");
                results.Add("PASS "+name);
            }catch(Exception e){results.Add("FAIL "+name+": "+e.Message);}
            Console.WriteLine(results[^1]);File.WriteAllLines("artifacts/truss-robustness-checks/checks.txt",results);
        }
        return results.Any(r=>r.StartsWith("FAIL"))?1:0;
    }
}
