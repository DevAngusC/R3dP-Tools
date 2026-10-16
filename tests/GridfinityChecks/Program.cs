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
        Directory.CreateDirectory("artifacts/gridfinity-checks");
        var results = new List<string>();
        void Assert(bool ok,string message) { if (!ok) throw new Exception(message); }
        void Test(string name,Action check) {
            try { check(); results.Add("PASS "+name); }
            catch (Exception e) { results.Add("FAIL "+name+": "+e.Message); }
            Console.WriteLine(results[^1]);
            File.WriteAllLines("artifacts/gridfinity-checks/checks.txt",results);
        }
        foreach (var p in new[] { new GridfinityParameters(1,1),new(2,3),new(3,1,.1,0,0),new(1,2,.4,.8,.6),new(4,4), new(1,1,BaseStyle:GridfinityBaseStyle.Lightweight), new(2,3,BaseStyle:GridfinityBaseStyle.Lightweight), new(3,1,.1,BaseStyle:GridfinityBaseStyle.Lightweight), new(1,2,.4,BaseStyle:GridfinityBaseStyle.Lightweight) })
        Test($"{p.Columns}x{p.Rows} {p.BaseStyle} clearance {p.Clearance}: solids, dimensions, holes, assembly, units", () => {
            var pair = GridfinityBuilder.Build(p);
            using var f = pair.Female; using var m = pair.Male;
            Assert(f.IsValid && f.IsSolid && m.IsValid && m.IsSolid,"Not closed valid solids");
            var fb=f.GetBoundingBox(true); var mb=m.GetBoundingBox(true);
            Assert(Math.Abs(fb.Min.Z)<.002 && Math.Abs(fb.Max.Z-(p.FloorHeight+4.4))<.002,"Female height");
            for(var x=0;x<p.Columns;x++) for(var y=0;y<p.Rows;y++) {
                Assert(f.IsPointInside(new(21+42*x,21+42*y,.1),.001,true)==p.HasFloor,"Floor/opening mismatch");
                if(!p.HasFloor) for(double z=.1;z<4.4;z+=.5)
                    Assert(!f.IsPointInside(new(21+42*x,21+42*y,z),.001,true),"Lightweight cell blocked");
            }
            Assert(Math.Abs(fb.Max.X-p.Width)<.002 && Math.Abs(fb.Max.Y-p.Length)<.002,"Female size");
            Assert(Math.Abs(mb.Min.X-.25)<.002 && Math.Abs(mb.Max.X-(p.Width-.25))<.002 && Math.Abs(mb.Max.Z-7)<.002,"Male size");
            for(var x=0;x<p.Columns;x++) for(var y=0;y<p.Rows;y++)
            foreach(var dx in new[]{-13,13}) foreach(var dy in new[]{-13,13}) {
                double cx=21+42*x+dx,cy=21+42*y+dy;
                Assert(!m.IsPointInside(new(cx,cy,.1),.001,true),"Male hole missing");
                Assert(m.IsPointInside(new(cx,cy,p.HoleDepth+.1),.001,true),"Male hole not blind/deep enough");
                Assert(!f.IsPointInside(new(cx,cy,p.FloorHeight-.1),.001,true),"Female hole missing");
                if(p.HasFloor) Assert(f.IsPointInside(new(cx,cy,p.FloorHeight-p.HoleDepth-.1),.001,true),"Female floor missing");
                Assert(m.IsPointInside(new(cx+p.HoleDiameter/2+.05,cy,1),.001,true),"Male hole too wide");
                Assert(!m.IsPointInside(new(cx+p.HoleDiameter/2-.05,cy,1),.001,true),"Male hole too narrow");
                if(p.HasFloor) Assert(f.IsPointInside(new(cx+p.HoleDiameter/2+.05,cy,p.FloorHeight-1),.001,true),"Female hole too wide");
                Assert(!f.IsPointInside(new(cx+p.HoleDiameter/2-.05,cy,p.FloorHeight-1),.001,true),"Female hole too narrow");
            }
            // Probe the vertical mating wall independently of the builder's profile definitions.
            Assert(f.IsPointInside(new(39.6+p.Clearance+.02,21,p.FloorHeight+1.5),.001,true),"Socket wall too far out");
            Assert(!f.IsPointInside(new(39.6+p.Clearance-.02,21,p.FloorHeight+1.5),.001,true),"Socket wall clearance missing");
            using var assembled=m.DuplicateBrep(); assembled.Translate(0,0,p.FloorHeight);
            // Exact seating has coincident planar faces; Rhino's intersection may return null there.
            // Check occupied space at seating, then use a 0.01 mm lift for the volume Boolean.
            for(double z=p.FloorHeight+.1;z<p.FloorHeight+4.4;z+=.4)
            for(double x=.37;x<p.Width;x+=2)
            for(double y=.29;y<p.Length;y+=2)
                Assert(!(assembled.IsPointInside(new(x,y,z),.001,true)&&f.IsPointInside(new(x,y,z),.001,true)),"Seated occupancy collision");
            assembled.Translate(0,0,.01);
            var intersection=Brep.CreateBooleanIntersection(f,assembled,.001);
            Assert(intersection != null,"Intersection failed");
            double volume=0;
            foreach(var b in intersection!) { using(b) { using var mass=VolumeMassProperties.Compute(b); volume+=Math.Abs(mass?.Volume??0); } }
            Assert(volume<.001,"Assembled parts collide: "+volume);
            foreach(var solid in new[]{f,m}) {
                using var mesh=new Mesh();
                var pieces=Mesh.CreateFromBrep(solid,MeshingParameters.Default);
                foreach(var piece in pieces) { mesh.Append(piece);piece.Dispose(); }
                mesh.Vertices.CombineIdentical(true,true);mesh.Weld(Math.PI);
                Assert(mesh.IsValid && mesh.IsClosed,"Export mesh not watertight");
            }
            using var inch=f.DuplicateBrep(); inch.Transform(Transform.Scale(Point3d.Origin,RhinoMath.UnitScale(UnitSystem.Millimeters,UnitSystem.Inches)));
            Assert(Math.Abs(inch.GetBoundingBox(true).Max.X-p.Width/25.4)<.0001,"Unit conversion");
            if(p.Columns==2&&p.Rows==3) {
                using var file=new File3dm();file.Settings.ModelUnitSystem=UnitSystem.Millimeters;
                file.Objects.AddBrep(f);m.Translate(p.Width+10,0,0);file.Objects.AddBrep(m);
                Assert(file.Write($"artifacts/gridfinity-checks/Gridfinity-{p.BaseStyle}-2x3.3dm",8),"Write example");
            }
        });
        Test("Invalid inputs rejected",()=> {
            foreach(var p in new[]{new GridfinityParameters(0,1),new(20,20),new(1,1,double.NaN),new(1,1,.5),new(1,1,.25,-1),new(1,1,.25,0,double.PositiveInfinity)}) {
                bool rejected=false;try{p.Validate();}catch(ArgumentException){rejected=true;}Assert(rejected,"Invalid accepted");
            }
        });
        Test("Cancellation before document changes",()=> {
            bool cancelled=false;try{var pair=GridfinityBuilder.Build(new(),()=>throw new OperationCanceledException());pair.Female.Dispose();pair.Male.Dispose();}catch(OperationCanceledException){cancelled=true;}
            Assert(cancelled,"Cancellation ignored");
        });
        return results.Any(r=>r.StartsWith("FAIL"))?1:0;
    }
}
