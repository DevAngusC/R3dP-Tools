using Rhino;
using Rhino.Geometry;

namespace PrintGear;

internal readonly record struct TrussSupportParameters(
    double NozzleSize, double PrintThickness, double HeadLength,
    double Width, double RodDiameter, double BaseWidth = 0, double FootHeight = 3)
{
    public double EffectiveBaseWidth => BaseWidth == 0 ? Width : BaseWidth;
}

internal static class TrussSupportBuilder
{
    internal readonly record struct HeadLayout(Point3d Root,Point3d TowerTop,Vector3d Axis);

    internal static HeadLayout LayoutHead(Point3d contact,Vector3d outward,TrussSupportParameters p)
    {
        outward.Unitize();
        var direction=outward;
        if(direction.Z>-Math.Sqrt(3)/2)
        {
            var horizontal=new Vector3d(direction.X,direction.Y,0);
            horizontal.Unitize();
            direction=horizontal*.5-Vector3d.ZAxis*(Math.Sqrt(3)/2);
        }
        var root=contact+direction*p.HeadLength;
        var center=new Point3d(contact.X+outward.X*(p.Width/2+p.RodDiameter),
            contact.Y+outward.Y*(p.Width/2+p.RodDiameter),0);
        var orbit=(p.Width-p.RodDiameter)/2;
        var maxRun=Enumerable.Range(0,3).Max(i=>{
            var dx=center.X+orbit*Math.Cos(i*2*Math.PI/3)-root.X;
            var dy=center.Y+orbit*Math.Sin(i*2*Math.PI/3)-root.Y;
            return Math.Sqrt(dx*dx+dy*dy);
        });
        // Lower the complete tower crown until all three joining rods reach 60 degrees.
        center.Z=Math.Min(contact.Z-p.HeadLength-p.RodDiameter,root.Z-Math.Sqrt(3)*maxRun);
        var tipCenter=contact+outward*(p.NozzleSize*.45);
        var axis=root-tipCenter;axis.Unitize();
        return new(root,center,axis);
    }

    internal static IReadOnlyList<Vector3d> ContactNormals(Brep target,Point3d picked,double tolerance,out Point3d contact)
    {
        if(!target.ClosestPoint(picked,out contact,out var component,out _,out _,tolerance*10,out _))
            throw new ArgumentException("沒有取得量體上的接觸點，請重新選點。");
        // ClosestPoint may choose either adjacent face at an edge. Its edge output is
        // a tangent, not a face normal. Resolve all coincident trimmed faces instead.
        target.FindCoincidentBrepComponents(contact,tolerance*2,out var faces,out var edges,out _);
        var faceIds=new HashSet<int>(faces ?? Array.Empty<int>());
        foreach(var edge in edges ?? Array.Empty<int>())
            foreach(var face in target.Edges[edge].AdjacentFaces())faceIds.Add(face);
        if(component.ComponentIndexType==ComponentIndexType.BrepFace)faceIds.Add(component.Index);
        if(component.ComponentIndexType==ComponentIndexType.BrepEdge)
            foreach(var face in target.Edges[component.Index].AdjacentFaces())faceIds.Add(face);
        var normals=new List<Vector3d>();
        foreach(var index in faceIds.OrderBy(i=>i))
        {
            var face=target.Faces[index];
            if(!face.ClosestPoint(contact,out var u,out var v))continue;
            var normal=face.NormalAt(u,v);
            // BrepFace.NormalAt already honors the face orientation. Only normalize
            // the whole solid if the caller supplied an inward-oriented Brep.
            if(target.SolidOrientation==BrepSolidOrientation.Inward)normal=-normal;
            if(!normal.Unitize() || normal.Z>.25)continue;
            if(normals.All(n=>n*normal<.999999))normals.Add(normal);
        }
        if(normals.Count==0)
            throw new ArgumentException("此接觸點沒有可向下支撐的相鄰面；請重新選取側面、下懸面或其邊線。");
        return normals.OrderBy(n=>n.Z).ToArray();
    }

