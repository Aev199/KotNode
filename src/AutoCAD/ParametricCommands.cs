using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
using Font=System.Drawing.Font;
using FlowDirection=System.Windows.Forms.FlowDirection;
using ModelJson=KotNode.Core.Json;
[assembly: CommandClass(typeof(KotNode.AutoCAD.ParametricCommands))]

namespace KotNode.AutoCAD
{
    // Stage 8: Interactive Parameters & Formulas editor + full parametric rebuild (3D + views + tables).
    public sealed class ParametricCommands
    {
        internal static void ApplySolvedMark(Database db,Transaction tr,AssemblyMark mark,Dictionary<Guid,BlockReference> refs,SolveReport report)
        {
            // Remove erased array instances
            foreach(var remId in report.RemovedInstances)
            {
                if(refs.TryGetValue(remId,out var br)&&!br.IsErased)
                {
                    if(!br.IsWriteEnabled)br.UpgradeOpen();
                    br.Erase();
                    refs.Remove(remId);
                }
            }
            // Rebuild geometry definitions for all parts and update placements
            var geomByPart=new Dictionary<Guid,ObjectId>();
            foreach(var part in mark.Parts)
                geomByPart[part.Id]=AssemblyDrawing.Geometry(db,tr,part);
            foreach(var inst in mark.Instances)
            {
                if(refs.TryGetValue(inst.Id,out var br))
                {
                    AssemblyDrawing.Replace(br,geomByPart[inst.PartId]);
                    AssemblyDrawing.Move(br,inst.Placement);
                    br.RecordGraphicsModified(true);
                }
                else
                {
                    refs[inst.Id]=AssemblyDrawing.Insert(db,tr,mark,inst,geomByPart[inst.PartId]);
                }
            }
            AssemblyViews.Update(db,tr,mark,refs);
            AssemblyTableDrawing.UpdateMarkTables(db,tr,mark);
            AssemblyDrawing.Save(db,tr,mark);
        }

