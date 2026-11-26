using System;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using KotNode.Core;

namespace KotNode.AutoCAD
{
    // DWG owns native serialization; JSON does not approximate curves, text or styles.
    internal static class FamilyManualGraphics
    {
        internal static string Export(Database source,Transaction tr,AssemblyViews.Entry entry)
        {
            var shell=(BlockTableRecord)tr.GetObject(entry.Reference.BlockTableRecord,OpenMode.ForRead);
            var ids=new ObjectIdCollection(shell.Cast<ObjectId>().Where(id=>!id.IsErased&&id.Handle.ToString()!=entry.Data.GeneratedChildHandle).ToArray());
            if(ids.Count==0)return null;
            string path=Path.Combine(Path.GetTempPath(),"kotnode-family-"+Guid.NewGuid().ToString("N")+".dwg");
            try
            {
                using(var payload=new Database(true,true))
                {
                    payload.Insunits=UnitsValue.Millimeters;
                    ObjectId destination;
                    using(var pt=payload.TransactionManager.StartTransaction())
                    { destination=AssemblyDrawing.ModelSpace(payload,pt).ObjectId;pt.Commit(); }
                    source.WblockCloneObjects(ids,destination,new IdMapping(),DuplicateRecordCloning.MangleName,false);
                    payload.SaveAs(path,DwgVersion.AC1032);
                }
                return Convert.ToBase64String(File.ReadAllBytes(path));
            }
            finally{if(File.Exists(path))File.Delete(path);}
        }
        internal static void Import(Database target,Transaction tr,BlockReference view,string content)
        {
            if(string.IsNullOrEmpty(content))return;
            string path=Path.Combine(Path.GetTempPath(),"kotnode-family-"+Guid.NewGuid().ToString("N")+".dwg");
            try
            {
                File.WriteAllBytes(path,Convert.FromBase64String(content));
                using(var payload=new Database(false,true))
                {
                    payload.ReadDwgFile(path,FileShare.Read,true,null);
                    payload.CloseInput(true);
                    ObjectIdCollection ids;
                    using(var pt=payload.TransactionManager.StartTransaction())
                    { ids=new ObjectIdCollection(AssemblyDrawing.ModelSpace(payload,pt).Cast<ObjectId>().Where(id=>!id.IsErased).ToArray());pt.Commit(); }
                    if(ids.Count>0)payload.WblockCloneObjects(ids,view.BlockTableRecord,new IdMapping(),DuplicateRecordCloning.MangleName,false);
                }
            }
            finally{if(File.Exists(path))File.Delete(path);}
        }
    }
}
