using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace KotNode.Core
{
    public sealed class XY
    {
        public double X { get; set; }
        public double Y { get; set; }
        public XY() { }
        public XY(double x,double y) { X=x;Y=y; }
        public static XY operator +(XY a,XY b) { return new XY(a.X+b.X,a.Y+b.Y); }
        public static XY operator -(XY a,XY b) { return new XY(a.X-b.X,a.Y-b.Y); }
        public static XY operator *(XY a,double k) { return new XY(a.X*k,a.Y*k); }
        public double Distance(XY b) { return Math.Sqrt((X-b.X)*(X-b.X)+(Y-b.Y)*(Y-b.Y)); }
    }
    public sealed class Primitive
    {
        public string Kind { get; set; }
        public string Layer { get; set; }
        public string Tag { get; set; }
        public XY A { get; set; }
        public XY B { get; set; }
        public XY C { get; set; }
        public double Radius { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public double Height { get; set; }
        public string Text { get; set; }
    }
    public sealed class DimensionPlan
    {
        public string Key { get; set; }
        public string Kind { get; set; }
        public XY A { get; set; }
        public XY B { get; set; }
        public XY Center { get; set; }
        public XY Line { get; set; }
        public XY TextAt { get; set; }
        public string Text { get; set; } = "";
        public int Precision { get; set; }
    }
    public sealed class LeaderPlan
    {
        public string Key { get; set; }
        public List<XY> Tips { get; set; } = new List<XY>();
        public XY Landing { get; set; }
        public string Text { get; set; }
    }
    public sealed class ViewPlan
    {
        public string Role { get; set; }
        public List<Primitive> Geometry { get; set; } = new List<Primitive>();
        public List<DimensionPlan> Dimensions { get; set; } = new List<DimensionPlan>();
        public List<LeaderPlan> Leaders { get; set; } = new List<LeaderPlan>();
    }
    public sealed class WeldEdge
    {
        public int Plate { get; set; }
        public int Side { get; set; }
        public XY A { get; set; }
        public XY B { get; set; }
        public XY C { get; set; }
        public bool Visible { get; set; }
    }
    public sealed class WeldRow
    {
        public double Edge { get; set; }
        public bool Visible { get; set; }
        public int Direction { get; set; }
    }
    public static class Drafting
    {
        public const int Revision=2;
        public static string F(double v) { return v.ToString("0.##",CultureInfo.InvariantCulture); }
        static XY P(double x,double y) { return new XY(x,y); }
        static XY Polar(XY c,double radius,double angle) { return c+P(radius*Math.Cos(angle),radius*Math.Sin(angle)); }
        public static XY KeepOffset(XY actual,XY oldDefault,XY newDefault) { return actual-oldDefault+newDefault; }
        public static List<List<XY>> Sectors(SpliceParameters p)
        {
            var g=Splice.Calculate(p);var sectors=new List<List<XY>>();
            for(int j=0;j<p.PlateCount;j++)
            {
                double theta=j*2*Math.PI/p.PlateCount;var c=Polar(P(0,0),g.Shift,theta);var points=new List<XY>();
                for(int i=0;i<=32;i++)points.Add(Polar(c,g.OuterRadius,theta-g.Angle/2+g.Angle*i/32));
                for(int i=32;i>=0;i--)points.Add(Polar(c,g.InnerRadius,theta-g.Angle/2+g.Angle*i/32));
                sectors.Add(points);
            }
            return sectors;
        }
        public static List<WeldEdge> Edges(SpliceParameters p)
        {
            var g=Splice.Calculate(p);double r=p.PipeDiameter/2;var list=new List<WeldEdge>();
            for(int j=0;j<p.PlateCount;j++)foreach(int side in new[]{-1,1})
            {
                double theta=j*2*Math.PI/p.PlateCount,a=theta+side*g.Angle/2;var center=Polar(P(0,0),g.Shift,theta);
                var inner=Polar(center,g.InnerRadius,a);var outer=Polar(center,g.OuterRadius,a);double beta=Math.Atan2(inner.Y,inner.X);
                list.Add(new WeldEdge{Plate=j,Side=side,A=inner,B=outer,C=Polar(P(0,0),r,beta+side*p.WeldLeg/r),Visible=inner.X>=-1e-8});
            }
            return list;
        }
        public static List<WeldRow> Rows(SpliceParameters p)
        {
            var sectors=Sectors(p);var list=new List<WeldRow>();
            foreach(var edge in Edges(p))
            {
                double lo=sectors[edge.Plate].Min(q=>q.Y),hi=sectors[edge.Plate].Max(q=>q.Y),mid=(edge.A.Y+edge.B.Y)/2;
                int dir=Math.Abs(mid-lo)<=Math.Abs(mid-hi)?-1:1;double y=dir==-1?lo:hi;
                var row=list.FirstOrDefault(q=>Math.Abs(q.Edge-y)<1e-6&&q.Direction==dir);
                if(row==null)list.Add(new WeldRow{Edge=y,Visible=edge.Visible,Direction=dir});else row.Visible|=edge.Visible;
            }
            return list.OrderByDescending(q=>q.Edge).ToList();
        }
        public static double WeldRadius(WeldRow row,List<WeldRow> rows,double h,double length)
        {
            double radius=Math.Min(h*.12,length/4);
            foreach(var other in rows){double gap=row.Direction*(other.Edge-row.Edge);if(gap>1e-6)radius=Math.Min(radius,gap*.22);}
            return radius;
        }
        public static double WeldY(WeldRow row,List<WeldRow> rows,double h,double length) { return row.Edge+row.Direction*WeldRadius(row,rows,h,length); }
        static void Raw(ViewPlan v,string tag,XY a,XY b,string layer) { if(a.Distance(b)>1e-9)v.Geometry.Add(new Primitive{Kind="line",Tag=tag,A=a,B=b,Layer=layer}); }
        static void Line(ViewPlan v,string tag,XY a,XY b,string layer,double h)
        {
            if(layer!="NP_HIDDEN"&&layer!="NP_AXIS"){Raw(v,tag,a,b,layer);return;}
            double length=a.Distance(b);if(length<=1e-8)return;
            var pattern=layer=="NP_AXIS"?new[]{h*3.2,-h*.48,h*.32,-h*.48}:new[]{h*.96,-h*.48};
            double distance=0;int i=0;
            while(distance<length-1e-6){double step=pattern[i++%pattern.Length],end=Math.Min(length,distance+Math.Abs(step));if(step>0)Raw(v,tag,a+(b-a)*(distance/length),a+(b-a)*(end/length),layer);distance=end;}
        }
        static void Rect(ViewPlan v,string tag,double x,double lo,double xx,double hi,string layer,double h)
        {Line(v,tag,P(x,lo),P(xx,lo),layer,h);Line(v,tag,P(xx,lo),P(xx,hi),layer,h);Line(v,tag,P(xx,hi),P(x,hi),layer,h);Line(v,tag,P(x,hi),P(x,lo),layer,h);}
        static void Text(ViewPlan v,string tag,XY p,double h,string text) { v.Geometry.Add(new Primitive{Kind="text",Tag=tag,A=p,Height=h,Text=text,Layer="NP_ANNOTATION"}); }
        static void Cross(ViewPlan v,string tag,XY c,double r) { Raw(v,tag,c-P(r,r),c+P(r,r),"NP_WELDS");Raw(v,tag,c+P(-r,r),c+P(r,-r),"NP_WELDS"); }
        static void Triangle(ViewPlan v,string tag,XY a,XY b,XY c,string layer) {v.Geometry.Add(new Primitive{Kind="solid",Tag=tag,A=a,B=b,C=c,Layer=layer});}
        static void Break(ViewPlan v,double x,double r,double h)
        {
            // Sampled S-shaped pipe break from 0.7.
            var points=new List<XY>();for(int i=0;i<=48;i++){double y=r*(1-2.0*i/48);points.Add(P(x+r*.13*Math.Sin(2*Math.PI*i/48),y));}
            for(int i=1;i<points.Count;i++)Raw(v,"pipe-break",points[i-1],points[i],"NP_GEOMETRY");
        }
        static void Cut(ViewPlan v,double x,double y,int sign,double h,string number)
        {
            double yy=y+sign*h*3;Triangle(v,"cut",P(x,y),P(x+h*.4,y),P(x,yy),"NP_ANNOTATION");Triangle(v,"cut",P(x+h*.4,y),P(x+h*.4,yy),P(x,yy),"NP_ANNOTATION");
            var a=P(x-h*1.6,y+sign*h*.75);var tip=P(x,y+sign*h*.75);Raw(v,"cut-arrow",a,tip,"NP_ANNOTATION");Triangle(v,"cut-arrow",tip,tip+P(-h,h*.22),tip+P(-h,-h*.22),"NP_ANNOTATION");Text(v,"cut-number",P(x-h*1.5,y+sign*h*1.8),h,number);
        }
        public static ViewPlan Build(NodeMark mark,string role,double scale)
        {
            if(double.IsNaN(scale)||double.IsInfinity(scale)||scale<1||scale>1000)throw new ArgumentOutOfRangeException(nameof(scale));
            var p=mark.Parameters;var g=Splice.Calculate(p);double h=2.5*scale,r=p.PipeDiameter/2,L=p.PlateLength,tail=Math.Max(160,p.PipeDiameter*.36),end=L/2+tail;
            var v=new ViewPlan{Role=role};var sectors=Sectors(p);var edges=Edges(p);var rows=Rows(p);string section=mark.SectionLabel??"1";
            string weld="Н1 {\\fArial|b0|i0;\\U+25B3}"+F(p.WeldLeg)+"\\PГОСТ 5264-80*";
            if(role=="main")
            {
                Text(v,"title",P(-L/2,r+h*10),h*1.3,mark.Code+" — Узел стыка труб %%c"+F(p.PipeDiameter)+"x"+F(p.PipeThickness));
                foreach(double y in new[]{r,-r}){Line(v,"pipe-profile",P(-end,y),P(-g.Gap/2,y),"NP_GEOMETRY",h);Line(v,"pipe-profile",P(g.Gap/2,y),P(end,y),"NP_GEOMETRY",h);}
                foreach(double y in new[]{r-p.PipeThickness,-r+p.PipeThickness}){Line(v,"pipe-wall",P(-end,y),P(-g.Gap/2,y),"NP_HIDDEN",h);Line(v,"pipe-wall",P(g.Gap/2,y),P(end,y),"NP_HIDDEN",h);}
                Break(v,-end,r,h);Break(v,end,r,h);
                foreach(double x in new[]{-g.Gap/2,g.Gap/2})Line(v,"pipe-end",P(x,-r),P(x,r),"NP_GEOMETRY",h);
                foreach(bool visible in new[]{false,true})for(int j=0;j<p.PlateCount;j++)
                {
                    if((Math.Cos(j*2*Math.PI/p.PlateCount)>=-1e-8)!=visible)continue;
                    Rect(v,"plate-"+j,-L/2,sectors[j].Min(q=>q.Y),L/2,sectors[j].Max(q=>q.Y),visible?"NP_DETAILS":"NP_HIDDEN",h);
                }
                foreach(var row in rows)
                {
                    double radius=WeldRadius(row,rows,h,L),y=WeldY(row,rows,h,L);int count=Math.Max(1,Math.Min(500,(int)(L/(radius*4))));
                    for(int i=0;i<count;i++)Cross(v,"long-weld",P(-L/2+L*(i+.5)/count,y),radius);
                }
                if(p.FrontWelds??true)for(int j=0;j<p.PlateCount;j++)if(Math.Cos(j*2*Math.PI/p.PlateCount)>=-1e-8)
                {
                    double lo=sectors[j].Min(q=>q.Y),hi=sectors[j].Max(q=>q.Y),radius=Math.Min(h*.12,(hi-lo)/4);int count=Math.Max(1,Math.Min(500,(int)((hi-lo)/(radius*4))));
                    for(int i=0;i<count;i++)foreach(int sign in new[]{-1,1})Cross(v,"front-weld",P(sign*(L/2+radius),lo+(hi-lo)*(i+.5)/count),radius);
                }
                Line(v,"axis",P(-end-h*2,0),P(end+h*2,0),"NP_AXIS",h);Line(v,"axis",P(0,-r-h*2),P(0,r+h*2),"NP_AXIS",h);
                if(p.ButtWeld)Line(v,"butt",P(0,-r),P(0,r),"NP_WELDS",h);
                v.Dimensions.Add(new DimensionPlan{Key="length",Kind="aligned",A=P(-L/2,-r),B=P(L/2,-r),Line=P(0,-r-h*4.5)});
                if(g.Gap>1e-6)v.Dimensions.Add(new DimensionPlan{Key="gap",Kind="aligned",A=P(-g.Gap/2,r),B=P(g.Gap/2,r),Line=P(0,r+h*2.3)});
                Cut(v,-h*4.5,r+h*3.5,1,h,section);Cut(v,-h*4.5,-r-h*7.5,-1,h,section);
                var front=rows.Where(q=>q.Visible).ToList();double upper=front.Count>0?WeldY(front[0],front,h,L):r,lower=front.Count>1?WeldY(front[1],front,h,L):upper;
                v.Leaders.Add(new LeaderPlan{Key="weld-main",Text=weld,Tips=new List<XY>{P(-L*.43,upper),P(-L*.32,lower>0?lower:upper)},Landing=P(-L/2-h*8,r+h*3)});
                v.Leaders.Add(new LeaderPlan{Key="pipe-main",Text="Труба %%c"+F(p.PipeDiameter)+"x"+F(p.PipeThickness),Tips=new List<XY>{P(-L/2-tail*.25,-r)},Landing=P(-L/2-tail*.7-h*7,-r-h*3)});
                if(p.ButtWeld)v.Leaders.Add(new LeaderPlan{Key="butt",Text="С8 k="+F(p.WeldLeg)+"\\PГОСТ 16037-80",Tips=new List<XY>{P(0,r)},Landing=P(h*5,r+h*4)});
            }
            else if(role=="section")
            {
                Text(v,"title",P(-h,r+h*10),h*1.3,section+"-"+section);
                foreach(double radius in new[]{r,r-p.PipeThickness})v.Geometry.Add(new Primitive{Kind="circle",Tag="pipe-section",A=P(0,0),Radius=radius,Layer="NP_GEOMETRY"});
                for(int j=0;j<p.PlateCount;j++)
                {
                    double theta=j*2*Math.PI/p.PlateCount,a1=theta-g.Angle/2,a2=theta+g.Angle/2;var center=Polar(P(0,0),g.Shift,theta);var pt1=Polar(center,g.OuterRadius,a1);var pt2=Polar(center,g.OuterRadius,a2);var mid=Polar(center,g.OuterRadius,theta);
                    foreach(double radius in new[]{g.InnerRadius,g.OuterRadius})v.Geometry.Add(new Primitive{Kind="arc",Tag="plate-"+j,A=center,Radius=radius,Start=a1,End=a2,Layer="NP_DETAILS"});
                    foreach(double a in new[]{a1,a2})Raw(v,"plate-end-"+j,Polar(center,g.InnerRadius,a),Polar(center,g.OuterRadius,a),"NP_DETAILS");
                    v.Dimensions.Add(new DimensionPlan{Key="arc-"+j,Kind="arc",Center=center,A=pt1,B=pt2,Line=Polar(center,g.OuterRadius+h*3,theta),TextAt=Polar(center,g.OuterRadius+h*4,theta)});
                    if(j==p.PlateCount/4)
                    {
                        for(int sign=-1;sign<=1;sign+=2)v.Dimensions.Add(new DimensionPlan{Key="half-"+(sign<0?"left":"right"),Kind="angle",Center=center,A=sign<0?pt1:mid,B=sign<0?mid:pt2,Line=Polar(center,r*.45,theta+sign*g.Angle/4),TextAt=Polar(center,r*.72,theta+sign*g.Angle/4),Text=F(g.Angle*90/Math.PI)+"%%d",Precision=1});
                    }
                }
                foreach(var e in edges)Triangle(v,"section-weld-"+e.Plate+"-"+e.Side,e.A,e.B,e.C,"NP_WELDS");
                Line(v,"axis",P(-r-h*2,0),P(r+h*2,0),"NP_AXIS",h);Line(v,"axis",P(0,-r-h*2),P(0,r+h*2),"NP_AXIS",h);
                if(p.PlateCount>1&&p.ClearSpacing>1e-6)
                {
                    double q=Math.PI/p.PlateCount,half=g.Angle/2,rr=half<=q?g.InnerRadius:g.OuterRadius;var a=P(g.Shift+rr*Math.Cos(half),rr*Math.Sin(half));var b=Polar(P(0,0),g.Shift,2*q)+Polar(P(0,0),rr,2*q-half);
                    v.Dimensions.Add(new DimensionPlan{Key="spacing",Kind="aligned",A=a,B=b,Line=(a+b)*.5+Polar(P(0,0),h*6.5,q),Text="c=<>",Precision=1});
                }
                v.Leaders.Add(new LeaderPlan{Key="position",Text=mark.DetailCode,Tips=new List<XY>{P(g.OuterRadius+g.Shift,0)},Landing=P(g.OuterRadius+g.Shift+h*4,r*.28)});
                var edge=edges.OrderByDescending(e=>e.A.Y-e.A.X).First();v.Leaders.Add(new LeaderPlan{Key="weld-section",Text=weld,Tips=new List<XY>{edge.A},Landing=P(-r-h*7,r+h*3)});
            }
            else throw new ArgumentException("Unknown view role");
            foreach(var dim in v.Dimensions)if(dim.TextAt==null)dim.TextAt=dim.Line+P(0,h*.8);
            return v;
        }
        public static XY SectionOffset(NodeMark mark,double scale)
        {double r=mark.Parameters.PipeDiameter,L=mark.Parameters.PlateLength,h=scale*2.5;return P(L/2+Math.Max(160,r*.36)+r*.95+h*8,0);}
        public static XY SpecOffset(NodeMark mark,double scale)
        {double r=mark.Parameters.PipeDiameter/2,h=scale*2.5;return P(-mark.Parameters.PlateLength/2-Math.Max(160,mark.Parameters.PipeDiameter*.36),-r-h*14);}
    }
}
