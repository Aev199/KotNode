using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    // Deliberately bounded proof of concept: orthogonal pipes and rectangular plates.
    // Observer is on view +Z. A cut retains Z <= 0. No AutoCAD types or raster HLR.
    public static class ParametricProjection
    {
        const double Eps = 1e-7;
        const double Tau = 2 * Math.PI;
        public sealed class Curve2
        {
            public bool Circular { get; set; }
            public double X0 { get; set; }
            public double Y0 { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public double Radius { get; set; }
            public double Start { get; set; }
            public double Sweep { get; set; }
            public bool Hidden { get; set; }
            public bool Cut { get; set; }
            public Guid InstanceId { get; set; }
            public Point2 At(double t) => Circular
                ? new Point2(X0 + Radius * Math.Cos(Start + Sweep*t), Y0 + Radius * Math.Sin(Start + Sweep*t))
                : new Point2(X0 + (X1-X0)*t, Y0 + (Y1-Y0)*t);
            internal Curve2 Piece(double a,double b)
            {
                var c=(Curve2)MemberwiseClone();
                if(Circular) { c.Start=Start+Sweep*a; c.Sweep=Sweep*(b-a); }
                else { var p=At(a);var q=At(b);c.X0=p.X;c.Y0=p.Y;c.X1=q.X;c.Y1=q.Y; }
                return c;
            }
        }
        public sealed class SectionArea
        {
            public Guid InstanceId { get; set; }
            // Outer loop first, followed by holes. Separate connected areas are separate entries.
            public List<List<Curve2>> Loops { get; set; } = new List<List<Curve2>>();
        }
        public sealed class Result
        {
            public List<Curve2> Curves { get; set; } = new List<Curve2>();
            public List<SectionArea> Sections { get; set; } = new List<SectionArea>();
        }
        sealed class Body
        {
            internal Guid Id;
            internal bool Pipe;
            internal int Axis;
            internal double[] Min,Max,Center;
            internal double Outer,Inner;
            // Closest material along a ray. The hollow core is genuinely empty.
            internal double? Front(Point2 p,bool cut)
            {
                var a=new[]{p.X,p.Y,0.0};
                if(!Pipe)
                {
                    if(a[0]<Min[0]-Eps||a[0]>Max[0]+Eps||a[1]<Min[1]-Eps||a[1]>Max[1]+Eps)return null;
                    double z=cut?Math.Min(0,Max[2]):Max[2];
                    return z<Min[2]-Eps?(double?)null:z;
                }
                if(Axis==2)
                {
                    double r2=Sq(p.X-Center[0])+Sq(p.Y-Center[1]);
                    if(r2>Sq(Outer)+Eps||r2<Sq(Inner)-Eps)return null;
                    double z=cut?Math.Min(0,Max[2]):Max[2];
                    return z<Min[2]-Eps?(double?)null:z;
                }
                int radial=1-Axis;
                if(a[Axis]<Min[Axis]-Eps||a[Axis]>Max[Axis]+Eps)return null;
                double q=Sq(a[radial]-Center[radial]);
                if(q>Sq(Outer)+Eps)return null;
                double ro=Math.Sqrt(Math.Max(0,Sq(Outer)-q));
                double lo=Center[2]-ro,hi=Center[2]+ro;
                double top=cut?Math.Min(0,hi):hi;
                if(top<lo-Eps)return null;
                if(q<Sq(Inner))
                {
                    double ri=Math.Sqrt(Sq(Inner)-q),holeLo=Center[2]-ri,holeHi=Center[2]+ri;
                    if(top>holeLo+Eps&&top<holeHi-Eps)top=holeLo;
                }
                return top;
            }
        }
        sealed class Edge
        {
            internal Curve2 Curve;
            internal Body Body;
            internal Func<Point2,double> Depth;
        }
        static double Sq(double x)=>x*x;
        static Curve2 Line(double x,double y,double u,double v)=>new Curve2{X0=x,Y0=y,X1=u,Y1=v};
        static Curve2 Circle(double x,double y,double r)=>new Curve2{Circular=true,X0=x,Y0=y,Radius=r,Start=0,Sweep=Tau};
        static Vector3 Sub(Vector3 a,Vector3 b)=>new Vector3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        static double[] InFrame(Vector3 p,RigidPlacement view,bool vector=false)
        {
            var d=vector?p:Sub(p,view.Origin);
            return new[]{d.Dot(view.AxisX),d.Dot(view.AxisY),d.Dot(view.AxisX.Cross(view.AxisY))};
        }
        static int Axis(double[] v)
        {
            int i=Array.FindIndex(v,x=>Math.Abs(Math.Abs(x)-1)<1e-9);
            if(i<0||Enumerable.Range(0,3).Any(j=>j!=i&&Math.Abs(v[j])>1e-9))
                throw new InvalidDataException("Прототип видов: оси деталей должны быть параллельны осям вида. Наклонные положения пока не поддержаны.");
            return i;
        }
        static List<Body> Bodies(AssemblyMark mark,RigidPlacement view)
        {
            mark.Validate();view.Validate();var result=new List<Body>();
            foreach(var instance in mark.Instances)
            {
                var p=mark.Parts.Single(x=>x.Id==instance.PartId);var place=instance.Placement;
                var dirs=new[]{InFrame(place.AxisX,view,true),InFrame(place.AxisY,view,true),InFrame(place.AxisX.Cross(place.AxisY),view,true)};
                var axes=p.Kind==PartKind.Pipe?new[]{0,0,Axis(dirs[2])}:dirs.Select(Axis).ToArray();var origin=InFrame(place.Origin,view);
                var b=new Body{Id=instance.Id,Min=(double[])origin.Clone(),Max=(double[])origin.Clone(),Center=(double[])origin.Clone()};
                if(p.Kind==PartKind.RectangularPlate)
                {
                    var size=new[]{p.Plate.Length,p.Plate.Width,p.Plate.Thickness};
                    for(int k=0;k<3;k++)
                    {
                        int a=axes[k];double end=origin[a]+dirs[k][a]*size[k];b.Min[a]=Math.Min(origin[a],end);b.Max[a]=Math.Max(origin[a],end);
                    }
                }
                else if(p.Kind==PartKind.Pipe)
                {
                    b.Pipe=true;b.Axis=axes[2];b.Outer=p.Pipe.OuterDiameter/2;b.Inner=b.Outer-p.Pipe.Thickness;
                    for(int k=0;k<3;k++)if(k!=b.Axis){b.Min[k]-=b.Outer;b.Max[k]+=b.Outer;}
                    double end=origin[b.Axis]+dirs[2][b.Axis]*p.Pipe.Length;
                    b.Min[b.Axis]=Math.Min(origin[b.Axis],end);b.Max[b.Axis]=Math.Max(origin[b.Axis],end);
                }
                else throw new InvalidDataException("Прототип видов поддерживает только трубы и прямоугольные пластины. Не поддержана деталь: "+p.Name);
                result.Add(b);
            }
            for(int i=0;i<result.Count;i++)for(int j=0;j<i;j++)
                if(Overlap(result[i],result[j]))throw new InvalidDataException("Прототип видов: взаимное проникновение деталей (или пересечение габаритов разнонаправленных труб) пока не поддержано.");
            return result;
        }
        static bool Overlap(Body a,Body b)
        {
            if(Enumerable.Range(0,3).Any(k=>Math.Min(a.Max[k],b.Max[k])-Math.Max(a.Min[k],b.Min[k])<=Eps))return false;
            if(!a.Pipe&&!b.Pipe)return true;
            if(!a.Pipe){var t=a;a=b;b=t;}
            var radial=Enumerable.Range(0,3).Where(k=>k!=a.Axis).ToArray();
            if(!b.Pipe)
            {
                double min=0,max=0;
                foreach(int k in radial)
                {
                    double q=a.Center[k],near=Math.Max(b.Min[k]-q,Math.Max(0,q-b.Max[k]));
                    min+=Sq(near);max+=Math.Max(Sq(b.Min[k]-q),Sq(b.Max[k]-q));
                }
                return min<Sq(a.Outer-Eps)&&max>Sq(a.Inner+Eps);
            }
            if(a.Axis!=b.Axis)return true; // conservative, explicit unsupported case
            double d=Math.Sqrt(radial.Sum(k=>Sq(a.Center[k]-b.Center[k])));
            return d<a.Outer+b.Outer-Eps&&d+a.Outer>b.Inner+Eps&&d+b.Outer>a.Inner+Eps;
        }
        static List<Curve2> Rectangle(double x,double y,double u,double v)=>new List<Curve2>{Line(x,y,u,y),Line(u,y,u,v),Line(u,v,x,v),Line(x,v,x,y)};
        static void Add(List<Edge> edges,Body b,Curve2 c,Func<Point2,double> depth,bool cut=false)
        {
            if(!c.Circular&&Sq(c.X1-c.X0)+Sq(c.Y1-c.Y0)<=Sq(Eps))return;
            c.InstanceId=b.Id;c.Cut=cut;edges.Add(new Edge{Curve=c,Body=b,Depth=depth});
        }
        static void Candidates(Body b,bool cut,List<Edge> edges)
        {
            if(!b.Pipe)
            {
                // Project all twelve box edges, retaining their actual depth.
                for(int axis=0;axis<3;axis++)for(int a=0;a<2;a++)for(int c=0;c<2;c++)
                {
                    var others=Enumerable.Range(0,3).Where(k=>k!=axis).ToArray();
                    var p=(double[])b.Min.Clone();p[others[0]]=a==0?b.Min[others[0]]:b.Max[others[0]];p[others[1]]=c==0?b.Min[others[1]]:b.Max[others[1]];
                    var q=(double[])p.Clone();q[axis]=b.Max[axis];double z=p[2];
                    if(axis!=2&&(!cut||z<=Eps))Add(edges,b,Line(p[0],p[1],q[0],q[1]),_=>z);
                }
            }
            else if(b.Axis==2)
            {
                double z=cut?Math.Min(0,b.Max[2]):b.Max[2];
                if(z<b.Min[2]-Eps)return;
                Add(edges,b,Circle(b.Center[0],b.Center[1],b.Outer),_=>z);
                Add(edges,b,Circle(b.Center[0],b.Center[1],b.Inner),_=>z);
            }
            else
            {
                int a=b.Axis,r=1-a;double z=b.Center[2];
                foreach(double radius in new[]{b.Outer,b.Inner})
                    foreach(int sign in new[]{-1,1})
                    {
                        var p=(double[])b.Center.Clone();var q=(double[])p.Clone();p[a]=b.Min[a];q[a]=b.Max[a];p[r]=q[r]=b.Center[r]+sign*radius;
                        if(!cut||z<=Eps)Add(edges,b,Line(p[0],p[1],q[0],q[1]),_=>z);
                    }
                foreach(double end in new[]{b.Min[a],b.Max[a]})
                {
                    var p=(double[])b.Center.Clone();var q=(double[])p.Clone();p[a]=q[a]=end;p[r]-=b.Outer;q[r]+=b.Outer;
                    Add(edges,b,Line(p[0],p[1],q[0],q[1]),point=>b.Front(point,cut)??double.PositiveInfinity);
                }
            }
        }
        static List<SectionArea> Sections(Body b)
        {
            var result=new List<SectionArea>();
            if(b.Min[2]>=-Eps||b.Max[2]<=Eps)return result; // tangent/coincident face is not a material cut
            if(!b.Pipe)result.Add(new SectionArea{InstanceId=b.Id,Loops=new List<List<Curve2>>{Rectangle(b.Min[0],b.Min[1],b.Max[0],b.Max[1])}});
            else if(b.Axis==2)result.Add(new SectionArea{InstanceId=b.Id,Loops=new List<List<Curve2>>{
                new List<Curve2>{Circle(b.Center[0],b.Center[1],b.Outer)},new List<Curve2>{Circle(b.Center[0],b.Center[1],b.Inner)}}});
            else
            {
                double h=Sq(b.Center[2]);double outer=Math.Sqrt(Math.Max(0,Sq(b.Outer)-h));
                var intervals=h>=Sq(b.Inner)?new[]{new[]{-outer,outer}}:new[]{new[]{-outer,-Math.Sqrt(Sq(b.Inner)-h)},new[]{Math.Sqrt(Sq(b.Inner)-h),outer}};
                foreach(var interval in intervals)
                {
                    int a=b.Axis,r=1-a;var p=(double[])b.Min.Clone();var q=(double[])b.Max.Clone();
                    p[r]=b.Center[r]+interval[0];q[r]=b.Center[r]+interval[1];
                    result.Add(new SectionArea{InstanceId=b.Id,Loops=new List<List<Curve2>>{Rectangle(p[0],p[1],q[0],q[1])}});
                }
            }
            foreach(var area in result)foreach(var loop in area.Loops)foreach(var c in loop){c.Cut=true;c.InstanceId=b.Id;}
            return result;
        }
        static double Cross(Point2 a,Point2 b)=>a.X*b.Y-a.Y*b.X;
        static Point2 Minus(Point2 a,Point2 b)=>new Point2(a.X-b.X,a.Y-b.Y);
        static IEnumerable<Point2> Intersections(Curve2 a,Curve2 b)
        {
            if(!a.Circular&&!b.Circular)
            {
                var p=a.At(0);var q=b.At(0);var r=Minus(a.At(1),p);var s=Minus(b.At(1),q);double d=Cross(r,s);
                if(Math.Abs(d)>Eps)
                {
                    double t=Cross(Minus(q,p),s)/d,u=Cross(Minus(q,p),r)/d;
                    if(t>=-Eps&&t<=1+Eps&&u>=-Eps&&u<=1+Eps)yield return a.At(t);
                }
                else if(Math.Abs(Cross(Minus(q,p),r))<=Eps*Math.Sqrt(Sq(r.X)+Sq(r.Y)))
                    foreach(var endpoint in new[]{a.At(0),a.At(1),b.At(0),b.At(1)})yield return endpoint;
                yield break;
            }
            if(!a.Circular){foreach(var p in Intersections(b,a))yield return p;yield break;}
            if(!b.Circular)
            {
                var p=b.At(0);var d=Minus(b.At(1),p);var v=new Point2(p.X-a.X0,p.Y-a.Y0);
                double aa=Sq(d.X)+Sq(d.Y),bb=2*(v.X*d.X+v.Y*d.Y),cc=Sq(v.X)+Sq(v.Y)-Sq(a.Radius),disc=bb*bb-4*aa*cc;
                if(disc< -Eps)yield break;
                foreach(double t in new[]{(-bb-Math.Sqrt(Math.Max(0,disc)))/(2*aa),(-bb+Math.Sqrt(Math.Max(0,disc)))/(2*aa)})
                    if(t>=-Eps&&t<=1+Eps)yield return b.At(t);
                yield break;
            }
            double dx=b.X0-a.X0,dy=b.Y0-a.Y0,dist=Math.Sqrt(Sq(dx)+Sq(dy));
            if(dist<Eps||dist>a.Radius+b.Radius+Eps||dist<Math.Abs(a.Radius-b.Radius)-Eps)yield break;
            double x=(Sq(a.Radius)-Sq(b.Radius)+Sq(dist))/(2*dist),h=Math.Sqrt(Math.Max(0,Sq(a.Radius)-Sq(x)));
            foreach(int sign in new[]{-1,1})yield return new Point2(a.X0+x*dx/dist-sign*h*dy/dist,a.Y0+x*dy/dist+sign*h*dx/dist);
        }
        static IEnumerable<double> Parameters(Curve2 c,Point2 p)
        {
            if(c.Circular)
            {
                if(Math.Abs(Math.Sqrt(Sq(p.X-c.X0)+Sq(p.Y-c.Y0))-c.Radius)>Eps*10)yield break;
                double angle=Math.Atan2(p.Y-c.Y0,p.X-c.X0);if(angle<0)angle+=Tau;
                yield return angle/Tau;
            }
            else
            {
                double dx=c.X1-c.X0,dy=c.Y1-c.Y0,t=((p.X-c.X0)*dx+(p.Y-c.Y0)*dy)/(Sq(dx)+Sq(dy));
                var near=c.At(t);
                if(t>=-Eps&&t<=1+Eps&&Sq(p.X-near.X)+Sq(p.Y-near.Y)<=Sq(Eps*10))yield return Math.Max(0,Math.Min(1,t));
            }
        }
        static string Key(Curve2 c)
        {
            // Direction-independent, geometric deduplication after all boundary splits.
            Func<Point2,string> key=p=>Math.Round(p.X,6).ToString("F6",System.Globalization.CultureInfo.InvariantCulture)+","+Math.Round(p.Y,6).ToString("F6",System.Globalization.CultureInfo.InvariantCulture);
            var a=key(c.At(0));var b=key(c.At(1));if(string.CompareOrdinal(a,b)>0){var t=a;a=b;b=t;}
            return (c.Circular?"C":"L")+a+"/"+b+"/"+key(c.At(.5));
        }
        public static Result Build(AssemblyMark mark,RigidPlacement view,bool section=false,bool showHidden=false)
        {
            mark.Validate();view.Validate();
            bool aligned=true;
            foreach(var instance in mark.Instances)
            {
                var part=mark.Parts.Single(p=>p.Id==instance.PartId);
                if(part.Kind!=PartKind.Pipe&&part.Kind!=PartKind.RectangularPlate){aligned=false;break;}
                try
                {
                    if(part.Kind==PartKind.Pipe)Axis(InFrame(instance.Placement.AxisX.Cross(instance.Placement.AxisY),view,true));
                    else {Axis(InFrame(instance.Placement.AxisX,view,true));Axis(InFrame(instance.Placement.AxisY,view,true));}
                }
                catch(InvalidDataException){aligned=false;break;}
            }
            if(!aligned)return SpatialProjection.Build(mark,view,section,showHidden);
            var bodies=Bodies(mark,view);var edges=new List<Edge>();var result=new Result();
            foreach(var b in bodies)
            {
                Candidates(b,section,edges);
                if(section)foreach(var area in Sections(b))
                {
                    result.Sections.Add(area);
                    foreach(var loop in area.Loops)foreach(var c in loop)Add(edges,b,c,_=>0,true);
                }
            }
            var unique=new Dictionary<string,Curve2>();
            foreach(var edge in edges)
            {
                var cuts=new List<double>{0,1};
                foreach(var other in edges)
                    if(other!=edge)foreach(var p in Intersections(edge.Curve,other.Curve))cuts.AddRange(Parameters(edge.Curve,p));
                var sorted=cuts.OrderBy(t=>t).ToList();var values=new List<double>();
                foreach(double t in sorted)if(values.Count==0||t-values[values.Count-1]>1e-10)values.Add(t);
                for(int i=1;i<values.Count;i++)
                {
                    double a=values[i-1],b=values[i];if(b-a<1e-10)continue;
                    var point=edge.Curve.At((a+b)/2);double z=edge.Depth(point);
                    if(double.IsInfinity(z)||double.IsNaN(z)||(section&&z>Eps))continue;
                    if(!edge.Body.Front(point,section).HasValue)continue;
                    bool hidden=bodies.Any(body=>{var front=body.Front(point,section);return front.HasValue&&front.Value>z+Eps;});
                    if(hidden&&!showHidden)continue;
                    var piece=edge.Curve.Piece(a,b);piece.Hidden=hidden;
                    string key=Key(piece);
                    if(!unique.TryGetValue(key,out var old)||old.Hidden&&!piece.Hidden||old.Hidden==piece.Hidden&&piece.Cut&&!old.Cut)unique[key]=piece;
                }
            }
            result.Curves=unique.Values.ToList();return result;
        }
    }
}
