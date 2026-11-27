using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.NodeCommands))]
namespace KotNode.AutoCAD
{
    public sealed class NodeCommands
    {
        static void Run(string name,Action<Document,Transaction> action)
        {
            var doc=AcApp.DocumentManager.MdiActiveDocument;if(doc==null)return;
            Diagnostics.Begin(name);Diagnostics.EnableLiveTrace(name);
            try
            {
                using(var tr=doc.Database.TransactionManager.StartTransaction()){action(doc,tr);tr.Commit();}
                doc.Editor.Regen();AssemblyCommands.RefreshPalette(doc);
            }
            catch(System.Exception ex){var log=Diagnostics.Write(ex);doc.Editor.WriteMessage("\nУзлы DWG: "+ex.Message+"\nЖурнал: "+log);}
            finally{Diagnostics.EndLiveTrace();}
        }
        internal static List<ObjectId> Owned(Database db,Transaction tr,AssemblyMark mark)
        {
            var views=AssemblyViews.Find(db,tr,mark);
            var viewRefs=new HashSet<ObjectId>(views.Select(v=>v.Reference.ObjectId));
            var viewIds=new HashSet<Guid>(views.Select(v=>v.Data.Id));
            var aux=new HashSet<ObjectId>(AssemblyDrawing.AuxiliaryGroups(db,tr,mark).SelectMany(g=>g.Item2));
            var ids=new List<ObjectId>();
            foreach(ObjectId id in AssemblyDrawing.ModelSpace(db,tr))
            {
                if(id.IsErased)continue;
                var entity=tr.GetObject(id,OpenMode.ForRead) as Entity;if(entity==null)continue;
                var part=entity is BlockReference b?AssemblyDrawing.Tag(b,tr):null;
                var anno=ViewAnnotationDrawing.ReadTag(entity,tr);
                var table=AssemblyTableDrawing.ReadTag(entity,tr);
                if(part?.MarkId==mark.Id||viewRefs.Contains(id)||anno!=null&&viewIds.Contains(anno.ViewId)||table?.MarkId==mark.Id||aux.Contains(id))ids.Add(id);
            }
            return ids;
        }
        internal static List<ObjectId> ExclusiveOwned(Database db,Transaction tr,AssemblyMark mark)
        {
            var ids=Owned(db,tr,mark);var own=new HashSet<ObjectId>(ids);
            foreach(var other in AssemblyDrawing.LoadNodes(db,tr).Nodes.Where(n=>n.Id!=mark.Id))
                if(Owned(db,tr,other).Any(own.Contains))
                    throw new InvalidOperationException("Найдена общая графика у узлов «"+mark.Code+"» и «"+other.Code+"». Перемещение/удаление остановлено, чтобы не затронуть соседний узел.");
            return ids;
        }
        static AssemblyMark Need(Database db,Transaction tr)=>AssemblyDrawing.Load(db,tr)??throw new InvalidOperationException("В DWG нет узлов.");
        [CommandMethod("NAACTIVATE",CommandFlags.Modal)]
        public void Activate()=>Run("NAACTIVATE",(doc,tr)=>{
            var r=doc.Editor.GetString("\nИдентификатор узла: ");if(r.Status!=PromptStatus.OK)return;
            if(!Guid.TryParse(r.StringResult,out var id))throw new InvalidOperationException("Выберите узел в списке палитры или через NANODES.");
            var nodes=AssemblyDrawing.LoadNodes(doc.Database,tr);nodes.Activate(id);AssemblyDrawing.SaveNodes(doc.Database,tr,nodes);
            doc.Editor.WriteMessage("\nАктивный узел: "+nodes.Active().Code);
        });
        [CommandMethod("NANODES",CommandFlags.Modal)]
        public void Choose()=>Run("NANODES",(doc,tr)=>{
            var nodes=AssemblyDrawing.LoadNodes(doc.Database,tr);if(nodes.Nodes.Count==0)throw new InvalidOperationException("Добавьте узел из семейства или создайте новый.");
            using(var f=new Fields("Активный узел в этом DWG"))
            {
                f.Choice("Узел","node",nodes.Nodes.ToDictionary(n=>n.Id.ToString(),n=>n.Code+" — "+n.Name),nodes.ActiveId.ToString());
                if(AcApp.ShowModalDialog(AcApp.MainWindow.Handle,f,false)!=DialogResult.OK)return;
                nodes.Activate(Guid.Parse(f.Get("node")));AssemblyDrawing.SaveNodes(doc.Database,tr,nodes);
            }
        });
        [CommandMethod("NAACTIVEPICK",CommandFlags.Modal)]
        public void Pick()=>Run("NAACTIVEPICK",(doc,tr)=>{
            var r=doc.Editor.GetEntity("\nВыберите деталь, вид, размер, выноску или таблицу нужного узла: ");if(r.Status!=PromptStatus.OK)return;
            var nodes=AssemblyDrawing.LoadNodes(doc.Database,tr);
            var found=nodes.Nodes.Where(n=>Owned(doc.Database,tr,n).Contains(r.ObjectId)).ToList();
            if(found.Count!=1)throw new InvalidOperationException("Объект не принадлежит одному управляемому узлу. Выберите его в списке палитры.");
            nodes.Activate(found[0].Id);AssemblyDrawing.SaveNodes(doc.Database,tr,nodes);
            doc.Editor.WriteMessage("\nАктивный узел: "+found[0].Code);
        });
        [CommandMethod("NANODESELECT",CommandFlags.Modal|CommandFlags.UsePickSet)]
        public void Select()=>Run("NANODESELECT",(doc,tr)=>doc.Editor.SetImpliedSelection(Owned(doc.Database,tr,Need(doc.Database,tr)).ToArray()));
        [CommandMethod("NANODERENAME",CommandFlags.Modal)]
        public void Rename()=>Run("NANODERENAME",(doc,tr)=>{
            var mark=Need(doc.Database,tr);
            using(var f=new Fields("Шифр и название активного узла"))
            {
                f.TextField("Шифр","code",mark.Code);f.TextField("Название","name",mark.Name);
                f.ValidateFields=()=>{if(string.IsNullOrWhiteSpace(f.Get("code"))||string.IsNullOrWhiteSpace(f.Get("name")))throw new InvalidOperationException("Заполните шифр и название.");};
                if(AcApp.ShowModalDialog(AcApp.MainWindow.Handle,f,false)!=DialogResult.OK)return;
                mark.Code=f.Get("code").Trim();mark.Name=f.Get("name").Trim();
            }
            // Validate the unique code before touching native tables/views.
            var nodes=AssemblyDrawing.LoadNodes(doc.Database,tr);nodes.Put(mark);
            var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);AssemblyViews.Update(doc.Database,tr,mark,refs);
            AssemblyTableDrawing.UpdateMarkTables(doc.Database,tr,mark);AssemblyDrawing.Save(doc.Database,tr,mark);
        });
        [CommandMethod("NAMOVENODE",CommandFlags.Modal)]
        public void Move()=>Run("NAMOVENODE",(doc,tr)=>{
            var mark=Need(doc.Database,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
            var a=doc.Editor.GetPoint("\nБазовая точка перемещения активного узла: ");if(a.Status!=PromptStatus.OK)return;
            var b=doc.Editor.GetPoint("\nНовое положение: ");if(b.Status!=PromptStatus.OK)return;
            var delta=(b.Value-a.Value).TransformBy(doc.Editor.CurrentUserCoordinateSystem);
            if(Math.Abs(delta.Z)>1e-8)throw new InvalidOperationException("Перемещайте узел в мировой XY.");
            Translate(doc.Database,tr,mark,refs,delta);
        });
        internal static void Translate(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,Vector3d delta)
        {
            if(Math.Abs(delta.Z)>1e-8)throw new InvalidOperationException("Перемещайте узел в мировой XY.");
            var ids=ExclusiveOwned(db,tr,mark);
            var views=AssemblyViews.Find(db,tr,mark);
            foreach(var v in views)ViewAnnotationDrawing.CaptureOffsets(db,tr,v);
            foreach(var id in ids)((Entity)tr.GetObject(id,OpenMode.ForWrite)).TransformBy(Matrix3d.Displacement(delta));
            var d=new Vector3(delta.X,delta.Y,0);NodePlacement.TranslateMark(mark,d);
            foreach(var v in views)
            {
                v.Data.Frame.Origin=NodePlacement.Add(v.Data.Frame.Origin,d);
                if(v.Data.ManualSnapshot||string.IsNullOrEmpty(v.Data.GeneratedChildHandle))
                {v.Data.SourceHash=AssemblyViews.Signature(mark);AssemblyViews.Save(v.Reference,tr,v.Data);}
                else AssemblyViews.Rebuild(db,tr,mark,refs,v);
            }
            AssemblyDrawing.Save(db,tr,mark);
        }
        [CommandMethod("NANODEDELETE",CommandFlags.Modal)]
        public void Delete()=>Run("NANODEDELETE",(doc,tr)=>{
            var mark=Need(doc.Database,tr);
            if(MessageBox.Show("Удалить узел «"+mark.Code+"» с деталями, видами, оформлением и таблицами?","Удаление узла",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
            foreach(var id in ExclusiveOwned(doc.Database,tr,mark))tr.GetObject(id,OpenMode.ForWrite).Erase();
            var groups=(DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId,OpenMode.ForRead);
            foreach(var g in AssemblyDrawing.AuxiliaryGroups(doc.Database,tr,mark))
            {var group=tr.GetObject(groups.GetAt(g.Item1),OpenMode.ForWrite);if(!groups.IsWriteEnabled)groups.UpgradeOpen();groups.Remove(g.Item1);group.Erase();}
            var nodes=AssemblyDrawing.LoadNodes(doc.Database,tr);nodes.Remove(mark.Id);AssemblyDrawing.SaveNodes(doc.Database,tr,nodes);
        });
        [CommandMethod("NAUPDATEALLNODES",CommandFlags.Modal)]
        public void UpdateAll()=>Run("NAUPDATEALLNODES",(doc,tr)=>{
            var nodes=AssemblyDrawing.LoadNodes(doc.Database,tr);
            foreach(var mark in nodes.Nodes.ToList())
            {
                var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
                ParametricCommands.ApplySolvedMark(doc.Database,tr,mark,refs,ParametricEngine.Solve(mark));
            }
        });
    }
}
