using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;
using ModelJson=KotNode.Core.Json;

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
                return ModelJson.Read<T>(Encoding.UTF8.GetString(data.AsArray().Where(v=>v.TypeCode==310).SelectMany(v=>(byte[])v.Value).ToArray()));
            }
        }
        static void Write<T>(DBDictionary dict,string key,T value,Transaction tr)
        {
            if(!dict.IsWriteEnabled)dict.UpgradeOpen();
            Xrecord xr;
            if(dict.Contains(key))xr=(Xrecord)tr.GetObject(dict.GetAt(key),OpenMode.ForWrite);
            else {xr=new Xrecord();dict.SetAt(key,xr);tr.AddNewlyCreatedDBObject(xr,true);}
            var bytes=Encoding.UTF8.GetBytes(ModelJson.Write(value));
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
        static string AuxPrefix(AssemblyMark mark)=>"NA_AUX_"+mark.Id.ToString("N")+"_";
        internal static List<Tuple<string,ObjectId[]>> AuxiliaryGroups(Database db,Transaction tr,AssemblyMark mark)
        {
            var result=new List<Tuple<string,ObjectId[]>>();
            var dict=(DBDictionary)tr.GetObject(db.GroupDictionaryId,OpenMode.ForRead);
            foreach(DBDictionaryEntry item in dict)
            {
                if(!item.Key.StartsWith(AuxPrefix(mark),StringComparison.OrdinalIgnoreCase))continue;
                var group=(Group)tr.GetObject(item.Value,OpenMode.ForRead);
                result.Add(Tuple.Create(item.Key,group.GetAllEntityIds().Where(id=>!id.IsErased).ToArray()));
            }
            return result;
        }
        internal static void AddAuxiliary(Database db,Transaction tr,AssemblyMark mark,ObjectId[] ids)
        {
            if(ids.Length==0)throw new InvalidOperationException("Не выбрана вспомогательная графика.");
            var existing=new HashSet<ObjectId>(AuxiliaryGroups(db,tr,mark).SelectMany(g=>g.Item2));
            var space=ModelSpace(db,tr);
            foreach(var id in ids)
            {
                var entity=tr.GetObject(id,OpenMode.ForRead) as Entity;
                if(entity==null||entity.OwnerId!=space.ObjectId||existing.Contains(id))
                    throw new InvalidOperationException("Объект уже входит во вспомогательный чертёж или находится вне модели.");
                if(entity is BlockReference block && !block.ExtensionDictionary.IsNull &&
                   ((DBDictionary)tr.GetObject(block.ExtensionDictionary,OpenMode.ForRead)).Contains(PartKey))
                    throw new InvalidOperationException("Управляемую деталь нельзя включить во вспомогательную графику.");
            }
            var dict=(DBDictionary)tr.GetObject(db.GroupDictionaryId,OpenMode.ForWrite);
            var group=new Group("Вспомогательная графика "+mark.Code,true);
            dict.SetAt(AuxPrefix(mark)+Guid.NewGuid().ToString("N"),group);
            tr.AddNewlyCreatedDBObject(group,true);
            group.Append(new ObjectIdCollection(ids));
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
        static Polyline Ring(System.Collections.Generic.IList<KotNode.Core.Point2> points)
        {
            var p=new Polyline(points.Count);
            double area=0;
            for(int i=0;i<points.Count;i++)
            {
                var a=points[i];var b=points[(i+1)%points.Count];
                area+=a.X*b.Y-a.Y*b.X;
            }
            for(int i=0;i<points.Count;i++)
            {
                var a=points[area>=0?i:points.Count-1-i];
                p.AddVertexAt(i,new Point2d(a.X,a.Y),0,0,0);
            }
            p.Closed=true;return p;
        }
        static Solid3d Polygon(PolygonPlateShape p)
        {
            var solid=Extrude(Ring(p.Outer),p.Thickness);
            try
            {
                foreach(var hole in p.Holes)
                    using(var cut=Extrude(Ring(hole),p.Thickness))
                        solid.BooleanOperation(BooleanOperationType.BoolSubtract,cut);
                foreach(var hole in p.RoundHoles)
                    using(var cut=Extrude(new Circle(new Point3d(hole.Center.X,hole.Center.Y,0),Vector3d.ZAxis,hole.Radius),p.Thickness))
                        solid.BooleanOperation(BooleanOperationType.BoolSubtract,cut);
                return solid;
            }
            catch {solid.Dispose();throw;}
        }
        static Solid3d Curved(CurvedPlateShape p)
        {
            double ri=p.InnerRadius,ro=ri+p.Thickness,a=p.AngleRadians;
            if(Math.Abs(a-2*Math.PI)<1e-10)
            {
                var tube=CircleSolid(ro,p.Length);
                try {using(var cut=CircleSolid(ri,p.Length))tube.BooleanOperation(BooleanOperationType.BoolSubtract,cut);return tube;}
                catch {tube.Dispose();throw;}
            }
            int n=(int)Math.Ceiling(a/(Math.PI/2));
            double step=a/n;
            var contour=new Polyline(2*(n+1));
            for(int i=0;i<=n;i++)
                contour.AddVertexAt(i,new Point2d(ro*Math.Cos(i*step),ro*Math.Sin(i*step)),i<n?Math.Tan(step/4):0,0,0);
            for(int i=0;i<=n;i++)
            {
                double angle=a-i*step;
                contour.AddVertexAt(n+1+i,new Point2d(ri*Math.Cos(angle),ri*Math.Sin(angle)),i<n?-Math.Tan(step/4):0,0,0);
            }
            contour.Closed=true;
            return Extrude(contour,p.Length);
        }
        static Solid3d Beam(IBeamShape p)
        {
            double b=p.Width/2,h=p.Height/2,w=p.WebThickness/2,t=p.FlangeThickness;
            var points=new[]{new KotNode.Core.Point2(-b,-h),new KotNode.Core.Point2(b,-h),
                new KotNode.Core.Point2(b,-h+t),new KotNode.Core.Point2(w,-h+t),new KotNode.Core.Point2(w,h-t),
                new KotNode.Core.Point2(b,h-t),new KotNode.Core.Point2(b,h),new KotNode.Core.Point2(-b,h),
                new KotNode.Core.Point2(-b,h-t),new KotNode.Core.Point2(-w,h-t),new KotNode.Core.Point2(-w,-h+t),
                new KotNode.Core.Point2(-b,-h+t)};
            return Extrude(Ring(points),p.Length);
        }
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
            if(part.Kind==PartKind.PolygonPlate)return Polygon(part.PolygonPlate);
            if(part.Kind==PartKind.CurvedPlate)return Curved(part.CurvedPlate);
            if(part.Kind==PartKind.IBeam)return Beam(part.IBeam);
            throw new NotSupportedException("Этот тип детали пока не строится в AutoCAD.");
        }
        static Matrix3d Frame(Point3d origin,Vector3d x,Vector3d y,Vector3d z)
            =>Matrix3d.AlignCoordinateSystem(Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis,origin,x,y,z);

        // Separate tangent ACIS bodies inside one managed definition. Avoid fragile
        // Boolean unions at tangential faces; quantities still count one bar.
        static List<Solid3d> BarBodies(RebarShape bar)
        {
            bar.Validate();
            var points=bar.Points.Select(P).ToArray();
            int n=points.Length;
            var dirs=new Vector3d[n-1];
            var trim=new double[n];var angles=new double[n];
            for(int i=0;i<n-1;i++)dirs[i]=(points[i+1]-points[i]).GetNormal();
            for(int i=1;i<n-1;i++)
            {
                angles[i]=Math.Acos(Math.Max(-1,Math.Min(1,dirs[i-1].DotProduct(dirs[i]))));
                if(angles[i]>1e-8)trim[i]=bar.BendRadii[i-1]*Math.Tan(angles[i]/2);
            }
            var bodies=new List<Solid3d>();
            try
            {
                for(int i=0;i<n-1;i++)
                {
                    var a=points[i]+dirs[i]*trim[i];
                    double length=(points[i+1]-points[i]).Length-trim[i]-trim[i+1];
                    if(length>1e-7)
                    {
                        var z=dirs[i];var x=z.GetPerpendicularVector().GetNormal();var y=z.CrossProduct(x);
                        var body=CircleSolid(bar.Diameter/2,length);bodies.Add(body);
                        body.TransformBy(Frame(a,x,y,z));
                    }
                    int j=i+1;
                    if(j>=n-1 || angles[j]<=1e-8)continue;
                    double r=bar.BendRadii[j-1],angle=angles[j];
                    var tangent=dirs[i];var normal=tangent.CrossProduct(dirs[j]).GetNormal();
                    var radial=tangent.CrossProduct(normal).GetNormal();
                    var start=points[j]-tangent*trim[j];
                    var center=start-radial*r;
                    // Construct circular sweep in a simple local XY frame.
                    using(var path=new Arc(Point3d.Origin,r,0,angle))
                    using(var profile=new Circle(new Point3d(r,0,0),Vector3d.YAxis,bar.Diameter/2))
                    {
                        var body=new Solid3d();bodies.Add(body);
                        body.CreateSweptSolid(profile,path,new SweepOptions());
                        body.TransformBy(Frame(center,radial,tangent,normal));
                    }
                }
                if(bodies.Count==0)throw new InvalidOperationException("Не удалось построить тело стержня.");
                return bodies;
            }
            catch {foreach(var body in bodies)body.Dispose();throw;}
        }
        internal static ObjectId Geometry(Database db,Transaction tr,PartDefinition part)
        {
            var solids=part.Kind==PartKind.Rebar?BarBodies(part.Rebar):new List<Solid3d>{Solid(part)};
            try
            {
                var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
                var block=new BlockTableRecord { Name="NA_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters };
                var id=bt.Add(block);tr.AddNewlyCreatedDBObject(block,true);
                foreach(var solid in solids)
                {
                    var body=(Solid3d)solid.Clone();
                    body.SetDatabaseDefaults(db);body.Layer="0";
                    block.AppendEntity(body);tr.AddNewlyCreatedDBObject(body,true);
                }
                return id;
            }
            finally {foreach(var solid in solids)solid.Dispose();}
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
