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
    public static class ViewLineOverrides
    {
        const double E=1e-10;
        public static bool HasSource(ParametricProjection.Curve2 c)=>c!=null&&c.InstanceId!=Guid.Empty&&!string.IsNullOrEmpty(c.SourceKey)&&Math.Abs(c.SourceTo-c.SourceFrom)>E;
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
