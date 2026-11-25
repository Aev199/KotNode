using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    // Experimental editor: one assembly per DWG, managed block references in model space.
    // The mark and its graphics are committed in the same database transaction (UNDO-safe).
    public static class AssemblyDrawing
    {
        const string MarkKey="KOTNODE_ASSEMBLY_1";
        const string PartKey="KOTNODE_INSTANCE_1";

        public sealed class PartTag
        {
            public Guid MarkId { get; set; }
            public Guid InstanceId { get; set; }
            public string Handle { get; set; }
        }

        static T Read<T>(DBDictionary dict,string key,Transaction tr) where T:class
        {
            if(!dict.Contains(key))return null;
            var xr=(Xrecord)tr.GetObject(dict.GetAt(key),OpenMode.ForRead);
            using(var data=xr.Data)
            {
                if(data==null)throw new InvalidOperationException("Повреждены данные конструктора.");
                return Json.Read<T>(Encoding.UTF8.GetString(data.AsArray().Where(v=>v.TypeCode==310).SelectMany(v=>(byte[])v.Value).ToArray()));
            }
        }
        static void Write<T>(DBDictionary dict,string key,T value,Transaction tr)
        {
            if(!dict.IsWriteEnabled)dict.UpgradeOpen();
            Xrecord xr;
            if(dict.Contains(key))xr=(Xrecord)tr.GetObject(dict.GetAt(key),OpenMode.ForWrite);
            else {xr=new Xrecord();dict.SetAt(key,xr);tr.AddNewlyCreatedDBObject(xr,true);}
            var bytes=Encoding.UTF8.GetBytes(Json.Write(value));
            var values=new List<TypedValue>();
            for(int i=0;i<bytes.Length;i+=127)values.Add(new TypedValue(310,bytes.Skip(i).Take(127).ToArray()));
            using(var rb=new ResultBuffer(values.ToArray()))xr.Data=rb;
        }
        static DBDictionary Root(Database db,Transaction tr)
            =>(DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId,OpenMode.ForRead);
        internal static AssemblyMark Load(Database db,Transaction tr)
        {
            var mark=Read<AssemblyMark>(Root(db,tr),MarkKey,tr);
            if(mark!=null)mark.Validate();
            return mark;
        }
        internal static void Save(Database db,Transaction tr,AssemblyMark mark)
        {
            mark.Validate();
            Write(Root(db,tr),MarkKey,mark,tr);
        }
        internal static BlockTableRecord ModelSpace(Database db,Transaction tr,OpenMode mode=OpenMode.ForRead)
        {
            var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],mode);
        }
        static PartTag Tag(BlockReference reference,Transaction tr)
        {
            if(reference.ExtensionDictionary.IsNull)return null;
            var tag=Read<PartTag>((DBDictionary)tr.GetObject(reference.ExtensionDictionary,OpenMode.ForRead),PartKey,tr);
            return tag!=null && tag.Handle==reference.Handle.ToString()?tag:null;
        }
        static void Tag(BlockReference reference,Transaction tr,AssemblyMark mark,PartInstance instance)
        {
            if(reference.ExtensionDictionary.IsNull)reference.CreateExtensionDictionary();
            Write((DBDictionary)tr.GetObject(reference.ExtensionDictionary,OpenMode.ForRead),PartKey,
                new PartTag { MarkId=mark.Id,InstanceId=instance.Id,Handle=reference.Handle.ToString() },tr);
        }
        static Vector3 V(Vector3d v)=>new Vector3(v.X,v.Y,v.Z);
        static Vector3 V(Point3d p)=>new Vector3(p.X,p.Y,p.Z);
        static Vector3d D(Vector3 v)=>new Vector3d(v.X,v.Y,v.Z);
        static Point3d P(Vector3 v)=>new Point3d(v.X,v.Y,v.Z);
        internal static RigidPlacement Placement(BlockReference reference)
        {
            var t=reference.BlockTransform;
            var x=Vector3d.XAxis.TransformBy(t);var y=Vector3d.YAxis.TransformBy(t);var z=Vector3d.ZAxis.TransformBy(t);
            var p=new RigidPlacement { Origin=V(Point3d.Origin.TransformBy(t)),AxisX=V(x),AxisY=V(y) };
            p.Validate();
            if((x.CrossProduct(y)-z).Length>1e-8)throw new InvalidOperationException("Масштабирование и зеркальное отражение деталей не поддерживаются.");
            return p;
        }
        static Matrix3d Transform(RigidPlacement p)
        {
            p.Validate();var x=D(p.AxisX);var y=D(p.AxisY);
            return Matrix3d.AlignCoordinateSystem(Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis,P(p.Origin),x,y,x.CrossProduct(y));
        }
        // Native MOVE/ROTATE are read back on the next constructor command.
        // Ordinary COPY is detached; an erased managed reference must be restored with UNDO.
        internal static Dictionary<Guid,BlockReference> Synchronize(Database db,Transaction tr,AssemblyMark mark,bool restoreMissing=false)
        {
            var result=new Dictionary<Guid,BlockReference>();
            foreach(ObjectId id in ModelSpace(db,tr))
            {
                var reference=tr.GetObject(id,OpenMode.ForRead) as BlockReference;
                if(reference==null)continue;
                var tag=Tag(reference,tr);
                if(tag==null)continue;
                if(tag.MarkId!=mark.Id || !mark.Instances.Any(i=>i.Id==tag.InstanceId) || result.ContainsKey(tag.InstanceId))
                    throw new InvalidOperationException("Обнаружена несогласованная деталь конструктора.");
                result.Add(tag.InstanceId,reference);
            }
            foreach(var instance in mark.Instances)
            {
                if(!result.TryGetValue(instance.Id,out var reference))
                {
                    if(!restoreMissing)
                        throw new InvalidOperationException("Деталь удалена обычной командой. Восстановите её через UNDO или NAREPAIR.");
                    continue;
                }
                instance.Placement=Placement(reference);
            }
            mark.Validate();
            if(restoreMissing)
            {
                // Restore only missing managed instances at their last saved positions.
                foreach(var instance in mark.Instances.Where(i=>!result.ContainsKey(i.Id)))
                {
                    var definition=mark.Parts.Single(p=>p.Id==instance.PartId);
                    var shared=mark.Instances.Where(i=>i.PartId==instance.PartId && result.ContainsKey(i.Id)).Select(i=>result[i.Id]).FirstOrDefault();
                    var geometry=shared==null?Geometry(db,tr,definition):shared.BlockTableRecord;
                    result.Add(instance.Id,Insert(db,tr,mark,instance,geometry));
                }
            }
            return result;
        }
        static Region MakeRegion(Curve curve)
        {
            using(curve)
            {
                var curves=new DBObjectCollection();curves.Add(curve);
                var regions=Region.CreateFromCurves(curves);
                if(regions.Count!=1)
                {
                    foreach(DBObject region in regions)region.Dispose();
                    throw new InvalidOperationException("Не удалось построить сечение детали.");
                }
                return (Region)regions[0];
            }
        }
        static Solid3d Extrude(Curve curve,double length)
        {
            using(var region=MakeRegion(curve))
            {
                var solid=new Solid3d();
                try {solid.Extrude(region,length,0);return solid;}
                catch {solid.Dispose();throw;}
            }
        }
        static Solid3d CircleSolid(double radius,double length)
            =>Extrude(new Circle(Point3d.Origin,Vector3d.ZAxis,radius),length);
        static Solid3d Solid(PartDefinition part)
        {
            part.Validate();
            if(part.Kind==PartKind.Pipe)
            {
                var pipe=part.Pipe;
                var solid=CircleSolid(pipe.OuterDiameter/2,pipe.Length);
                try {using(var inner=CircleSolid(pipe.OuterDiameter/2-pipe.Thickness,pipe.Length))
                    solid.BooleanOperation(BooleanOperationType.BoolSubtract,inner);return solid;}
                catch {solid.Dispose();throw;}
            }
            if(part.Kind==PartKind.RectangularPlate)
            {
                var p=part.Plate;
                var contour=new Polyline(4);
                contour.AddVertexAt(0,new Point2d(0,0),0,0,0);
                contour.AddVertexAt(1,new Point2d(p.Length,0),0,0,0);
                contour.AddVertexAt(2,new Point2d(p.Length,p.Width),0,0,0);
                contour.AddVertexAt(3,new Point2d(0,p.Width),0,0,0);
                contour.Closed=true;
                return Extrude(contour,p.Thickness);
            }
            throw new NotSupportedException("В экспериментальном редакторе пока доступны труба и прямоугольный лист.");
        }
        internal static ObjectId Geometry(Database db,Transaction tr,PartDefinition part)
        {
            using(var solid=Solid(part))
            {
                var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
                var block=new BlockTableRecord { Name="NA_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters };
                var id=bt.Add(block);tr.AddNewlyCreatedDBObject(block,true);
                // Clone into the database; the transient solid can then be disposed.
                var body=(Solid3d)solid.Clone();
                body.SetDatabaseDefaults(db);body.Layer="0";
                block.AppendEntity(body);tr.AddNewlyCreatedDBObject(body,true);
                return id;
            }
        }
        internal static BlockReference Insert(Database db,Transaction tr,AssemblyMark mark,PartInstance instance,ObjectId geometry)
        {
            var space=ModelSpace(db,tr,OpenMode.ForWrite);
            var reference=new BlockReference(Point3d.Origin,geometry);
            reference.SetDatabaseDefaults(db);
            reference.TransformBy(Transform(instance.Placement));
            space.AppendEntity(reference);tr.AddNewlyCreatedDBObject(reference,true);
            Tag(reference,tr,mark,instance);
            return reference;
        }
        internal static void Move(BlockReference reference,RigidPlacement placement)
        {
            if(!reference.IsWriteEnabled)reference.UpgradeOpen();
            reference.TransformBy(Transform(placement)*reference.BlockTransform.Inverse());
        }
        internal static void Replace(BlockReference reference,ObjectId geometry)
        {
            if(!reference.IsWriteEnabled)reference.UpgradeOpen();
            reference.BlockTableRecord=geometry;
        }
    }
}
