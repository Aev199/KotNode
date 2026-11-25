using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    // Give Hatch an explicit closed native curve, rather than asking it to
    // interpret an implicitly closed vertex/bulge array.
    internal static class SectionHatchBoundary
    {
        internal static Curve Create(IList<ParametricProjection.Curve2> loop)
        {
            if(loop==null||loop.Count==0)throw new InvalidDataException("Пустая граница штриховки.");
            if(loop.Count==1&&loop[0].Circular)
            {
                var c=loop[0];
                if(c.Radius<=0||Math.Abs(c.Sweep-2*Math.PI)>1e-9)
                    throw new InvalidDataException("Граница штриховки должна быть полной окружностью.");
                return new Circle(new Point3d(c.X0,c.Y0,0),Vector3d.ZAxis,c.Radius);
            }
            if(loop.Count<3)throw new InvalidDataException("Недостаточно сторон границы штриховки.");
            for(int i=0;i<loop.Count;i++)
            {
                var c=loop[i];var next=loop[(i+1)%loop.Count];
                if(c.Circular)throw new InvalidDataException("Смешанная дуговая граница пока не поддержана.");
                double dx=c.X1-next.X0,dy=c.Y1-next.Y0;
                if(dx*dx+dy*dy>1e-12)
                    throw new InvalidDataException("Граница штриховки не замкнута или содержит разрыв.");
            }
            var boundary=new Polyline(loop.Count);
            try
            {
                for(int i=0;i<loop.Count;i++)
                    boundary.AddVertexAt(i,new Point2d(loop[i].X0,loop[i].Y0),0,0,0);
                boundary.Closed=true;
                return boundary;
            }
            catch {boundary.Dispose();throw;}
        }
    }
}