    internal static Brep BuildAtContact(Brep target,Point3d picked,TrussSupportParameters p,double tolerance,
        out Point3d contact,Action? checkpoint=null)
    {
        var normals=ContactNormals(target,picked,tolerance,out contact);
        Exception? failure=null;
        foreach(var normal in normals)
        {
            checkpoint?.Invoke();
            try { return Build(target,contact,normal,p,tolerance,checkpoint); }
            catch(OperationCanceledException){throw;}
            catch(ArgumentException e){failure ??= e;}
            catch(InvalidOperationException e){failure ??= e;}
        }
        throw new InvalidOperationException(normals.Count>1
            ? "已嘗試相鄰側面／下懸面，仍無法建立："+failure!.Message
            : failure!.Message,failure);
    }

    public static Brep Build(Brep target, Point3d contact, Vector3d outward,
        TrussSupportParameters p, double tolerance, Action? checkpoint = null)
    {
        var baseZ=target.GetBoundingBox(true).Min.Z;
        using var local=target.DuplicateBrep();
        local.Translate(-contact.X,-contact.Y,-baseZ);
        TrussMergeException? failure=null;
        foreach(var precision in new[]{1.0,.2})
        foreach(var angle in new[]{.371,1.173,2.231})
        {
            try
            {
                var result=BuildLocal(local,new Point3d(0,0,contact.Z-baseZ),outward,p,tolerance,checkpoint,angle,precision,precision==.2 && angle==2.231);
                if(!result.Translate(contact.X,contact.Y,baseZ)){result.Dispose();throw new InvalidOperationException("無法還原支撐位置。");}
                return result;
            }
            catch(TrussMergeException e){failure=e;}
        }
        throw failure!;
    }

