using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    internal static class ParametricViewDrawing
    {
        static Curve Entity(ParametricProjection.Curve2 c)
        {
            if(!c.Circular)return new Line(new Point3d(c.X0,c.Y0,0),new Point3d(c.X1,c.Y1,0));
            var center=new Point3d(c.X0,c.Y0,0);
            if(c.Sweep>=2*Math.PI-1e-9)return new Circle(center,Vector3d.ZAxis,c.Radius);
            return new Arc(center,Vector3d.ZAxis,c.Radius,c.Start,c.Start+c.Sweep);
        }
        internal static ObjectId Build(Database db,Transaction tr,AssemblyMark mark,AssemblyViews.ViewData data)
        {
            // Compute and validate everything before adding drawing objects.
            var result=ParametricProjection.Build(mark,data.Frame,data.IsSection,data.Hidden);
            if(result.Curves.Count==0)throw new InvalidOperationException("Параметрическое представление пустое: проверьте направление и секущую плоскость.");
            var table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
            var block=new BlockTableRecord{Name="NA_PARAM_VIEW_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
            var id=table.Add(block);tr.AddNewlyCreatedDBObject(block,true);
            double scale=data.AnnotationScale>0?data.AnnotationScale:20;
            foreach(var c in result.Curves)
                using(var curve=Entity(c))AssemblyViews.StoreCurve(block,tr,db,curve,c.Hidden?AssemblyViews.HiddenLayer:c.Cut?AssemblyViews.CutLayer:AssemblyViews.VisibleLayer,c.Hidden?scale:1);
            if(data.NoHatch)return id;
            int index=0;
            foreach(var area in result.Sections)
            {
                // Native hatch boundaries; there is no generated bitmap or custom drawable.
                var hatch=new Hatch();hatch.SetDatabaseDefaults(db);hatch.Layer=AssemblyViews.HatchLayer;hatch.ColorIndex=256;
                block.AppendEntity(hatch);tr.AddNewlyCreatedDBObject(hatch,true);
                hatch.SetHatchPattern(HatchPatternType.UserDefined,"_USER");
                hatch.PatternAngle=index++%2==0?Math.PI/4:3*Math.PI/4;
                hatch.PatternSpace=2*scale;hatch.HatchStyle=HatchStyle.Normal;hatch.Associative=false;
                var boundaries=new System.Collections.Generic.List<ObjectId>();
                for(int i=0;i<area.Loops.Count;i++)
                {
                    using(var boundary=SectionHatchBoundary.Create(area.Loops[i]))
                    {
                        boundary.SetDatabaseDefaults(db);
                        boundary.Layer=AssemblyViews.CutLayer;
                        boundary.Visible=false;
                        var boundaryId=block.AppendEntity(boundary);
                        tr.AddNewlyCreatedDBObject(boundary,true);
                        boundaries.Add(boundaryId);
                        // Associative=false: AppendLoop extracts the closed geometry;
                        // temporary drawing entities are removed after evaluation.
                        hatch.AppendLoop(i==0?HatchLoopTypes.Outermost:HatchLoopTypes.Default,
                            new ObjectIdCollection(new[]{boundaryId}));
                    }
                }
                hatch.EvaluateHatch(true);
                foreach(var boundaryId in boundaries)tr.GetObject(boundaryId,OpenMode.ForWrite).Erase();
            }
            return id;
        }
    }
}
