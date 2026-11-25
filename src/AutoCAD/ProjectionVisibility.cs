using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.BoundaryRepresentation;

namespace KotNode.AutoCAD
{
    // Experimental inter-body visibility: exact native curves, intersection cuts,
    // and front-depth queries. Tangencies/coincident edges may still be ambiguous.
    internal static class ProjectionVisibility
    {
        const double Eps=1e-5;
        internal sealed class Edge
        {
            internal readonly int Body;
            internal readonly Curve Curve;
            internal readonly bool HiddenPass;
            internal readonly Extents3d Bounds;
            internal Edge(int body,Curve curve,bool hidden)
            {Body=body;Curve=curve;HiddenPass=hidden;Bounds=curve.GeometricExtents;}
        }
        static bool Overlap(Extents3d a,Extents3d b)
            =>a.MinPoint.X<=b.MaxPoint.X+Eps&&a.MaxPoint.X>=b.MinPoint.X-Eps&&a.MinPoint.Y<=b.MaxPoint.Y+Eps&&a.MaxPoint.Y>=b.MinPoint.Y-Eps;
        static bool Contains(Extents3d a,Point3d p)
            =>p.X>=a.MinPoint.X-Eps&&p.X<=a.MaxPoint.X+Eps&&p.Y>=a.MinPoint.Y-Eps&&p.Y<=a.MaxPoint.Y+Eps;
        static bool On(Curve curve,Point3d p)
            =>curve.GetClosestPointTo(p,false).DistanceTo(p)<=Eps;
        static Point3d At(Curve c,double fraction)
            =>c.GetPointAtParameter(c.StartParam+(c.EndParam-c.StartParam)*fraction);
        static List<double> Cuts(Edge edge,List<Edge> others)
        {
            var c=edge.Curve;var values=new List<double>();
            foreach(var other in others)
            {
                if(other==edge||!Overlap(edge.Bounds,other.Bounds))continue;
                if(other.Body==edge.Body&&!edge.HiddenPass)continue;
                var intersections=new Point3dCollection();
                c.IntersectWith(other.Curve,Intersect.OnBothOperands,intersections,IntPtr.Zero,IntPtr.Zero);
                // Include overlap endpoints: IntersectWith may return nothing for collinear curves.
                foreach(var p in new[]{other.Curve.StartPoint,other.Curve.EndPoint})
                    if(On(c,p))intersections.Add(p);
                foreach(Point3d p in intersections)
                {
                    var near=c.GetClosestPointTo(p,false);
                    if(near.DistanceTo(p)>Eps)continue;
                    double t=c.GetParameterAtPoint(near);
                    if(t>c.StartParam+1e-9&&t<c.EndParam-1e-9)values.Add(t);
                }
            }
            var unique=new List<double>();
            foreach(double t in values.OrderBy(x=>x))
                if(unique.Count==0||t-unique[unique.Count-1]>1e-8)unique.Add(t);
            return unique;
        }
        static double? Depth(Brep body,Point3d p,double top)
        {
            using(var ray=new Ray3d(new Point3d(p.X,p.Y,top),-Vector3d.ZAxis))
            {
                var hits=body.GetLineContainment(ray,1);
                if(hits==null||hits.Length==0)return null;
                return hits[0].Point.Z;
            }
        }
        static bool Occluded(int source,Point3d p,List<Brep> breps,Extents3d[] bounds,double top)
        {
            // If a tangent ray misses the source body, only a body entirely
            // in front of its bounding box is allowed to hide the edge.
            double? own=null;
            bool ownQueried=false;
            for(int j=0;j<breps.Count;j++)
            {
                if(j==source||!Contains(bounds[j],p)||bounds[j].MaxPoint.Z<bounds[source].MinPoint.Z+Eps)continue;
                if(!ownQueried){own=Depth(breps[source],p,top);ownQueried=true;}
                var z=Depth(breps[j],p,top);
                if(z.HasValue&&z.Value>(own??bounds[source].MaxPoint.Z)+Eps)return true;
            }
            return false;
        }
        static bool Covered(Curve part,List<Curve> curves)
        {
            // Exact coincidence is tested at several interior points, not by bounding box alone.
            return new[]{0.11,0.33,0.5,0.67,0.89}.All(t=>{
                var p=At(part,t);
                return curves.Any(c=>On(c,p));
            });
        }
        static IEnumerable<Curve> Split(Edge edge,List<Edge> all)
        {
            var parameters=Cuts(edge,all);
            if(parameters.Count==0){yield return (Curve)edge.Curve.Clone();yield break;}
            // Closed curves need a split at the original seam as well.
            if(edge.Curve.Closed)parameters.Insert(0,edge.Curve.StartParam);
            var pieces=edge.Curve.GetSplitCurves(new DoubleCollection(parameters.ToArray()));
            foreach(DBObject item in pieces)
            {
                if(item is Curve c)yield return c;
                else item.Dispose();
            }
        }
        internal static void Write(BlockTableRecord block,Transaction tr,Database db,List<Solid3d> bodies,List<Edge> edges,bool showHidden,double scale)
        {
            var breps=new List<Brep>();
            var visible=new List<Curve>();var hidden=new List<Curve>();
            var bounds=bodies.Select(b=>b.GeometricExtents).ToArray();
            double top=bounds.Max(b=>b.MaxPoint.Z)+1;
            try
            {
                foreach(var body in bodies)breps.Add(new Brep(body));
                // First pass contains only native visible curves. The second pass
                // includes native hidden curves; discard portions already covered.
                foreach(var edge in edges.OrderBy(e=>e.HiddenPass))
                {
                    foreach(var part in Split(edge,edges))
                    {
                        bool keep=false;
                        try
                        {
                            if(edge.HiddenPass&&Covered(part,visible.Concat(hidden).ToList()))continue;
                            var isHidden=edge.HiddenPass||Occluded(edge.Body,At(part,0.5),breps,bounds,top);
                            if(isHidden&&!showHidden)continue;
                            var list=isHidden?hidden:visible;
                            if(Covered(part,list))continue;
                            list.Add(part);keep=true;
                        }
                        finally{if(!keep)part.Dispose();}
                    }
                }
                foreach(var c in visible)AssemblyViews.StoreCurve(block,tr,db,c,AssemblyViews.VisibleLayer);
                foreach(var c in hidden)
                    if(!Covered(c,visible))AssemblyViews.StoreCurve(block,tr,db,c,AssemblyViews.HiddenLayer,scale);
            }
            finally
            {
                foreach(var c in visible.Concat(hidden))c.Dispose();
                foreach(var b in breps)b.Dispose();
            }
        }
    }
}
