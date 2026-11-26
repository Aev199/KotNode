using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    public enum ViewLineMode { Auto=0, Visible=1, Hidden=2, Suppressed=3 }
    public sealed class ViewLineOverride
    {
        public Guid InstanceId { get; set; }
        public string SourceKey { get; set; }
        public double From { get; set; }
        public double To { get; set; }=1;
        public ViewLineMode Mode { get; set; }
        public Guid BatchId { get; set; }
    }
    public sealed class CurveChain
    {
        public Guid InstanceId { get; set; }
        public string SourceKey { get; set; }
        public bool Hidden { get; set; }
        public bool Cut { get; set; }
        public bool Circular { get; set; }
        public bool Closed { get; set; }
        public bool IsFullLoop { get; set; }
        public double TotalLength { get; set; }
        public List<ParametricProjection.Curve2> Segments { get; set; }=new List<ParametricProjection.Curve2>();
    }
    public sealed class PickHit
    {
        public CurveChain Chain { get; set; }
        public double Distance { get; set; }
        public double ChainParameter { get; set; }
        public List<CurveChain> Coincident { get; set; }=new List<CurveChain>();
    }
    public static class ViewLineOverrides
    {
        const double E=1e-10;
        const double JoinTol=1e-3;
        public static bool HasSource(ParametricProjection.Curve2 c)=>c!=null&&c.InstanceId!=Guid.Empty&&!string.IsNullOrEmpty(c.SourceKey)&&Math.Abs(c.SourceTo-c.SourceFrom)>E;
        public static bool HasSource(CurveChain chain)=>chain!=null&&chain.InstanceId!=Guid.Empty&&!string.IsNullOrEmpty(chain.SourceKey)&&chain.Segments.Count>0&&chain.Segments.Any(HasSource);
        public static double CurveLength(ParametricProjection.Curve2 c)=>c.Circular?c.Radius*c.Sweep:Math.Sqrt((c.X1-c.X0)*(c.X1-c.X0)+(c.Y1-c.Y0)*(c.Y1-c.Y0));
        public static ViewLineOverride Create(ParametricProjection.Curve2 c,ViewLineMode mode,bool whole,Guid batch)
        {
            if(!HasSource(c))throw new InvalidDataException("Для линии пока нет устойчивой исходной кромки.");
            return new ViewLineOverride{InstanceId=c.InstanceId,SourceKey=c.SourceKey,From=whole?0:Math.Min(c.SourceFrom,c.SourceTo),To=whole?1:Math.Max(c.SourceFrom,c.SourceTo),Mode=mode,BatchId=batch};
        }
        // Parameter on the selected displayed curve, not on the entire source edge.
        public static double ParameterAt(ParametricProjection.Curve2 c,double x,double y)
        {
            if(c==null||!Guard.Finite(x)||!Guard.Finite(y))throw new InvalidDataException("Неверная точка линии.");
            if(!c.Circular)
            {
                double dx=c.X1-c.X0,dy=c.Y1-c.Y0,d=dx*dx+dy*dy;
                if(d<=E*E)throw new InvalidDataException("Вырожденная линия.");
                return Math.Max(0,Math.Min(1,((x-c.X0)*dx+(y-c.Y0)*dy)/d));
            }
            if(c.Radius<=E||c.Sweep<=E)throw new InvalidDataException("Вырожденная дуга.");
            if(Math.Abs(x-c.X0)+Math.Abs(y-c.Y0)<=E)
                throw new InvalidDataException("Укажите точку на дуге, а не её центр.");
            double tau=2*Math.PI,angle=Math.Atan2(y-c.Y0,x-c.X0)-c.Start;
            angle=(angle%tau+tau)%tau;
            if(angle<=c.Sweep+E)return Math.Min(1,angle/c.Sweep);
            var a=c.At(0);var b=c.At(1);
            double da=(x-a.X)*(x-a.X)+(y-a.Y)*(y-a.Y),db=(x-b.X)*(x-b.X)+(y-b.Y)*(y-b.Y);
            return da<=db?0:1;
        }
        public static double DistanceToCurve(ParametricProjection.Curve2 c,double x,double y,out double param,out double tx,out double ty)
        {
            param=0;tx=1;ty=0;
            if(c==null||!Guard.Finite(x)||!Guard.Finite(y))return double.PositiveInfinity;
            if(!c.Circular)
            {
                double dx=c.X1-c.X0,dy=c.Y1-c.Y0,len2=dx*dx+dy*dy;
                if(len2<=E*E)return double.PositiveInfinity;
                double len=Math.Sqrt(len2);
                param=Math.Max(0,Math.Min(1,((x-c.X0)*dx+(y-c.Y0)*dy)/len2));
                tx=dx/len;ty=dy/len;
                double px=c.X0+dx*param,py=c.Y0+dy*param;
                return Math.Sqrt((x-px)*(x-px)+(y-py)*(y-py));
            }
            if(c.Radius<=E||c.Sweep<=E)return double.PositiveInfinity;
            double rx=x-c.X0,ry=y-c.Y0,r=Math.Sqrt(rx*rx+ry*ry);
            if(r<=E)
            {
                param=0;tx=-Math.Sin(c.Start);ty=Math.Cos(c.Start);
                return c.Radius;
            }
            double tau=2*Math.PI,angle=Math.Atan2(ry,rx)-c.Start;
            angle=(angle%tau+tau)%tau;
            if(angle<=c.Sweep+E)
            {
                param=Math.Min(1,angle/c.Sweep);
                double a=c.Start+c.Sweep*param;
                tx=-Math.Sin(a);ty=Math.Cos(a);
                return Math.Abs(r-c.Radius);
            }
            var p0=c.At(0);var p1=c.At(1);
            double d0=Math.Sqrt((x-p0.X)*(x-p0.X)+(y-p0.Y)*(y-p0.Y));
            double d1=Math.Sqrt((x-p1.X)*(x-p1.X)+(y-p1.Y)*(y-p1.Y));
            if(d0<=d1){param=0;tx=-Math.Sin(c.Start);ty=Math.Cos(c.Start);return d0;}
            param=1;double ae=c.Start+c.Sweep;tx=-Math.Sin(ae);ty=Math.Cos(ae);return d1;
        }
        static ParametricProjection.Curve2 Reversed(ParametricProjection.Curve2 c)
        {
            return new ParametricProjection.Curve2
            {
                Circular=c.Circular,X0=c.X1,Y0=c.Y1,X1=c.X0,Y1=c.Y0,
                Radius=c.Radius,Start=c.Start,Sweep=c.Sweep,
                Hidden=c.Hidden,Cut=c.Cut,InstanceId=c.InstanceId,
                SourceKey=c.SourceKey,SourceFrom=c.SourceTo,SourceTo=c.SourceFrom
            };
        }
        static double Dist2(Point2 a,Point2 b)=>(a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y);
        public static List<CurveChain> Chain(IEnumerable<ParametricProjection.Curve2> curves)
        {
            var result=new List<CurveChain>();
            if(curves==null)return result;
            var straightByKey=new Dictionary<string,List<ParametricProjection.Curve2>>(StringComparer.Ordinal);
            foreach(var c in curves)
            {
                if(c==null)continue;
                double len=CurveLength(c);
                if(len<=E)continue;
                if(c.Circular||!HasSource(c))
                {
                    bool full=c.Circular&&c.Sweep>=2*Math.PI-1e-9;
                    result.Add(new CurveChain
                    {
                        InstanceId=c.InstanceId,SourceKey=c.SourceKey,Hidden=c.Hidden,Cut=c.Cut,
                        Circular=c.Circular,Closed=full,IsFullLoop=full,TotalLength=len,
                        Segments=new List<ParametricProjection.Curve2>{c}
                    });
                    continue;
                }
                string groupKey=c.InstanceId.ToString("N")+"|"+c.SourceKey+"|"+(c.Hidden?"H":"V")+"|"+(c.Cut?"C":"N");
                if(!straightByKey.TryGetValue(groupKey,out var list)){list=new List<ParametricProjection.Curve2>();straightByKey[groupKey]=list;}
                list.Add(c);
            }
            double tol2=JoinTol*JoinTol;
            foreach(var group in straightByKey.Values)
            {
                var remaining=new List<ParametricProjection.Curve2>(group);
                while(remaining.Count>0)
                {
                    var chain=new List<ParametricProjection.Curve2>{remaining[0]};
                    remaining.RemoveAt(0);
                    bool extended=true;
                    while(extended&&remaining.Count>0)
                    {
                        extended=false;
                        var tail=chain[chain.Count-1].At(1);
                        int next=-1;bool rev=false;double best=tol2;
                        for(int i=0;i<remaining.Count;i++)
                        {
                            double d0=Dist2(remaining[i].At(0),tail);
                            if(d0<=best){best=d0;next=i;rev=false;}
                            double d1=Dist2(remaining[i].At(1),tail);
                            if(d1<=best){best=d1;next=i;rev=true;}
                        }
                        if(next>=0)
                        {
                            var seg=remaining[next];remaining.RemoveAt(next);
                            chain.Add(rev?Reversed(seg):seg);
                            extended=true;continue;
                        }
                        var head=chain[0].At(0);
                        best=tol2;
                        for(int i=0;i<remaining.Count;i++)
                        {
                            double d1=Dist2(remaining[i].At(1),head);
                            if(d1<=best){best=d1;next=i;rev=false;}
                            double d0=Dist2(remaining[i].At(0),head);
                            if(d0<=best){best=d0;next=i;rev=true;}
                        }
                        if(next>=0)
                        {
                            var seg=remaining[next];remaining.RemoveAt(next);
                            chain.Insert(0,rev?Reversed(seg):seg);
                            extended=true;
                        }
                    }
                    double totalLen=chain.Sum(CurveLength);
                    bool closed=chain.Count>=3&&Dist2(chain[0].At(0),chain[chain.Count-1].At(1))<=tol2;
                    double srcCoverage=chain.Sum(s=>Math.Abs(s.SourceTo-s.SourceFrom));
                    bool fullLoop=closed&&srcCoverage>=1-1e-3;
                    var first=chain[0];
                    result.Add(new CurveChain
                    {
                        InstanceId=first.InstanceId,SourceKey=first.SourceKey,Hidden=first.Hidden,Cut=first.Cut,
                        Circular=false,Closed=closed,IsFullLoop=fullLoop,TotalLength=totalLen,Segments=chain
                    });
                }
            }
            return result;
        }
        public static double DistanceToChain(CurveChain chain,double x,double y,out double chainParam,out double tx,out double ty)
        {
            chainParam=0;tx=1;ty=0;
            if(chain==null||chain.Segments.Count==0||chain.TotalLength<=E)return double.PositiveInfinity;
            double bestDist=double.PositiveInfinity,accum=0;
            foreach(var seg in chain.Segments)
            {
                double len=CurveLength(seg);
                double d=DistanceToCurve(seg,x,y,out var t,out var stx,out var sty);
                if(d<bestDist-1e-12)
                {
                    bestDist=d;
                    chainParam=(accum+len*t)/chain.TotalLength;
                    tx=stx;ty=sty;
                }
                accum+=len;
            }
            chainParam=Math.Max(0,Math.Min(1,chainParam));
            return bestDist;
        }
        public static double ParameterAt(CurveChain chain,double x,double y)
        {
            if(chain==null||chain.Segments.Count==0||!Guard.Finite(x)||!Guard.Finite(y))
                throw new InvalidDataException("Неверная точка линии.");
            if(chain.Segments.Count==1&&chain.Circular)
                return ParameterAt(chain.Segments[0],x,y);
            double d=DistanceToChain(chain,x,y,out var u,out _,out _);
            if(double.IsInfinity(d))throw new InvalidDataException("Вырожденная линия.");
            return u;
        }
        public static PickHit Pick(IList<CurveChain> chains,double x,double y,double tolerance)
        {
            if(chains==null||chains.Count==0||!Guard.Finite(x)||!Guard.Finite(y)||tolerance<=0)return null;
            CurveChain best=null;
            double bestDist=double.PositiveInfinity,bestParam=0,bestTx=1,bestTy=0;
            var candidates=new List<Tuple<CurveChain,double,double,double,double>>();
            foreach(var chain in chains)
            {
                if(!HasSource(chain))continue;
                double d=DistanceToChain(chain,x,y,out var u,out var tx,out var ty);
                if(d>tolerance)continue;
                candidates.Add(Tuple.Create(chain,d,u,tx,ty));
                bool better=best==null||d<bestDist-1e-3||
                    (Math.Abs(d-bestDist)<=1e-3&&((best.Hidden&&!chain.Hidden)||(best.Hidden==chain.Hidden&&chain.Cut&&!best.Cut)));
                if(better){best=chain;bestDist=d;bestParam=u;bestTx=tx;bestTy=ty;}
            }
            if(best==null)return null;
            var coincident=new List<CurveChain>{best};
            foreach(var c in candidates)
            {
                if(c.Item1==best)continue;
                if(c.Item2>bestDist+0.08)continue;
                double dot=Math.Abs(c.Item4*bestTx+c.Item5*bestTy);
                if(dot>=0.995)coincident.Add(c.Item1);
            }
            return new PickHit{Chain=best,Distance=bestDist,ChainParameter=bestParam,Coincident=coincident};
        }
        static List<Tuple<double,double>> MergeIntervals(List<Tuple<double,double>> raw)
        {
            var merged=new List<Tuple<double,double>>();
            if(raw.Count==0)return merged;
            raw.Sort((a,b)=>a.Item1.CompareTo(b.Item1));
            double curLo=raw[0].Item1,curHi=raw[0].Item2;
            for(int i=1;i<raw.Count;i++)
            {
                if(raw[i].Item1<=curHi+1e-6)curHi=Math.Max(curHi,raw[i].Item2);
                else {merged.Add(Tuple.Create(curLo,curHi));curLo=raw[i].Item1;curHi=raw[i].Item2;}
            }
            merged.Add(Tuple.Create(curLo,curHi));
            return merged;
        }
        static void CollectChainSlice(CurveChain chain,double uLo,double uHi,List<Tuple<double,double>> intervals,List<ParametricProjection.Curve2> preview)
        {
            if(uHi-uLo<=E||chain.TotalLength<=E)return;
            double accum=0;
            foreach(var seg in chain.Segments)
            {
                double len=CurveLength(seg);
                double s0=accum/chain.TotalLength,s1=(accum+len)/chain.TotalLength;
                accum+=len;
                double a=Math.Max(uLo,s0),b=Math.Min(uHi,s1);
                if(b-a<=E||s1-s0<=E)continue;
                double t0=Math.Max(0,Math.Min(1,(a-s0)/(s1-s0)));
                double t1=Math.Max(0,Math.Min(1,(b-s0)/(s1-s0)));
                if(t1-t0<=E)continue;
                var piece=seg.Piece(t0,t1);
                if(preview!=null)preview.Add(piece);
                if(intervals!=null&&HasSource(piece))
                {
                    double lo=Math.Max(0,Math.Min(piece.SourceFrom,piece.SourceTo));
                    double hi=Math.Min(1,Math.Max(piece.SourceFrom,piece.SourceTo));
                    if(hi-lo>E)intervals.Add(Tuple.Create(lo,hi));
                }
            }
        }
        public static List<ViewLineOverride> CreateForChain(CurveChain chain,ViewLineMode mode,bool whole,Guid batch,List<ParametricProjection.Curve2> preview=null)
        {
            if(!HasSource(chain))throw new InvalidDataException("Для линии пока нет устойчивой исходной кромки.");
            if(whole)
            {
                if(preview!=null)preview.AddRange(chain.Segments);
                var r=new ViewLineOverride{InstanceId=chain.InstanceId,SourceKey=chain.SourceKey,From=0,To=1,Mode=mode,BatchId=batch};
                Validate(r);return new List<ViewLineOverride>{r};
            }
            var intervals=new List<Tuple<double,double>>();
            CollectChainSlice(chain,0,1,intervals,preview);
            var merged=MergeIntervals(intervals);
            var result=new List<ViewLineOverride>();
            foreach(var iv in merged)
            {
                var r=new ViewLineOverride{InstanceId=chain.InstanceId,SourceKey=chain.SourceKey,From=iv.Item1,To=iv.Item2,Mode=mode,BatchId=batch};
                Validate(r);result.Add(r);
            }
            if(result.Count==0)throw new InvalidDataException("Для линии пока нет устойчивой исходной кромки.");
            return result;
        }
        public static List<ViewLineOverride> CreateRangeForChain(CurveChain chain,ViewLineMode mode,double a,double b,bool longerArc,Guid batch,List<ParametricProjection.Curve2> preview=null)
        {
            if(!HasSource(chain)||!Guard.Finite(a)||!Guard.Finite(b)||a<0||a>1||b<0||b>1)
                throw new InvalidDataException("Неверные границы корректировки.");
            double lo=Math.Min(a,b),hi=Math.Max(a,b);
            if(hi-lo<=E||(chain.IsFullLoop&&hi-lo>=1-E))
                throw new InvalidDataException("Границы совпадают. Для всей линии используйте Segment или Edge.");
            var intervals=new List<Tuple<double,double>>();
            bool wrap=chain.IsFullLoop&&((hi-lo>.5)!=longerArc);
            if(wrap)
            {
                CollectChainSlice(chain,0,lo,intervals,preview);
                CollectChainSlice(chain,hi,1,intervals,preview);
            }
            else
            {
                CollectChainSlice(chain,lo,hi,intervals,preview);
            }
            var merged=MergeIntervals(intervals);
            var result=new List<ViewLineOverride>();
            foreach(var iv in merged)
            {
                var r=new ViewLineOverride{InstanceId=chain.InstanceId,SourceKey=chain.SourceKey,From=iv.Item1,To=iv.Item2,Mode=mode,BatchId=batch};
                Validate(r);result.Add(r);
            }
            if(result.Count==0)throw new InvalidDataException("Границы совпадают. Для всей линии используйте Segment или Edge.");
            return result;
        }
        public static List<ViewLineOverride> CreateRange(ParametricProjection.Curve2 c,ViewLineMode mode,double a,double b,bool longerArc,Guid batch)
        {
            if(!HasSource(c)||!Guard.Finite(a)||!Guard.Finite(b)||a<0||a>1||b<0||b>1)
                throw new InvalidDataException("Неверные границы корректировки.");
            double lo=Math.Min(a,b),hi=Math.Max(a,b);
            if(hi-lo<=E||(c.Circular&&c.Sweep>=2*Math.PI-E&&hi-lo>=1-E))
                throw new InvalidDataException("Границы совпадают. Для всей линии используйте Segment или Edge.");
            var result=new List<ViewLineOverride>();
            Action<double,double> add=(u,v)=>{
                if(v-u<=E)return;
                double x=c.SourceFrom+(c.SourceTo-c.SourceFrom)*u,y=c.SourceFrom+(c.SourceTo-c.SourceFrom)*v;
                var r=new ViewLineOverride{InstanceId=c.InstanceId,SourceKey=c.SourceKey,From=Math.Min(x,y),To=Math.Max(x,y),Mode=mode,BatchId=batch};
                Validate(r);result.Add(r);
            };
            bool wrap=c.Circular&&c.Sweep>=2*Math.PI-E&&((hi-lo>.5)!=longerArc);
            if(wrap){add(0,lo);add(hi,1);}
            else add(lo,hi);
            return result;
        }
        // Keep several dash cycles on short segments. No mutation of the drawing LTSCALE.
        public static double HiddenLineScale(double length,double annotationScale,double globalScale,double patternLength)
        {
            if(!Guard.Finite(length)||length<=0||!Guard.Finite(annotationScale)||annotationScale<=0||
                !Guard.Finite(globalScale)||globalScale<=0||!Guard.Finite(patternLength)||patternLength<=0)
                throw new InvalidDataException("Некорректный масштаб штриховой линии.");
            return Math.Min(annotationScale,length/(3*patternLength))/globalScale;
        }
        public static void Validate(ViewLineOverride r)
        {
            if(r==null||r.InstanceId==Guid.Empty||string.IsNullOrWhiteSpace(r.SourceKey)||
                !Guard.Finite(r.From)||!Guard.Finite(r.To)||r.From< -E||r.To>1+E||r.To-r.From<=E||
                !Enum.IsDefined(typeof(ViewLineMode),r.Mode))
                throw new InvalidDataException("Повреждена запись ручной корректировки линии.");
        }
        public static List<ParametricProjection.Curve2> Apply(IEnumerable<ParametricProjection.Curve2> curves,
            IEnumerable<ViewLineOverride> rules,bool showHidden,out int unmatched)
        {
            var journal=(rules??Enumerable.Empty<ViewLineOverride>()).ToList();
            foreach(var r in journal)Validate(r);
            var applied=new bool[journal.Count];var result=new List<ParametricProjection.Curve2>();
            foreach(var c in curves)
            {
                if(!HasSource(c)){if(showHidden||!c.Hidden)result.Add(c);continue;}
                double d=c.SourceTo-c.SourceFrom;
                double lo=Math.Min(c.SourceFrom,c.SourceTo),hi=Math.Max(c.SourceFrom,c.SourceTo);
                var relevant=new List<int>();var cuts=new List<double>{0,1};
                for(int k=0;k<journal.Count;k++)
                {
                    var r=journal[k];
                    if(c.InstanceId!=r.InstanceId||!string.Equals(c.SourceKey,r.SourceKey,StringComparison.Ordinal)||Math.Min(hi,r.To)-Math.Max(lo,r.From)<=E)continue;
                    relevant.Add(k);applied[k]=true;
                    foreach(double p in new[]{r.From,r.To}){double t=(p-c.SourceFrom)/d;if(t>E&&t<1-E)cuts.Add(t);}
                }
                if(relevant.Count==0){if(showHidden||!c.Hidden)result.Add(c);continue;}
                cuts.Sort();
                for(int k=1;k<cuts.Count;k++)
                {
                    double a=cuts[k-1],b=cuts[k];if(b-a<=E)continue;
                    double p=c.SourceFrom+d*(a+b)/2;
                    var mode=ViewLineMode.Auto;
                    foreach(int idx in relevant)if(journal[idx].From<=p&&journal[idx].To>=p)mode=journal[idx].Mode;
                    if(mode==ViewLineMode.Suppressed)continue;
                    var piece=c.Piece(a,b);
                    if(mode==ViewLineMode.Visible){piece.Hidden=false;piece.Cut=false;}
                    else if(mode==ViewLineMode.Hidden){piece.Hidden=true;piece.Cut=false;}
                    if(showHidden||!piece.Hidden||mode==ViewLineMode.Hidden)result.Add(piece);
                }
            }
            unmatched=applied.Count(a=>!a);return result;
        }
    }
}
