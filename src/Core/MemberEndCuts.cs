using System;

namespace KotNode.Core
{
    // A complete planar end cut within the original stock envelope. Positive
    // OffsetMm adds an inward setback of the longest retained edge. Local Z is
    // the member axis; slope angles are measured in its XZ and YZ planes.
    public sealed class PlanarEndCut
    {
        public double AngleXDeg { get; set; }
        public double AngleYDeg { get; set; }
        public double OffsetMm { get; set; }
    }
    public sealed class MemberEndTreatment
    {
        public PlanarEndCut Start { get; set; }
        public PlanarEndCut End { get; set; }
    }
    public static class MemberEndCuts
    {
        public static bool HasCuts(PartDefinition part)=>part.EndCuts?.Start!=null||part.EndCuts?.End!=null;
        public static double SlopeX(PlanarEndCut cut)=>cut==null?0:Math.Tan(cut.AngleXDeg*Math.PI/180);
        public static double SlopeY(PlanarEndCut cut)=>cut==null?0:Math.Tan(cut.AngleYDeg*Math.PI/180);
        public static double Length(PartDefinition p)=>p.Kind==PartKind.Pipe?p.Pipe.Length:p.IBeam.Length;
        public static double Support(PartDefinition p,double sx,double sy)
            =>p.Kind==PartKind.Pipe?p.Pipe.OuterDiameter/2*Math.Sqrt(sx*sx+sy*sy):p.IBeam.Width/2*Math.Abs(sx)+p.IBeam.Height/2*Math.Abs(sy);
        public static double Intercept(PartDefinition p,bool start)
        {
            var c=start?p.EndCuts?.Start:p.EndCuts?.End;
            double inward=(c?.OffsetMm??0)+Support(p,SlopeX(c),SlopeY(c));
            return start?inward:Length(p)-inward;
        }
        public static double Z(PartDefinition p,bool start,double x,double y)
        {
            var c=start?p.EndCuts?.Start:p.EndCuts?.End;
            return Intercept(p,start)+SlopeX(c)*x+SlopeY(c)*y;
        }
        public static void Validate(PartDefinition part)
        {
            if(!HasCuts(part))return;
            Guard.Require(part.Kind==PartKind.Pipe||part.Kind==PartKind.IBeam,"Подрезка торцов доступна только для труб и двутавров.");
            foreach(var cut in new[]{part.EndCuts.Start,part.EndCuts.End})
                if(cut!=null)Guard.Require(Guard.Finite(cut.AngleXDeg)&&Guard.Finite(cut.AngleYDeg)&&Math.Abs(cut.AngleXDeg)<90&&Math.Abs(cut.AngleYDeg)<90&&Guard.Finite(cut.OffsetMm)&&cut.OffsetMm>=0,"Неверные углы или отступ подрезки торца.");
            var a=part.EndCuts.Start;var b=part.EndCuts.End;
            double remaining=Intercept(part,false)-Intercept(part,true)-Support(part,SlopeX(b)-SlopeX(a),SlopeY(b)-SlopeY(a));
            Guard.Require(Guard.Finite(remaining)&&remaining>1e-5,"Плоскости подрезки пересекаются или полностью срезают сечение. Увеличьте длину заготовки либо уменьшите углы/отступы.");
        }
        public static PlanarEndCut FromPlane(PartDefinition part,RigidPlacement placement,RigidPlacement worldPlane,bool start)
        {
            var plane=worldPlane.RelativeTo(placement);
            var n=plane.AxisX.Cross(plane.AxisY);
            Guard.Require(Math.Abs(n.Z)>1e-9,"Плоскость параллельна оси детали: это продольный вырез, а не подрезка торца.");
            double sx=-n.X/n.Z,sy=-n.Y/n.Z;
            double intercept=plane.Origin.Z-sx*plane.Origin.X-sy*plane.Origin.Y;
            double offset=(start?intercept:Length(part)-intercept)-Support(part,sx,sy);
            Guard.Require(Guard.Finite(offset)&&offset>=-1e-5,"Плоскость должна целиком пересекать торец внутри длины заготовки. Для подрезки от крайней точки используйте режим Angles с отступом 0.");
            return new PlanarEndCut{AngleXDeg=Math.Atan(sx)*180/Math.PI,AngleYDeg=Math.Atan(sy)*180/Math.PI,OffsetMm=Math.Max(0,offset)};
        }
        public static double TessellationFactor(PartDefinition part)
        {
            double factor=1;
            foreach(var c in new[]{part.EndCuts?.Start,part.EndCuts?.End})
                factor=Math.Max(factor,Math.Sqrt(1+Math.Pow(SlopeX(c),2)+Math.Pow(SlopeY(c),2)));
            return factor;
        }
    }
}
