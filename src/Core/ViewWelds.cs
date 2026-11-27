using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    public enum ViewWeldStyle { Factory=0, Site=1, Hidden=2 }
    public enum ViewWeldSide { Both=0, Left=1, Right=-1 }
    public sealed class ViewWeldOverlay
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public Guid DefinitionId { get; set; }
        public bool SectionOnly { get; set; }
        public ViewAnchor SectionAnchor { get; set; }
        public int SectionAlongSign { get; set; }=1;
        public Guid BatchId { get; set; }
        public Guid GroupId { get; set; }
        public Guid InstanceId { get; set; }
        public string SourceKey { get; set; }
        public double From { get; set; }
        public double To { get; set; }=1;
        public ViewWeldStyle Style { get; set; }=ViewWeldStyle.Site;
        public ViewWeldSide Side { get; set; }=ViewWeldSide.Left;
        public double LegMm { get; set; }=6;
        public double OffsetMm { get; set; }
        public bool? ShowInSections { get; set; }
    }
    public sealed class WeldStroke
    {
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public bool Hidden { get; set; }
        public Guid WeldId { get; set; }
    }
    public static class ViewWelds
    {
        const double E=1e-10;
        public static void Validate(ViewWeldOverlay w)
        {
            if(w==null||w.Id==Guid.Empty||w.InstanceId==Guid.Empty||string.IsNullOrWhiteSpace(w.SourceKey)||
                !Guard.Finite(w.From)||!Guard.Finite(w.To)||w.From<-E||w.To>1+E||w.To-w.From<=E||
                !Enum.IsDefined(typeof(ViewWeldStyle),w.Style)||!Enum.IsDefined(typeof(ViewWeldSide),w.Side)||
                !Guard.Finite(w.LegMm)||w.LegMm<=0||w.LegMm>100||!Guard.Finite(w.OffsetMm))
                throw new InvalidDataException("Повреждена запись параметрического сварного шва.");
            if(w.SectionOnly)
            {
                ViewAnnotations.ValidateAnchor(w.SectionAnchor);
                if(w.SectionAnchor.Kind!=ViewAnchorKind.EdgePoint||w.SectionAnchor.InstanceId!=w.InstanceId||w.SectionAnchor.SourceKey!=w.SourceKey||Math.Abs(w.SectionAlongSign)!=1)
                    throw new InvalidDataException("Неверная привязка шва на сечении.");
            }
        }
        public static ViewWeldSide SideFromPoint(CurveChain chain,double x,double y)
        {
            if(chain==null||chain.Segments.Count==0||!Guard.Finite(x)||!Guard.Finite(y))return ViewWeldSide.Left;
            double bestDist=double.PositiveInfinity;
            double bestPx=0,bestPy=0,bestTx=1,bestTy=0;
            foreach(var seg in chain.Segments)
            {
                double d=ViewLineOverrides.DistanceToCurve(seg,x,y,out var t,out var tx,out var ty);
                if(d<bestDist-1e-12)
                {
                    bestDist=d;
                    var p=seg.At(t);
                    bestPx=p.X;bestPy=p.Y;bestTx=tx;bestTy=ty;
                }
            }
            // Left normal is (-ty, tx).
            double sideVal=(x-bestPx)*(-bestTy)+(y-bestPy)*bestTx;
            return sideVal>=0?ViewWeldSide.Left:ViewWeldSide.Right;
        }
        public static List<ViewWeldOverlay> CreateForChain(CurveChain chain,ViewWeldStyle style,ViewWeldSide side,double legMm,bool whole,Guid batch)
        {
            var raw=ViewLineOverrides.CreateForChain(chain,ViewLineMode.Visible,whole,batch);
            return raw.Select(r=>{
                var w=new ViewWeldOverlay
                {
                    BatchId=batch,InstanceId=r.InstanceId,SourceKey=r.SourceKey,
                    From=r.From,To=r.To,Style=style,Side=side,LegMm=legMm
                };
                Validate(w);return w;
            }).ToList();
        }
        public static List<ViewWeldOverlay> CreateRangeForChain(CurveChain chain,ViewWeldStyle style,ViewWeldSide side,double legMm,double a,double b,bool longerArc,Guid batch)
        {
            var raw=ViewLineOverrides.CreateRangeForChain(chain,ViewLineMode.Visible,a,b,longerArc,batch);
            return raw.Select(r=>{
                var w=new ViewWeldOverlay
                {
                    BatchId=batch,InstanceId=r.InstanceId,SourceKey=r.SourceKey,
                    From=r.From,To=r.To,Style=style,Side=side,LegMm=legMm
                };
                Validate(w);return w;
            }).ToList();
        }
        static void SampleAt(CurveChain chain,double dist,out double px,out double py,out double tx,out double ty)
        {
            double accum=0;
            var last=chain.Segments[chain.Segments.Count-1];
            for(int i=0;i<chain.Segments.Count;i++)
            {
                var seg=chain.Segments[i];
                double len=ViewLineOverrides.CurveLength(seg);
                if(dist<=accum+len+1e-9||i==chain.Segments.Count-1)
                {
                    double t=len>E?Math.Max(0,Math.Min(1,(dist-accum)/len)):0.5;
                    var p=seg.At(t);
                    px=p.X;py=p.Y;
                    if(seg.Circular)
                    {
                        double ang=seg.Start+seg.Sweep*t;
                        tx=-Math.Sin(ang);ty=Math.Cos(ang);
                    }
                    else
                    {
                        double dx=seg.X1-seg.X0,dy=seg.Y1-seg.Y0;
                        tx=len>E?dx/len:1;ty=len>E?dy/len:0;
                    }
                    return;
                }
                accum+=len;
            }
            var end=last.At(1);px=end.X;py=end.Y;tx=1;ty=0;
        }
        static void EmitChainStrokes(CurveChain chain,ViewWeldOverlay w,double scale,List<WeldStroke> strokes)
        {
            double L=chain.TotalLength;
            if(L<=1e-6)return;
            int sideSign=(int)w.Side;
            if(w.Style==ViewWeldStyle.Site)
            {
                double r=Math.Min(0.45*scale,Math.Max(0.4,L/4));
                double step=Math.Max(r*3.6,1e-3);
                int count=Math.Max(1,Math.Min(500,(int)Math.Round(L/step)));
                double off=sideSign*r*1.15;
                for(int i=0;i<count;i++)
                {
                    SampleAt(chain,L*(i+0.5)/count,out var px,out var py,out var tx,out var ty);
                    double nx=-ty,ny=tx;
                    px+=nx*w.OffsetMm;py+=ny*w.OffsetMm;
                    double cx=px+nx*off,cy=py+ny*off;
                    double d1x=(tx+nx)*r*0.7071067811865476,d1y=(ty+ny)*r*0.7071067811865476;
                    double d2x=(tx-nx)*r*0.7071067811865476,d2y=(ty-ny)*r*0.7071067811865476;
                    strokes.Add(new WeldStroke{X0=cx-d1x,Y0=cy-d1y,X1=cx+d1x,Y1=cy+d1y,Hidden=false,WeldId=w.Id});
                    strokes.Add(new WeldStroke{X0=cx-d2x,Y0=cy-d2y,X1=cx+d2x,Y1=cy+d2y,Hidden=false,WeldId=w.Id});
                }
            }
            else
            {
                bool hidden=w.Style==ViewWeldStyle.Hidden;
                double tickLen=Math.Min((hidden?0.95:1.1)*scale,Math.Max(0.8,L/2));
                double step=Math.Max((hidden?1.2:0.75)*scale,1e-3);
                int count=Math.Max(2,Math.Min(600,(int)Math.Floor(L/step)+1));
                for(int i=0;i<count;i++)
                {
                    SampleAt(chain,L*(i+0.5)/count,out var px,out var py,out var tx,out var ty);
                    double nx=-ty,ny=tx;
                    px+=nx*w.OffsetMm;py+=ny*w.OffsetMm;
                    if(sideSign==0)
                    {
                        double h=tickLen*0.6;
                        strokes.Add(new WeldStroke{X0=px-nx*h,Y0=py-ny*h,X1=px+nx*h,Y1=py+ny*h,Hidden=hidden,WeldId=w.Id});
                    }
                    else
                    {
                        double dx=nx*sideSign*tickLen,dy=ny*sideSign*tickLen;
                        strokes.Add(new WeldStroke{X0=px,Y0=py,X1=px+dx,Y1=py+dy,Hidden=hidden,WeldId=w.Id});
                    }
                }
            }
        }
        public static List<WeldStroke> Build(IEnumerable<ParametricProjection.Curve2> baseCurves,IEnumerable<ViewWeldOverlay> welds,double annotationScale,out int unmatched)
        {
            var list=(welds??Enumerable.Empty<ViewWeldOverlay>()).ToList();
            foreach(var w in list)Validate(w);
            double scale=annotationScale>0&&Guard.Finite(annotationScale)?annotationScale:20;
            var curves=(baseCurves??Enumerable.Empty<ParametricProjection.Curve2>()).Where(ViewLineOverrides.HasSource).ToList();
            var strokes=new List<WeldStroke>();
            unmatched=0;
            foreach(var w in list)
            {
                if(w.SectionOnly)continue;
                var matched=new List<ParametricProjection.Curve2>();
                foreach(var c in curves)
                {
                    if(c.InstanceId!=w.InstanceId||!string.Equals(c.SourceKey,w.SourceKey,StringComparison.Ordinal))continue;
                    double lo=Math.Min(c.SourceFrom,c.SourceTo),hi=Math.Max(c.SourceFrom,c.SourceTo);
                    double ovLo=Math.Max(lo,w.From),ovHi=Math.Min(hi,w.To);
                    if(ovHi-ovLo<=E)continue;
                    double d=c.SourceTo-c.SourceFrom;
                    double t0=(ovLo-c.SourceFrom)/d,t1=(ovHi-c.SourceFrom)/d;
                    double a=Math.Max(0,Math.Min(t0,t1)),b=Math.Min(1,Math.Max(t0,t1));
                    if(b-a<=E)continue;
                    var piece=c.Piece(a,b);
                    // Normalize Hidden/Cut flags so contiguous sub-pieces merge into one smooth chain.
                    piece.Hidden=false;piece.Cut=false;
                    matched.Add(piece);
                }
                if(matched.Count==0){unmatched++;continue;}
                foreach(var chain in ViewLineOverrides.Chain(matched))
                    EmitChainStrokes(chain,w,scale,strokes);
            }
            return strokes;
        }
    }
}
