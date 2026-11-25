using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;
using ModelJson=KotNode.Core.Json;

namespace KotNode.AutoCAD
{
    // Each view is a native 2D block with its own source frame and persistent ID.
    // Its block reference is the placement; replacing the definition preserves placement.
    public static class AssemblyViews
    {
        internal const string Key="KOTNODE_VIEW_1";
        public sealed class ViewData
        {
            public Guid Id { get; set; }=Guid.NewGuid();
            public Guid MarkId { get; set; }
            public string Name { get; set; }
            public bool IsSection { get; set; }
            public bool Hidden { get; set; }
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
        static void AddCurve(BlockTableRecord block,Transaction tr,Database db,Entity entity,Matrix3d transform)
        {
            // Clone: generated entities remain owned by the calling routine.
            if(!(entity is Curve))throw new InvalidOperationException("Проекция содержит неподдерживаемую геометрию: "+entity.GetType().Name);
            using(var copy=(Entity)entity.Clone())
            {
                copy.TransformBy(transform);
                var ext=copy.GeometricExtents;
                if(Math.Abs(ext.MinPoint.Z)>1e-5||Math.Abs(ext.MaxPoint.Z)>1e-5)
                    throw new InvalidOperationException("Не удалось привести геометрию представления к плоскости XY.");
                var stored=(Entity)copy.Clone();
                stored.SetDatabaseDefaults(db);stored.Layer="0";stored.ColorIndex=256;
                block.AppendEntity(stored);tr.AddNewlyCreatedDBObject(stored,true);
            }
        }
        static void SectionBoundary(BlockTableRecord block,Transaction tr,Database db,Region region)
        {
            var items=new DBObjectCollection();
            try
            {
                region.Explode(items);
                foreach(DBObject item in items)
                {
                    if(item is Region nested)SectionBoundary(block,tr,db,nested);
                    else if(item is Entity e)AddCurve(block,tr,db,e,Matrix3d.Identity);
                    else throw new InvalidOperationException("Неизвестный тип границы сечения.");
                }
            }
            finally{foreach(DBObject item in items)item.Dispose();}
        }
        static void SectionCurves(BlockTableRecord block,Transaction tr,Database db,List<Solid3d> bodies)
        {
            using(var plane=new Plane(Point3d.Origin,Vector3d.ZAxis))
                foreach(var solid in bodies)
                    using(var region=solid.GetSection(plane))
                        if(region!=null)SectionBoundary(block,tr,db,region);
        }
        static IEnumerable<Entity> Entities(Array a)
        {
            if(a==null)yield break;
            foreach(var item in a)if(item is Entity entity)yield return entity;
        }
        // Use the native section/elevation kernel per solid. It does not compute
        // occlusion between different solids, so these are preliminary views.
        static void ProjectCurves(BlockTableRecord block,Transaction tr,Database db,List<Solid3d> bodies,bool hidden)
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
            try
            {
                var settings=(SectionSettings)tr.GetObject(section.Settings,OpenMode.ForWrite);
                var type=SectionType.Section2d;
                settings.CurrentSectionType=type;
                settings.SetVisibility(type,SectionGeometry.BackgroundGeometry,true);
                settings.SetVisibility(type,SectionGeometry.ForegroundGeometry,true);
                settings.SetVisibility(type,SectionGeometry.CurveTangencyLines,false);
                settings.SetHiddenLine(type,SectionGeometry.BackgroundGeometry,hidden);
                settings.SetHiddenLine(type,SectionGeometry.ForegroundGeometry,hidden);
                var shift=Matrix3d.Displacement(new Vector3d(0,0,-z));
                foreach(var body in bodies)
                {
                    section.GenerateSectionGeometry(body,out Array cut,out Array fill,out Array back,out Array front,out Array tangent);
                    var all=Entities(cut).Concat(Entities(fill)).Concat(Entities(back)).Concat(Entities(front)).Concat(Entities(tangent)).ToList();
                    try
                    {
                        var projected=Entities(back).Concat(Entities(front)).ToList();
                        if(projected.Count==0)throw new InvalidOperationException("Ядро AutoCAD не вернуло проекцию тела.");
                        foreach(var e in projected)
                        {
                            // In different kernel builds output may already lie at Z=0.
                            var ext=e.GeometricExtents;
                            bool atZero=Math.Abs(ext.MinPoint.Z)<1e-5&&Math.Abs(ext.MaxPoint.Z)<1e-5;
                            AddCurve(block,tr,db,e,atZero?Matrix3d.Identity:shift);
                        }
                    }
                    finally {foreach(var e in all)e.Dispose();}
                }
            }
            finally {section.Erase();}
        }
        static ObjectId Build(Database db,Transaction tr,Dictionary<Guid,BlockReference> refs,ViewData data)
        {
            if(refs.Count==0)throw new InvalidOperationException("В марке нет деталей для представления.");
            var bodies=Bodies(tr,refs,ToLocal(data.Frame));
            try
            {
                if(bodies.Count==0)throw new InvalidOperationException("В марке нет трёхмерных тел.");
                var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
                var block=new BlockTableRecord{Name="NA_VIEW_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
                var id=bt.Add(block);tr.AddNewlyCreatedDBObject(block,true);
                if(data.IsSection)SectionCurves(block,tr,db,bodies);
                else ProjectCurves(block,tr,db,bodies,data.Hidden);
                if(!block.Cast<ObjectId>().Any())throw new InvalidOperationException("Представление пустое. Проверьте секущую плоскость.");
                return id;
            }
            finally{foreach(var body in bodies)body.Dispose();}
        }
        internal static void Create(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,ViewData data,Point3d location)
        {
            data.MarkId=mark.Id;data.SourceHash=Signature(mark);
            var geometry=Build(db,tr,refs,data);
            var reference=new BlockReference(new Point3d(location.X,location.Y,0),geometry);
            reference.SetDatabaseDefaults(db);
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            space.AppendEntity(reference);tr.AddNewlyCreatedDBObject(reference,true);
            Save(reference,tr,data);
        }
        internal static int Update(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,Guid? only=null)
        {
            var entries=Find(db,tr,mark).Where(e=>!only.HasValue||e.Data.Id==only.Value).ToList();
            if(only.HasValue&&entries.Count==0)throw new InvalidOperationException("Представление не найдено.");
            string hash=Signature(mark);
            foreach(var entry in entries)
            {
                var geometry=Build(db,tr,refs,entry.Data);
                AssemblyDrawing.Replace(entry.Reference,geometry);
                entry.Data.SourceHash=hash;
                Save(entry.Reference,tr,entry.Data);
            }
            return entries.Count;
        }
    }
}
