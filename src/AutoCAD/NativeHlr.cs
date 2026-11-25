using System;
using System.Collections.Generic;
using System.IO;
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
                method=type.GetMethod("Project",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Solid3d[]),typeof(bool)},null);
                if(method==null)throw new MissingMethodException("Projector.Project");
            }
            catch(System.Exception ex)
            {
                throw new InvalidOperationException("Не удалось загрузить HLR. Проверьте версию DLL и наличие AsdkHlrApi24.dbx в AutoCAD 2021.",ex);
            }
        }
        internal static List<Tuple<Entity,bool>> Project(List<Solid3d> solids,bool hidden)
        {
            Require();
            try
            {
                var result=method.Invoke(null,new object[]{solids.ToArray(),hidden}) as List<Tuple<Entity,bool>>;
                if(result==null)throw new InvalidOperationException("Несовместимый формат ответа HLR.");
                return result;
            }
            catch(TargetInvocationException ex)
            {
                throw new InvalidOperationException("Ошибка нативного HLR.",ex.InnerException??ex);
            }
        }
    }
}
