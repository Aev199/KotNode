using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.AssemblyTableDrawing))]

namespace KotNode.AutoCAD
{
    // Stage 6: Native AutoCAD Specification & VRS tables for universal AssemblyMarks and Schema-2 AssemblyProjects.
    public sealed class AssemblyTableDrawing
    {
        internal const string TableKey="KOTNODE_ASSEMBLY_TABLE_1";
        public sealed class TableTag
        {
            public Guid MarkId { get; set; }
            public Guid ProjectId { get; set; }
            public string Kind { get; set; } // "spec" | "vrs" | "project_vrs"
            public string Scope { get; set; }
            public double AnnotationScale { get; set; }=20;
            public string SelfHandle { get; set; }
        }
        internal static TableTag ReadTag(Entity e,Transaction tr)
        {
            if(e==null||e.ExtensionDictionary.IsNull)return null;
            var dict=(DBDictionary)tr.GetObject(e.ExtensionDictionary,OpenMode.ForRead);
            var tag=AssemblyDrawing.Read<TableTag>(dict,TableKey,tr);
            return tag!=null&&tag.SelfHandle==e.Handle.ToString()?tag:null;
        }
        static void WriteTag(Entity e,Transaction tr,TableTag tag)
        {
            if(!e.IsWriteEnabled)e.UpgradeOpen();
            if(e.ExtensionDictionary.IsNull)e.CreateExtensionDictionary();
            tag.SelfHandle=e.Handle.ToString();
            AssemblyDrawing.Write((DBDictionary)tr.GetObject(e.ExtensionDictionary,OpenMode.ForRead),TableKey,tag,tr);
        }
#pragma warning disable CS0618
        static void ShowHeaders(Table t){t.IsTitleSuppressed=false;t.IsHeaderSuppressed=false;}
#pragma warning restore CS0618
        static void ClearMerges(Table t)
        {
            for(int r=0;r<t.Rows.Count;r++)
                for(int c=0;c<t.Columns.Count;c++)
                {
                    var cell=t.Cells[r,c];
                    if(cell.IsMerged==true)
                    {
                        var range=cell.GetMergeRange();
                        if(range!=null)t.UnmergeCells(range);
                    }
                }
        }
        internal static Table RenderTable(Database db,Transaction tr,BlockTableRecord space,Table old,Point3d at,double scale,TablePlan plan)
        {
            ViewAnnotationDrawing.EnsureStylesAndLayers(db,tr);
            bool fresh=old==null;
            var t=old??new Table();
            int cols=plan.ColumnWidths.Length;
            if(fresh)
            {
                t.SetDatabaseDefaults(db);
                t.TableStyle=db.Tablestyle;
                ShowHeaders(t);
                t.Position=at;
                t.Layer=ViewAnnotationDrawing.AnnoLayer;
                t.SetSize(plan.Rows.Count,cols);
            }
            else
            {
                if(!t.IsWriteEnabled)t.UpgradeOpen();
                ClearMerges(t);
                if(t.Rows.Count!=plan.Rows.Count||t.Columns.Count!=cols)t.SetSize(plan.Rows.Count,cols);
            }
            double h=2.5*(scale>0?scale:20);
            var textStyle=ViewAnnotationDrawing.TextStyleId(db,tr);
            if(fresh)
            {
                t.SetRowHeight(h*2.6);
                for(int c=0;c<cols;c++)t.Columns[c].Width=plan.ColumnWidths[c]*h;
            }
            ClearMerges(t);
            var mergedSet=new HashSet<int>(plan.MergedRows??new int[0]);
            foreach(int row in mergedSet)
                if(row>=0&&row<plan.Rows.Count)t.MergeCells(CellRange.Create(t,row,0,row,cols-1));
            for(int r=0;r<plan.Rows.Count;r++)
                for(int c=0;c<cols;c++)
                {
                    if(mergedSet.Contains(r)&&c>0)continue;
                    t.Cells[r,c].TextString=c<plan.Rows[r].Length?(plan.Rows[r][c]??""):"";
                    if(fresh){t.Cells[r,c].TextHeight=h;t.Cells[r,c].TextStyleId=textStyle;}
                }
            if(fresh)
            {
                t.Rows[0].Height=h*3.2;
                t.Cells[0,0].TextHeight=h*1.2;
                space.AppendEntity(t);tr.AddNewlyCreatedDBObject(t,true);
            }
            t.GenerateLayout();
            return t;
        }
        internal static int UpdateMarkTables(Database db,Transaction tr,AssemblyMark mark)
        {
            int updated=0;
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            foreach(ObjectId id in space)
            {
                if(id.IsErased)continue;
                var t=tr.GetObject(id,OpenMode.ForRead) as Table;
                var tag=ReadTag(t,tr);
                if(tag==null||tag.MarkId!=mark.Id)continue;
                if(tag.Kind=="spec")
                {
                    RenderTable(db,tr,space,t,t.Position,tag.AnnotationScale,AssemblyReporting.BuildMarkSpec(mark));
                    updated++;
                }
                else if(tag.Kind=="vrs")
                {
                    RenderTable(db,tr,space,t,t.Position,tag.AnnotationScale,AssemblyReporting.BuildMarkVrs(mark));
                    updated++;
                }
            }
            return updated;
        }
        internal static void PlaceProjectVrs(Database db,Transaction tr,AssemblyProject project,string scope,Point3d at,double scale)
        {
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            var plan=AssemblyReporting.BuildProjectVrs(project,scope);
            var t=RenderTable(db,tr,space,null,at,scale,plan);
            WriteTag(t,tr,new TableTag{ProjectId=project.Id,Kind="project_vrs",Scope=scope,AnnotationScale=scale});
        }
        internal static int UpdateProjectTables(Database db,Transaction tr,AssemblyProject project)
        {
            int updated=0;
            var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
            foreach(ObjectId id in space)
            {
                if(id.IsErased)continue;
                var t=tr.GetObject(id,OpenMode.ForRead) as Table;
                var tag=ReadTag(t,tr);
                if(tag==null||tag.ProjectId!=project.Id||tag.Kind!="project_vrs")continue;
                RenderTable(db,tr,space,t,t.Position,tag.AnnotationScale,AssemblyReporting.BuildProjectVrs(project,tag.Scope));
                updated++;
            }
            return updated;
        }
        static void PlaceMarkTable(string cmd,string kind)
        {
            Diagnostics.Begin(cmd);
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                if(!doc.Database.TileMode||doc.Database.Insunits!=UnitsValue.Millimeters)
                    throw new InvalidOperationException("Откройте пространство модели (TILEMODE=1), единицы DWG — миллиметры (INSUNITS=4).");
                var ed=doc.Editor;
                var scaleOpt=new PromptDoubleOptions("\nМасштаб оформления таблицы 1 <20>: "){AllowNone=true,AllowNegative=false,AllowZero=false,DefaultValue=20,UseDefaultValue=true};
                var scaleRes=ed.GetDouble(scaleOpt);
                if(scaleRes.Status!=PromptStatus.OK&&scaleRes.Status!=PromptStatus.None)return;
                double scale=scaleRes.Status==PromptStatus.None?20:scaleRes.Value;
                var pt=ed.GetPoint("\nТочка вставки таблицы: ");
                if(pt.Status!=PromptStatus.OK)return;
                var at=pt.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)throw new InvalidOperationException("Создайте марку командой NANEW.");
                    AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    var space=AssemblyDrawing.ModelSpace(doc.Database,tr,OpenMode.ForWrite);
                    var plan=kind=="spec"?AssemblyReporting.BuildMarkSpec(m):AssemblyReporting.BuildMarkVrs(m);
                    var t=RenderTable(doc.Database,tr,space,null,new Point3d(at.X,at.Y,0),scale,plan);
                    WriteTag(t,tr,new TableTag{MarkId=m.Id,Kind=kind,AnnotationScale=scale});
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                ed.Regen();
                ed.WriteMessage("\nТаблица "+(kind=="spec"?"спецификации":"ВРС")+" вставлена.");
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nТаблица: "+ex.Message+"\nЖурнал: "+log);
            }
        }
        [CommandMethod("NASPEC",CommandFlags.Modal)]
        public void InsertSpec()=>PlaceMarkTable("NASPEC","spec");
        [CommandMethod("NAVRS",CommandFlags.Modal)]
        public void InsertVrs()=>PlaceMarkTable("NAVRS","vrs");
        [CommandMethod("NAUPDATETABLES",CommandFlags.Modal)]
        public void UpdateTables()
        {
            Diagnostics.Begin("NAUPDATETABLES");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                int count;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)throw new InvalidOperationException("В текущем DWG нет марки.");
                    AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    count=UpdateMarkTables(doc.Database,tr,m);
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                doc.Editor.Regen();
                doc.Editor.WriteMessage("\nОбновлено таблиц марки: "+count);
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nТаблицы: "+ex.Message+"\nЖурнал: "+log);
            }
        }
        [CommandMethod("NAEXPORTCSV",CommandFlags.Modal)]
        public void ExportCsv()
        {
            Diagnostics.Begin("NAEXPORTCSV");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                AssemblyMark m;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)throw new InvalidOperationException("В текущем DWG нет марки.");
                    AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    tr.Commit();
                }
                using(var dlg=new System.Windows.Forms.SaveFileDialog{Filter="CSV (*.csv)|*.csv",FileName=m.Code+"-spec-vrs.csv",OverwritePrompt=true})
                {
                    if(dlg.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                    var content=AssemblyReporting.ToCsv(AssemblyReporting.BuildMarkSpec(m))+"\r\n"+AssemblyReporting.ToCsv(AssemblyReporting.BuildMarkVrs(m));
                    File.WriteAllText(dlg.FileName,content,new UTF8Encoding(true));
                    doc.Editor.WriteMessage("\nСпецификация и ВРС выгружены: "+dlg.FileName);
                }
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nCSV: "+ex.Message+"\nЖурнал: "+log);
            }
        }
    }
}
