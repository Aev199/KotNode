using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    internal static class ParametricViewDrawing
    {
        internal const string LineSourceTag="KOTNODE_LINE_SOURCE_1";
        internal static ParametricProjection.Curve2 Source(Transaction tr,Entity entity)
        {
            if(entity==null||entity.ExtensionDictionary.IsNull)return null;
            return AssemblyDrawing.Read<ParametricProjection.Curve2>((DBDictionary)tr.GetObject(entity.ExtensionDictionary,OpenMode.ForRead),LineSourceTag,tr);
        }
        internal static Curve CreateEntity(ParametricProjection.Curve2 c)
        {
            if(!c.Circular)return new Line(new Point3d(c.X0,c.Y0,0),new Point3d(c.X1,c.Y1,0));
            var center=new Point3d(c.X0,c.Y0,0);
            if(c.Sweep>=2*Math.PI-1e-9)return new Circle(center,Vector3d.ZAxis,c.Radius);
            return new Arc(center,Vector3d.ZAxis,c.Radius,c.Start,c.Start+c.Sweep);
        }
        internal static Curve CreateEntity(CurveChain chain)
        {
            if(chain.Segments.Count==1)return CreateEntity(chain.Segments[0]);
            int count=chain.Closed?chain.Segments.Count:chain.Segments.Count+1;
            var poly=new Polyline(count);
            try
            {
                for(int i=0;i<chain.Segments.Count;i++)
                {
                    var s=chain.Segments[i];
                    poly.AddVertexAt(i,new Point2d(s.X0,s.Y0),0,0,0);
                }
                if(!chain.Closed)
                {
                    var last=chain.Segments[chain.Segments.Count-1];
                    poly.AddVertexAt(chain.Segments.Count,new Point2d(last.X1,last.Y1),0,0,0);
                }
                poly.Closed=chain.Closed;
                poly.Plinegen=true;
                return poly;
            }
            catch {poly.Dispose();throw;}
        }
        internal static List<CurveChain> ComputeChains(AssemblyMark mark,AssemblyViews.ViewData data)
        {
            var frame=AssemblyViews.ResolveFrame(mark,data);
            var result=ParametricProjection.Build(mark,frame,data.IsSection,true);
            var curves=ViewLineOverrides.Apply(result.Curves,data.LineOverrides,data.Hidden,out _);
            return ViewLineOverrides.Chain(curves);
        }
        internal static ObjectId Build(Database db,Transaction tr,AssemblyMark mark,AssemblyViews.ViewData data)
        {
            // Compute and validate everything before adding drawing objects.
            var result=ParametricProjection.Build(mark,data.Frame,data.IsSection,true);
            result.Curves=ViewLineOverrides.Apply(result.Curves,data.LineOverrides,data.Hidden,out var unmatched);
            data.UnmatchedLineOverrides=unmatched;
            if(result.Curves.Count==0)throw new InvalidOperationException("Параметрическое представление пустое: проверьте направление и секущую плоскость.");
            var chains=ViewLineOverrides.Chain(result.Curves);
            var table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
            var block=new BlockTableRecord{Name="NA_PARAM_VIEW_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
            var id=table.Add(block);tr.AddNewlyCreatedDBObject(block,true);
            double scale=data.AnnotationScale>0?data.AnnotationScale:20;
            double globalScale=db.Ltscale>1e-6?db.Ltscale:1;
            var types=(LinetypeTable)tr.GetObject(db.LinetypeTableId,OpenMode.ForRead);
            var dashed=types["NA_DASHED"];var continuous=types["Continuous"];
            double period=((LinetypeTableRecord)tr.GetObject(dashed,OpenMode.ForRead)).PatternLength;
            if(period<=1e-6)period=6;
            foreach(var chain in chains)
                using(var curve=CreateEntity(chain))
                {
                    var layer=chain.Hidden?AssemblyViews.HiddenLayer:chain.Cut?AssemblyViews.CutLayer:AssemblyViews.VisibleLayer;
                    double ltScale=chain.Hidden?ViewLineOverrides.HiddenLineScale(chain.TotalLength,scale,globalScale,period):1;
                    var curveId=AssemblyViews.StoreCurve(block,tr,db,curve,layer,ltScale);
                    var entity=(Entity)tr.GetObject(curveId,OpenMode.ForWrite);
                    // Set the native linetype explicitly, even when existing DWG layers were customized.
                    entity.LinetypeId=chain.Hidden?dashed:continuous;
                    entity.LinetypeScale=ltScale;
                    if(entity is Polyline poly)poly.Plinegen=true;
                    if(chain.Segments.Count==1&&ViewLineOverrides.HasSource(chain.Segments[0]))
                    {
                        entity.CreateExtensionDictionary();
                        AssemblyDrawing.Write((DBDictionary)tr.GetObject(entity.ExtensionDictionary,OpenMode.ForRead),LineSourceTag,chain.Segments[0],tr);
                    }
                }
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
                var boundaries=new List<ObjectId>();
                for(int i=0;i<area.Loops.Count;i++)
                {
                    var boundary=SectionHatchBoundary.Create(area.Loops[i]);
                    try
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
                    catch {if(boundary.ObjectId.IsNull)boundary.Dispose();throw;}
                }
                hatch.EvaluateHatch(true);
                foreach(var boundaryId in boundaries)tr.GetObject(boundaryId,OpenMode.ForWrite).Erase();
            }
            return id;
        }
    }
}
