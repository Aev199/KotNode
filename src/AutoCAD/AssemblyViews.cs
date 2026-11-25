using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using KotNode.Core;
using ModelJson=KotNode.Core.Json;

namespace KotNode.AutoCAD
{
    // Each view is a native 2D block with its own source frame and persistent ID.
    // Its block reference is the placement; replacing the definition preserves placement.
    public static class AssemblyViews
    {
        internal const string Key="KOTNODE_VIEW_1";
        internal const int Revision=2;
        internal const string VisibleLayer="NA_VISIBLE",HiddenLayer="NA_HIDDEN",CutLayer="NA_CUT",HatchLayer="NA_HATCH";
        static double PaperScale(ViewData d)=>d.AnnotationScale>0?d.AnnotationScale:20;
        static void Layers(Database db,Transaction tr)
        {
            var types=(LinetypeTable)tr.GetObject(db.LinetypeTableId,OpenMode.ForRead);
            const string dashed="NA_DASHED";
            if(!types.Has(dashed))
            {
                types.UpgradeOpen();
                var type=new LinetypeTableRecord{Name=dashed,AsciiDescription="KotNode hidden edges",PatternLength=4,NumDashes=2};
                type.SetDashLengthAt(0,3);type.SetDashLengthAt(1,-1);
                types.Add(type);tr.AddNewlyCreatedDBObject(type,true);
            }
            var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
            foreach(var name in new[]{VisibleLayer,HiddenLayer,CutLayer,HatchLayer})
            {
                if(layers.Has(name))continue;
                if(!layers.IsWriteEnabled)layers.UpgradeOpen();
                var layer=new LayerTableRecord{Name=name,Color=Color.FromColorIndex(ColorMethod.ByAci,7),
                    LinetypeObjectId=name==HiddenLayer?types[dashed]:types["Continuous"],
                    LineWeight=name==CutLayer?LineWeight.LineWeight035:name==VisibleLayer?LineWeight.LineWeight025:LineWeight.LineWeight013};
                layers.Add(layer);tr.AddNewlyCreatedDBObject(layer,true);
            }
        }
        internal static bool IsStale(ViewData data,string hash)=>data.GeneratorRevision!=Revision||data.SourceHash!=hash;
        public sealed class ViewData
        {
            public Guid Id { get; set; }=Guid.NewGuid();
            public Guid MarkId { get; set; }
            public string Name { get; set; }
            public bool IsSection { get; set; }
            public bool Hidden { get; set; }
            // Zero is legacy default; use 20 as annotation/printing scale.
            public double AnnotationScale { get; set; }
            public bool NoHatch { get; set; }
            public int GeneratorRevision { get; set; }
            public RigidPlacement Frame { get; set; }
            public string SourceHash { get; set; }
            public string Handle { get; set; }
        }
        internal sealed class Entry
        {
            public BlockReference Reference;
            public ViewData Data;
        }
        internal static string Signature(AssemblyMark mark)
        {
            using(var sha=SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(ModelJson.Write(mark))));
        }
        internal static List<Entry> Find(Database db,Transaction tr,AssemblyMark mark)
        {
            var result=new List<Entry>();
            foreach(ObjectId id in AssemblyDrawing.ModelSpace(db,tr))
            {
                var b=tr.GetObject(id,OpenMode.ForRead) as BlockReference;
                if(b==null||b.ExtensionDictionary.IsNull)continue;
                var dict=(DBDictionary)tr.GetObject(b.ExtensionDictionary,OpenMode.ForRead);
                var data=AssemblyDrawing.Read<ViewData>(dict,Key,tr);
                if(data==null||data.Handle!=b.Handle.ToString()||data.MarkId!=mark.Id)continue;
                if(data.Id==Guid.Empty||data.Frame==null||string.IsNullOrWhiteSpace(data.Name)||result.Any(e=>e.Data.Id==data.Id))
                    throw new InvalidOperationException("Повреждены данные представления.");
                data.Frame.Validate();
                result.Add(new Entry{Reference=b,Data=data});
            }
            return result;
        }
        static void Save(BlockReference b,Transaction tr,ViewData data)
        {
            if(!b.IsWriteEnabled)b.UpgradeOpen();
            if(b.ExtensionDictionary.IsNull)b.CreateExtensionDictionary();
            data.Handle=b.Handle.ToString();
            AssemblyDrawing.Write((DBDictionary)tr.GetObject(b.ExtensionDictionary,OpenMode.ForRead),Key,data,tr);
        }
        static Matrix3d ToLocal(RigidPlacement p)
        {
            p.Validate();
            var o=new Point3d(p.Origin.X,p.Origin.Y,p.Origin.Z);
            var x=new Vector3d(p.AxisX.X,p.AxisX.Y,p.AxisX.Z);
            var y=new Vector3d(p.AxisY.X,p.AxisY.Y,p.AxisY.Z);
            return Matrix3d.AlignCoordinateSystem(o,x,y,x.CrossProduct(y),Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis);
        }
        static List<Solid3d> Bodies(Transaction tr,Dictionary<Guid,BlockReference> refs,Matrix3d toLocal)
        {
            var result=new List<Solid3d>();
            try
            {
                foreach(var reference in refs.Values)
                {
                    var block=(BlockTableRecord)tr.GetObject(reference.BlockTableRecord,OpenMode.ForRead);
                    foreach(ObjectId id in block)
                    {
                        if(!(tr.GetObject(id,OpenMode.ForRead) is Solid3d body))continue;
                        var clone=(Solid3d)body.Clone();result.Add(clone);
                        clone.TransformBy(reference.BlockTransform);
                        clone.TransformBy(toLocal);
                    }
                }
                return result;
            }
            catch {foreach(var b in result)b.Dispose();throw;}
        }
        internal static Curve Flatten(Entity entity,Matrix3d transform)
        {
            if(!(entity is Curve))throw new InvalidOperationException("Проекция содержит неподдерживаемую геометрию: "+entity.GetType().Name);
            var copy=(Curve)entity.Clone();
            try
            {
                copy.TransformBy(transform);
                var ext=copy.GeometricExtents;
                if(Math.Abs(ext.MinPoint.Z)>1e-5||Math.Abs(ext.MaxPoint.Z)>1e-5)
                    throw new InvalidOperationException("Не удалось привести геометрию представления к плоскости XY.");
                return copy;
            }
            catch{copy.Dispose();throw;}
        }
        internal static ObjectId StoreCurve(BlockTableRecord block,Transaction tr,Database db,Curve curve,string layer,double scale=1)
        {
            var stored=(Curve)curve.Clone();
            stored.SetDatabaseDefaults(db);stored.Layer=layer;stored.ColorIndex=256;
            stored.Linetype="ByLayer";stored.LineWeight=LineWeight.ByLayer;stored.LinetypeScale=scale;
            var id=block.AppendEntity(stored);tr.AddNewlyCreatedDBObject(stored,true);return id;
        }
        static void SectionBoundary(Region region,List<Curve> curves)
        {
            var items=new DBObjectCollection();
            try
            {
                region.Explode(items);
                foreach(DBObject item in items)
                {
                    if(item is Region nested)SectionBoundary(nested,curves);
                    else if(item is Curve e)curves.Add(Flatten(e,Matrix3d.Identity));
                    else throw new InvalidOperationException("Неизвестный тип границы сечения.");
                }
            }
            finally{foreach(DBObject item in items)item.Dispose();}
        }
        static List<List<int>> Loops(List<Curve> curves)
        {
            var remaining=new HashSet<int>(Enumerable.Range(0,curves.Count));
            var loops=new List<List<int>>();
            const double eps=1e-5;
            while(remaining.Count>0)
            {
                int first=remaining.Min();remaining.Remove(first);
                var loop=new List<int>{first};
                var curve=curves[first];
                if(!curve.Closed)
                {
                    var start=curve.StartPoint;var end=curve.EndPoint;
                    while(end.DistanceTo(start)>eps)
                    {
                        int next=-1;bool reverse=false;
                        foreach(int i in remaining)
                        {
                            var c=curves[i];
                            bool direct=c.StartPoint.DistanceTo(end)<=eps,back=c.EndPoint.DistanceTo(end)<=eps;
                            if(direct||back){next=i;reverse=!direct;break;}
                        }
                        if(next<0)throw new InvalidOperationException("Не удалось замкнуть контур штриховки.");
                        remaining.Remove(next);loop.Add(next);
                        end=reverse?curves[next].StartPoint:curves[next].EndPoint;
                    }
                }
                loops.Add(loop);
            }
            // Largest envelope first (outer boundary of a connected region).
            return loops.OrderByDescending(loop=>{
                double x0=loop.Min(i=>curves[i].GeometricExtents.MinPoint.X),x1=loop.Max(i=>curves[i].GeometricExtents.MaxPoint.X);
                double y0=loop.Min(i=>curves[i].GeometricExtents.MinPoint.Y),y1=loop.Max(i=>curves[i].GeometricExtents.MaxPoint.Y);
                return (x1-x0)*(y1-y0);
            }).ToList();
        }
        static void SectionCurves(BlockTableRecord block,Transaction tr,Database db,List<Solid3d> bodies,ViewData data)
        {
            using(var plane=new Plane(Point3d.Origin,Vector3d.ZAxis))
                for(int i=0;i<bodies.Count;i++)
                {
                    var solid=bodies[i];var ext=solid.GeometricExtents;
                    if(ext.MinPoint.Z>1e-7||ext.MaxPoint.Z< -1e-7)continue;
                    using(var region=solid.GetSection(plane))
                    {
                        if(region==null||region.Area<=1e-10)continue;
                        var curves=new List<Curve>();
                        try
                        {
                            SectionBoundary(region,curves);
                            if(curves.Count==0)continue;
                            var ids=curves.Select(c=>StoreCurve(block,tr,db,c,CutLayer)).ToArray();
                            if(data.NoHatch)continue;
                            var loops=Loops(curves);
                            var hatch=new Hatch();
                            hatch.SetDatabaseDefaults(db);hatch.Layer=HatchLayer;hatch.ColorIndex=256;
                            block.AppendEntity(hatch);tr.AddNewlyCreatedDBObject(hatch,true);
                            hatch.SetHatchPattern(HatchPatternType.UserDefined,"_USER");
                            hatch.PatternAngle=(i%2==0?Math.PI/4:3*Math.PI/4);
                            hatch.PatternSpace=2*PaperScale(data);
                            hatch.HatchStyle=HatchStyle.Normal;hatch.Associative=false;
                            for(int k=0;k<loops.Count;k++)
                                hatch.AppendLoop(k==0?HatchLoopTypes.Outermost:HatchLoopTypes.Default,new ObjectIdCollection(loops[k].Select(j=>ids[j]).ToArray()));
                            hatch.EvaluateHatch(true);
                        }
                        finally{foreach(var c in curves)c.Dispose();}
                    }
                }
        }
        static IEnumerable<Entity> Entities(Array a)
        {
            if(a==null)yield break;
            foreach(var item in a)if(item is Entity entity)yield return entity;
        }
        // Use native projections first; the separate visibility pass compares
        // depths of overlapping solids and splits curves at projected intersections.
        static void ProjectCurves(BlockTableRecord block,Transaction tr,Database db,List<Solid3d> bodies,ViewData data)
        {
            double minX=bodies.Min(b=>b.GeometricExtents.MinPoint.X);
            double maxX=bodies.Max(b=>b.GeometricExtents.MaxPoint.X);
            double maxZ=bodies.Max(b=>b.GeometricExtents.MaxPoint.Z);
            double margin=Math.Max(1,(maxX-minX)*0.01);
            double z=maxZ+margin;
            var points=new Point3dCollection(new[]{new Point3d(minX-margin,0,z),new Point3d(maxX+margin,0,z)});
            var section=new Section(points,Vector3d.YAxis,Vector3d.ZAxis){State=SectionState.Plane};
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            space.AppendEntity(section);tr.AddNewlyCreatedDBObject(section,true);
            var edges=new List<ProjectionVisibility.Edge>();
            try
            {
                var settings=(SectionSettings)tr.GetObject(section.Settings,OpenMode.ForWrite);
                var type=SectionType.Section2d;
                settings.CurrentSectionType=type;
                settings.SetVisibility(type,SectionGeometry.BackgroundGeometry,true);
                settings.SetVisibility(type,SectionGeometry.ForegroundGeometry,true);
                settings.SetVisibility(type,SectionGeometry.CurveTangencyLines,false);
                var shift=Matrix3d.Displacement(new Vector3d(0,0,-z));
                for(int i=0;i<bodies.Count;i++)
                {
                    for(int pass=0;pass<(data.Hidden?2:1);pass++)
                    {
                        bool hidden=pass==1;
                        settings.SetHiddenLine(type,SectionGeometry.BackgroundGeometry,hidden);
                        settings.SetHiddenLine(type,SectionGeometry.ForegroundGeometry,hidden);
                        section.GenerateSectionGeometry(bodies[i],out Array cut,out Array fill,out Array back,out Array front,out Array tangent);
                        var all=Entities(cut).Concat(Entities(fill)).Concat(Entities(back)).Concat(Entities(front)).Concat(Entities(tangent)).ToList();
                        try
                        {
                            var projected=Entities(back).Concat(Entities(front)).ToList();
                            if(pass==0&&projected.Count==0)throw new InvalidOperationException("Ядро AutoCAD не вернуло проекцию тела.");
                            foreach(var e in projected)
                            {
                                var ext=e.GeometricExtents;
                                bool atZero=Math.Abs(ext.MinPoint.Z)<1e-5&&Math.Abs(ext.MaxPoint.Z)<1e-5;
                                edges.Add(new ProjectionVisibility.Edge(i,Flatten(e,atZero?Matrix3d.Identity:shift),hidden));
                            }
                        }
                        finally {foreach(var e in all)e.Dispose();}
                    }
                }
                ProjectionVisibility.Write(block,tr,db,bodies,edges,data.Hidden,PaperScale(data));
            }
            finally {foreach(var edge in edges)edge.Curve.Dispose();section.Erase();}
        }
        static ObjectId Build(Database db,Transaction tr,Dictionary<Guid,BlockReference> refs,ViewData data)
        {
            Layers(db,tr);
            if(refs.Count==0)throw new InvalidOperationException("В марке нет деталей для представления.");
            var bodies=Bodies(tr,refs,ToLocal(data.Frame));
            try
            {
                if(bodies.Count==0)throw new InvalidOperationException("В марке нет трёхмерных тел.");
                var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
                var block=new BlockTableRecord{Name="NA_VIEW_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
                var id=bt.Add(block);tr.AddNewlyCreatedDBObject(block,true);
                if(data.IsSection)SectionCurves(block,tr,db,bodies,data);
                else ProjectCurves(block,tr,db,bodies,data);
                if(!block.Cast<ObjectId>().Any())throw new InvalidOperationException("Представление пустое. Проверьте секущую плоскость.");
                return id;
            }
            finally{foreach(var body in bodies)body.Dispose();}
        }
        internal static void Create(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,ViewData data,Point3d location)
        {
            data.MarkId=mark.Id;data.SourceHash=Signature(mark);data.GeneratorRevision=Revision;
            var geometry=Build(db,tr,refs,data);
            var reference=new BlockReference(new Point3d(location.X,location.Y,0),geometry);
            reference.SetDatabaseDefaults(db);
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            space.AppendEntity(reference);tr.AddNewlyCreatedDBObject(reference,true);
            Save(reference,tr,data);
        }
        internal static void Rebuild(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,Entry entry)
        {
            var geometry=Build(db,tr,refs,entry.Data);
            AssemblyDrawing.Replace(entry.Reference,geometry);
            entry.Data.SourceHash=Signature(mark);entry.Data.GeneratorRevision=Revision;
            Save(entry.Reference,tr,entry.Data);
        }
        internal static int Update(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,Guid? only=null)
        {
            var entries=Find(db,tr,mark).Where(e=>!only.HasValue||e.Data.Id==only.Value).ToList();
            if(only.HasValue&&entries.Count==0)throw new InvalidOperationException("Представление не найдено.");
            foreach(var entry in entries)Rebuild(db,tr,mark,refs,entry);
            return entries.Count;
        }
    }
}
