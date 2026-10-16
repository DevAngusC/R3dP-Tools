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
        void Assert(bool ok,string message) { if(!ok)throw new Exception(message); }
        void Test(string name,Action check) {
            if(Environment.GetEnvironmentVariable("TRUSS_TIP_ONLY")=="1" && !name.Contains("continuous material"))return;
            if(Environment.GetEnvironmentVariable("TRUSS_NORMAL_ONLY")=="1" && !name.Contains("edge resolver"))return;
            try { check(); results.Add("PASS "+name); }
            catch(Exception e) { results.Add("FAIL "+name+": "+e.Message); }
            Console.WriteLine(results[^1]);
            Directory.CreateDirectory("artifacts/truss-checks");File.WriteAllLines("artifacts/truss-checks/checks.txt",results);
        }
        Brep Target(double z) {
            using var leg=Brep.CreateFromBox(new BoundingBox(-10,-6,z,-6,6,z+22));
            using var slab=Brep.CreateFromBox(new BoundingBox(-10,-6,z+20,10,6,z+24));
            return Brep.CreateBooleanUnion(new[]{leg,slab},.001)[0];
        }
        foreach(var z in new[]{0.0,-30.0})
        foreach(var side in new[]{false,true})
        Test($"valid closed truss, base={z}, side={side}",()=>{
            using var target=Target(z);
            var contact=side?new Point3d(10,0,z+22):new Point3d(2,0,z+20);
            var normal=side?Vector3d.XAxis:-Vector3d.ZAxis;
            using var solid=TrussSupportBuilder.Build(target,contact,normal,new(.4,.2,3,4,.8),.001);
            Assert(solid.IsValid&&solid.IsSolid,"Invalid solid");
            foreach(var face in solid.Faces)
                if(face.TryGetCylinder(out var cylinder,.0001))
                {
                    var axis=cylinder.Axis;
                    var angle=Math.Atan2(Math.Abs(axis.Z),Math.Sqrt(axis.X*axis.X+axis.Y*axis.Y))*180/Math.PI;
                    Assert(angle>=60-1e-5,"Cylinder in straight tower is below 60 degrees");
                }

            using var exportMesh=new Mesh();
            foreach(var m in Mesh.CreateFromBrep(solid,MeshingParameters.Default)) { using(m)exportMesh.Append(m); }
            exportMesh.Vertices.CombineIdentical(true,true);exportMesh.Weld(Math.PI);exportMesh.Normals.ComputeNormals();
            Assert(exportMesh.IsValid && exportMesh.IsClosed,"Export mesh not watertight");
            Assert(Math.Abs(solid.GetBoundingBox(true).Min.Z-z)<.002,"Wrong base");
            var cx=contact.X+(side?2.8:0);
            Assert(!solid.IsPointInside(new Point3d(cx,0,z+8),.001,true),"Tower interior blocked");
            Assert(solid.IsPointInside(new Point3d(cx+1.6,0,z+8),.001,true),"Missing upright");
            Assert(solid.IsPointInside(new Point3d(cx,0,z+.4),.001,true),"Missing base");
            for(var i=0;i<3;i++)
                Assert(solid.IsPointInside(new Point3d(cx+1.6*Math.Cos(i*2*Math.PI/3),1.6*Math.Sin(i*2*Math.PI/3),z+6),.001,true),"Missing triangular upright");
            Assert(!solid.IsPointInside(new Point3d(cx-1.6,-1.6,z+6),.001,true),"Unexpected fourth upright");
            var top=contact.Z-3-.8;
            var cellStep=4.8;
            var ax=cx+1.6;var bx=cx-.8;var by=1.6*Math.Sin(2*Math.PI/3);
            Assert(solid.IsPointInside(new Point3d(ax+(bx-ax)*.25,by*.25,z+.48+cellStep*.25),.001,true),"Missing single brace");
            Assert(!solid.IsPointInside(new Point3d(ax+(bx-ax)*.25,by*.25,z+.48+cellStep*.75),.001,true),"Unexpected X brace");
            var tipPlane=new Plane(contact,normal);
            Assert(solid.IsPointInside(contact+normal*.18+tipPlane.XAxis*.19,.001,true),"Missing hemisphere equator");
            Assert(!solid.IsPointInside(contact+normal*.18+tipPlane.XAxis*.21,.001,true),"Hemisphere diameter too large");
            Assert(!solid.IsPointInside(contact+normal*.01+tipPlane.XAxis*.15,.001,true),"Tip is not rounded");
            var embedded=contact-normal*.01;
            Assert(solid.IsPointInside(embedded,.0001,true) && target.IsPointInside(embedded,.0001,true),"No positive contact overlap");
            Assert(!solid.IsPointInside(contact-normal*.03,.0001,true),"Tip penetration too deep");
            Assert(!solid.IsPointInside(contact+tipPlane.XAxis*.11,.0001,true),"Contact patch too wide");

            using var model=new File3dm();model.Objects.AddBrep(target);model.Objects.AddBrep(solid);
            model.Write($"artifacts/truss-checks/truss-{z}-{side}.3dm",8);
        });
        foreach(var size in new[]{.2,.4,.8})
        Test($"hemisphere diameter and overlap {size}",()=>{
            using var target=Target(0);
            var contact=new Point3d(2,0,20);
            var normal=-Vector3d.ZAxis;
            using var solid=TrussSupportBuilder.Build(target,contact,normal,new(size,.3,3,4,.8),.01);
            var equator=contact+normal*(size*.45);
            Assert(solid.IsPointInside(equator+Vector3d.XAxis*(size*.48),.0001,true),"Diameter too small");
            Assert(!solid.IsPointInside(equator+Vector3d.XAxis*(size*.52),.0001,true),"Diameter too large");
            var embedded=contact-normal*(size*.025);
            Assert(solid.IsPointInside(embedded,.0001,true)&&target.IsPointInside(embedded,.0001,true),"No material overlap");
            Assert(!solid.IsPointInside(contact-normal*(size*.075),.0001,true),"Too much penetration");
        });
        Test("hemisphere on inclined curved surface",()=>{
            var center=new Point3d(0,0,50);
            var normal=new Vector3d(Math.Sqrt(3)/2,0,-.5);
            using var target=new Sphere(center,40).ToBrep();
            var contact=center+normal*40;
            using var solid=TrussSupportBuilder.Build(target,contact,normal,new(.4,.2,3,4,.8),.01);
            var embedded=contact-normal*.01;
            Assert(solid.IsPointInside(embedded,.0001,true)&&target.IsPointInside(embedded,.0001,true),"Curved contact has no overlap");
            Assert(solid.IsValid&&solid.IsSolid,"Curved contact output invalid");
        });
        Test("reject tower collision with intermediate shelf",()=>{
            using var original=Target(0);
            using var shelf=Brep.CreateFromBox(new BoundingBox(-10,-5,8,8,5,11));
            using var target=Brep.CreateBooleanUnion(new[]{original,shelf},.001)[0];
            try { using var b=TrussSupportBuilder.Build(target,new(2,0,20),-Vector3d.ZAxis,new(.4,.2,3,4,.8),.001);throw new Exception("Accepted collision"); }
            catch(InvalidOperationException e) { Assert(e.Message.Contains("穿入"),e.Message); }
        });
        Test("inch document scale",()=>{
            const double scale=1.0/25.4;
            using var target=Target(0);target.Transform(Transform.Scale(Point3d.Origin,scale));
            using var solid=TrussSupportBuilder.Build(target,new(2*scale,0,20*scale),-Vector3d.ZAxis,
                new(.4*scale,.2*scale,3*scale,4*scale,.8*scale,0,3*scale),.001*scale);
            Assert(solid.IsSolid && Math.Abs(solid.GetBoundingBox(true).Min.Z)<.002*scale,"Units failed");
        });
        foreach(var baseWidth in new[]{0.0,4.0,8.0,12.0})
        Test($"taper base width {baseWidth}",()=>{
            using var target=Target(0);
            using var solid=TrussSupportBuilder.Build(target,new(2,0,20),-Vector3d.ZAxis,new(.4,.2,3,4,.8,baseWidth),.001);
            Assert(solid.IsSolid && solid.IsValid,"Invalid tapered solid");
            Assert(Math.Abs(solid.GetBoundingBox(true).Min.Z)<.002,"Incorrect base");
            var width=baseWidth==0?4:baseWidth;
            // Probe all three sloping columns at two heights, independently of triangulation.
            foreach(var z in new[]{2.0,12.0})for(var i=0;i<3;i++) {
                var bottom=.48;
                var r=(width/2-.4)+(1.6-(width/2-.4))*(z-bottom)/((17-Math.Sqrt(3)*1.6)-bottom);
                Assert(solid.IsPointInside(new(2+r*Math.Cos(i*2*Math.PI/3),r*Math.Sin(i*2*Math.PI/3),z),.001,true),"Missing sloping column");
            }
            using var model=new File3dm();model.Objects.AddBrep(target);model.Objects.AddBrep(solid);
            model.Write($"artifacts/truss-checks/taper-{baseWidth}.3dm",8);
        });
        foreach(var baseWidth in new[]{4.0,8.0,12.0})
        Test($"60 degree braces and unbraced remainder, width={baseWidth}",()=>{
            using var target=Target(0);
            using var solid=TrussSupportBuilder.Build(target,new(2,0,20),-Vector3d.ZAxis,new(.4,.2,3,4,.8,baseWidth),.001);
            var levels=TrussSupportBuilder.BraceLevels(.48,(17-Math.Sqrt(3)*1.6),baseWidth/2-.4,1.6);
            Point3d At(int index,double z){var r=(baseWidth/2-.4)+(1.6-(baseWidth/2-.4))*(z-.48)/((17-Math.Sqrt(3)*1.6)-.48);return new(2+r*Math.Cos(index*2*Math.PI/3),r*Math.Sin(index*2*Math.PI/3),z);}
            for(var cell=0;cell<levels.Count-1;cell++)for(var i=0;i<3;i++)
            {
                var a=At(cell%2==0?i:(i+1)%3,levels[cell]);
                var b=At(cell%2==0?(i+1)%3:i,levels[cell+1]);
                var d=b-a;
                Assert(Math.Abs(Math.Atan2(d.Z,Math.Sqrt(d.X*d.X+d.Y*d.Y))*180/Math.PI-60)<1e-8,"Brace angle is not 60 degrees");
                Assert(solid.IsPointInside(a+d*.5,.001,true),"Missing calculated brace");
            }
            var finalStart=At(0,levels[^1]);var finalEnd=At(1,(17-Math.Sqrt(3)*1.6));var delta=finalEnd-finalStart;
            Assert(delta.Z < Math.Sqrt(3)*Math.Sqrt(delta.X*delta.X+delta.Y*delta.Y),"Skipped complete brace");
            if(delta.Z>1.6)Assert(!solid.IsPointInside(finalStart+delta*.5,.001,true),"Unexpected remainder brace");
            Assert(solid.IsPointInside(new(2,0,.5),.001,true),"Missing thin base");
            Assert(!solid.IsPointInside(new(2,0,.7),.001,true),"Base too thick");
        });
        foreach(var invalid in new[]{-1.0,2.0,12.01,double.NaN})
        Test($"reject invalid base width {invalid}",()=>{
            using var target=Target(0);
            try { using var b=TrussSupportBuilder.Build(target,new(2,0,20),-Vector3d.ZAxis,new(.4,.2,3,4,.8,invalid),.001);throw new Exception("Accepted invalid width"); }
            catch(ArgumentException){}
        });
        foreach(var picked in new[]{new Point3d(10,0,24),new Point3d(10,0,20),new Point3d(10,6,22),new Point3d(10,6,24),new Point3d(10,6,20)})
        Test($"resolve edge or vertex {picked}",()=>{
            using var target=Target(0);
            var normals=TrussSupportBuilder.ContactNormals(target,picked,.001,out var resolved);
            Assert(resolved.DistanceTo(picked)<.0001,"Contact moved off selected edge");
            Assert(normals.Count>0 && normals.All(n=>n.Z<=.25),"Upward face chosen");
            using var solid=TrussSupportBuilder.BuildAtContact(target,picked,new(.4,.2,3,4,.8),.001,out var contact);
            Assert(solid.IsSolid&&solid.IsValid,"Invalid edge support");
            var overlap=false;
            foreach(var x in new[]{-.01,0,.01})foreach(var y in new[]{-.01,0,.01})foreach(var z in new[]{-.01,0,.01})
            {
                var sample=contact+new Vector3d(x,y,z);
                overlap |= target.IsPointInside(sample,.0001,true)&&solid.IsPointInside(sample,.0001,true);
            }
            Assert(overlap,"Edge tip has no material overlap");
        });
        Test("edge resolver respects face orientation and rejects top interior",()=>{
            using var target=Target(0);
            var lower=TrussSupportBuilder.ContactNormals(target,new(2,0,20),.001,out _);
            Assert(lower.Count==1&&lower[0].Z<-.99,"Wrong underside normal");
            target.Flip();
            lower=TrussSupportBuilder.ContactNormals(target,new(2,0,20),.001,out _);
            Assert(lower.Count==1&&lower[0].Z<-.99,"Inward solid changes outward direction");
            try { TrussSupportBuilder.ContactNormals(target,new(2,0,24),.001,out _);throw new Exception("Accepted top interior"); }
            catch(ArgumentException e){Assert(e.Message.Contains("相鄰面"),e.Message);}
        });
        foreach(var width in new[]{4.0,10.0})
        foreach(var normal in new[]{Vector3d.XAxis,-Vector3d.ZAxis,new Vector3d(.8,0,-.6),new Vector3d(0,Math.Sqrt(.96),.2)})
        Test($"head and crown minimum angle width={width} normal={normal}",()=>{
            var contact=new Point3d(20,10,80);
            var p=new TrussSupportParameters(.4,.2,3,width,.8);
            var layout=TrussSupportBuilder.LayoutHead(contact,normal,p);
            double Angle(Vector3d v)=>Math.Atan2(Math.Abs(v.Z),Math.Sqrt(v.X*v.X+v.Y*v.Y))*180/Math.PI;
            Assert(Angle(layout.Root-contact)>=60-1e-8,"Contact-to-root angle below 60");
            Assert(Angle(layout.Root-(contact+normal*.18))>=60-1e-8,"Cone centerline below 60");
            Assert(layout.Root.Z<contact.Z,"Root is not below contact");
            Assert(Math.Abs(layout.Root.DistanceTo(contact)-3)<1e-8,"Head length changed");
            for(var i=0;i<3;i++)
            {
                var node=layout.TowerTop+new Vector3d((width-.8)/2*Math.Cos(i*2*Math.PI/3),(width-.8)/2*Math.Sin(i*2*Math.PI/3),0);
                Assert(layout.Root.Z>node.Z&&Angle(layout.Root-node)>=60-1e-8,"Crown joining rod below 60");
            }
        });
        foreach(var width in new[]{4.0,10.0})
        foreach(var angleDegrees in new[]{0,60,90,120,180,240,270,300})
        Test($"side head keeps continuous material width={width} heading={angleDegrees}",()=>{
            using var target=Target(0);
            var contact=new Point3d(10,0,22);var normal=Vector3d.XAxis;
            var rotate=Transform.Rotation(angleDegrees*Math.PI/180,Vector3d.ZAxis,Point3d.Origin);
            target.Transform(rotate);contact.Transform(rotate);normal.Transform(rotate);
            var p=new TrussSupportParameters(.4,.2,3,width,.8);
            var layout=TrussSupportBuilder.LayoutHead(contact,normal,p);
            using var solid=TrussSupportBuilder.Build(target,contact,normal,p,.001);
            var start=contact+normal*.18;
            var cross=new Plane(start,layout.Axis);
            // A thin material core must survive every slice, not merely pass IsSolid.
            for(var step=5;step<=95;step++)
            {
                var center=start+(layout.Root-start)*(step/100.0);
                for(var side=0;side<8;side++)
                {
                    var sample=center+(cross.XAxis*Math.Cos(side*Math.PI/4)+cross.YAxis*Math.Sin(side*Math.PI/4))*.04;
                    Assert(solid.IsPointInside(sample,.0001,true),$"Pinched tip at {step}% angle {side}");
                }
            }
        });
        Test("reject upwards surface",()=>{
            using var target=Target(0);
            try { using var b=TrussSupportBuilder.Build(target,new(2,0,24),Vector3d.ZAxis,new(.4,.2,3,4,.8),.001);throw new Exception("Accepted"); }
            catch(ArgumentException){}
        });
        Test("reject insufficient height",()=>{
            using var target=Target(0);
            try { using var b=TrussSupportBuilder.Build(target,new(-6,0,1),Vector3d.XAxis,new(.4,.2,3,4,.8),.001);throw new Exception("Accepted"); }
            catch(ArgumentException){}
        });
        Test("cancel geometry",()=>{
            using var target=Target(0);
            try { using var b=TrussSupportBuilder.Build(target,new(2,0,20),-Vector3d.ZAxis,new(.4,.2,3,4,.8),.001,()=>throw new OperationCanceledException());throw new Exception("Accepted"); }
            catch(OperationCanceledException){}
        });
        return results.Any(r=>r.StartsWith("FAIL"))?1:0;
    }
}
