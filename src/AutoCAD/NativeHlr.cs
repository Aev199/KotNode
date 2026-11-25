using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Geometry;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace KotNode.AutoCAD
{
    // Optional mixed-mode component. The model constructor and pure sections
    // remain usable without the native SDK or this DLL.
    internal static class NativeHlr
    {
        static MethodInfo method;
        internal static void Require()
        {
            if(method!=null)return;
            var folder=Path.GetDirectoryName(typeof(NativeHlr).Assembly.Location);
            var path=Path.Combine(folder,"KotNode.HlrBridge.dll");
            if(!File.Exists(path))
                throw new InvalidOperationException("Для автоматических видов и разрезов требуется KotNode.HlrBridge.dll (AutoCAD 2021 x64). См. docs/HLR.md. Доступны NACROSS, NAEXPORTCUT и FLATSHOT.");
            try
            {
                var assembly=Assembly.LoadFrom(path);
                var type=assembly.GetType("KotNode.Native.Projector",true);
                method=type.GetMethod("Project",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Solid3d[]),typeof(bool),typeof(string).MakeByRefType()},null);
                if(method==null)throw new MissingMethodException("Projector.Project");
            }
            catch(System.Exception ex)
            {
                throw new InvalidOperationException("Не удалось загрузить HLR. Проверьте версию DLL и наличие AsdkHlrApi24.dbx в AutoCAD 2021.",ex);
            }
        }
        internal static List<Tuple<Entity,bool>> Project(Database db,Transaction tr,List<Solid3d> solids,bool hidden)
        {
            Require();
            if(solids.Count==0)throw new ArgumentException("Нет тел HLR.");
            var temporary=new List<Solid3d>();
            // Work on resident, normalized copies. Leave original model intact.
            var bounds=solids.Select(s=>s.GeometricExtents).ToList();
            var center=new Point3d(
                (bounds.Min(e=>e.MinPoint.X)+bounds.Max(e=>e.MaxPoint.X))/2,
                (bounds.Min(e=>e.MinPoint.Y)+bounds.Max(e=>e.MaxPoint.Y))/2,
                (bounds.Min(e=>e.MinPoint.Z)+bounds.Max(e=>e.MaxPoint.Z))/2);
            var shift=Matrix3d.Displacement(Point3d.Origin-center);
            try
            {
                var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
                foreach(var solid in solids)
                {
                    var copy=(Solid3d)solid.Clone();
                    try
                    {
                        copy.SetDatabaseDefaults(db);copy.Layer="0";
                        copy.Visible=true;
                        copy.TransformBy(shift);
                        space.AppendEntity(copy);tr.AddNewlyCreatedDBObject(copy,true);
                        temporary.Add(copy);
                        copy.DowngradeOpen();
                    }
                    catch {if(copy.ObjectId.IsNull)copy.Dispose();throw;}
                }
                Diagnostics.Step("HLR: DB-resident copies="+temporary.Count+", center="+center);
                var args=new object[]{temporary.ToArray(),hidden,null};
                var result=method.Invoke(null,args) as List<Tuple<Entity,bool>>;
                Diagnostics.Step("HLR native: "+(args[2]??"нет диагностики"));
                if(result==null)throw new InvalidOperationException("Несовместимый формат ответа HLR.");
                try
                {
                    var back=Matrix3d.Displacement(new Vector3d(center.X,center.Y,0));
                    foreach(var item in result)item.Item1.TransformBy(back);
                    return result;
                }
                catch {foreach(var item in result)item.Item1.Dispose();throw;}
            }
            catch(TargetInvocationException ex)
            {
                throw new InvalidOperationException("Ошибка нативного HLR.",ex.InnerException??ex);
            }
            finally
            {
                foreach(var copy in temporary)
                {
                    if(!copy.IsWriteEnabled)copy.UpgradeOpen();
                    if(!copy.IsErased)copy.Erase();
                }
            }
        }
        internal static string SelfTest(Database db,Transaction tr)
        {
            using(var box=new Solid3d())
            {
                box.CreateBox(200,100,50);
                var curves=Project(db,tr,new List<Solid3d>{box},false);
                try
                {
                    if(curves.Count==0)throw new InvalidOperationException("HLR не вернул контуры даже для простого параллелепипеда.");
                    var ext=curves.Select(x=>x.Item1.GeometricExtents).ToList();
                    double w=ext.Max(e=>e.MaxPoint.X)-ext.Min(e=>e.MinPoint.X);
                    double h=ext.Max(e=>e.MaxPoint.Y)-ext.Min(e=>e.MinPoint.Y);
                    if(Math.Abs(w-200)>1e-5||Math.Abs(h-100)>1e-5)
                        throw new InvalidOperationException("HLR: неверные габариты тестового вида "+w+" x "+h);
                    if(ext.Any(e=>Math.Abs(e.MinPoint.Z)>1e-5||Math.Abs(e.MaxPoint.Z)>1e-5))
                        throw new InvalidOperationException("HLR: тестовая проекция не лежит в XY.");
                    return "HLR OK: "+curves.Count+" кривых, 200 x 100 мм.";
                }
                finally {foreach(var item in curves)item.Item1.Dispose();}
            }
        }
    }
}
