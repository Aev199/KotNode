using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    internal static class MemberCutDrawing
    {
        internal static void Apply(Solid3d solid,PartDefinition part)
        {
            if(!MemberEndCuts.HasCuts(part))return;
            foreach(bool start in new[]{true,false})
            {
                var cut=start?part.EndCuts.Start:part.EndCuts.End;
                if(cut==null||(cut.AngleXDeg==0&&cut.AngleYDeg==0&&cut.OffsetMm==0))continue;
                double sx=MemberEndCuts.SlopeX(cut),sy=MemberEndCuts.SlopeY(cut);
                // Slice retains the positive-normal half. For Start it is +Z;
                // for End it is -Z. The discarded half is not allocated.
                var normal=start?new Vector3d(-sx,-sy,1):new Vector3d(sx,sy,-1);
                using(var plane=new Plane(new Point3d(0,0,MemberEndCuts.Intercept(part,start)),normal))
                    solid.Slice(plane,false);
            }
        }
    }
}