        [CommandMethod("NASOLVE",CommandFlags.Modal)]
        public void SolveNow()
        {
            Diagnostics.Begin("NASOLVE");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                SolveReport report;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)throw new InvalidOperationException("В текущем DWG нет марки.");
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    report=ParametricEngine.Solve(m);
                    ApplySolvedMark(doc.Database,tr,m,refs,report);
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                doc.Editor.Regen();
                AssemblyCommands.RefreshPalette(doc);
                doc.Editor.WriteMessage("\nПараметры пересчитаны: деталей обновлено "+report.UpdatedParts+", положений "+report.UpdatedInstances+", добавлено "+report.AddedInstances.Count+", удалено "+report.RemovedInstances.Count+".");
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nПараметры: "+ex.Message+"\nЖурнал: "+log);
            }
        }

        [CommandMethod("NAPARAMS",CommandFlags.Modal)]
        public void EditParameters()
        {
            Diagnostics.Begin("NAPARAMS");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                AssemblyMark draft;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)throw new InvalidOperationException("Создайте марку командой NANEW.");
                    AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    draft=ModelJson.Copy(m);
                    tr.Commit();
                }
                using(var dlg=new ParametersDialog(draft))
                {
                    if(AcApp.ShowModalDialog(dlg)!=DialogResult.OK)return;
                }
                SolveReport report;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)throw new InvalidOperationException("Марка не найдена.");
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    m.Parameters=draft.Parameters;
                    m.Bindings=draft.Bindings;
                    m.PlacementRules=draft.PlacementRules;
                    report=ParametricEngine.Solve(m);
                    ApplySolvedMark(doc.Database,tr,m,refs,report);
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                doc.Editor.Regen();
                AssemblyCommands.RefreshPalette(doc);
                doc.Editor.WriteMessage("\nПараметры и модель обновлены (деталей: "+report.UpdatedParts+", массивов/привязок: "+report.UpdatedInstances+").");
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nПараметры: "+ex.Message+"\nЖурнал: "+log);
            }
        }
    }

    internal sealed class ParametersDialog : Form
    {
        readonly AssemblyMark mark;
        readonly DataGridView paramsGrid=MakeGrid();
        readonly DataGridView bindingsGrid=MakeGrid();

        static DataGridView MakeGrid()=>new DataGridView
        {
            Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,BackgroundColor=Color.White
        };

        public ParametersDialog(AssemblyMark mark)
        {
            this.mark=mark;
            if(mark.Parameters==null)mark.Parameters=new List<MarkParameter>();
            if(mark.Bindings==null)mark.Bindings=new List<PartBinding>();
            Text="Параметры и формулы семейства — "+mark.Code;
            Font=new Font("Segoe UI",9f);
            Width=720;Height=540;StartPosition=FormStartPosition.CenterScreen;

            var tabs=new TabControl{Dock=DockStyle.Fill};
            tabs.TabPages.Add(BuildParamsTab());
            tabs.TabPages.Add(BuildBindingsTab());

            var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(6)};
            var ok=new Button{Text="Применить и перестроить",AutoSize=true,Height=28,FlatStyle=FlatStyle.System};
            var cancel=new Button{Text="Отмена",DialogResult=DialogResult.Cancel,AutoSize=true,Height=28,FlatStyle=FlatStyle.System};
            ok.Click+=(s,e)=>{
                try
                {
                    ReadParamsFromGrid();
                    ParametricEngine.Solve(ModelJson.Copy(mark));
                    DialogResult=DialogResult.OK;Close();
                }
                catch(System.Exception ex){MessageBox.Show(ex.Message,"Ошибка формул",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            };
            footer.Controls.Add(ok);footer.Controls.Add(cancel);
            Controls.Add(tabs);Controls.Add(footer);
            AcceptButton=ok;CancelButton=cancel;
            LoadGrids();
        }

        TabPage BuildParamsTab()
        {
            var tab=new TabPage("Параметры узла"){Padding=new Padding(4)};
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};
            var add=new Button{Text="+ Параметр",AutoSize=true,Height=26,FlatStyle=FlatStyle.System};
            var del=new Button{Text="Удалить",AutoSize=true,Height=26,FlatStyle=FlatStyle.System};
            add.Click+=(s,e)=>{ReadParamsFromGrid();mark.Parameters.Add(new MarkParameter{Name="P"+(mark.Parameters.Count+1),Label="Параметр",Value=100});LoadGrids();};
            del.Click+=(s,e)=>{if(paramsGrid.CurrentRow!=null){ReadParamsFromGrid();mark.Parameters.RemoveAt(paramsGrid.CurrentRow.Index);LoadGrids();}};
            bar.Controls.Add(add);bar.Controls.Add(del);
            paramsGrid.Columns.Add("Name","Имя");
            paramsGrid.Columns.Add("Label","Описание");
            paramsGrid.Columns.Add("Value","Значение");
            paramsGrid.Columns.Add("Formula","Формула (необяз.)");
            paramsGrid.Columns.Add("Unit","Ед.");
            tab.Controls.Add(paramsGrid);tab.Controls.Add(bar);
            return tab;
        }

        TabPage BuildBindingsTab()
        {
            var tab=new TabPage("Привязка размеров деталей"){Padding=new Padding(4)};
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};
            var add=new Button{Text="+ Привязка размера",AutoSize=true,Height=26,FlatStyle=FlatStyle.System};
            var del=new Button{Text="Удалить",AutoSize=true,Height=26,FlatStyle=FlatStyle.System};
            add.Click+=(s,e)=>AddBinding();
            del.Click+=(s,e)=>{if(bindingsGrid.CurrentRow!=null){mark.Bindings.RemoveAt(bindingsGrid.CurrentRow.Index);LoadGrids();}};
            bar.Controls.Add(add);bar.Controls.Add(del);
            bindingsGrid.ReadOnly=true;
            bindingsGrid.Columns.Add("Part","Деталь");
            bindingsGrid.Columns.Add("Prop","Свойство");
            bindingsGrid.Columns.Add("Formula","Формула");
            tab.Controls.Add(bindingsGrid);tab.Controls.Add(bar);
            return tab;
        }

        void AddBinding()
        {
            if(mark.Parts.Count==0){MessageBox.Show("В марке нет деталей.");return;}
            using(var f=new Fields("Новая привязка размера детали"))
            {
                var partMap=mark.Parts.ToDictionary(p=>p.Id.ToString(),p=>(string.IsNullOrEmpty(p.Position)?"":"Поз."+p.Position+" ")+p.Name+" ("+p.Kind+")");
                f.Choice("Деталь","part",partMap,mark.Parts[0].Id.ToString());
                var props=new Dictionary<string,string>
                {
                    {"Pipe.OuterDiameter","Труба: диаметр D"},{"Pipe.Thickness","Труба: толщина t"},{"Pipe.Length","Труба: длина L"},
                    {"Plate.Length","Лист: длина L"},{"Plate.Width","Лист: ширина W"},{"Plate.Thickness","Лист: толщина t"},
                    {"PolygonPlate.Thickness","Контурный лист: толщина t"},
                    {"CurvedPlate.InnerRadius","Кривой лист: радиус Rвн"},{"CurvedPlate.Thickness","Кривой лист: толщина t"},
                    {"CurvedPlate.Length","Кривой лист: длина L"},{"CurvedPlate.AngleDeg","Кривой лист: угол, град"},
                    {"IBeam.Length","Двутавр: длина L"},{"Rebar.Diameter","Арматура: диаметр d"}
                };
                f.Choice("Свойство","prop",props,"Plate.Length");
                f.TextField("Формула","formula","L");
                f.ValidateFields=()=>{if(string.IsNullOrWhiteSpace(f.Get("formula")))throw new InvalidOperationException("Введите формулу.");};
                if(f.ShowDialog()!=DialogResult.OK)return;
                mark.Bindings.Add(new PartBinding{PartId=Guid.Parse(f.Get("part")),Property=f.Get("prop"),Formula=f.Get("formula").Trim()});
                LoadGrids();
            }
        }

        void LoadGrids()
        {
            paramsGrid.Rows.Clear();
            foreach(var p in mark.Parameters)
                paramsGrid.Rows.Add(p.Name,p.Label,p.Value.ToString("0.###",CultureInfo.InvariantCulture),p.Formula??"",p.Unit??"мм");
            bindingsGrid.Rows.Clear();
            foreach(var b in mark.Bindings)
            {
                var part=mark.Parts.FirstOrDefault(p=>p.Id==b.PartId);
                bindingsGrid.Rows.Add(part==null?"?":part.Name,b.Property,b.Formula);
            }
        }

        void ReadParamsFromGrid()
        {
            var next=new List<MarkParameter>();
            foreach(DataGridViewRow row in paramsGrid.Rows)
            {
                string name=Convert.ToString(row.Cells[0].Value)?.Trim()??"";
                string label=Convert.ToString(row.Cells[1].Value)?.Trim()??"";
                string valStr=(Convert.ToString(row.Cells[2].Value)??"0").Replace(',','.');
                string formula=Convert.ToString(row.Cells[3].Value)?.Trim()??"";
                string unit=Convert.ToString(row.Cells[4].Value)?.Trim()??"мм";
                double.TryParse(valStr,NumberStyles.Float,CultureInfo.InvariantCulture,out var val);
                next.Add(new MarkParameter{Name=name,Label=label,Value=val,Formula=formula,Unit=unit});
            }
            mark.Parameters=next;
        }
    }
}
