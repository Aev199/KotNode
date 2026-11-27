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
        internal const int Revision=21;
        internal const string VisibleLayer="NA_VISIBLE",HiddenLayer="NA_HIDDEN",CutLayer="NA_CUT",HatchLayer="NA_HATCH",WeldLayer="NA_WELD";
        static double PaperScale(ViewData d)=>d.AnnotationScale>0?d.AnnotationScale:20;
        static void Layers(Database db,Transaction tr)
        {
            var types=(LinetypeTable)tr.GetObject(db.LinetypeTableId,OpenMode.ForRead);
            const string dashed="NA_DASHED";
            if(!types.Has(dashed))
            {
                types.UpgradeOpen();
                var type=new LinetypeTableRecord{Name=dashed,AsciiDescription="KotNode hidden edges",PatternLength=6,NumDashes=2};
                type.SetDashLengthAt(0,4);type.SetDashLengthAt(1,-2);
                types.Add(type);tr.AddNewlyCreatedDBObject(type,true);
            }
            else
            {
                var existing=(LinetypeTableRecord)tr.GetObject(types[dashed],OpenMode.ForRead);
                if(existing.AsciiDescription=="KotNode hidden edges"&&Math.Abs(existing.PatternLength-4)<1e-6&&existing.NumDashes==2)
                {
                    existing.UpgradeOpen();
                    existing.PatternLength=6;
                    existing.SetDashLengthAt(0,4);existing.SetDashLengthAt(1,-2);
                }
            }
            var layers=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
            foreach(var name in new[]{VisibleLayer,HiddenLayer,CutLayer,HatchLayer,WeldLayer})
            {
                if(layers.Has(name))continue;
                if(!layers.IsWriteEnabled)layers.UpgradeOpen();
                var layer=new LayerTableRecord{Name=name,Color=Color.FromColorIndex(ColorMethod.ByAci,7),
                    LinetypeObjectId=name==HiddenLayer?types[dashed]:types["Continuous"],
                    LineWeight=name==CutLayer?LineWeight.LineWeight035:name==VisibleLayer?LineWeight.LineWeight025:name==WeldLayer?LineWeight.LineWeight018:LineWeight.LineWeight013};
                layers.Add(layer);tr.AddNewlyCreatedDBObject(layer,true);
            }
        }
        internal static bool IsStale(ViewData data,string hash)=>(!data.ManualSnapshot&&data.GeneratorRevision!=Revision)||data.SourceHash!=hash;
        public sealed class ViewData
        {
            public Guid Id { get; set; }=Guid.NewGuid();
            public Guid MarkId { get; set; }
            public string Name { get; set; }
            public bool IsSection { get; set; }
            public bool FullCut { get; set; }
            public bool Hidden { get; set; }
            // Zero is legacy default; use 20 as annotation/printing scale.
            public double AnnotationScale { get; set; }
            public bool NoHatch { get; set; }
            public bool ParametricPrototype { get; set; }
            public bool ManualSnapshot { get; set; }
            public int GeneratorRevision { get; set; }
            public RigidPlacement Frame { get; set; }
            public Guid? FrameAnchorId { get; set; }
            public RigidPlacement RelativeFrame { get; set; }
            public string SourceHash { get; set; }
            public string Handle { get; set; }
            // Stable wrapper contains user graphics; only its marked child is regenerated.
            public string GeneratedChildHandle { get; set; }
            public string GeneratedStamp { get; set; }
            public List<ViewLineOverride> LineOverrides { get; set; }=new List<ViewLineOverride>();
            public int UnmatchedLineOverrides { get; set; }
            public List<ViewWeldOverlay> Welds { get; set; }=new List<ViewWeldOverlay>();
            public int UnmatchedWelds { get; set; }
            public List<ViewDimension> Dimensions { get; set; }=new List<ViewDimension>();
            public List<ViewLeader> Leaders { get; set; }=new List<ViewLeader>();
            public List<ViewAxisMark> Axes { get; set; }=new List<ViewAxisMark>();
            public List<ViewSectionCallout> SectionCallouts { get; set; }=new List<ViewSectionCallout>();
            public int UnmatchedAnnotations { get; set; }
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
        internal static Matrix3d ToLocal(RigidPlacement p)
        {
            p.Validate();
            var o=new Point3d(p.Origin.X,p.Origin.Y,p.Origin.Z);
            var x=new Vector3d(p.AxisX.X,p.AxisX.Y,p.AxisX.Z);
            var y=new Vector3d(p.AxisY.X,p.AxisY.Y,p.AxisY.Z);
            return Matrix3d.AlignCoordinateSystem(o,x,y,x.CrossProduct(y),Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis);
        }
        internal static List<Solid3d> Bodies(Transaction tr,Dictionary<Guid,BlockReference> refs,Matrix3d toLocal)
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
        // All solid copies are already expressed in the chosen view frame.
        // The observer is on +Z; the retained half is Z <= 0.
        internal static List<Solid3d> ClipBehind(List<Solid3d> bodies)
        {
            var result=new List<Solid3d>();
            try
            {
                using(var plane=new Plane(Point3d.Origin,-Vector3d.ZAxis))
                    foreach(var body in bodies)
                    {
                        var ext=body.GeometricExtents;
                        if(ext.MinPoint.Z>=-1e-7)continue;
                        var clone=(Solid3d)body.Clone();
                        try
                        {
                            if(ext.MaxPoint.Z>1e-7)clone.Slice(plane);
                            result.Add(clone);
                        }
                        catch{clone.Dispose();throw;}
                    }
                return result;
            }
            catch{foreach(var body in result)body.Dispose();throw;}
        }
        static void ProjectCurves(BlockTableRecord block,Transaction tr,Database db,List<Solid3d> bodies,ViewData data)
        {
            if(bodies.Count==0)return;
            Diagnostics.Step("HLR: единая проекция "+bodies.Count+" тел");
            var curves=NativeHlr.Project(db,tr,bodies,data.Hidden);
            try
            {
                int count=0;
                foreach(var item in curves)
                {
                    if(item.Item2&&!data.Hidden)continue;
                    using(var curve=Flatten(item.Item1,Matrix3d.Identity))
                    {
                        StoreCurve(block,tr,db,curve,item.Item2?HiddenLayer:VisibleLayer,item.Item2?PaperScale(data):1);
                        count++;
                    }
                }
                Diagnostics.Step("HLR: сохранено кривых "+count);
                if(count==0)throw new InvalidOperationException("HLR не вернул видимых контуров.");
            }
            finally{foreach(var item in curves)item.Item1.Dispose();}
        }
        // Independent scratch drawing for verifying the retained half using
        // the host's FLATSHOT, without touching the original assembly.
        internal static void ExportCut(Transaction tr,Dictionary<Guid,BlockReference> refs,RigidPlacement frame,string path)
        {
            if(System.IO.File.Exists(path))throw new InvalidOperationException("Файл уже существует. Выберите новое имя.");
            var bodies=Bodies(tr,refs,ToLocal(frame));
            try
            {
                var clipped=ClipBehind(bodies);
                try
                {
                    if(clipped.Count==0)throw new InvalidOperationException("За секущей плоскостью нет тел.");
                    using(var scratch=new Database(true,true))
                    {
                        scratch.Insunits=UnitsValue.Millimeters;
                        using(var tx=scratch.TransactionManager.StartTransaction())
                        {
                            var space=AssemblyDrawing.ModelSpace(scratch,tx,OpenMode.ForWrite);
                            foreach(var body in clipped)
                            {
                                var copy=(Solid3d)body.Clone();
                                try
                                {
                                    copy.SetDatabaseDefaults(scratch);copy.Layer="0";
                                    space.AppendEntity(copy);tx.AddNewlyCreatedDBObject(copy,true);
                                }
                                catch{if(copy.ObjectId.IsNull)copy.Dispose();throw;}
                            }
                            tx.Commit();
                        }
                        scratch.SaveAs(path,DwgVersion.Current);
                    }
                }
                finally{foreach(var body in clipped)body.Dispose();}
            }
            finally{foreach(var body in bodies)body.Dispose();}
        }
        internal static RigidPlacement ResolveFrame(AssemblyMark mark,ViewData data)
        {
            if(!data.FrameAnchorId.HasValue)return data.Frame;
            var anchor=mark.Instances.SingleOrDefault(i=>i.Id==data.FrameAnchorId.Value);
            if(anchor==null)throw new InvalidOperationException("Деталь привязки плоскости удалена. Задайте новую плоскость в NAEDITVIEW или снимите привязку.");
            if(data.RelativeFrame==null)throw new InvalidOperationException("Повреждена локальная плоскость представления.");
            return data.RelativeFrame.InParent(anchor.Placement);
        }
        static ObjectId Build(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,ViewData data,Func<Guid,string> viewNameLookup=null,List<ParametricProjection.Curve2> rawOut=null)
        {
            data.Frame=ResolveFrame(mark,data);
            Layers(db,tr);
            ViewAnnotationDrawing.EnsureStylesAndLayers(db,tr);
            if(data.ParametricPrototype)return ParametricViewDrawing.Build(db,tr,mark,data,viewNameLookup,rawOut);
            if(refs.Count==0)throw new InvalidOperationException("В марке нет деталей для представления.");
            var bodies=Bodies(tr,refs,ToLocal(data.Frame));
            try
            {
                if(bodies.Count==0)throw new InvalidOperationException("В марке нет трёхмерных тел.");
                var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
                var block=new BlockTableRecord{Name="NA_VIEW_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
                var id=bt.Add(block);tr.AddNewlyCreatedDBObject(block,true);
                if(data.IsSection)
                {
                    if(data.FullCut)
                    {
                        var clipped=ClipBehind(bodies);
                        try{ProjectCurves(block,tr,db,clipped,data);}
                        finally{foreach(var body in clipped)body.Dispose();}
                    }
                    SectionCurves(block,tr,db,bodies,data);
                }
                else ProjectCurves(block,tr,db,bodies,data);
                if(!block.Cast<ObjectId>().Any())throw new InvalidOperationException("Представление пустое. Проверьте секущую плоскость.");
                return id;
            }
            finally{foreach(var body in bodies)body.Dispose();}
        }
        internal static BlockReference Create(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,ViewData data,Point3d location)
        {
            data.MarkId=mark.Id;data.SourceHash=Signature(mark);data.GeneratorRevision=Revision;
            var rawCurves=new List<ParametricProjection.Curve2>();
            var geometry=Build(db,tr,mark,refs,data,null,rawCurves);
            var shell=ViewShell.Create(db,tr,geometry,data);
            var reference=new BlockReference(new Point3d(location.X,location.Y,0),shell);
            reference.SetDatabaseDefaults(db);
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            space.AppendEntity(reference);tr.AddNewlyCreatedDBObject(reference,true);
            if(data.ParametricPrototype)ViewAnnotationDrawing.Sync(db,tr,mark,new Entry{Reference=reference,Data=data},rawCurves);
            Save(reference,tr,data);
            return reference;
        }
        // Adopt an explicitly selected native 2D block as a manual snapshot.
        // No claim is made about its source or geometric accuracy: user verifies it.
        internal static void AttachSnapshot(Database db,Transaction tr,AssemblyMark mark,BlockReference reference,ViewData data)
        {
            if(reference.OwnerId!=AssemblyDrawing.ModelSpace(db,tr).ObjectId)
                throw new InvalidOperationException("Выберите блок в пространстве модели.");
            if(!reference.ExtensionDictionary.IsNull)
            {
                var dict=(DBDictionary)tr.GetObject(reference.ExtensionDictionary,OpenMode.ForRead);
                if(dict.Contains(Key)||dict.Contains("KOTNODE_INSTANCE_1"))
                    throw new InvalidOperationException("Объект уже управляется конструктором.");
            }
            if(AssemblyDrawing.AuxiliaryGroups(db,tr,mark).Any(g=>g.Item2.Contains(reference.ObjectId)))
                throw new InvalidOperationException("Блок уже включён во вспомогательную графику.");
            var placement=AssemblyDrawing.Placement(reference);
            if(Math.Abs(placement.Origin.Z)>1e-5||Math.Abs(placement.AxisX.Z)>1e-8||Math.Abs(placement.AxisY.Z)>1e-8)
                throw new InvalidOperationException("Блок FLATSHOT должен быть в мировой XY, Z=0, масштаб 1:1.");
            var def=(BlockTableRecord)tr.GetObject(reference.BlockTableRecord,OpenMode.ForRead);
            int count=0;
            foreach(ObjectId id in def)
            {
                var entity=tr.GetObject(id,OpenMode.ForRead) as Curve;
                if(entity==null)throw new InvalidOperationException("Допускается только блок плоских кривых FLATSHOT, без вложенных блоков.");
                var ext=entity.GeometricExtents;
                if(Math.Abs(ext.MinPoint.Z)>1e-5||Math.Abs(ext.MaxPoint.Z)>1e-5)
                    throw new InvalidOperationException("Блок содержит неплоскую геометрию.");
                count++;
            }
            if(count==0)throw new InvalidOperationException("Пустой блок.");
            data.MarkId=mark.Id;data.ManualSnapshot=true;data.SourceHash=Signature(mark);data.GeneratorRevision=Revision;
            Save(reference,tr,data);
        }
        internal static void ExportForFlatshot(Transaction tr,Dictionary<Guid,BlockReference> refs,FlatshotWorkflow.Job job,string path)
        {
            var bodies=Bodies(tr,refs,ToLocal(job.Frame));
            try
            {
                var clipped=job.IsSection?ClipBehind(bodies):null;
                try
                {
                    var input=clipped??bodies;
                    if(input.Count==0)throw new InvalidOperationException("Нет тел для проецирования. Проверьте положение разреза.");
                    using(var scratch=new Database(true,true))
                    {
                        scratch.Insunits=UnitsValue.Millimeters;
                        using(var tx=scratch.TransactionManager.StartTransaction())
                        {
                            var root=(DBDictionary)tx.GetObject(scratch.NamedObjectsDictionaryId,OpenMode.ForWrite);
                            AssemblyDrawing.Write(root,FlatshotWorkflow.JobKey,job,tx);
                            var space=AssemblyDrawing.ModelSpace(scratch,tx,OpenMode.ForWrite);
                            foreach(var solid in input)
                            {
                                var copy=(Solid3d)solid.Clone();
                                try
                                {
                                    copy.SetDatabaseDefaults(scratch);
                                    copy.Layer="0";
                                    space.AppendEntity(copy);tx.AddNewlyCreatedDBObject(copy,true);
                                }
                                catch{if(copy.ObjectId.IsNull)copy.Dispose();throw;}
                            }
                            tx.Commit();
                        }
                        scratch.SaveAs(path,DwgVersion.Current);
                    }
                }
                finally{if(clipped!=null)foreach(var solid in clipped)solid.Dispose();}
            }
            finally{foreach(var solid in bodies)solid.Dispose();}
        }
        internal static void AddSectionOverlay(Database db,Transaction tr,Dictionary<Guid,BlockReference> refs,RigidPlacement frame,BlockTableRecord target,ViewData data)
        {
            Layers(db,tr);
            var bodies=Bodies(tr,refs,ToLocal(frame));
            try{SectionCurves(target,tr,db,bodies,data);}
            finally{foreach(var solid in bodies)solid.Dispose();}
        }
        internal static void CreateImported(Database db,Transaction tr,AssemblyMark mark,ObjectId block,ViewData data,Point3d at)
        {
            data.MarkId=mark.Id;data.SourceHash=Signature(mark);data.GeneratorRevision=Revision;data.ManualSnapshot=true;
            var reference=new BlockReference(at,block);
            reference.SetDatabaseDefaults(db);
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            space.AppendEntity(reference);tr.AddNewlyCreatedDBObject(reference,true);
            Save(reference,tr,data);
        }
        internal static void Rebuild(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,Entry entry)
        {
            if(entry.Data.ManualSnapshot)throw new InvalidOperationException("Старый снимок не связан с параметрами. Создайте новый вид командой NAVIEW или NASECTION.");
            if(string.IsNullOrEmpty(entry.Data.GeneratedChildHandle))
                throw new InvalidOperationException("Вид старого формата. Выполните NAMIGRATEVIEW с резервной копией; правки не будут перенесены автоматически.");
            var child=ViewShell.RequireUnchanged(tr,entry);
            if(!entry.Data.IsSection||entry.Data.FullCut)entry.Data.ParametricPrototype=true;
            var allViews=Find(db,tr,mark).ToDictionary(e=>e.Data.Id,e=>e.Data.Name);
            Func<Guid,string> lookup=id=>allViews.TryGetValue(id,out var n)?n:null;
            var rawCurves=new List<ParametricProjection.Curve2>();
            var geometry=Build(db,tr,mark,refs,entry.Data,lookup,rawCurves);
            AssemblyDrawing.Replace(child,geometry);
            child.RecordGraphicsModified(true);
            if(!entry.Reference.IsWriteEnabled)entry.Reference.UpgradeOpen();
            entry.Reference.RecordGraphicsModified(true);
            entry.Data.GeneratedStamp=ViewShell.Stamp(tr,geometry);
            entry.Data.SourceHash=Signature(mark);entry.Data.GeneratorRevision=Revision;entry.Data.ManualSnapshot=false;
            if(entry.Data.ParametricPrototype)ViewAnnotationDrawing.Sync(db,tr,mark,entry,rawCurves);
            Save(entry.Reference,tr,entry.Data);
        }
        internal static void Migrate(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,Entry entry,Point3d backupAt)
        {
            if(entry.Data.ManualSnapshot||!string.IsNullOrEmpty(entry.Data.GeneratedChildHandle))
                throw new InvalidOperationException("Переход доступен только старым автоматически построенным видам.");
            // Preserve a referenced copy of the entire legacy definition, including manual edits.
            // It is deliberately not tagged as a live managed view.
            var original=entry.Reference;
            var backup=new BlockReference(Point3d.Origin,original.BlockTableRecord);
            backup.SetDatabaseDefaults(db);
            backup.TransformBy(original.BlockTransform);
            backup.Position=new Point3d(backupAt.X,backupAt.Y,0);
            backup.Layer=original.Layer;
            AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite).AppendEntity(backup);
            tr.AddNewlyCreatedDBObject(backup,true);
            if(!entry.Data.IsSection||entry.Data.FullCut)entry.Data.ParametricPrototype=true;
            var geometry=Build(db,tr,mark,refs,entry.Data);
            var shell=ViewShell.Create(db,tr,geometry,entry.Data);
            AssemblyDrawing.Replace(original,shell);
            original.RecordGraphicsModified(true);
            entry.Data.SourceHash=Signature(mark);
            entry.Data.GeneratorRevision=Revision;
            Save(original,tr,entry.Data);
        }
        internal static void UpgradeReferences(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs)
        {
            foreach(var entry in Find(db,tr,mark).Where(e=>e.Data.ParametricPrototype&&!e.Data.ManualSnapshot&&!string.IsNullOrEmpty(e.Data.GeneratedChildHandle)))
                if(entry.Data.GeneratorRevision<20)Rebuild(db,tr,mark,refs,entry);
        }
        internal static int Update(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,Guid? only=null)
        {
            var entries=Find(db,tr,mark).Where(e=>!only.HasValue||e.Data.Id==only.Value).ToList();
            if(only.HasValue&&entries.Count==0)throw new InvalidOperationException("Представление не найдено.");
            int count=0;
            foreach(var entry in entries)
            {
                if(entry.Data.ManualSnapshot)
                {
                    if(only.HasValue)throw new InvalidOperationException("Старый несвязанный снимок. Создайте новый вид командой NAVIEW или NASECTION.");
                    continue;
                }
                if(string.IsNullOrEmpty(entry.Data.GeneratedChildHandle)&&!only.HasValue)continue;
                Rebuild(db,tr,mark,refs,entry);count++;
            }
            return count;
        }
    }
}
