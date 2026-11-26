using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    // Shared spatial boundary pipeline for extruded profiles. No AutoCAD dependency.
    // Curved profiles are faceted with a bounded sagitta; planar profiles remain exact.
    internal static class SpatialProjection
    {
        const double E = 1e-7, Join = 1e-5, Sagitta = .05;
        const double Tau = 2*Math.PI;
        sealed class Segment
        {
            internal Vector3 A,B;
            internal Guid Id;
            internal bool Cut;
            internal string SourceKey;
            internal double SourceFrom,SourceTo=1;
            internal Segment(Vector3 a,Vector3 b,Guid id,bool cut=false){A=a;B=b;Id=id;Cut=cut;}
            internal Vector3 At(double t)=>Add(A,Mul(Sub(B,A),t));
        }
        sealed class Face
        {
            internal List<List<Vector3>> Rings;
            internal Vector3 Normal,Origin;
            internal double MinX,MaxX,MinY,MaxY;
            internal string CutKey;
            internal double CutFrom,CutTo=1;
            internal Face(params List<Vector3>[] rings)
            {
                Rings=rings.ToList();Origin=rings[0][0];
                Normal=Sub(rings[0][1],Origin).Cross(Sub(rings[0][2],Origin));
                for(int i=2;i<rings[0].Count&&Normal.Dot(Normal)<E*E;i++)
                    Normal=Sub(rings[0][1],Origin).Cross(Sub(rings[0][i],Origin));
                var all=Rings.SelectMany(r=>r).ToList();
                MinX=all.Min(p=>p.X);MaxX=all.Max(p=>p.X);MinY=all.Min(p=>p.Y);MaxY=all.Max(p=>p.Y);
            }
            internal double? Depth(Vector3 p)
            {
                if(p.X<MinX-E||p.X>MaxX+E||p.Y<MinY-E||p.Y>MaxY+E||Math.Abs(Normal.Z)<=E*Math.Sqrt(Normal.Dot(Normal)))return null;
                if(!Inside(p,Rings[0],true)||Rings.Skip(1).Any(r=>Inside(p,r,false)))return null;
                return Origin.Z-(Normal.X*(p.X-Origin.X)+Normal.Y*(p.Y-Origin.Y))/Normal.Z;
            }
        }
        sealed class Profile
        {
            internal List<List<Vector3>> Rings=new List<List<Vector3>>();
            // Sharp vertices, where an axial edge is a real crease rather than a facet seam.
            internal List<HashSet<int>> Sharp=new List<HashSet<int>>();
            internal double Length;
            internal void Add(List<Vector3> ring,IEnumerable<int> sharp=null)
            {
                Rings.Add(ring);Sharp.Add(new HashSet<int>(sharp??Enumerable.Range(0,ring.Count)));
            }
        }
        static Vector3 Add(Vector3 a,Vector3 b)=>new Vector3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        static Vector3 Sub(Vector3 a,Vector3 b)=>new Vector3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        static Vector3 Mul(Vector3 a,double t)=>new Vector3(a.X*t,a.Y*t,a.Z*t);
        static Vector3 P(double x,double y)=>new Vector3(x,y,0);
        static double Cross(Vector3 a,Vector3 b)=>a.X*b.Y-a.Y*b.X;
        static double Dist2(Vector3 a,Vector3 b){var d=Sub(a,b);return d.Dot(d);}
        static double Len2(Vector3 a,Vector3 b)=>Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2);
        static List<Vector3> Points(params double[] xy)=>Enumerable.Range(0,xy.Length/2).Select(i=>P(xy[2*i],xy[2*i+1])).ToList();
        static int Steps(double radius,double angle)
        {
            double step=2*Math.Acos(Math.Max(-1,1-Sagitta/radius));
            double count=Math.Max(Math.Ceiling(angle/Math.Max(step,1e-12)),Math.Ceiling(angle/(Math.PI/12)));
            if(count>2048)throw new InvalidDataException("Радиус слишком велик для заданной точности проекции.");
            return Math.Max(1,(int)count);
        }
        static List<Vector3> Arc(double r,double angle,bool full,double x=0,double y=0)
        {
            int n=Steps(r,angle);
            return Enumerable.Range(0,full?n:n+1).Select(i=>P(x+r*Math.Cos(angle*i/n),y+r*Math.Sin(angle*i/n))).ToList();
        }
        static Profile Shape(PartDefinition p)
        {
            var q=new Profile();
            switch(p.Kind)
            {
                case PartKind.RectangularPlate:
                    q.Length=p.Plate.Thickness;q.Add(Points(0,0,p.Plate.Length,0,p.Plate.Length,p.Plate.Width,0,p.Plate.Width));break;
                case PartKind.Pipe:
                    q.Length=p.Pipe.Length;double r=p.Pipe.OuterDiameter/2;
                    q.Add(Arc(r,Tau,true),new int[0]);q.Add(Arc(r-p.Pipe.Thickness,Tau,true),new int[0]);break;
                case PartKind.CurvedPlate:
                    var c=p.CurvedPlate;q.Length=c.Length;
                    bool full=Math.Abs(c.AngleRadians-Tau)<1e-10;
                    var outer=Arc(c.InnerRadius+c.Thickness,c.AngleRadians,full);
                    var inner=Arc(c.InnerRadius,c.AngleRadians,full);
                    if(full){q.Add(outer,new int[0]);q.Add(inner,new int[0]);}
                    else {int n=outer.Count;inner.Reverse();outer.AddRange(inner);q.Add(outer,new[]{0,n-1,n,outer.Count-1});}
                    break;
                case PartKind.PolygonPlate:
                    q.Length=p.PolygonPlate.Thickness;
                    q.Add(p.PolygonPlate.Outer.Select(a=>P(a.X,a.Y)).ToList());
                    foreach(var h in p.PolygonPlate.Holes)q.Add(h.Select(a=>P(a.X,a.Y)).ToList());
                    foreach(var h in p.PolygonPlate.RoundHoles)q.Add(Arc(h.Radius,Tau,true,h.Center.X,h.Center.Y),new int[0]);
                    break;
                case PartKind.IBeam:
                    var b=p.IBeam;
                    if(b.RootRadius>0)throw new InvalidDataException("Пространственная проекция двутавра с радиусами сопряжения ещё не реализована: "+p.Name);
                    double w=b.Width/2,h2=b.Height/2,t=b.WebThickness/2,f=b.FlangeThickness;
                    q.Length=b.Length;q.Add(Points(-w,-h2,w,-h2,w,-h2+f,t,-h2+f,t,h2-f,w,h2-f,w,h2,-w,h2,-w,h2-f,-t,h2-f,-t,-h2+f,-w,-h2+f));break;
                default:throw new InvalidDataException("Пространственная проекция пока не поддерживает деталь: "+p.Name);
            }
            return q;
        }
        static Vector3 View(Vector3 p,RigidPlacement frame)
        {
            var d=Sub(p,frame.Origin);return new Vector3(d.Dot(frame.AxisX),d.Dot(frame.AxisY),d.Dot(frame.AxisX.Cross(frame.AxisY)));
        }
        static bool Inside(Vector3 p,List<Vector3> ring,bool boundary)
        {
            bool inside=false;
            for(int i=0;i<ring.Count;i++)
            {
                var a=ring[i];var b=ring[(i+1)%ring.Count];var d=Sub(b,a);double len=Len2(a,b);
                if(len>E*E)
                {
                    double t=((p.X-a.X)*d.X+(p.Y-a.Y)*d.Y)/len;
                    if(t>=-E&&t<=1+E&&Math.Abs(Cross(d,Sub(p,a)))<=E*Math.Sqrt(len))return boundary;
                }
                if((a.Y>p.Y)!=(b.Y>p.Y)&&p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
            }
            return inside;
        }
        static void AddEdge(List<Segment> list,Vector3 a,Vector3 b,Guid id,bool section,string key=null,double from=0,double to=1)
        {
            double t0=0,t1=1;
            if(section)
            {
                if(a.Z>E&&b.Z>E)return;
                if(a.Z>E){t0=a.Z/(a.Z-b.Z);a=Add(a,Mul(Sub(b,a),t0));}
                else if(b.Z>E){t1=a.Z/(a.Z-b.Z);b=Add(a,Mul(Sub(b,a),t1));}
            }
            if(Len2(a,b)>E*E)list.Add(new Segment(a,b,id){SourceKey=key,SourceFrom=from+(to-from)*t0,SourceTo=from+(to-from)*t1});
        }
        static IEnumerable<Segment> CutFace(Face face,Guid id)
        {
            var hits=new List<Vector3>();
            foreach(var ring in face.Rings)
                for(int i=0;i<ring.Count;i++)
                {
                    var a=ring[i];var b=ring[(i+1)%ring.Count];
                    if(Math.Abs(a.Z)<=E)hits.Add(P(a.X,a.Y));
                    if(a.Z*b.Z<0&&Math.Abs(a.Z)>E&&Math.Abs(b.Z)>E)
                    {var p=Add(a,Mul(Sub(b,a),a.Z/(a.Z-b.Z)));hits.Add(P(p.X,p.Y));}
                }
            if(hits.Count<2||face.Rings.SelectMany(r=>r).All(p=>Math.Abs(p.Z)<=E))yield break;
            var d=P(-face.Normal.Y,face.Normal.X);
            if(d.Dot(d)<E*E)yield break;
            var ordered=hits.OrderBy(p=>p.Dot(d)).ToList();
            var unique=new List<Vector3>();
            foreach(var p in ordered)if(unique.Count==0||Dist2(p,unique.Last())>Join*Join)unique.Add(p);
            // The intersection line is parameterized on a face-local plane, including holes.
            var n=face.Normal;
            var u=Mul(d,1/Math.Sqrt(d.Dot(d)));var v=n.Cross(u);
            if(v.Dot(v)<E*E)yield break;
            v=Mul(v,1/Math.Sqrt(v.Dot(v)));
            Func<Vector3,Vector3> local=p=>P(Sub(p,face.Origin).Dot(u),Sub(p,face.Origin).Dot(v));
            var rings=face.Rings.Select(r=>r.Select(local).ToList()).ToList();
            var span=Sub(unique.Last(),unique[0]);double totalLen=Math.Sqrt(span.Dot(span));
            for(int i=1;i<unique.Count;i++)
            {
                var a=unique[i-1];var b=unique[i];var m=local(Mul(Add(a,b),.5));
                if(Inside(m,rings[0],true)&&!rings.Skip(1).Any(r=>Inside(m,r,false)))
                {
                    double u0=0,u1=1;
                    if(totalLen>E)
                    {
                        u0=Math.Max(0,Math.Min(1,Sub(a,unique[0]).Dot(span)/(totalLen*totalLen)));
                        u1=Math.Max(0,Math.Min(1,Sub(b,unique[0]).Dot(span)/(totalLen*totalLen)));
                    }
                    double f0=face.CutFrom+(face.CutTo-face.CutFrom)*u0;
                    double f1=face.CutFrom+(face.CutTo-face.CutFrom)*u1;
                    yield return new Segment(a,b,id,true){SourceKey=face.CutKey,SourceFrom=f0,SourceTo=f1};
                }
            }
        }
        static string Key(Vector3 p)=>Math.Round(p.X/Join).ToString("F0",System.Globalization.CultureInfo.InvariantCulture)+","+Math.Round(p.Y/Join).ToString("F0",System.Globalization.CultureInfo.InvariantCulture);
        static string Key(Segment s)
        {var a=Key(s.A);var b=Key(s.B);return string.CompareOrdinal(a,b)<=0?a+"/"+b:b+"/"+a;}
        static ParametricProjection.Curve2 Curve(Segment s,bool hidden=false)=>new ParametricProjection.Curve2{X0=s.A.X,Y0=s.A.Y,X1=s.B.X,Y1=s.B.Y,InstanceId=s.Id,Cut=s.Cut,Hidden=hidden,SourceKey=s.SourceKey,SourceFrom=s.SourceFrom,SourceTo=s.SourceTo};
        static double Area(List<Vector3> ring)
        {
            var o=ring[0];double a=0;
            for(int i=0;i<ring.Count;i++)a+=Cross(Sub(ring[i],o),Sub(ring[(i+1)%ring.Count],o));
            return Math.Abs(a)/2;
        }
        static void Sections(List<Segment> segments,Guid id,PartDefinition part,ParametricProjection.Result result,List<Face> faces,List<Segment> candidates)
        {
            var pending=segments.GroupBy(Key).Select(g=>g.First()).ToList();
            var rings=new List<Tuple<List<Vector3>,List<Segment>>>();
            while(pending.Count>0)
            {
                var s=pending[0];pending.RemoveAt(0);
                var ring=new List<Vector3>{s.A,s.B};
                var chain=new List<Segment>{s};
                while(Dist2(ring[0],ring.Last())>Join*Join)
                {
                    int k=pending.FindIndex(e=>Dist2(e.A,ring.Last())<=Join*Join||Dist2(e.B,ring.Last())<=Join*Join);
                    if(k<0)throw new InvalidDataException("Не удалось замкнуть пространственный контур разреза.");
                    var e=pending[k];pending.RemoveAt(k);
                    bool direct=Dist2(e.A,ring.Last())<=Join*Join;
                    var oriented=direct
                        ? new Segment(e.A,e.B,e.Id,true){SourceKey=e.SourceKey,SourceFrom=e.SourceFrom,SourceTo=e.SourceTo}
                        : new Segment(e.B,e.A,e.Id,true){SourceKey=e.SourceKey,SourceFrom=e.SourceTo,SourceTo=e.SourceFrom};
                    ring.Add(oriented.B);
                    chain.Add(oriented);
                }
                ring.RemoveAt(ring.Count-1);
                if(ring.Count>=3&&Area(ring)>Join*Join)rings.Add(Tuple.Create(ring,chain));
            }
            var ordered=rings.OrderByDescending(r=>Area(r.Item1)).ToList();
            var parent=new int[ordered.Count];var depth=new int[ordered.Count];
            var areas=new Dictionary<int,ParametricProjection.SectionArea>();
            for(int i=0;i<ordered.Count;i++)
            {
                parent[i]=-1;
                for(int j=i-1;j>=0;j--)if(Inside(ordered[i].Item1[0],ordered[j].Item1,false)){parent[i]=j;break;}
                depth[i]=parent[i]<0?0:depth[parent[i]]+1;
                var ring=ordered[i].Item1;var srcChain=ordered[i].Item2;
                var edges=Enumerable.Range(0,ring.Count).Select(k=>{
                    var orig=k<srcChain.Count?srcChain[k]:null;
                    string sk=orig!=null&&!string.IsNullOrEmpty(orig.SourceKey)?orig.SourceKey:Source(part,"cut:loop:"+depth[i]+":"+i+":"+k);
                    double sf=orig!=null&&!string.IsNullOrEmpty(orig.SourceKey)?orig.SourceFrom:0;
                    double st=orig!=null&&!string.IsNullOrEmpty(orig.SourceKey)?orig.SourceTo:1;
                    if(Math.Abs(st-sf)<=1e-10){sf=0;st=1;}
                    return new Segment(ring[k],ring[(k+1)%ring.Count],id,true){SourceKey=sk,SourceFrom=sf,SourceTo=st};
                }).ToList();
                candidates.AddRange(edges);
                if(depth[i]%2==0)
                {
                    var a=new ParametricProjection.SectionArea{InstanceId=id};
                    a.Loops.Add(edges.Select(e=>Curve(e)).ToList());areas.Add(i,a);result.Sections.Add(a);
                }
                else areas[parent[i]].Loops.Add(edges.Select(e=>Curve(e)).ToList());
            }
            foreach(var a in areas.Values)
                faces.Add(new Face(a.Loops.Select(l=>l.Select(c=>P(c.X0,c.Y0)).ToList()).ToArray()));
        }
        static void Splits(Segment s,Vector3 a,Vector3 b,List<double> cuts)
        {
            var r=Sub(s.B,s.A);var d=Sub(b,a);var q=Sub(a,s.A);double den=Cross(r,d),len=Len2(s.A,s.B);
            if(Math.Abs(den)>E*Math.Sqrt(len*Len2(a,b)))
            {
                double t=Cross(q,d)/den,u=Cross(q,r)/den;
                if(t>0&&t<1&&u>=-E&&u<=1+E)cuts.Add(t);
            }
            else if(Math.Abs(Cross(q,r))<=E*Math.Sqrt(len))
                foreach(var p in new[]{a,b})
                {double t=((p.X-s.A.X)*r.X+(p.Y-s.A.Y)*r.Y)/len;if(t>0&&t<1)cuts.Add(t);}
        }
        static bool Overlaps(Segment s,Face f)=>Math.Max(s.A.X,s.B.X)>=f.MinX-E&&Math.Min(s.A.X,s.B.X)<=f.MaxX+E&&Math.Max(s.A.Y,s.B.Y)>=f.MinY-E&&Math.Min(s.A.Y,s.B.Y)<=f.MaxY+E;
        // Stable physical contour roles. Facet count is not identity.
        static void Rim(PartDefinition p,Profile profile,int ring,int j,out string key,out double from,out double to)
        {
            key=null;from=0;to=1;
            int n=profile.Rings[ring].Count;
            if(p.Kind==PartKind.RectangularPlate||p.Kind==PartKind.IBeam)
            {key="edge:"+j;return;}
            if(p.Kind==PartKind.Pipe||(p.Kind==PartKind.CurvedPlate&&profile.Rings.Count==2))
            {
                key="rim:"+ring;
                from=(double)j/n;to=(double)(j+1)/n;return;
            }
            if(p.Kind==PartKind.CurvedPlate)
            {
                int outer=Steps(p.CurvedPlate.InnerRadius+p.CurvedPlate.Thickness,p.CurvedPlate.AngleRadians);
                int inner=Steps(p.CurvedPlate.InnerRadius,p.CurvedPlate.AngleRadians);
                if(j<outer){key="outer";from=(double)j/outer;to=(double)(j+1)/outer;}
                else if(j==outer)key="radial-end";
                else if(j<n-1){key="inner";int k=j-outer-1;from=1-(double)k/inner;to=1-(double)(k+1)/inner;}
                else key="radial-start";
                return;
            }
            if(p.Kind==PartKind.PolygonPlate)
            {
                if(ring==0){key="outer:"+j;return;}
                int polyHoles=p.PolygonPlate.Holes.Count;
                if(ring<=polyHoles){key="hole:"+(ring-1)+":"+j;return;}
                int roundHole=ring-1-polyHoles;
                key="roundhole:"+roundHole;
                from=(double)j/n;to=(double)(j+1)/n;
            }
        }
        static string SideTag(Vector3 bottomPt,Vector3 topPt,Vector3 bottomCenter,Vector3 topCenter,int fallbackIndex)
        {
            var axis=Sub(topCenter,bottomCenter);
            double axLen=Math.Sqrt(axis.X*axis.X+axis.Y*axis.Y);
            if(axLen>E)
            {
                double px=-axis.Y/axLen,py=axis.X/axLen;
                var mid=Mul(Add(bottomPt,topPt),.5);
                var midAxis=Mul(Add(bottomCenter,topCenter),.5);
                double sideVal=(mid.X-midAxis.X)*px+(mid.Y-midAxis.Y)*py;
                return sideVal>=0?"pos":"neg";
            }
            return fallbackIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        static Vector3 RingCenter(List<Vector3> ring)
        {
            double x=0,y=0,z=0;
            for(int i=0;i<ring.Count;i++){x+=ring[i].X;y+=ring[i].Y;z+=ring[i].Z;}
            return new Vector3(x/ring.Count,y/ring.Count,z/ring.Count);
        }
        static string AxialKey(PartDefinition p,Profile profile,int ring,int j,Vector3 bottomPt,Vector3 topPt,Vector3 bottomCenter,Vector3 topCenter)
        {
            if(p.Kind==PartKind.RectangularPlate||p.Kind==PartKind.IBeam)return "vertex:"+j;
            if(p.Kind==PartKind.Pipe||(p.Kind==PartKind.CurvedPlate&&profile.Rings.Count==2))
                return "sil:"+ring+":"+SideTag(bottomPt,topPt,bottomCenter,topCenter,j);
            if(p.Kind==PartKind.CurvedPlate&&profile.Rings.Count==1)
            {
                int outer=Steps(p.CurvedPlate.InnerRadius+p.CurvedPlate.Thickness,p.CurvedPlate.AngleRadians);
                if(j==0)return "outer-start";
                if(j==outer)return "outer-end";
                if(j==outer+1)return "inner-end";
                if(j==profile.Rings[ring].Count-1)return "inner-start";
                string arcRole=j<outer?"outer":"inner";
                return "sil:"+arcRole+":"+SideTag(bottomPt,topPt,bottomCenter,topCenter,j);
            }
            if(p.Kind==PartKind.PolygonPlate)
            {
                if(ring==0)return "outer-v:"+j;
                int polyHoles=p.PolygonPlate.Holes.Count;
                if(ring<=polyHoles)return "hole-v:"+(ring-1)+":"+j;
                int roundHole=ring-1-polyHoles;
                return "roundhole-sil:"+roundHole+":"+SideTag(bottomPt,topPt,bottomCenter,topCenter,j);
            }
            return "axial:"+ring+":"+j;
        }
        static string Source(PartDefinition p,string role)=>role==null?null:"spatial:"+p.Kind+":"+role;
        internal static ParametricProjection.Result Build(AssemblyMark mark,RigidPlacement frame,bool section,bool showHidden)
        {
            mark.Validate();frame.Validate();
            var faces=new List<Face>();var candidates=new List<Segment>();var result=new ParametricProjection.Result();
            foreach(var i in mark.Instances)
            {
                var p=mark.Parts.Single(x=>x.Id==i.PartId);var profile=Shape(p);
                Func<Vector3,double,Vector3> world=(v,z)=>View(i.Placement.ToWorld(new Vector3(v.X,v.Y,z)),frame);
                var bottom=profile.Rings.Select(r=>r.Select(v=>world(v,0)).ToList()).ToArray();
                var top=profile.Rings.Select(r=>r.Select(v=>world(v,profile.Length)).ToList()).ToArray();
                var own=new List<Face>{
                    new Face(bottom){CutKey=Source(p,"cut:bottom")},
                    new Face(top){CutKey=Source(p,"cut:top")}};
                for(int k=0;k<bottom.Length;k++)
                {
                    int n=bottom[k].Count;var side=new Face[n];
                    var bCenter=RingCenter(bottom[k]);var tCenter=RingCenter(top[k]);
                    for(int j=0;j<n;j++)
                    {
                        int next=(j+1)%n;
                        Rim(p,profile,k,j,out var rim,out var from,out var to);
                        side[j]=new Face(new List<Vector3>{bottom[k][j],bottom[k][next],top[k][next],top[k][j]})
                        {
                            CutKey=Source(p,rim==null?null:"cut:side:"+rim),CutFrom=from,CutTo=to
                        };
                        own.Add(side[j]);
                        AddEdge(candidates,bottom[k][j],bottom[k][next],i.Id,section,Source(p,rim==null?null:"bottom:"+rim),from,to);
                        AddEdge(candidates,top[k][j],top[k][next],i.Id,section,Source(p,rim==null?null:"top:"+rim),from,to);
                    }
                    for(int j=0;j<n;j++)
                    {
                        var a=side[(j+n-1)%n].Normal;var b=side[j].Normal;
                        double az=a.Z/Math.Sqrt(a.Dot(a)),bz=b.Z/Math.Sqrt(b.Dot(b));
                        if(profile.Sharp[k].Contains(j)||(az> E&&bz<=E)||(bz>E&&az<=E)||(az< -E&&bz>=-E)||(bz< -E&&az>=-E))
                            AddEdge(candidates,bottom[k][j],top[k][j],i.Id,section,Source(p,AxialKey(p,profile,k,j,bottom[k][j],top[k][j],bCenter,tCenter)));
                    }
                }
                faces.AddRange(own);
                var all=bottom.Concat(top).SelectMany(r=>r).ToList();
                if(section&&all.Min(v=>v.Z)<-E&&all.Max(v=>v.Z)>E)
                    Sections(own.SelectMany(f=>CutFace(f,i.Id)).ToList(),i.Id,p,result,faces,candidates);
            }
            var unique=new Dictionary<string,ParametricProjection.Curve2>();
            foreach(var s in candidates)
            {
                var near=faces.Where(f=>Overlaps(s,f)).ToList();var cuts=new List<double>{0,1};
                foreach(var f in near)
                {
                    foreach(var ring in f.Rings)for(int k=0;k<ring.Count;k++)Splits(s,ring[k],ring[(k+1)%ring.Count],cuts);
                    double a=Sub(s.A,f.Origin).Dot(f.Normal),b=Sub(s.B,f.Origin).Dot(f.Normal);
                    if(a*b<0)cuts.Add(a/(a-b));
                }
                if(section)foreach(var c in candidates.Where(e=>e.Cut))Splits(s,c.A,c.B,cuts);
                cuts.Sort();
                for(int k=1;k<cuts.Count;k++)
                {
                    double a=cuts[k-1],b=cuts[k];if((b-a)*Math.Sqrt(Len2(s.A,s.B))<=Join)continue;
                    var m=s.At((a+b)/2);
                    bool hidden=near.Any(f=>{var z=f.Depth(m);return z.HasValue&&(!section||z.Value<=E)&&z.Value>m.Z+E;});
                    if(hidden&&!showHidden)continue;
                    var piece=new Segment(s.At(a),s.At(b),s.Id,s.Cut){SourceKey=s.SourceKey,SourceFrom=s.SourceFrom+(s.SourceTo-s.SourceFrom)*a,SourceTo=s.SourceFrom+(s.SourceTo-s.SourceFrom)*b};var key=Key(piece);var c=Curve(piece,hidden);
                    if(!unique.TryGetValue(key,out var old)||old.Hidden&&!hidden||old.Hidden==hidden&&c.Cut&&!old.Cut)unique[key]=c;
                }
            }
            result.Curves=unique.Values.ToList();return result;
        }
    }
}