    private static Brep BuildLocal(Brep target, Point3d contact, Vector3d outward,
        TrussSupportParameters p, double tolerance, Action? checkpoint, double seamAngle, double precision, bool sequential)
    {
        void Checkpoint() => checkpoint?.Invoke();
        var dimensions = new[] { p.NozzleSize, p.PrintThickness, p.HeadLength, p.Width, p.RodDiameter, p.FootHeight };
        if (dimensions.Any(v => !RhinoMath.IsValidDouble(v) || v <= tolerance * 8))
            throw new ArgumentException("所有尺寸須大於文件公差的八倍。");
        if (!target.IsValid || !target.IsSolid || !contact.IsValid || !outward.Unitize())
            throw new ArgumentException("請選取有效的封閉量體與表面接觸點。");
        if (!RhinoMath.IsValidDouble(p.BaseWidth) || p.BaseWidth < 0 ||
            (p.BaseWidth != 0 && (p.BaseWidth < p.Width || p.BaseWidth > p.Width * 3)))
            throw new ArgumentException("底部桁架寬度須為 0（沿用桁架寬度），或介於桁架寬度與其三倍之間。");
        if (p.Width <= p.RodDiameter * 3 || p.Width < Math.Max(p.NozzleSize, p.PrintThickness) * 2)
            throw new ArgumentException("桁架寬度須大於三倍桿徑，且至少為接觸尖端尺寸的兩倍。");
        if (p.HeadLength <= p.NozzleSize / 2 + p.RodDiameter)
            throw new ArgumentException("尖端長度不可小於接觸截面尺寸。");
        if (outward.Z > .25)
            throw new ArgumentException("此面朝上，向下支撐會穿入量體；請選側面或下懸面。");
        var baseZ = target.GetBoundingBox(true).Min.Z;
        var layout=LayoutHead(contact,outward,p);
        var topZ = layout.TowerTop.Z;
        if (topZ - baseZ < Math.Max(p.RodDiameter * 3, p.FootHeight + p.RodDiameter))
            throw new ArgumentException("高度不足以容納至少 60° 的尖端支撐與桁架；請提高接觸點、縮小桁架寬度或縮短尖端。");
        var center = layout.TowerTop;
        var parts = new List<Brep>();
        try
        {
            Brep Solid(Brep? b)
            {
                if (b is null || !b.IsValid || !b.IsSolid) { b?.Dispose(); throw new InvalidOperationException("桁架零件未形成有效封閉實體。"); }
                if (b.SolidOrientation == BrepSolidOrientation.Inward) b.Flip();
                parts.Add(b); return b;
            }
            void Joint(Point3d point,double radius)
            {
                var sphere=new Sphere(point,radius).ToBrep();
                if(seamAngle!=.371)sphere.Rotate(seamAngle,new Vector3d(1,2,3),point);
                Solid(sphere);
            }
            var radius = p.RodDiameter / 2;
            var orbit = p.Width / 2 - radius;
            var baseOrbit = p.EffectiveBaseWidth / 2 - radius;
            var plateThickness=p.PrintThickness*3;
            if(plateThickness>=p.FootHeight)throw new ArgumentException("列印厚度的三倍須小於 3 mm 象腿高度。");
            // Keep the lowest inclined brace entirely above the base plane.
            var bottom=baseZ+Math.Max(plateThickness/2,radius*1.2);
            var top=topZ;
            Point3d At(int i,double z)
            {
                var t=(z-bottom)/(top-bottom);
                var r=baseOrbit+(orbit-baseOrbit)*t;
                return new Point3d(center.X+r*Math.Cos(i*2*Math.PI/3),
                    center.Y+r*Math.Sin(i*2*Math.PI/3),z);
            }
            void Rod(Point3d a,Point3d b)
            {
                Checkpoint();
                var direction=b-a;
                var plane=new Plane(a,direction);
                plane.Rotate(seamAngle,plane.ZAxis);
                Solid(Brep.CreateFromCylinder(new Cylinder(new Circle(plane,radius),direction.Length),true,true));
            }
            var basePoints=Enumerable.Range(0,4).Select(i => new Point3d(
                center.X+(baseOrbit+p.RodDiameter*3)*Math.Cos(i%3*2*Math.PI/3),
                center.Y+(baseOrbit+p.RodDiameter*3)*Math.Sin(i%3*2*Math.PI/3),baseZ));
            using var baseOutline=new PolylineCurve(basePoints);
            Solid(Extrusion.Create(baseOutline,plateThickness,true)?.ToBrep());
            for(var i=0;i<3;i++)
            {
                using var footBase=new Circle(new Plane(At(i,baseZ),Vector3d.ZAxis),radius*3).ToNurbsCurve();
                using var footTop=new Circle(new Plane(At(i,baseZ+p.FootHeight),Vector3d.ZAxis),radius*1.1).ToNurbsCurve();
                var footLofts=Brep.CreateFromLoft(new Curve[]{footBase,footTop},Point3d.Unset,Point3d.Unset,LoftType.Straight,false);
                if(footLofts is null || footLofts.Length!=1)throw new InvalidOperationException("無法建立象腿補強。");
                using(var foot=footLofts[0])Solid(foot.CapPlanarHoles(tolerance));
            }
            var levels=BraceLevels(bottom,top,baseOrbit,orbit);
            for(var node=1;node<levels.Count;node++)
            {
                var nodeZ=levels[node];
                if(top-nodeZ<radius*2.5)continue;
                for(var i=0;i<3;i++)Joint(At(i,nodeZ),radius*1.2);
            }
            for(var i=0;i<3;i++)Rod(At(i,bottom),At(i,top));
            for(var cell=0;cell<levels.Count-1;cell++)
            {
                var z0=levels[cell];
                var z1=levels[cell+1];
                for(var i=0;i<3;i++)
                    if(cell%2==0)Rod(At(i,z0),At((i+1)%3,z1));
                    else Rod(At((i+1)%3,z0),At(i,z1));
            }
            var rootPoint=layout.Root;
            for(var i=0;i<3;i++)
            {
                Joint(At(i,top),radius*1.2);
                Rod(At(i,top),rootPoint);
            }
            var rootRadius=p.RodDiameter*.75;
            Joint(rootPoint,rootRadius*.9);
            var tipPlane=new Plane(contact,outward);
            tipPlane.Rotate(seamAngle,tipPlane.ZAxis);
            // A tangent sphere has zero contact area. Embed only its small polar cap:
            // diameter = nozzle size, penetration = 5% of diameter, independent of layer thickness.
            var tipRadius=p.NozzleSize/2;
            var penetration=p.NozzleSize*.05;
            var tipCenter=contact+outward*(tipRadius-penetration);
            using var arc=new ArcCurve(new Arc(tipCenter-outward*tipRadius,
                tipCenter+(tipPlane.XAxis-outward)*(tipRadius/Math.Sqrt(2)),tipCenter+tipPlane.XAxis*tipRadius));
            using var domeSurface=RevSurface.Create(arc,new Line(tipCenter,tipCenter+outward));
            if(domeSurface is null)throw new InvalidOperationException("無法建立半球接觸尖端。");
            using var dome=domeSurface.ToBrep();
            using var tip=new Circle(new Plane(tipCenter,tipPlane.XAxis,tipPlane.YAxis),tipRadius).ToNurbsCurve();
            // Carry the same circle seam and winding through the bend. Independently
            // constructed frames can differ by half a turn, pinching a ruled loft.
            var rootPlane=new Plane(tipCenter,tipPlane.XAxis,tipPlane.YAxis);
            rootPlane.Transform(Transform.Rotation(outward,layout.Axis,tipCenter));
            rootPlane.Origin=rootPoint;
            using var root=new Circle(rootPlane,rootRadius).ToNurbsCurve();
            var lofts=Brep.CreateFromLoft(new Curve[]{tip,root},Point3d.Unset,Point3d.Unset,LoftType.Straight,false);
            if(lofts is null || lofts.Length!=1)throw new InvalidOperationException("無法建立圓錐尖端。");
            using(var loft=lofts[0])
            {
                var joined=Brep.JoinBreps(new[]{dome,loft},Math.Min(tolerance,tipRadius*.005));
                if(joined is null || joined.Length!=1)
                {
                    if(joined is not null)foreach(var piece in joined)piece.Dispose();
                    throw new InvalidOperationException("半球尖端與漸粗支撐未連接。");
                }
                using(var head=joined[0])
                {
                    var headSolid=Solid(head.CapPlanarHoles(tolerance));
                    var probeRadius=Math.Min(tipRadius,rootRadius)*.15;
                    var probeTolerance=Math.Min(tolerance,probeRadius*.01);
                    for(var step=1;step<20;step++)
                    {
                        var centerPoint=tipCenter+(rootPoint-tipCenter)*(step/20.0);
                        for(var side=0;side<8;side++)
                        {
                            var sample=centerPoint+(rootPlane.XAxis*Math.Cos(side*Math.PI/4)+
                                rootPlane.YAxis*Math.Sin(side*Math.PI/4))*probeRadius;
                            if(!headSolid.IsPointInside(sample,probeTolerance,true))
                                throw new InvalidOperationException("尖端出現縮頸或扭轉，未輸出不連續支撐。");
                        }
                    }
                }
            }
            Checkpoint();
            var unionTolerance=Math.Min(tolerance,Math.Min(p.PrintThickness,p.RodDiameter)*.005)*precision;
            var result=MergeParts(parts,unionTolerance,Checkpoint,sequential);
            try
            {
                Checkpoint();
                bool AtTip(Point3d point)
                {
                    var depth=tipPlane.DistanceTo(point);
                    var allowance=Math.Min(tolerance*2,tipRadius*.05);
                    return depth>=-penetration-allowance && depth<=tipRadius+allowance &&
                        point.DistanceTo(tipCenter)<=tipRadius+allowance;
                }
                if(!Rhino.Geometry.Intersect.Intersection.BrepBrep(target,result,tolerance,out var curves,out var points))
                    throw new InvalidOperationException("無法確認支撐與量體交線，未輸出。");
                var collides=points.Any(point=>!AtTip(point));
                foreach(var curve in curves)
                {
                    using(curve)
                        for(var i=0;i<=20;i++) if(!AtTip(curve.PointAtNormalizedLength(i/20.0)))collides=true;
                }
                // Surface samples also detect complete containment without crossing boundaries.
                foreach(var pair in new[]{(Source:result,Other:target),(Source:target,Other:result)})
                {
                    var meshes=Mesh.CreateFromBrep(pair.Source,MeshingParameters.FastRenderMesh);
                    if(meshes is null || meshes.Length==0)throw new InvalidOperationException("無法建立碰撞檢查網格。");
                    foreach(var mesh in meshes)
                    {
                        using(mesh)
                            foreach(var vertex in mesh.Vertices)
                            {
                                var sample=new Point3d(vertex);
                                if(!AtTip(sample) && pair.Other.IsPointInside(sample,tolerance*2,true))collides=true;
                            }
                    }
                }
                if(collides)throw new InvalidOperationException("桁架或尖端穿入量體；請換接觸點，或調整尖端長度／桁架寬度。");
                if(Math.Abs(result.GetBoundingBox(true).Min.Z-baseZ)>tolerance*2)
                    throw new InvalidOperationException($"桁架底面未對齊量體最低 Z：{result.GetBoundingBox(true).Min.Z:G}，基準 {baseZ:G}。");
                using var exportMesh=new Mesh();
                var exportParts=Mesh.CreateFromBrep(result,MeshingParameters.Default);
                if(exportParts is null)throw new InvalidOperationException("無法建立可匯出的桁架網格。");
                foreach(var mesh in exportParts) { using(mesh)exportMesh.Append(mesh); }
                exportMesh.Vertices.CombineIdentical(true,true);exportMesh.Weld(Math.PI);exportMesh.Normals.ComputeNormals();
                if(!exportMesh.IsValid || !exportMesh.IsClosed)
                    throw new InvalidOperationException("桁架匯出網格未封閉，請調整尺寸。");
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { foreach(var b in parts)b.Dispose(); }
    }
    // Adjacent triangular columns are 120 degrees apart. Solve rise/run = tan(60)
    // using their actual radii at both heights; tapered towers have unequal cell heights.
    internal static IReadOnlyList<double> BraceLevels(double bottom,double top,double baseOrbit,double topOrbit)
    {
        var levels=new List<double>{bottom};
        double Radius(double z)=>baseOrbit+(topOrbit-baseOrbit)*(z-bottom)/(top-bottom);
        while(true)
        {
            var start=levels[^1];
            var r0=Radius(start);
            double Error(double end)
            {
                var r1=Radius(end);
                return end-start-Math.Sqrt(3)*Math.Sqrt(r0*r0+r1*r1+r0*r1);
            }
            // Never flatten a final short brace just to reach the tower top.
            if(Error(top)<0)break;
            if(levels.Count>32)throw new ArgumentException("桁架超過 32 節，請增加桁架寬度或降低支撐高度。");
            var low=start;var high=top;
            for(var iteration=0;iteration<64;iteration++)
            {
                var mid=(low+high)/2;
                if(Error(mid)<0)low=mid;else high=mid;
            }
            levels.Add((low+high)/2);
        }
        return levels;
    }

    private static bool HasClosedExportMesh(Brep solid)
    {
        using var mesh=new Mesh();
        var pieces=Mesh.CreateFromBrep(solid,MeshingParameters.Default);
        if(pieces is null)return false;
        foreach(var piece in pieces){using(piece)mesh.Append(piece);}
        mesh.Vertices.CombineIdentical(true,true);mesh.Weld(Math.PI);mesh.Normals.ComputeNormals();
        return mesh.IsValid && mesh.IsClosed;
    }

    private sealed class TrussMergeException(string message) : InvalidOperationException(message);

    private static Brep MergeParts(IReadOnlyList<Brep> parts,double tolerance,Action checkpoint,bool sequential)
    {
        Brep? Union(IEnumerable<Brep> items)
        {
            checkpoint();
            Brep[]? merged;
            try { merged=Brep.CreateBooleanUnion(items,tolerance,true); }
            catch { return null; }
            if(merged is not null && merged.Length==1 && merged[0].IsValid && merged[0].IsSolid)
            {
                if(HasClosedExportMesh(merged[0]))return merged[0];
                merged[0].Repair(tolerance);
                if(merged[0].IsValid && merged[0].IsSolid && HasClosedExportMesh(merged[0]))return merged[0];
            }
            if(merged is not null)foreach(var b in merged)b.Dispose();
            return null;
        }
        var batch=Union(parts);
        if(batch is not null)return batch;
        if(!sequential)throw new TrussMergeException("桁架批次合併失敗，重試不同曲面接縫。");
        // Grow a connected solid from the base. Floating joints wait for their columns;
        // no failed part is silently dropped, and no dimensions are enlarged to force a union.
        var result=parts[0].DuplicateBrep();
        var pending=parts.Skip(1).ToList();
        try
        {
            while(pending.Count>0)
            {
                var progress=false;
                for(var i=0;i<pending.Count;)
                {
                    var a=result.GetBoundingBox(true);a.Inflate(tolerance*2);
                    if(!BoundingBox.Intersection(a,pending[i].GetBoundingBox(true)).IsValid){i++;continue;}
                    var merged=Union(new[]{result,pending[i]});
                    if(merged is null){i++;continue;}
                    result.Dispose();result=merged;pending.RemoveAt(i);progress=true;
                }
                if(!progress)throw new TrussMergeException($"桁架接點合併失敗，尚有 {pending.Count} 個零件未連接；未輸出不完整支撐。");
            }
            return result;
        }
        catch { result.Dispose();throw; }
    }

}
