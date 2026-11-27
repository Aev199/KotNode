using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using KotNode.Core;
using ModelJson=KotNode.Core.Json;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
using Font=System.Drawing.Font;
using OpenFileDialog=System.Windows.Forms.OpenFileDialog;
using SaveFileDialog=System.Windows.Forms.SaveFileDialog;
[assembly: CommandClass(typeof(KotNode.AutoCAD.AssemblyProjectCommands))]

namespace KotNode.AutoCAD
{
    // Stage 7: Schema-2 Project Manager (supports both universal AssemblyMarks and legacy NodeMarks).
    public sealed class AssemblyProjectPanel : UserControl
    {
        internal AssemblyProjectStore Store;
        internal AssemblyProject Project;
        readonly Label title=new Label{AutoSize=true,Font=new Font("Segoe UI",9.5f,FontStyle.Bold),Text="Откройте, создайте или мигрируйте проект (Schema 2)"};
        readonly Label summary=new Label{AutoSize=true,ForeColor=SystemColors.ControlDarkDark};
        readonly DataGridView marksGrid=Grid(),partsGrid=Grid(),usesGrid=Grid(),vrsGrid=Grid();
        readonly ComboBox scopeBox=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=200};
        readonly Label status=new Label{AutoSize=true,MaximumSize=new Size(460,0)};

        static DataGridView Grid()=>new DataGridView
        {
            Dock=DockStyle.Fill,ReadOnly=true,AutoGenerateColumns=true,
            SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,
            AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.AllCells,
            RowHeadersVisible=false,BackgroundColor=Color.White
        };

        public AssemblyProjectPanel()
        {
            AutoScaleMode=AutoScaleMode.Dpi;
            Font=new Font("Segoe UI",9f);
            Dock=DockStyle.Fill;MinimumSize=new Size(480,480);Padding=new Padding(6);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5};
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var fileBar=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
            Btn(fileBar,"Создать",NewProject);
            Btn(fileBar,"Открыть",OpenProject);
            Btn(fileBar,"Мигрировать 0.2",MigrateLegacy);
            Btn(fileBar,"Обновить",Reload);
            layout.Controls.Add(fileBar,0,0);
            layout.Controls.Add(title,0,1);

