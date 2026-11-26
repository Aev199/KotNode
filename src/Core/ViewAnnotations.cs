using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    public enum ViewAnchorKind { EdgePoint=0, CurveCenter=1, PartExtremum=2, WeldMidpoint=3 }
    public sealed class ViewAnchor
    {
        public ViewAnchorKind Kind { get; set; }
        public Guid InstanceId { get; set; }
        public string SourceKey { get; set; }
        public double Param { get; set; }
        public int Axis { get; set; }
        public int Sign { get; set; }
        public Guid WeldId { get; set; }
    }
    public sealed class AnchorSnap
    {
        public ViewAnchor Anchor { get; set; }
        public Point2 Point { get; set; }
        public double Distance { get; set; }
        public string Label { get; set; }
    }
    public enum ViewDimensionKind { Horizontal=0, Vertical=1, Aligned=2, Diameter=3, Radius=4 }
    public sealed class ViewDimension
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public ViewDimensionKind Kind { get; set; }
        public ViewAnchor AnchorA { get; set; }
        public ViewAnchor AnchorB { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public string TextOverride { get; set; }="";
        public double? TextOffsetX { get; set; }
        public double? TextOffsetY { get; set; }
        public int Precision { get; set; }
    }
    public enum ViewLeaderKind { Position=0, Weld=1, Note=2 }
    public sealed class ViewLeader
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public ViewLeaderKind Kind { get; set; }
        public ViewAnchor Target { get; set; }
        public double LandingOffsetX { get; set; }
        public double LandingOffsetY { get; set; }
        public string CustomText { get; set; }="";
    }
    public sealed class ViewAxisMark
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public ViewAnchor Center { get; set; }
        public bool Horizontal { get; set; }=true;
        public bool Vertical { get; set; }=true;
        public double OverhangScale { get; set; }=2.5;
    }
    public sealed class ViewSectionCallout
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        public Guid TargetViewId { get; set; }
        public string Label { get; set; }="1";
        public ViewAnchor Start { get; set; }
        public ViewAnchor End { get; set; }
        public int DirectionSign { get; set; }=1;
    }
    public sealed class ResolvedDimension
    {
        public ViewDimension Source { get; set; }
        public Point2 A { get; set; }
        public Point2 B { get; set; }
        public Point2 Center { get; set; }
        public double Radius { get; set; }
        public Point2 LinePoint { get; set; }
    }
    public sealed class ResolvedLeader
    {
        public ViewLeader Source { get; set; }
        public Point2 Tip { get; set; }
        public Point2 Landing { get; set; }
        public string Text { get; set; }
    }
    public sealed class AxisStroke
    {
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
    }
    public sealed class CalloutGraphics
    {
        public List<AxisStroke> ThickLines { get; set; }=new List<AxisStroke>();
        public List<AxisStroke> ThinLines { get; set; }=new List<AxisStroke>();
        public List<Tuple<Point2,Point2,Point2>> Arrows { get; set; }=new List<Tuple<Point2,Point2,Point2>>();
        public List<Tuple<Point2,double,string>> Labels { get; set; }=new List<Tuple<Point2,double,string>>();
    }
    public static class ViewAnnotations
    {
        const double E=1e-9;
        static double Dist(Point2 a,Point2 b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        public static void ValidateAnchor(ViewAnchor a)
        {
            if(a==null||!Enum.IsDefined(typeof(ViewAnchorKind),a.Kind))
                throw new InvalidDataException("Повреждён якорь оформления.");
            if(a.Kind==ViewAnchorKind.EdgePoint||a.Kind==ViewAnchorKind.CurveCenter)
            {
                if(a.InstanceId==Guid.Empty||string.IsNullOrWhiteSpace(a.SourceKey)||!Guard.Finite(a.Param)||a.Param<-E||a.Param>1+E)
                    throw new InvalidDataException("Неверная ссылка якоря на кромку.");
            }
            else if(a.Kind==ViewAnchorKind.PartExtremum)
            {
                if(a.InstanceId==Guid.Empty||(a.Axis!=0&&a.Axis!=1)||(a.Sign!=-1&&a.Sign!=1))
                    throw new InvalidDataException("Неверная ссылка якоря на габарит детали.");
            }
            else if(a.Kind==ViewAnchorKind.WeldMidpoint)
            {
                if(a.WeldId==Guid.Empty)throw new InvalidDataException("Неверная ссылка якоря на сварной шов.");
            }
        }
        public static void ValidateDimension(ViewDimension d)
        {
            if(d==null||d.Id==Guid.Empty||!Enum.IsDefined(typeof(ViewDimensionKind),d.Kind)||
                !Guard.Finite(d.OffsetX)||!Guard.Finite(d.OffsetY)||d.Precision<0||d.Precision>4)
                throw new InvalidDataException("Повреждена запись ассоциативного размера.");
            if(d.TextOffsetX.HasValue!=d.TextOffsetY.HasValue||(d.TextOffsetX.HasValue&&(!Guard.Finite(d.TextOffsetX.Value)||!Guard.Finite(d.TextOffsetY.Value))))
                throw new InvalidDataException("Неверное ручное положение текста размера.");
            ValidateAnchor(d.AnchorA);
            if(d.Kind==ViewDimensionKind.Horizontal||d.Kind==ViewDimensionKind.Vertical||d.Kind==ViewDimensionKind.Aligned)
                ValidateAnchor(d.AnchorB);
        }
        public static void ValidateLeader(ViewLeader l)
        {
            if(l==null||l.Id==Guid.Empty||!Enum.IsDefined(typeof(ViewLeaderKind),l.Kind)||
                !Guard.Finite(l.LandingOffsetX)||!Guard.Finite(l.LandingOffsetY))
                throw new InvalidDataException("Повреждена запись ассоциативной выноски.");
            ValidateAnchor(l.Target);
        }
        public static bool TryLoopCenterAndRadius(IEnumerable<ParametricProjection.Curve2> curves,out Point2 center,out double radius)
        {
            center=null;radius=0;
            var list=curves.ToList();
            if(list.Count==0)return false;
            var circ=list.FirstOrDefault(c=>c.Circular&&c.Radius>E);
            if(circ!=null){center=new Point2(circ.X0,circ.Y0);radius=circ.Radius;return true;}
            var pts=new List<Point2>();
            foreach(var c in list){pts.Add(c.At(0));pts.Add(c.At(1));}
            if(pts.Count<6)return false;
            double minX=pts.Min(p=>p.X),maxX=pts.Max(p=>p.X),minY=pts.Min(p=>p.Y),maxY=pts.Max(p=>p.Y);
            double rx=(maxX-minX)/2,ry=(maxY-minY)/2;
            if(rx<=E||ry<=E)return false;
            var cEst=new Point2((minX+maxX)/2,(minY+maxY)/2);
            double rAvg=pts.Average(p=>Dist(p,cEst));
            double maxErr=pts.Max(p=>Math.Abs(Dist(p,cEst)-rAvg));
            if(rAvg>E&&maxErr<=Math.Max(0.25,rAvg*0.02))
            {
                center=cEst;radius=rAvg;return true;
            }
            return false;
        }
        public static Point2 ResolveAnchor(ViewAnchor anchor,IEnumerable<ParametricProjection.Curve2> rawCurves,IEnumerable<ViewWeldOverlay> welds,double scale,out double radius)
        {
            radius=0;
            if(anchor==null)return null;
            var curves=(rawCurves??Enumerable.Empty<ParametricProjection.Curve2>()).ToList();
            if(anchor.Kind==ViewAnchorKind.EdgePoint)
            {
                var matched=curves.Where(c=>ViewLineOverrides.HasSource(c)&&c.InstanceId==anchor.InstanceId&&string.Equals(c.SourceKey,anchor.SourceKey,StringComparison.Ordinal)).ToList();
                if(matched.Count==0)return null;
                ParametricProjection.Curve2 best=null;double bestErr=double.PositiveInfinity;double bestT=0;
                foreach(var c in matched)
                {
                    double d=c.SourceTo-c.SourceFrom;
                    if(Math.Abs(d)<=E)continue;
                    double t=(anchor.Param-c.SourceFrom)/d;
                    double clamped=Math.Max(0,Math.Min(1,t));
                    double sAt=c.SourceFrom+d*clamped;
                    double err=Math.Abs(sAt-anchor.Param);
                    if(err<bestErr){bestErr=err;best=c;bestT=clamped;}
                }
                return best?.At(bestT);
            }
            if(anchor.Kind==ViewAnchorKind.CurveCenter)
            {
                var matched=curves.Where(c=>ViewLineOverrides.HasSource(c)&&c.InstanceId==anchor.InstanceId&&string.Equals(c.SourceKey,anchor.SourceKey,StringComparison.Ordinal)).ToList();
                if(matched.Count==0)return null;
                if(TryLoopCenterAndRadius(matched,out var c,out radius))return c;
                double minX=matched.Min(m=>Math.Min(m.At(0).X,m.At(1).X)),maxX=matched.Max(m=>Math.Max(m.At(0).X,m.At(1).X));
                double minY=matched.Min(m=>Math.Min(m.At(0).Y,m.At(1).Y)),maxY=matched.Max(m=>Math.Max(m.At(0).Y,m.At(1).Y));
                radius=Math.Max(maxX-minX,maxY-minY)/2;
                return new Point2((minX+maxX)/2,(minY+maxY)/2);
            }
            if(anchor.Kind==ViewAnchorKind.PartExtremum)
            {
                var matched=curves.Where(c=>c.InstanceId==anchor.InstanceId).ToList();
                if(matched.Count==0)return null;
                var pts=matched.SelectMany(c=>c.Circular
                    ? new[]{c.At(0),c.At(.25),c.At(.5),c.At(.75),c.At(1)}
                    : new[]{c.At(0),c.At(1)}).ToList();
                double minX=pts.Min(p=>p.X),maxX=pts.Max(p=>p.X),minY=pts.Min(p=>p.Y),maxY=pts.Max(p=>p.Y);
                if(anchor.Axis==0)return new Point2(anchor.Sign<0?minX:maxX,(minY+maxY)/2);
                return new Point2((minX+maxX)/2,anchor.Sign<0?minY:maxY);
            }
            if(anchor.Kind==ViewAnchorKind.WeldMidpoint)
            {
                var group=(welds??Enumerable.Empty<ViewWeldOverlay>()).Where(x=>x.Id==anchor.WeldId||x.GroupId==anchor.WeldId).ToList();
                if(group.Count==0)return null;
                var strokes=ViewWelds.Build(curves,group,scale,out var unmatched);
                if(unmatched>0||strokes.Count==0)return null;
                var mid=strokes[strokes.Count/2];
                return new Point2((mid.X0+mid.X1)/2,(mid.Y0+mid.Y1)/2);
            }
            return null;
        }
        public static AnchorSnap SnapAnchor(IList<CurveChain> chains,double x,double y,double tolerance,bool preferCenter=false)
        {
            if(chains==null||chains.Count==0||!Guard.Finite(x)||!Guard.Finite(y)||tolerance<=0)return null;
            var click=new Point2(x,y);
            AnchorSnap best=null;
            Action<ViewAnchor,Point2,double,string> consider=(anchor,pt,score,label)=>{
                if(best==null||score<best.Distance)best=new AnchorSnap{Anchor=anchor,Point=pt,Distance=score,Label=label};
            };
            foreach(var ch in chains)
            {
                if(!ViewLineOverrides.HasSource(ch))continue;
                if(TryLoopCenterAndRadius(ch.Segments,out var center,out var r))
                {
                    double dc=Dist(click,center);
                    if(dc<=tolerance)
                        consider(new ViewAnchor{Kind=ViewAnchorKind.CurveCenter,InstanceId=ch.InstanceId,SourceKey=ch.SourceKey},center,preferCenter?dc*0.2:dc*0.6,"Центр");
                    if(preferCenter)
                    {
                        double dr=Math.Abs(dc-r);
                        if(dr<=tolerance)
                            consider(new ViewAnchor{Kind=ViewAnchorKind.CurveCenter,InstanceId=ch.InstanceId,SourceKey=ch.SourceKey},center,dr*0.4,"Окружность (Ø"+(2*r).ToString("0.#",CultureInfo.InvariantCulture)+")");
                    }
                }
                if(!ch.IsFullLoop)
                {
                    var p0=ch.Segments[0].At(0);
                    double s0=ch.Segments[0].SourceFrom;
                    double d0=Dist(click,p0);
                    if(d0<=tolerance)
                        consider(new ViewAnchor{Kind=ViewAnchorKind.EdgePoint,InstanceId=ch.InstanceId,SourceKey=ch.SourceKey,Param=Math.Max(0,Math.Min(1,s0))},p0,d0*0.5,"Конец кромки");
                    var last=ch.Segments[ch.Segments.Count-1];
                    var p1=last.At(1);
                    double s1=last.SourceTo;
                    double d1=Dist(click,p1);
                    if(d1<=tolerance)
                        consider(new ViewAnchor{Kind=ViewAnchorKind.EdgePoint,InstanceId=ch.InstanceId,SourceKey=ch.SourceKey,Param=Math.Max(0,Math.Min(1,s1))},p1,d1*0.5,"Конец кромки");
                    // Midpoint of chain
                    var midSeg=ch.Segments[ch.Segments.Count/2];
                    var pm=midSeg.At(0.5);
                    double sm=(midSeg.SourceFrom+midSeg.SourceTo)/2;
                    double dm=Dist(click,pm);
                    if(dm<=tolerance)
                        consider(new ViewAnchor{Kind=ViewAnchorKind.EdgePoint,InstanceId=ch.InstanceId,SourceKey=ch.SourceKey,Param=Math.Max(0,Math.Min(1,sm))},pm,dm*0.7,"Середина кромки");
                }
                double dcCurve=ViewLineOverrides.DistanceToChain(ch,x,y,out var u,out _,out _);
                if(dcCurve<=tolerance)
                {
                    // Find exact point & source parameter on closest segment
                    double bestSegD=double.PositiveInfinity;Point2 bestPt=null;double bestS=0;
                    foreach(var seg in ch.Segments)
                    {
                        double sd=ViewLineOverrides.DistanceToCurve(seg,x,y,out var t,out _,out _);
                        if(sd<bestSegD){bestSegD=sd;bestPt=seg.At(t);bestS=seg.SourceFrom+(seg.SourceTo-seg.SourceFrom)*t;}
                    }
                    if(bestPt!=null)
                        consider(new ViewAnchor{Kind=ViewAnchorKind.EdgePoint,InstanceId=ch.InstanceId,SourceKey=ch.SourceKey,Param=Math.Max(0,Math.Min(1,bestS))},bestPt,bestSegD,"Точка на кромке");
                }
            }
            return best;
        }
        public static List<ResolvedDimension> ResolveDimensions(IEnumerable<ViewDimension> dims,IEnumerable<ParametricProjection.Curve2> rawCurves,IEnumerable<ViewWeldOverlay> welds,double scale,out int unmatched)
        {
            var result=new List<ResolvedDimension>();unmatched=0;
            if(dims==null)return result;
            var curves=(rawCurves??Enumerable.Empty<ParametricProjection.Curve2>()).ToList();
            var weldList=(welds??Enumerable.Empty<ViewWeldOverlay>()).ToList();
            foreach(var d in dims)
            {
                ValidateDimension(d);
                if(d.Kind==ViewDimensionKind.Diameter||d.Kind==ViewDimensionKind.Radius)
                {
                    var c=ResolveAnchor(d.AnchorA,curves,weldList,scale,out var r);
                    if(c==null||r<=1e-6){unmatched++;continue;}
                    double ang=Math.Atan2(d.OffsetY,d.OffsetX);
                    if(Math.Abs(d.OffsetX)+Math.Abs(d.OffsetY)<=1e-6)ang=Math.PI/4;
                    var a=new Point2(c.X+r*Math.Cos(ang),c.Y+r*Math.Sin(ang));
                    var b=new Point2(c.X-r*Math.Cos(ang),c.Y-r*Math.Sin(ang));
                    result.Add(new ResolvedDimension{Source=d,A=a,B=b,Center=c,Radius=r,LinePoint=new Point2(c.X+d.OffsetX,c.Y+d.OffsetY)});
                }
                else
                {
                    var a=ResolveAnchor(d.AnchorA,curves,weldList,scale,out _);
                    var b=ResolveAnchor(d.AnchorB,curves,weldList,scale,out _);
                    if(a==null||b==null||Dist(a,b)<=1e-6){unmatched++;continue;}
                    var mid=new Point2((a.X+b.X)/2,(a.Y+b.Y)/2);
                    result.Add(new ResolvedDimension{Source=d,A=a,B=b,LinePoint=new Point2(mid.X+d.OffsetX,mid.Y+d.OffsetY)});
                }
            }
            return result;
        }
        public static string FormatLeaderText(ViewLeader leader,AssemblyMark mark,IEnumerable<ViewWeldOverlay> welds)
        {
            if(!string.IsNullOrWhiteSpace(leader.CustomText))return leader.CustomText;
            if(leader.Kind==ViewLeaderKind.Position)
            {
                var inst=mark.Instances.FirstOrDefault(i=>i.Id==leader.Target.InstanceId);
                var part=inst==null?null:mark.Parts.FirstOrDefault(p=>p.Id==inst.PartId);
                if(part==null)return "?";
                if(part.IncludeInSpec&&!string.IsNullOrWhiteSpace(part.Position))
                    return "Поз. "+part.Position.Trim();
                return part.Name;
            }
            if(leader.Kind==ViewLeaderKind.Weld)
            {
                var w=(welds??Enumerable.Empty<ViewWeldOverlay>()).FirstOrDefault(x=>x.Id==leader.Target.WeldId||x.GroupId==leader.Target.WeldId);
                if(w==null)return "Шов";
                string k=w.LegMm.ToString("0.#",CultureInfo.InvariantCulture);
                string suffix=w.Style==ViewWeldStyle.Site?" (монт.)":"";
                return "{\\fArial|b0|i0;\\U+25B3}"+k+suffix;
            }
            return "";
        }
        public static List<ResolvedLeader> ResolveLeaders(IEnumerable<ViewLeader> leaders,AssemblyMark mark,IEnumerable<ParametricProjection.Curve2> rawCurves,IEnumerable<ViewWeldOverlay> welds,double scale,out int unmatched)
        {
            var result=new List<ResolvedLeader>();unmatched=0;
            if(leaders==null)return result;
            var curves=(rawCurves??Enumerable.Empty<ParametricProjection.Curve2>()).ToList();
            var weldList=(welds??Enumerable.Empty<ViewWeldOverlay>()).ToList();
            foreach(var l in leaders)
            {
                ValidateLeader(l);
                var tip=ResolveAnchor(l.Target,curves,weldList,scale,out _);
                if(tip==null){unmatched++;continue;}
                var landing=new Point2(tip.X+l.LandingOffsetX,tip.Y+l.LandingOffsetY);
                result.Add(new ResolvedLeader{Source=l,Tip=tip,Landing=landing,Text=FormatLeaderText(l,mark,weldList)});
            }
            return result;
        }
        public static List<AxisStroke> BuildAxes(IEnumerable<ViewAxisMark> axes,IEnumerable<ParametricProjection.Curve2> rawCurves,double annotationScale,out int unmatched)
        {
            var result=new List<AxisStroke>();unmatched=0;
            if(axes==null)return result;
            double scale=annotationScale>0?annotationScale:20;
            var curves=(rawCurves??Enumerable.Empty<ParametricProjection.Curve2>()).ToList();
            foreach(var ax in axes)
            {
                ValidateAnchor(ax.Center);
                var center=ResolveAnchor(ax.Center,curves,null,scale,out var r);
                if(center==null){unmatched++;continue;}
                double half=Math.Max(r,5.0)+(ax.OverhangScale>0?ax.OverhangScale:2.5)*scale;
                if(ax.Horizontal)result.Add(new AxisStroke{X0=center.X-half,Y0=center.Y,X1=center.X+half,Y1=center.Y});
                if(ax.Vertical)result.Add(new AxisStroke{X0=center.X,Y0=center.Y-half,X1=center.X,Y1=center.Y+half});
            }
            return result;
        }
        public static CalloutGraphics BuildSectionCallouts(IEnumerable<ViewSectionCallout> callouts,IEnumerable<ParametricProjection.Curve2> rawCurves,Func<Guid,string> viewNameLookup,double annotationScale,out int unmatched)
        {
            var g=new CalloutGraphics();unmatched=0;
            if(callouts==null)return g;
            double scale=annotationScale>0?annotationScale:20;
            double h=2.5*scale;
            var curves=(rawCurves??Enumerable.Empty<ParametricProjection.Curve2>()).ToList();
            foreach(var c in callouts)
            {
                ValidateAnchor(c.Start);ValidateAnchor(c.End);
                var a=ResolveAnchor(c.Start,curves,null,scale,out _);
                var b=ResolveAnchor(c.End,curves,null,scale,out _);
                if(a==null||b==null||Dist(a,b)<=1e-6){unmatched++;continue;}
                double dx=b.X-a.X,dy=b.Y-a.Y,len=Math.Sqrt(dx*dx+dy*dy);
                double tx=dx/len,ty=dy/len;
                int dir=c.DirectionSign>=0?1:-1;
                double nx=-ty*dir,ny=tx*dir;
                double strokeLen=3.2*h,over=1.5*h;
                var pA=new Point2(a.X-tx*over,a.Y-ty*over);
                var pB=new Point2(b.X+tx*over,b.Y+ty*over);
                string label=c.TargetViewId!=Guid.Empty&&viewNameLookup!=null?viewNameLookup(c.TargetViewId):null;
                if(string.IsNullOrWhiteSpace(label))label=string.IsNullOrWhiteSpace(c.Label)?"1":c.Label;
                // Extract short tag if view name is "1-1" -> "1"
                int dash=label.IndexOf('-');
                string markText=dash>0?label.Substring(0,dash).Trim():label.Trim();
                foreach(var pair in new[]{Tuple.Create(pA,1.0),Tuple.Create(pB,-1.0)})
                {
                    var tip=pair.Item1;double sgn=pair.Item2;
                    var end=new Point2(tip.X+tx*sgn*strokeLen,tip.Y+ty*sgn*strokeLen);
                    g.ThickLines.Add(new AxisStroke{X0=tip.X,Y0=tip.Y,X1=end.X,Y1=end.Y});
                    var arrowTip=new Point2(tip.X+tx*sgn*strokeLen*0.35,tip.Y+ty*sgn*strokeLen*0.35);
                    var arrowTail=new Point2(arrowTip.X-nx*h*2.0,arrowTip.Y-ny*h*2.0);
                    g.ThinLines.Add(new AxisStroke{X0=arrowTail.X,Y0=arrowTail.Y,X1=arrowTip.X,Y1=arrowTip.Y});
                    var base1=new Point2(arrowTip.X-nx*h*0.9+tx*h*0.25,arrowTip.Y-ny*h*0.9+ty*h*0.25);
                    var base2=new Point2(arrowTip.X-nx*h*0.9-tx*h*0.25,arrowTip.Y-ny*h*0.9-ty*h*0.25);
                    g.Arrows.Add(Tuple.Create(arrowTip,base1,base2));
                    var textAt=new Point2(arrowTail.X-nx*h*0.6-tx*sgn*h*0.5,arrowTail.Y-ny*h*0.6-ty*sgn*h*0.5);
                    g.Labels.Add(Tuple.Create(textAt,h*1.4,markText));
                }
            }
            return g;
        }
    }
}
