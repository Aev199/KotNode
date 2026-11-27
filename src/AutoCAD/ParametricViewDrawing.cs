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
            var raw=ComputeRawCurves(mark,data);
            return ViewLineOverrides.Chain(ViewLineOverrides.Apply(raw,data.LineOverrides,data.Hidden,out _));
        }
        static List<ViewAnchor> Anchors(AssemblyViews.ViewData d)
        {
            var result=new List<ViewAnchor>();
            foreach(var dim in d.Dimensions??new List<ViewDimension>()){result.Add(dim.AnchorA);result.Add(dim.AnchorB);}
            foreach(var leader in d.Leaders??new List<ViewLeader>())result.Add(leader.Target);
            foreach(var axis in d.Axes??new List<ViewAxisMark>())result.Add(axis.Center);
            foreach(var callout in d.SectionCallouts??new List<ViewSectionCallout>()){result.Add(callout.Start);result.Add(callout.End);}
            return result;
        }
        internal static List<ParametricProjection.Curve2> ComputeRawCurves(AssemblyMark mark,AssemblyViews.ViewData data)
            =>ComputeResult(mark,data).Curves;
        static ParametricProjection.Result ComputeResult(AssemblyMark mark,AssemblyViews.ViewData data)
        {
            var frame=AssemblyViews.ResolveFrame(mark,data);
            var result=ParametricProjection.BuildStable(mark,frame,data.IsSection,true);
            var stable=result.Curves;
            var anchors=Anchors(data);
            if(ViewReferenceMigration.Needed(data.LineOverrides,data.Welds,anchors))
            {
                // Reconstruct legacy reference geometry without unrelated new shape
                // types that used to switch the whole scene to a different engine.
                var legacy=KotNode.Core.Json.Copy(mark);
                legacy.Instances.RemoveAll(i=>{var p=legacy.Parts.Single(x=>x.Id==i.PartId);return p.Kind!=PartKind.Pipe&&p.Kind!=PartKind.RectangularPlate;});
                var old=ParametricProjection.Build(legacy,frame,data.IsSection,true).Curves;
                var lines=ViewReferenceMigration.Lines(data.LineOverrides,old,stable);
                var welds=ViewReferenceMigration.Welds(data.Welds,old,stable);
                // Work on copies: a rejected migration must not partially alter data.
                var copies=anchors.Select(x=>x==null?null:KotNode.Core.Json.Copy(x)).ToList();
                foreach(var anchor in copies)ViewReferenceMigration.Anchor(anchor,old,stable);
                data.LineOverrides=lines;data.Welds=welds;
                for(int i=0;i<anchors.Count;i++)if(anchors[i]!=null)
                {anchors[i].SourceKey=copies[i].SourceKey;anchors[i].Param=copies[i].Param;}
            }
            return result;
        }
        internal static ObjectId Build(Database db,Transaction tr,AssemblyMark mark,AssemblyViews.ViewData data,Func<Guid,string> viewNameLookup=null,List<ParametricProjection.Curve2> rawOut=null)
        {
            // Compute and validate everything before adding drawing objects.
            var result=ComputeResult(mark,data);
            var rawCurves=result.Curves;
            if(rawOut!=null){rawOut.Clear();rawOut.AddRange(rawCurves);}
            result.Curves=ViewLineOverrides.Apply(rawCurves,data.LineOverrides,data.Hidden,out var unmatched);
            data.UnmatchedLineOverrides=unmatched;
            if(result.Curves.Count==0)throw new InvalidOperationException("Параметрическое представление пустое: проверьте направление и секущую плоскость.");
            var chains=ViewLineOverrides.Chain(result.Curves);
            double scale=data.AnnotationScale>0?data.AnnotationScale:20;
            var localWelds=(data.Welds??new List<ViewWeldOverlay>()).Select(w=>WeldCatalog.Effective(mark,w)).ToList();
            var weldStrokes=ViewWelds.Build(rawCurves,localWelds,scale,out var unmatchedWelds);
            data.UnmatchedWelds=unmatchedWelds;
            var allWelds=WeldContext.Effective(mark,WeldContext.Sources(db,tr,mark,new AssemblyViews.Entry{Data=data}));
            var sectionWelds=WeldContext.Patches(mark,data,rawCurves,allWelds);
            WeldSections.BuildLocal(rawCurves,localWelds,out var unmatchedLocalWelds);
            data.UnmatchedWelds+=unmatchedLocalWelds;
            var axisStrokes=ViewAnnotations.BuildAxes(data.Axes,rawCurves,scale,out var unmatchedAxes);
            var callouts=ViewAnnotations.BuildSectionCallouts(data.SectionCallouts,rawCurves,viewNameLookup,scale,out var unmatchedCallouts);
            data.UnmatchedAnnotations=unmatchedAxes+unmatchedCallouts;
            var table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
            var block=new BlockTableRecord{Name="NA_PARAM_VIEW_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
            var id=table.Add(block);tr.AddNewlyCreatedDBObject(block,true);
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
            foreach(var stroke in weldStrokes)
                using(var line=new Line(new Point3d(stroke.X0,stroke.Y0,0),new Point3d(stroke.X1,stroke.Y1,0)))
                {
                    double len=Math.Sqrt((stroke.X1-stroke.X0)*(stroke.X1-stroke.X0)+(stroke.Y1-stroke.Y0)*(stroke.Y1-stroke.Y0));
                    double ltScale=stroke.Hidden&&len>1e-6?ViewLineOverrides.HiddenLineScale(len,scale,globalScale,period):1;
                    var lineId=AssemblyViews.StoreCurve(block,tr,db,line,AssemblyViews.WeldLayer,ltScale);
                    var entity=(Entity)tr.GetObject(lineId,OpenMode.ForWrite);
                    entity.LinetypeId=stroke.Hidden?dashed:continuous;
                    entity.LinetypeScale=ltScale;
                }
            foreach(var patch in sectionWelds)
                for(int i=1;i<patch.Points.Count-1;i++)
                {
                    var a=patch.Points[0];var b=patch.Points[i];var c=patch.Points[i+1];
                    var solid=new Solid(new Point3d(a.X,a.Y,0),new Point3d(b.X,b.Y,0),new Point3d(c.X,c.Y,0),new Point3d(c.X,c.Y,0));
                    solid.SetDatabaseDefaults(db);solid.Layer=AssemblyViews.WeldLayer;solid.ColorIndex=256;
                    block.AppendEntity(solid);tr.AddNewlyCreatedDBObject(solid,true);
                }
            if(axisStrokes.Count>0&&types.Has("NA_CENTER"))
            {
                var centerType=types["NA_CENTER"];
                double centerPeriod=((LinetypeTableRecord)tr.GetObject(centerType,OpenMode.ForRead)).PatternLength;
                if(centerPeriod<=1e-6)centerPeriod=18;
                foreach(var ax in axisStrokes)
                    using(var line=new Line(new Point3d(ax.X0,ax.Y0,0),new Point3d(ax.X1,ax.Y1,0)))
                    {
                        double len=Math.Sqrt((ax.X1-ax.X0)*(ax.X1-ax.X0)+(ax.Y1-ax.Y0)*(ax.Y1-ax.Y0));
                        double ltScale=len>1e-6?ViewLineOverrides.HiddenLineScale(len,scale,globalScale,centerPeriod):1;
                        var lineId=AssemblyViews.StoreCurve(block,tr,db,line,ViewAnnotationDrawing.AxisLayer,ltScale);
                        var entity=(Entity)tr.GetObject(lineId,OpenMode.ForWrite);
                        entity.LinetypeId=centerType;
                        entity.LinetypeScale=ltScale;
                    }
            }
            if(callouts.ThickLines.Count>0||callouts.ThinLines.Count>0)
            {
                var textStyle=ViewAnnotationDrawing.TextStyleId(db,tr);
                foreach(var s in callouts.ThickLines)
                    using(var line=new Line(new Point3d(s.X0,s.Y0,0),new Point3d(s.X1,s.Y1,0)))
                    {
                        var lid=AssemblyViews.StoreCurve(block,tr,db,line,ViewAnnotationDrawing.AnnoLayer,1);
                        var e=(Entity)tr.GetObject(lid,OpenMode.ForWrite);
                        e.LineWeight=LineWeight.LineWeight050;
                    }
                foreach(var s in callouts.ThinLines)
                    using(var line=new Line(new Point3d(s.X0,s.Y0,0),new Point3d(s.X1,s.Y1,0)))
                        AssemblyViews.StoreCurve(block,tr,db,line,ViewAnnotationDrawing.AnnoLayer,1);
                foreach(var tri in callouts.Arrows)
                {
                    var solid=new Solid(new Point3d(tri.Item1.X,tri.Item1.Y,0),new Point3d(tri.Item2.X,tri.Item2.Y,0),new Point3d(tri.Item3.X,tri.Item3.Y,0),new Point3d(tri.Item3.X,tri.Item3.Y,0));
                    solid.SetDatabaseDefaults(db);solid.Layer=ViewAnnotationDrawing.AnnoLayer;solid.ColorIndex=256;
                    block.AppendEntity(solid);tr.AddNewlyCreatedDBObject(solid,true);
                }
                foreach(var lbl in callouts.Labels)
                {
                    var txt=new DBText{TextString=lbl.Item3,Position=new Point3d(lbl.Item1.X,lbl.Item1.Y,0),Height=lbl.Item2,TextStyleId=textStyle,Layer=ViewAnnotationDrawing.AnnoLayer};
                    txt.SetDatabaseDefaults(db);txt.Layer=ViewAnnotationDrawing.AnnoLayer;
                    block.AppendEntity(txt);tr.AddNewlyCreatedDBObject(txt,true);
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