            var tabs=new TabControl{Dock=DockStyle.Fill};
            Tab(tabs,"Марки",marksGrid,bar=>{
                Btn(bar,"Загрузить марку из DWG",()=>Queue("NAPROJIMPORT"));
                Btn(bar,"Удалить марку",RemoveMark);
            });
            Tab(tabs,"Состав марки",partsGrid,bar=>{
                Btn(bar,"Спецификация DWG (NASPEC)",()=>Queue("NASPEC"));
                Btn(bar,"ВРС марки DWG (NAVRS)",()=>Queue("NAVRS"));
            });
            marksGrid.SelectionChanged+=(s,e)=>RefreshParts();
            Tab(tabs,"Применения",usesGrid,bar=>{
                Btn(bar,"Добавить",NewUse);
                Btn(bar,"Изменить",EditUse);
                Btn(bar,"Удалить",RemoveUse);
            });
            Tab(tabs,"Сводная ВРС",vrsGrid,bar=>{
                bar.Controls.Add(scopeBox);
                Btn(bar,"Таблица ВРС в DWG",()=>{Need();Queue("NAPROJVRS");});
                Btn(bar,"Обновить таблицы",()=>{Need();Queue("NAPROJUPDATE");});
                Btn(bar,"CSV",ExportCsv);
            });
            scopeBox.SelectedIndexChanged+=(s,e)=>RefreshVrs();
            layout.Controls.Add(tabs,0,2);
            layout.Controls.Add(summary,0,3);
            layout.Controls.Add(status,0,4);
            Controls.Add(layout);
        }

        void Tab(TabControl tabs,string name,DataGridView grid,Action<FlowLayoutPanel> buttons)
        {
            var tab=new TabPage(name){Padding=new Padding(3)};
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var bar=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
            buttons(bar);
            panel.Controls.Add(bar,0,0);panel.Controls.Add(grid,0,1);
            tab.Controls.Add(panel);tabs.TabPages.Add(tab);
        }
        void Btn(Control parent,string text,Action click)
        {
            var b=new PaletteButton{Text=text,AutoSize=true,Height=26,FlatStyle=FlatStyle.Standard};
            b.Click+=(s,e)=>Guard(click);
            parent.Controls.Add(b);
        }
        internal void Guard(Action action)
        {
            try{action();}
            catch(System.Exception ex){MessageBox.Show(ex.Message,"Проект KotNode (Schema 2)",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
        internal void Need(){if(Project==null||Store==null)throw new InvalidOperationException("Сначала откройте или создайте проект Schema 2.");}
        internal string CurrentScope()=>scopeBox.SelectedIndex<=0?null:(string)scopeBox.SelectedItem;
        internal void Commit(AssemblyProject p){Project=Store.Save(p);RefreshAll();}

        void NewProject()
        {
            using(var f=new Fields("Новый проект (Schema 2)"))
            {
                f.TextField("Шифр","code","ПР-001");
                f.TextField("Название","name","Ограждение котлована");
                f.ValidateFields=()=>{if(string.IsNullOrWhiteSpace(f.Get("code"))||string.IsNullOrWhiteSpace(f.Get("name")))throw new InvalidOperationException("Заполните шифр и название.");};
                if(f.ShowDialog()!=DialogResult.OK)return;
                using(var dlg=new SaveFileDialog{Filter="Проект KotNode v2 (*.knproject2.json)|*.knproject2.json",FileName="project.knproject2.json",OverwritePrompt=true})
                {
                    if(dlg.ShowDialog()!=DialogResult.OK)return;
                    if(File.Exists(dlg.FileName))throw new IOException("Выберите новое имя файла проекта.");
                    var store=new AssemblyProjectStore(dlg.FileName);
                    var p=store.Save(new AssemblyProject{Code=f.Get("code").Trim(),Name=f.Get("name").Trim()});
                    Store=store;Project=p;RefreshAll();
                }
            }
        }
        void OpenProject()
        {
            using(var dlg=new OpenFileDialog{Filter="Проект KotNode v2 (*.knproject2.json)|*.knproject2.json|JSON (*.json)|*.json"})
            {
                if(dlg.ShowDialog()!=DialogResult.OK)return;
                var store=new AssemblyProjectStore(dlg.FileName);
                var p=store.Load();
                Store=store;Project=p;RefreshAll();
            }
        }
        void MigrateLegacy()
        {
            using(var open=new OpenFileDialog{Title="Выберите старый проект 0.2 (*.knproject.json)",Filter="Проект 0.2 (*.knproject.json)|*.knproject.json|JSON (*.json)|*.json"})
            {
                if(open.ShowDialog()!=DialogResult.OK)return;
                var legacy=new ProjectStore(open.FileName).Load();
                var migrated=AssemblyProject.FromLegacy(legacy);
                using(var save=new SaveFileDialog{Title="Сохранить новый проект Schema 2",Filter="Проект KotNode v2 (*.knproject2.json)|*.knproject2.json",FileName=Path.GetFileNameWithoutExtension(open.FileName)+".v2.knproject2.json",OverwritePrompt=true})
                {
                    if(save.ShowDialog()!=DialogResult.OK)return;
                    if(string.Equals(Path.GetFullPath(save.FileName),Path.GetFullPath(open.FileName),StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Новый файл Schema 2 должен иметь отдельное имя; исходный файл не перезаписывается.");
                    var store=new AssemblyProjectStore(save.FileName);
                    var p=store.Save(migrated);
                    Store=store;Project=p;RefreshAll();
                    status.Text="Миграция выполнена. Исходный файл 0.2 сохранён без изменений.";
                }
            }
        }
        internal void Reload(){Need();Project=Store.Load();RefreshAll();}

        void RemoveMark()
        {
            Need();
            if(marksGrid.CurrentRow==null)throw new InvalidOperationException("Выберите марку.");
            int idx=marksGrid.CurrentRow.Index;
            var allIds=Project.Assemblies.Select(a=>a.Id).Concat(Project.Marks.Select(m=>m.Id)).ToList();
            if(idx<0||idx>=allIds.Count)return;
            var id=allIds[idx];
            if(Project.Uses.Any(u=>u.MarkId==id))throw new InvalidOperationException("Сначала удалите применения этой марки.");
            if(MessageBox.Show("Удалить выбранную марку из проекта?","Проект KotNode",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            var p=ModelJson.Copy(Project);
            p.Assemblies.RemoveAll(a=>a.Id==id);
            p.Marks.RemoveAll(m=>m.Id==id);
            Commit(p);
        }
        void NewUse()
        {
            Need();
            if(Project.Assemblies.Count==0&&Project.Marks.Count==0)throw new InvalidOperationException("В проекте нет марок. Загрузите марку из DWG.");
            var firstId=Project.Assemblies.Count>0?Project.Assemblies[0].Id:Project.Marks[0].Id;
            var u=new Use{MarkId=firstId};
            if(!UseForm(u))return;
            var p=ModelJson.Copy(Project);p.Uses.Add(u);Commit(p);
        }
        void EditUse()
        {
            Need();
            if(usesGrid.CurrentRow==null)throw new InvalidOperationException("Выберите применение.");
            var u=ModelJson.Copy(Project.Uses[usesGrid.CurrentRow.Index]);
            if(!UseForm(u))return;
            var p=ModelJson.Copy(Project);
            p.Uses[p.Uses.FindIndex(x=>x.Id==u.Id)]=u;
            Commit(p);
        }
        void RemoveUse()
        {
            Need();
            if(usesGrid.CurrentRow==null)throw new InvalidOperationException("Выберите применение.");
            var u=Project.Uses[usesGrid.CurrentRow.Index];
            if(MessageBox.Show("Удалить применение «"+u.Location+"» ("+u.Count+" шт.)?","Проект",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            var p=ModelJson.Copy(Project);p.Uses.RemoveAll(x=>x.Id==u.Id);Commit(p);
        }
        bool UseForm(Use u)
        {
            using(var f=new Fields("Применение марки узла"))
            {
                var choices=Project.Assemblies.ToDictionary(a=>a.Id.ToString(),a=>a.Code+" (универс.)");
                foreach(var m in Project.Marks)choices[m.Id.ToString()]=m.Code+" (0.2)";
                f.Choice("Марка","mark",choices,u.MarkId.ToString());
                f.TextField("Ярус / участок","location",u.Location);
                f.Number("Количество узлов","count",u.Count);
                f.Check("Включить в ведомость","included",u.Included);
                f.ValidateFields=()=>{
                    double n=f.Value("count");
                    if(n!=Math.Truncate(n)||n<1||n>1000000)throw new InvalidOperationException("Количество: целое от 1 до 1000000.");
                    if(string.IsNullOrWhiteSpace(f.Get("location")))throw new InvalidOperationException("Укажите ярус/участок.");
                    u.MarkId=Guid.Parse(f.Get("mark"));u.Location=f.Get("location").Trim();u.Count=(int)n;u.Included=f.Checked("included");
                };
                return f.ShowDialog()==DialogResult.OK;
            }
        }
        void ExportCsv()
        {
            Need();
            using(var dlg=new SaveFileDialog{Filter="CSV (*.csv)|*.csv",FileName=Project.Code+"-vrs.csv",OverwritePrompt=true})
            {
                if(dlg.ShowDialog()!=DialogResult.OK)return;
                var plan=AssemblyReporting.BuildProjectVrs(Project,CurrentScope());
                File.WriteAllText(dlg.FileName,AssemblyReporting.ToCsv(plan),new UTF8Encoding(true));
                status.Text="Сводная ВРС выгружена в CSV: "+dlg.FileName;
            }
        }
        void RefreshParts()
        {
            if(Project==null||marksGrid.CurrentRow==null){partsGrid.DataSource=null;return;}
            int idx=marksGrid.CurrentRow.Index;
            if(idx<Project.Assemblies.Count)
            {
                var a=Project.Assemblies[idx];
                partsGrid.DataSource=a.Specification().Select(r=>new
                {
                    Позиция=r.Position,
                    Деталь=r.Name,
                    Профиль=AssemblyReporting.Designation(a.Parts.Single(p=>p.Id==r.PartId)),
                    Сталь=r.Material,
                    Кол=r.Count,
                    Масса_ед_кг=Math.Round(r.UnitMassKg,3),
                    Масса_кг=Math.Round(r.TotalMassKg,2)
                }).ToList();
            }
            else
            {
                int mIdx=idx-Project.Assemblies.Count;
                if(mIdx<0||mIdx>=Project.Marks.Count)return;
                var m=Project.Marks[mIdx];var g=Splice.Calculate(m.Parameters);
                partsGrid.DataSource=new[]{new{Позиция=m.DetailCode,Деталь="Накладка",Профиль="Ø"+m.Parameters.DonorDiameter+"×"+m.Parameters.PlateThickness,Сталь=m.Parameters.Material,Кол=m.Parameters.PlateCount,Масса_ед_кг=Math.Round(g.PlateMass,3),Масса_кг=Math.Round(g.PlateMass*m.Parameters.PlateCount,2)}};
            }
        }
        void RefreshVrs()
        {
            if(Project==null){vrsGrid.DataSource=null;return;}
            var rows=AssemblyReporting.SummarizeProjectVrs(Project,CurrentScope());
            vrsGrid.DataSource=rows.Select(r=>new{Профиль=r.Profile,Сталь=r.Material,Деталей=r.DetailCount,Масса_кг=Math.Round(r.MassKg,2),Масса_т=Math.Round(r.MassKg/1000.0,3)}).ToList();
            double total=rows.Sum(r=>r.MassKg);
            summary.Text="Деталей в ВРС: "+rows.Sum(r=>r.DetailCount)+" шт. • Масса: "+total.ToString("0.00")+" кг ("+(total/1000.0).ToString("0.000")+" т)";
        }
        internal void RefreshAll()
        {
            title.Text=Project.Code+" — "+Project.Name+" | v"+Project.Version+" (Schema 2)";
            var markRows=Project.Assemblies.Select(a=>new
            {
                Марка=a.Code,Наименование=a.Name,Тип="Универсальная",
                Позиций=a.Specification().Count,
                Масса_узла_кг=Math.Round(a.Specification().Sum(r=>r.TotalMassKg),2)
            }).Concat(Project.Marks.Select(m=>new
            {
                Марка=m.Code,Наименование=m.Name,Тип="Стык 0.2",
                Позиций=1,
                Масса_узла_кг=Math.Round(Splice.Calculate(m.Parameters).PlateMass*m.Parameters.PlateCount,2)
            })).ToList();
            marksGrid.DataSource=markRows;
            var codeById=Project.Assemblies.ToDictionary(a=>a.Id,a=>a.Code);
            foreach(var m in Project.Marks)codeById[m.Id]=m.Code;
            usesGrid.DataSource=Project.Uses.Select(u=>new{Марка=codeById.TryGetValue(u.MarkId,out var c)?c:"?",Участок=u.Location,Узлов=u.Count,В_ВРС=u.Included}).ToList();
            scopeBox.Items.Clear();scopeBox.Items.Add("Весь проект");
            foreach(var loc in Project.Uses.Select(u=>u.Location).Distinct())scopeBox.Items.Add(loc);
            scopeBox.SelectedIndex=0;
            status.Text="Проект загружен: "+Store.Path;
            RefreshParts();RefreshVrs();
        }
        static void Queue(string cmd)=>PaletteCommands.Queue(cmd);
    }

    public sealed class AssemblyProjectCommands
    {
        static PaletteSet palette;
        static AssemblyProjectPanel panel;

        [CommandMethod("NPROJECT2",CommandFlags.Modal)]
        public void OpenProject2()
        {
            if(palette==null)
            {
                panel=new AssemblyProjectPanel();
                palette=new PaletteSet("KotNode — Проект (Schema 2)",new Guid("c4e12f9b-83a1-4b22-9f4e-77c3b20f1e02")){Size=new Size(560,600),MinimumSize=new Size(480,460),KeepFocus=false};
                palette.Add("Проект 2.0",panel);
            }
            palette.Visible=true;
        }
        [CommandMethod("NAPROJIMPORT",CommandFlags.Modal)]
        public void ImportFromDwg()
        {
            Diagnostics.Begin("NAPROJIMPORT");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                if(panel==null||panel.Project==null)throw new InvalidOperationException("Откройте проект командой NPROJECT2.");
                AssemblyMark mark;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    mark=AssemblyDrawing.Load(doc.Database,tr);
                    if(mark==null)throw new InvalidOperationException("В текущем DWG нет универсальной марки (создайте через NANEW).");
                    AssemblyDrawing.Synchronize(doc.Database,tr,mark);
                    AssemblyDrawing.Save(doc.Database,tr,mark);
                    tr.Commit();
                }
                var proj=ModelJson.Copy(panel.Project);
                int idx=proj.Assemblies.FindIndex(a=>a.Id==mark.Id||string.Equals(a.Code.Trim(),mark.Code.Trim(),StringComparison.OrdinalIgnoreCase));
                if(idx>=0)
                {
                    // Preserve project-side mark Id if matched by Code so existing Uses remain intact.
                    var keepId=proj.Assemblies[idx].Id;
                    var copy=ModelJson.Copy(mark);copy.Id=keepId;
                    proj.Assemblies[idx]=copy;
                }
                else proj.Assemblies.Add(ModelJson.Copy(mark));
                panel.Commit(proj);
                doc.Editor.WriteMessage("\nМарка «"+mark.Code+"» синхронизирована с проектом "+proj.Code+".");
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nПроект: "+ex.Message+"\nЖурнал: "+log);
            }
        }
        [CommandMethod("NAPROJVRS",CommandFlags.Modal)]
        public void InsertProjectVrs()
        {
            Diagnostics.Begin("NAPROJVRS");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                if(panel==null||panel.Project==null)throw new InvalidOperationException("Откройте проект командой NPROJECT2.");
                var ed=doc.Editor;
                var scaleOpt=new PromptDoubleOptions("\nМасштаб таблицы ВРС 1 <20>: "){AllowNone=true,AllowNegative=false,AllowZero=false,DefaultValue=20,UseDefaultValue=true};
                var scaleRes=ed.GetDouble(scaleOpt);
                if(scaleRes.Status!=PromptStatus.OK&&scaleRes.Status!=PromptStatus.None)return;
                double scale=scaleRes.Status==PromptStatus.None?20:scaleRes.Value;
                var pt=ed.GetPoint("\nТочка вставки сводной ВРС проекта: ");
                if(pt.Status!=PromptStatus.OK)return;
                var at=pt.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    AssemblyTableDrawing.PlaceProjectVrs(doc.Database,tr,panel.Project,panel.CurrentScope(),new Point3d(at.X,at.Y,0),scale);
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                ed.Regen();
                ed.WriteMessage("\nСводная таблица ВРС проекта вставлена.");
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nВРС проекта: "+ex.Message+"\nЖурнал: "+log);
            }
        }
        [CommandMethod("NAPROJUPDATE",CommandFlags.Modal)]
        public void UpdateProjectTables()
        {
            Diagnostics.Begin("NAPROJUPDATE");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                if(panel==null||panel.Project==null)throw new InvalidOperationException("Откройте проект командой NPROJECT2.");
                panel.Reload();
                int count;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    count=AssemblyTableDrawing.UpdateProjectTables(doc.Database,tr,panel.Project);
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                doc.Editor.Regen();
                doc.Editor.WriteMessage("\nОбновлено сводных таблиц ВРС проекта: "+count);
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nВРС проекта: "+ex.Message+"\nЖурнал: "+log);
            }
        }
    }
}
