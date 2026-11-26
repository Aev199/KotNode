using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    // Outer definition is deliberately never replaced after creation/migration.
    // Native annotations and extra linework in it keep their handles on updates.
    internal static class ViewShell
    {
        static string N(double v)=>v.ToString("R",CultureInfo.InvariantCulture);
        static string P(Point3d p)=>ViewGeometryStamp.Record(N(p.X),N(p.Y),N(p.Z));
        static string V(Vector3d p)=>ViewGeometryStamp.Record(N(p.X),N(p.Y),N(p.Z));
        static string EntityStamp(Entity e)
        {
            var fields=new List<string>{
                e.Handle.ToString(),e.GetType().FullName,e.Layer,e.Color.ToString(),e.ColorIndex.ToString(CultureInfo.InvariantCulture),
                e.Linetype,N(e.LinetypeScale),e.LineWeight.ToString(),e.Visible.ToString()};
            if(e is Line line)fields.AddRange(new[]{P(line.StartPoint),P(line.EndPoint),V(line.Normal),N(line.Thickness)});
            else if(e is Arc arc)fields.AddRange(new[]{P(arc.Center),N(arc.Radius),N(arc.StartAngle),N(arc.EndAngle),V(arc.Normal),N(arc.Thickness)});
            else if(e is Circle circle)fields.AddRange(new[]{P(circle.Center),N(circle.Radius),V(circle.Normal),N(circle.Thickness)});
            else if(e is Polyline poly)
            {
                fields.AddRange(new[]{poly.Closed.ToString(),poly.Plinegen.ToString(),poly.NumberOfVertices.ToString(CultureInfo.InvariantCulture)});
                for(int i=0;i<poly.NumberOfVertices;i++)
                {
                    var pt=poly.GetPoint2dAt(i);
                    fields.Add(ViewGeometryStamp.Record(N(pt.X),N(pt.Y),N(poly.GetBulgeAt(i))));
                }
            }
            else if(e is Hatch hatch)fields.AddRange(new[]{hatch.PatternName,N(hatch.PatternAngle),N(hatch.PatternSpace),N(hatch.PatternScale),hatch.HatchStyle.ToString(),hatch.NumberOfLoops.ToString(CultureInfo.InvariantCulture)});
            else if(e is DBText txt)fields.AddRange(new[]{txt.TextString??"",P(txt.Position),N(txt.Height)});
            else if(e is Solid solid)fields.AddRange(new[]{P(solid.GetPointAt(0)),P(solid.GetPointAt(1)),P(solid.GetPointAt(2))});
            // Extents supplement the inventory; for other native curves or complex
            // hatch edits this is not a complete serialization of the object.
            var bounds=e.GeometricExtents;
            fields.Add(P(bounds.MinPoint));fields.Add(P(bounds.MaxPoint));
            return ViewGeometryStamp.Record(fields.ToArray());
        }
        internal static string Stamp(Transaction tr,ObjectId geometry)
        {
            var block=(BlockTableRecord)tr.GetObject(geometry,OpenMode.ForRead);
            return ViewGeometryStamp.Compute(block.Cast<ObjectId>().Where(id=>!id.IsErased)
                .Select(id=>EntityStamp((Entity)tr.GetObject(id,OpenMode.ForRead))));
        }
        internal static ObjectId Create(Database db,Transaction tr,ObjectId geometry,AssemblyViews.ViewData data)
        {
            var table=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
            var block=new BlockTableRecord{Name="NA_VIEW_SHELL_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters};
            var id=table.Add(block);tr.AddNewlyCreatedDBObject(block,true);
            var child=new BlockReference(Point3d.Origin,geometry);
            child.SetDatabaseDefaults(db);child.Layer="0";
            block.AppendEntity(child);tr.AddNewlyCreatedDBObject(child,true);
            data.GeneratedChildHandle=child.Handle.ToString();
            data.GeneratedStamp=Stamp(tr,geometry);
            return id;
        }
        internal static BlockReference RequireUnchanged(Transaction tr,AssemblyViews.Entry entry)
        {
            var data=entry.Data;
            var shell=(BlockTableRecord)tr.GetObject(entry.Reference.BlockTableRecord,OpenMode.ForRead);
            var child=shell.Cast<ObjectId>().Where(id=>!id.IsErased)
                .Select(id=>tr.GetObject(id,OpenMode.ForRead) as BlockReference)
                .SingleOrDefault(b=>b!=null&&b.Handle.ToString()==data.GeneratedChildHandle);
            if(child==null)
                throw new InvalidOperationException("Вложенный блок автогеометрии удалён или заменён. Обновление остановлено, чтобы не потерять ручную работу.");
            var scale=child.ScaleFactors;
            if(child.Position.DistanceTo(Point3d.Origin)>1e-8||Math.Abs(child.Rotation)>1e-8||
                Math.Abs(scale.X-1)>1e-8||Math.Abs(scale.Y-1)>1e-8||Math.Abs(scale.Z-1)>1e-8||
                (child.Normal-Vector3d.ZAxis).Length>1e-8||child.Layer!="0"||!child.Visible)
                throw new InvalidOperationException("Изменено положение или свойства вложенного блока автогеометрии. Обновление остановлено.");
            if(string.IsNullOrEmpty(data.GeneratedStamp)||Stamp(tr,child.BlockTableRecord)!=data.GeneratedStamp)
                throw new InvalidOperationException("Обнаружены изменения автогеометрии. Обновление остановлено; сохраните ручные правки отдельно. Редактируйте внешний блок вида.");
            return child;
        }
    }
}
