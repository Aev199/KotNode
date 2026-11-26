using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace KotNode.Core
{
    public static class ViewReferenceMigration
    {
        const double Tol=0.11;
        sealed class Span
        {
            public ParametricProjection.Curve2 Old,New;
            public double OldFrom,OldTo;
        }
        static bool Legacy(string key)=>key!=null&&key.StartsWith("analytic:",StringComparison.Ordinal);
        public static bool Needed(IEnumerable<ViewLineOverride> lines,IEnumerable<ViewWeldOverlay> welds,IEnumerable<ViewAnchor> anchors)
            =>(lines??Enumerable.Empty<ViewLineOverride>()).Any(x=>Legacy(x.SourceKey))
                ||(welds??Enumerable.Empty<ViewWeldOverlay>()).Any(x=>Legacy(x.SourceKey))
                ||(anchors??Enumerable.Empty<ViewAnchor>()).Any(x=>x!=null&&Legacy(x.SourceKey));
        static InvalidDataException Failure()=>new InvalidDataException("Не удалось однозначно перенести старые аналитические привязки. Обновите исходный вид до изменения его плоскости/положения деталей; исходное оформление сохранено.");
        static double OldParam(ParametricProjection.Curve2 old,Point2 p)
        {
            if(old.Circular)
            {
                double angle=Math.Atan2(p.Y-old.Y0,p.X-old.X0);
                return (angle<0?angle+2*Math.PI:angle)/(2*Math.PI);
            }
            double dx=old.X1-old.X0,dy=old.Y1-old.Y0;
            double t=((p.X-old.X0)*dx+(p.Y-old.Y0)*dy)/(dx*dx+dy*dy);
            return old.SourceFrom+(old.SourceTo-old.SourceFrom)*t;
        }

        static List<Span> Match(Guid instance,string key,IList<ParametricProjection.Curve2> oldCurves,IList<ParametricProjection.Curve2> newCurves)
        {
            var old=oldCurves.Where(x=>x.InstanceId==instance&&x.SourceKey==key).ToList();
            if(old.Count==0)throw Failure();
            var spans=new List<Span>();
            var candidates=newCurves.Where(x=>x.InstanceId==instance&&ViewLineOverrides.HasSource(x))
                .SelectMany(c=>c.Circular?Enumerable.Range(0,24).Select(i=>c.Piece((double)i/24,(double)(i+1)/24)):new[]{c});
            foreach(var c in candidates)
            {
                var samples=new[]{c.At(0),c.At(.5),c.At(1)};
                if(!samples.All(p=>old.Any(x=>ViewLineOverrides.DistanceToCurve(x,p.X,p.Y,out _,out _,out _)<=Tol)))continue;
                var source=old.OrderBy(x=>ViewLineOverrides.DistanceToCurve(x,samples[1].X,samples[1].Y,out _,out _,out _)).First();
                double a=OldParam(source,samples[0]),b=OldParam(source,samples[2]);
                if(source.Circular)
                {
                    double mid=OldParam(source,samples[1]);
                    if(a-mid>.5)a-=1;if(mid-a>.5)a+=1;
                    if(b-mid>.5)b-=1;if(mid-b>.5)b+=1;
                    if(Math.Abs(a)<1e-8)a=0;if(Math.Abs(b-1)<1e-8)b=1;
                }
                if(Math.Abs(a-b)>1e-10)spans.Add(new Span{Old=source,New=c,OldFrom=a,OldTo=b});
            }
            if(spans.Count==0)throw Failure();
            if(old.All(c=>c.Circular))
            {
                // Facet endpoints lie on their physical circle. Distinguish outer
                // and inner rims even when wall thickness is below tessellation
                // tolerance; proximity alone must never select both rings.
                var ranks=spans.GroupBy(s=>s.New.SourceKey).Select(g=>new{
                    Key=g.Key,Error=g.Min(s=>new[]{s.New.At(0),s.New.At(1)}.Sum(p=>
                        Math.Abs(Math.Sqrt((p.X-s.Old.X0)*(p.X-s.Old.X0)+(p.Y-s.Old.Y0)*(p.Y-s.Old.Y0))-s.Old.Radius)))
                }).ToList();
                double best=ranks.Min(x=>x.Error);
                var keys=new HashSet<string>(ranks.Where(x=>x.Error<=best+1e-7).Select(x=>x.Key));
                spans.RemoveAll(s=>!keys.Contains(s.New.SourceKey));
            }
            return spans;
        }
        static List<ViewLineOverride> Ranges(Guid instance,string key,double from,double to,IList<ParametricProjection.Curve2> old,IList<ParametricProjection.Curve2> current)
        {
            if(!Legacy(key)||!current.Any(c=>c.InstanceId==instance))return new List<ViewLineOverride>{new ViewLineOverride{InstanceId=instance,SourceKey=key,From=from,To=to}};
            var pieces=new List<ViewLineOverride>();
            var covered=new List<Tuple<double,double>>();
            foreach(var span in Match(instance,key,old,current))
            {
                double lo=Math.Max(from,Math.Min(span.OldFrom,span.OldTo)),hi=Math.Min(to,Math.Max(span.OldFrom,span.OldTo));
                if(hi-lo<1e-10)continue;
                double a=(lo-span.OldFrom)/(span.OldTo-span.OldFrom),b=(hi-span.OldFrom)/(span.OldTo-span.OldFrom);
                double x=span.New.SourceFrom+(span.New.SourceTo-span.New.SourceFrom)*a,y=span.New.SourceFrom+(span.New.SourceTo-span.New.SourceFrom)*b;
                pieces.Add(new ViewLineOverride{InstanceId=instance,SourceKey=span.New.SourceKey,From=Math.Max(0,Math.Min(x,y)),To=Math.Min(1,Math.Max(x,y))});
                covered.Add(Tuple.Create(lo,hi));
            }
            // Only the displayed part may have existed (occlusion can split a source).
            // Require coverage of every legacy displayed interval, not invisible material.
            foreach(var c in old.Where(x=>x.InstanceId==instance&&x.SourceKey==key))
            {
                double lo=Math.Max(from,Math.Min(c.SourceFrom,c.SourceTo)),hi=Math.Min(to,Math.Max(c.SourceFrom,c.SourceTo));
                if(hi-lo<=1e-9)continue;
                double at=lo;
                foreach(var interval in covered.OrderBy(x=>x.Item1))
                {if(interval.Item1>at+1e-5)break;if(interval.Item2>at)at=interval.Item2;}
                if(at<hi-1e-5)throw Failure();
            }
            var merged=new List<ViewLineOverride>();
            foreach(var group in pieces.GroupBy(x=>x.SourceKey))
                foreach(var p in group.OrderBy(x=>x.From))
                {
                    var last=merged.LastOrDefault();
                    if(last!=null&&last.SourceKey==p.SourceKey&&p.From<=last.To+1e-8)last.To=Math.Max(last.To,p.To);
                    else merged.Add(p);
                }
            if(merged.Count==0)throw Failure();
            return merged;
        }
        // Keep exact analytic geometry where available, but attach only the common
        // physical source identities. Other shapes use the same identities directly.
        public static List<ParametricProjection.Curve2> Project(IList<ParametricProjection.Curve2> exact,IList<ParametricProjection.Curve2> stable)
        {
            var result=new List<ParametricProjection.Curve2>();
            foreach(var c in exact)
            {
                var spans=Match(c.InstanceId,c.SourceKey,exact,stable);
                double from=Math.Min(c.SourceFrom,c.SourceTo),to=Math.Max(c.SourceFrom,c.SourceTo);
                var cuts=new List<double>{from,to};
                foreach(var span in spans)
                {
                    cuts.Add(Math.Max(from,Math.Min(to,span.OldFrom)));
                    cuts.Add(Math.Max(from,Math.Min(to,span.OldTo)));
                }
                var sorted=cuts.Distinct().OrderBy(x=>x).ToList();
                for(int i=1;i<sorted.Count;i++)
                {
                    double lo=sorted[i-1],hi=sorted[i];if(hi-lo<=1e-10)continue;
                    double mid=(lo+hi)/2;
                    var point=c.At((mid-c.SourceFrom)/(c.SourceTo-c.SourceFrom));
                    var matches=spans.Where(x=>mid>=Math.Min(x.OldFrom,x.OldTo)-1e-9&&mid<=Math.Max(x.OldFrom,x.OldTo)+1e-9).ToList();
                    if(matches.Count==0)
                    {
                        // Analytic extrema may extend less than one sagitta beyond
                        // the polygon. Keep the exact endpoint on the nearest rim.
                        matches=spans.Where(x=>ViewLineOverrides.DistanceToCurve(x.New,point.X,point.Y,out _,out _,out _)<=Tol).ToList();
                        if(matches.Count==0)throw Failure();
                    }
                    var span=matches.OrderBy(x=>ViewLineOverrides.DistanceToCurve(x.New,point.X,point.Y,out _,out _,out _))
                        .ThenBy(x=>x.New.SourceKey,StringComparer.Ordinal).First();
                    double a=(lo-c.SourceFrom)/(c.SourceTo-c.SourceFrom),b=(hi-c.SourceFrom)/(c.SourceTo-c.SourceFrom);
                    var piece=c.Piece(Math.Min(a,b),Math.Max(a,b));
                    double u=(lo-span.OldFrom)/(span.OldTo-span.OldFrom),v=(hi-span.OldFrom)/(span.OldTo-span.OldFrom);
                    piece.SourceKey=span.New.SourceKey;
                    piece.SourceFrom=Math.Max(0,Math.Min(1,span.New.SourceFrom+(span.New.SourceTo-span.New.SourceFrom)*u));
                    piece.SourceTo=Math.Max(0,Math.Min(1,span.New.SourceFrom+(span.New.SourceTo-span.New.SourceFrom)*v));
                    result.Add(piece);
                }
            }

            // Restore full native circles (and their exact radii), not independent
            // facet arcs. Source direction can oppose the view's CCW direction.
            var merged=new List<ParametricProjection.Curve2>();
            foreach(var group in result.GroupBy(c=>Tuple.Create(c.InstanceId,c.SourceKey,c.Hidden,c.Cut)))
            {
                var list=group.ToList();var first=list[0];
                if(list.All(c=>c.Circular)&&Math.Abs(list.Sum(c=>c.Sweep)-2*Math.PI)<1e-6)
                {
                    var seam=list.OrderBy(c=>Math.Abs(c.SourceFrom)).First();
                    var circle=Json.Copy(first);circle.Start=seam.Start;circle.Sweep=2*Math.PI;
                    bool forward=first.SourceTo>first.SourceFrom;
                    circle.SourceFrom=forward?0:1;circle.SourceTo=forward?1:0;
                    if(!forward)circle.Start=seam.Start+seam.Sweep;
                    merged.Add(circle);
                }
                else merged.AddRange(list);
            }
            return merged;
        }
        public static List<ViewLineOverride> Lines(IEnumerable<ViewLineOverride> lines,IList<ParametricProjection.Curve2> old,IList<ParametricProjection.Curve2> current)
        {
            var result=new List<ViewLineOverride>();
            foreach(var line in lines??Enumerable.Empty<ViewLineOverride>())
                foreach(var r in Ranges(line.InstanceId,line.SourceKey,line.From,line.To,old,current))
                {r.BatchId=line.BatchId;r.Mode=line.Mode;result.Add(r);}
            return result;
        }
        public static List<ViewWeldOverlay> Welds(IEnumerable<ViewWeldOverlay> welds,IList<ParametricProjection.Curve2> old,IList<ParametricProjection.Curve2> current)
        {
            var result=new List<ViewWeldOverlay>();
            foreach(var w in welds??Enumerable.Empty<ViewWeldOverlay>())
            {
                bool first=true;
                foreach(var r in Ranges(w.InstanceId,w.SourceKey,w.From,w.To,old,current))
                {
                    var copy=Json.Copy(w);copy.SourceKey=r.SourceKey;copy.From=r.From;copy.To=r.To;
                    if(!first){copy.GroupId=w.GroupId==Guid.Empty?w.Id:w.GroupId;copy.Id=Guid.NewGuid();}
                    result.Add(copy);first=false;
                }
            }
            return result;
        }
        public static void Anchor(ViewAnchor anchor,IList<ParametricProjection.Curve2> old,IList<ParametricProjection.Curve2> current)
        {
            if(anchor==null||!Legacy(anchor.SourceKey)||!current.Any(c=>c.InstanceId==anchor.InstanceId))return;
            var spans=Match(anchor.InstanceId,anchor.SourceKey,old,current);
            if(anchor.Kind==ViewAnchorKind.CurveCenter)
            {
                var keys=spans.Select(x=>x.New.SourceKey).Distinct().ToList();
                if(keys.Count!=1)throw Failure();anchor.SourceKey=keys[0];return;
            }
            var matched=spans.Where(x=>anchor.Param>=Math.Min(x.OldFrom,x.OldTo)-1e-8&&anchor.Param<=Math.Max(x.OldFrom,x.OldTo)+1e-8).ToList();
            if(matched.Count==0)throw Failure();
            var span=matched[0];double t=(anchor.Param-span.OldFrom)/(span.OldTo-span.OldFrom);
            anchor.SourceKey=span.New.SourceKey;
            anchor.Param=Math.Max(0,Math.Min(1,span.New.SourceFrom+(span.New.SourceTo-span.New.SourceFrom)*t));
        }
    }
}
