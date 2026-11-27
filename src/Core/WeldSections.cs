using System;
using System.Collections.Generic;
using System.Linq;
namespace KotNode.Core
{
    public sealed class WeldCutPatch
    {
        public Guid WeldId { get; set; }
        public List<Point2> Points { get; set; }=new List<Point2>();
    }
    public static class WeldSections
    {
        // The longitudinal boundaries of a curved cover plate have an explicit
        // contact with the supporting pipe. Smooth silhouettes do not identify a joint.
        public static bool TryEdge(string key,out int edge)
        {
            edge=0;
            if(key=="spatial:CurvedPlate:inner-start"||key=="spatial:CurvedPlate:outer-start")return true;
            if(key=="spatial:CurvedPlate:inner-end"||key=="spatial:CurvedPlate:outer-end"){edge=1;return true;}
            return false;
        }
        static bool PipeContact(AssemblyMark mark,Vector3 world)
        {
            foreach(var inst in mark.Instances)
            {
                var part=mark.Parts.FirstOrDefault(p=>p.Id==inst.PartId);
                if(part?.Kind!=PartKind.Pipe)continue;
                var delta=new Vector3(world.X-inst.Placement.Origin.X,world.Y-inst.Placement.Origin.Y,world.Z-inst.Placement.Origin.Z);
                double x=delta.Dot(inst.Placement.AxisX),y=delta.Dot(inst.Placement.AxisY),z=delta.Dot(inst.Placement.AxisX.Cross(inst.Placement.AxisY));
                if(z>=-1e-6&&z<=part.Pipe.Length+1e-6&&Math.Abs(Math.Sqrt(x*x+y*y)-part.Pipe.OuterDiameter/2)<1e-5)return true;
            }
            return false;
        }
        public static List<WeldCutPatch> Build(AssemblyMark mark,RigidPlacement frame,IEnumerable<ViewWeldOverlay> welds)
        {
            var result=new List<WeldCutPatch>();frame.Validate();
            foreach(var w in welds??Enumerable.Empty<ViewWeldOverlay>())
            {
                if(w.ShowInSections==false||!TryEdge(w.SourceKey,out var edge))continue;
                var inst=mark.Instances.FirstOrDefault(i=>i.Id==w.InstanceId);
                var part=inst==null?null:mark.Parts.FirstOrDefault(p=>p.Id==inst.PartId);
                if(part?.Kind!=PartKind.CurvedPlate)continue;
                ViewWelds.Validate(w);
                var shape=part.CurvedPlate;
                double angle=edge==0?0:shape.AngleRadians,sign=edge==0?-1:1;
                double ux=Math.Cos(angle),uy=Math.Sin(angle),tx=-uy*sign,ty=ux*sign;
                double r=shape.InnerRadius,k=w.LegMm;
                var section=new[]{new Point2(r*ux,r*uy),new Point2((r+k)*ux,(r+k)*uy),new Point2(r*ux+tx*k,r*uy+ty*k)};
                var points=new List<Vector3>();
                foreach(double t in new[]{w.From,w.To})
                    foreach(var p in section)
                    {
                        var world=inst.Placement.ToWorld(new Vector3(p.X,p.Y,t*shape.Length));
                        var d=new Vector3(world.X-frame.Origin.X,world.Y-frame.Origin.Y,world.Z-frame.Origin.Z);
                        points.Add(new Vector3(d.Dot(frame.AxisX),d.Dot(frame.AxisY),d.Dot(frame.AxisX.Cross(frame.AxisY))));
                    }
                // A tangent end face is not a material section.
                if(points.Min(p=>p.Z)>=-1e-7||points.Max(p=>p.Z)<=1e-7)continue;
                double fraction=Math.Abs(points[0].Z-points[3].Z)>1e-9?points[0].Z/(points[0].Z-points[3].Z):.5;
                if(fraction<-1e-7||fraction>1+1e-7)continue;
                double axial=(w.From+(w.To-w.From)*fraction)*shape.Length;
                var root=inst.Placement.ToWorld(new Vector3(r*ux,r*uy,axial));
                if(!PipeContact(mark,root))continue;
                var hits=new List<Point2>();
                Action<Vector3> add=p=>{
                    var q=new Point2(p.X,p.Y);
                    if(!hits.Any(h=>(h.X-q.X)*(h.X-q.X)+(h.Y-q.Y)*(h.Y-q.Y)<1e-12))hits.Add(q);
                };
                var edges=new[]{new[]{0,1},new[]{1,2},new[]{2,0},new[]{3,4},new[]{4,5},new[]{5,3},new[]{0,3},new[]{1,4},new[]{2,5}};
                foreach(var e in edges)
                {
                    var a=points[e[0]];var b=points[e[1]];
                    if(Math.Abs(a.Z)<1e-7)add(a);
                    if(a.Z*b.Z<0)
                    {
                        double t=a.Z/(a.Z-b.Z);
                        add(new Vector3(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t,0));
                    }
                }
                if(hits.Count<3)continue;
                double cx=hits.Average(p=>p.X),cy=hits.Average(p=>p.Y);
                hits=hits.OrderBy(p=>Math.Atan2(p.Y-cy,p.X-cx)).ToList();
                result.Add(new WeldCutPatch{WeldId=w.Id,Points=hits});
            }
            return result;
        }
    }
}
