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
            // The AutoCAD 2021 array constructor can throw before we can check
            // Count (notably on a view with no manually added entities).
            // Build the native collection incrementally; do the same on import.
            var ids=new ObjectIdCollection();
            foreach(ObjectId id in shell)
                if(!id.IsErased&&id.Handle.ToString()!=entry.Data.GeneratedChildHandle)ids.Add(id);
            Diagnostics.Step("Семейство: ручная графика «"+entry.Data.Name+"», объектов: "+ids.Count);
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
                    {
                        ids=new ObjectIdCollection();
                        foreach(ObjectId id in AssemblyDrawing.ModelSpace(payload,pt))
                            if(!id.IsErased)ids.Add(id);
                        pt.Commit();
                    }
                    if(ids.Count>0)payload.WblockCloneObjects(ids,view.BlockTableRecord,new IdMapping(),DuplicateRecordCloning.MangleName,false);
                }
            }
            finally{if(File.Exists(path))File.Delete(path);}
        }
    }
}
