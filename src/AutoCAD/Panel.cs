using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using KotNode.Core;
using ModelJson=KotNode.Core.Json;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;

namespace KotNode.AutoCAD
{
    public sealed class ProjectPanel : UserControl
    {
        internal ProjectStore Store;
        internal Project Project;
        readonly Label title=new Label{AutoSize=true,Text="Откройте или создайте проект"};
        readonly Label summary=new Label{AutoSize=true};
        readonly DataGridView marks=Grid(),uses=Grid(),steel=Grid(),parts=Grid();
        readonly ComboBox scope=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=260};
        readonly Label status=new Label{AutoSize=true,MaximumSize=new Size(520,0)};
        static DataGridView Grid(){return new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AutoGenerateColumns=true,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.AllCells,RowHeadersVisible=false,BackgroundColor=Color.White};}
        public ProjectPanel()
        {
            Dock=DockStyle.Fill;MinimumSize=new Size(440,450);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(6)};
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var filebar=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};
            Button(filebar,"Создать проект",NewProject);Button(filebar,"Открыть",Open);Button(filebar,"Обновить",Reload);
            layout.Controls.Add(filebar,0,0);layout.Controls.Add(title,0,1);
            var tabs=new TabControl{Dock=DockStyle.Fill};
            Tab(tabs,"Марки узлов",marks,bar=>{Button(bar,"Новая марка",NewMark);Button(bar,"Изменить",EditMark);Button(bar,"Вставить виды",()=>Queue("NPPLACE "+SelectedMark().Id.ToString("D")));});
            Tab(tabs,"Состав узла",parts,bar=>{Button(bar,"Спецификация марки",()=>Queue("NPSPEC "+SelectedMark().Id.ToString("D")));});
            marks.SelectionChanged+=(s,e)=>RefreshParts();
            Tab(tabs,"Применения",uses,bar=>{Button(bar,"Добавить",NewUse);Button(bar,"Изменить",EditUse);Button(bar,"Удалить",RemoveUse);});
            Tab(tabs,"Расход стали",steel,bar=>{bar.Controls.Add(scope);Button(bar,"Выгрузить CSV",Export);Button(bar,"Таблица всего проекта",()=>{Need();Queue("NPTABLE");});});
            scope.SelectedIndexChanged+=(s,e)=>RefreshSteel();
            layout.Controls.Add(tabs,0,2);layout.Controls.Add(summary,0,3);
            var bottom=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};Button(bottom,"Обновить виды DWG",()=>{Need();Queue("NPUPDATE");});Button(bottom,"Проверить DWG",()=>{Need();Queue("NPSTATUS");});bottom.Controls.Add(status);layout.Controls.Add(bottom,0,4);Controls.Add(layout);
        }
        void Tab(TabControl tabs,string name,DataGridView grid,Action<FlowLayoutPanel> buttons)
        {var tab=new TabPage(name);var panel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));var bar=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};buttons(bar);panel.Controls.Add(bar,0,0);panel.Controls.Add(grid,0,1);tab.Controls.Add(panel);tabs.TabPages.Add(tab);}
        void Button(Control parent,string text,Action click)
        {var b=new Button{Text=text,AutoSize=true};b.Click+=(s,e)=>Guard(click);parent.Controls.Add(b);}
        internal void Guard(Action action)
        {try{action();}catch(Exception e){MessageBox.Show(e.Message,"Узлы",MessageBoxButtons.OK,MessageBoxIcon.Warning);}}
        void Need(){if(Project==null)throw new InvalidOperationException("Сначала откройте проект.");}
        NodeMark SelectedMark()
        {Need();if(marks.CurrentRow==null)throw new InvalidOperationException("Выберите марку на вкладке «Марки узлов».");return Project.Marks[marks.CurrentRow.Index];}
        Use SelectedUse()
        {Need();if(uses.CurrentRow==null)throw new InvalidOperationException("Выберите применение.");return Project.Uses[uses.CurrentRow.Index];}
        void Commit(Project p){Project=Store.Save(p);RefreshAll();}
        void NewProject()
        {
            using(var fields=new Fields("Новый проект"))
            {
                fields.TextField("Шифр","code","ПР-001");fields.TextField("Название","name","Ограждение котлована");
                fields.ValidateFields=()=>{if(string.IsNullOrWhiteSpace(fields.Get("code"))||string.IsNullOrWhiteSpace(fields.Get("name")))throw new InvalidOperationException("Заполните шифр и название.");};
                if(fields.ShowDialog()!=DialogResult.OK)return;
                using(var dlg=new SaveFileDialog{Filter="Проект узлов (*.knproject.json)|*.knproject.json",FileName="project.knproject.json",OverwritePrompt=true})
                {
                    if(dlg.ShowDialog()!=DialogResult.OK)return;
                    if(File.Exists(dlg.FileName))throw new IOException("Для нового проекта выберите новое имя. Существующий проект можно открыть.");
                    var store=new ProjectStore(dlg.FileName);var p=store.Save(new Project{Code=fields.Get("code"),Name=fields.Get("name")});Store=store;Project=p;RefreshAll();
                }
            }
        }
        void Open(){using(var dlg=new OpenFileDialog{Filter="Проект узлов (*.knproject.json)|*.knproject.json|JSON (*.json)|*.json"})if(dlg.ShowDialog()==DialogResult.OK){var store=new ProjectStore(dlg.FileName);var p=store.Load();Store=store;Project=p;RefreshAll();}}
        void Reload(){ReloadForCommand();}
        internal void ReloadForCommand(){Need();Project=Store.Load();RefreshAll();}
        void NewMark(){Need();var m=new NodeMark{Code="У-"+(Project.Marks.Count+1).ToString("00")};if(!MarkForm(m))return;var p=ModelJson.Copy(Project);p.Marks.Add(m);Commit(p);}
        void EditMark(){var old=SelectedMark();var m=ModelJson.Copy(old);if(!MarkForm(m))return;if(ModelJson.Write(m)==ModelJson.Write(old))return;m.Revision=checked(m.Revision+1);var p=ModelJson.Copy(Project);p.Marks[p.Marks.FindIndex(x=>x.Id==m.Id)]=m;Commit(p);}
        bool MarkForm(NodeMark m)
        {
            using(var f=new Fields("Марка узла — труба–труба"))
            {
                f.TextField("Марка","code",m.Code);f.TextField("Наименование","name",m.Name);var x=m.Parameters;
                f.Number("Диаметр трубы, мм","d",x.PipeDiameter);f.Number("Толщина трубы, мм","t",x.PipeThickness);f.Number("Длина накладки, мм","l",x.PlateLength);f.Number("Толщина накладки, мм","s",x.PlateThickness);f.Number("Диаметр трубы-заготовки, мм","db",x.DonorDiameter);f.Number("Число накладок (1–12)","n",x.PlateCount);f.Number("Между накладками в свету, мм","pitch",x.ClearSpacing);f.Number("Зазор торцов без стыкового шва, мм","gap",x.EndGap);f.Number("Катет шва, мм","k",x.WeldLeg);f.TextField("Марка стали накладок","steel",x.Material);f.Check("Есть стыковой шов","butt",x.ButtWeld);f.Check("Торцевые швы накладок","fronts",x.FrontWelds??true);f.Number("Угол при одной накладке, град","angle",x.SinglePlateAngleDegrees??84);f.TextField("Позиция накладки","position",m.DetailCode);f.TextField("Номер сечения","section",m.SectionLabel??"1");
                Action modes=()=>{double count;double.TryParse(f.Get("n"),out count);f.EnabledField("angle",count==1);f.EnabledField("pitch",count!=1);f.EnabledField("gap",!f.Checked("butt"));};f.Changed("n",modes);f.Changed("butt",modes);modes();
                f.ValidateFields=()=>
                {
                    double n=f.Value("n");if(n!=Math.Truncate(n))throw new InvalidOperationException("Количество накладок должно быть целым.");
                    var candidate=new SpliceParameters{PipeDiameter=f.Value("d"),PipeThickness=f.Value("t"),PlateLength=f.Value("l"),PlateThickness=f.Value("s"),DonorDiameter=f.Value("db"),PlateCount=checked((int)n),ClearSpacing=f.Value("pitch"),EndGap=f.Value("gap"),WeldLeg=f.Value("k"),Material=f.Get("steel"),ButtWeld=f.Checked("butt"),FrontWelds=f.Checked("fronts"),SinglePlateAngleDegrees=f.Value("angle")};
                    Splice.Calculate(candidate);if(string.IsNullOrWhiteSpace(f.Get("code")))throw new InvalidOperationException("Укажите марку.");
                    if(string.IsNullOrWhiteSpace(f.Get("position"))||string.IsNullOrWhiteSpace(f.Get("section")))throw new InvalidOperationException("Укажите позицию и номер сечения.");
                    m.DetailCode=f.Get("position").Trim();m.SectionLabel=f.Get("section").Trim();m.Code=f.Get("code").Trim();m.Name=f.Get("name");m.Parameters=candidate;
                };
                return f.ShowDialog()==DialogResult.OK;
            }
        }
        void NewUse(){Need();if(Project.Marks.Count==0)throw new InvalidOperationException("Создайте марку узла.");var u=new Use{MarkId=SelectedMark().Id};if(!UseForm(u))return;var p=ModelJson.Copy(Project);p.Uses.Add(u);Commit(p);}
        void EditUse(){var u=ModelJson.Copy(SelectedUse());if(!UseForm(u))return;var p=ModelJson.Copy(Project);p.Uses[p.Uses.FindIndex(x=>x.Id==u.Id)]=u;Commit(p);}
        bool UseForm(Use u)
        {
            using(var f=new Fields("Применение узла"))
            {
                var choices=Project.Marks.ToDictionary(m=>m.Id.ToString(),m=>m.Code);f.Choice("Марка","mark",choices,u.MarkId.ToString());f.TextField("Ярус / участок","location",u.Location);f.Number("Количество узлов","count",u.Count);f.Check("Включить в ведомость","included",u.Included);
                f.ValidateFields=()=>{double n=f.Value("count");if(n!=Math.Truncate(n)||n<1||n>1000000)throw new InvalidOperationException("Количество: целое от 1 до 1000000.");if(string.IsNullOrWhiteSpace(f.Get("location")))throw new InvalidOperationException("Укажите место применения.");u.MarkId=Guid.Parse(f.Get("mark"));u.Location=f.Get("location").Trim();u.Count=(int)n;u.Included=f.Checked("included");};return f.ShowDialog()==DialogResult.OK;
            }
        }
        void RemoveUse(){var u=SelectedUse();if(MessageBox.Show("Удалить применение «"+u.Location+"», "+u.Count+" шт.?","Узлы",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;var p=ModelJson.Copy(Project);p.Uses.RemoveAll(x=>x.Id==u.Id);Commit(p);}
        void Export(){Need();using(var dlg=new SaveFileDialog{Filter="CSV (*.csv)|*.csv",FileName="steel.csv"})if(dlg.ShowDialog()==DialogResult.OK){File.WriteAllText(dlg.FileName,Report.Csv(Project,Scope()),new UTF8Encoding(true));status.Text="CSV сохранен";}}
        string Scope(){return scope.SelectedIndex<=0?null:(string)scope.SelectedItem;}
        void RefreshParts()
        {
            if(Project==null||marks.CurrentRow==null||marks.CurrentRow.Index>=Project.Marks.Count)return;
            var m=Project.Marks[marks.CurrentRow.Index];var p=m.Parameters;var g=Splice.Calculate(p);
            parts.DataSource=new[]{new{Позиция=m.DetailCode+" (в пределах "+m.Code+")",Деталь="Накладка из трубы",Диаметр_заготовки=p.DonorDiameter,Толщина=p.PlateThickness,Длина=p.PlateLength,Угол_град=Math.Round(g.Angle*180/Math.PI,3),Сталь=p.Material,Количество=p.PlateCount,Масса_шт_кг=Math.Round(g.PlateMass,3)}};
        }
        void RefreshSteel()
        {
            if(Project==null)return;var rows=Project.Steel(Scope());steel.DataSource=rows.Select(r=>new{Марка=r.Mark,Редакция=r.Revision,Сталь=r.Material,Толщина=r.Thickness,Узлов=r.NodeCount,Накладок=r.DetailCount,Масса_кг=Math.Round(r.TotalMass,2),Источник=r.Sources}).ToList();summary.Text="Накладки: "+rows.Sum(r=>r.DetailCount)+" шт. | Масса: "+rows.Sum(r=>r.TotalMass).ToString("0.00")+" кг";
        }
        void RefreshAll()
        {
            title.Text=Project.Code+" — "+Project.Name+" | версия "+Project.Version;
            marks.DataSource=Project.Marks.Select(m=>new{Марка=m.Code,Наименование=m.Name,Редакция=m.Revision,Накладок=m.Parameters.PlateCount,Масса_узла_кг=Math.Round(Splice.Calculate(m.Parameters).PlateMass*m.Parameters.PlateCount,2)}).ToList();
            uses.DataSource=Project.Uses.Select(u=>new{Марка=Project.Marks.Single(m=>m.Id==u.MarkId).Code,Участок=u.Location,Количество=u.Count,В_ведомости=u.Included}).ToList();
            scope.Items.Clear();scope.Items.Add("Весь проект");foreach(var s in Project.Uses.Select(u=>u.Location).Distinct())scope.Items.Add(s);scope.SelectedIndex=0;status.Text="Проект загружен / сохранен";RefreshSteel();RefreshParts();
        }
        static void Queue(string command){var doc=AcApp.DocumentManager.MdiActiveDocument;if(doc==null)throw new InvalidOperationException("Откройте DWG.");doc.SendStringToExecute(command+"\n",true,false,false);}
        internal void SetStatus(string value){status.Text=value;}
    }
    sealed class Fields : Form
    {
        readonly TableLayoutPanel table=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Top,ColumnCount=2,Padding=new Padding(8)};
        readonly System.Collections.Generic.Dictionary<string,Control> inputs=new System.Collections.Generic.Dictionary<string,Control>();
        public Action ValidateFields;
        public Fields(string title)
        {
            Text=title;Width=610;Height=660;AutoScroll=true;StartPosition=FormStartPosition.CenterScreen;MinimizeBox=false;MaximizeBox=false;
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,FlowDirection=FlowDirection.RightToLeft};var ok=new Button{Text="Применить",AutoSize=true};var cancel=new Button{Text="Отмена",DialogResult=DialogResult.Cancel};ok.Click+=(s,e)=>{try{ValidateFields?.Invoke();DialogResult=DialogResult.OK;Close();}catch(Exception ex){MessageBox.Show(ex.Message,"Параметры");}};buttons.Controls.Add(ok);buttons.Controls.Add(cancel);Controls.Add(table);Controls.Add(buttons);AcceptButton=ok;CancelButton=cancel;
        }
        void Add(string title,string key,Control c){int row=table.RowCount++;table.Controls.Add(new Label{Text=title,AutoSize=true,Margin=new Padding(3,8,8,3)},0,row);c.Width=220;table.Controls.Add(c,1,row);inputs.Add(key,c);}
        public void TextField(string label,string key,string value){Add(label,key,new TextBox{Text=value});}
        public void Number(string label,string key,double value){TextField(label,key,value.ToString("0.###",System.Globalization.CultureInfo.CurrentCulture));}
        public void Check(string label,string key,bool value){Add(label,key,new CheckBox{Checked=value});}
        public void Choice(string label,string key,System.Collections.Generic.Dictionary<string,string> options,string value){var c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,DisplayMember="Value",ValueMember="Key",DataSource=options.ToList()};c.SelectedValue=value;Add(label,key,c);}
        public void EnabledField(string key,bool enabled){inputs[key].Enabled=enabled;}
        public void Changed(string key,Action action){if(inputs[key] is CheckBox cb)cb.CheckedChanged+=(s,e)=>action();else inputs[key].TextChanged+=(s,e)=>action();}
        public string Get(string key){return inputs[key] is ComboBox c?(string)c.SelectedValue:inputs[key].Text;}
        public bool Checked(string key){return ((CheckBox)inputs[key]).Checked;}
        public double Value(string key){double v;if(!double.TryParse(Get(key).Replace(',','.'),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out v)||double.IsNaN(v)||double.IsInfinity(v))throw new InvalidOperationException("Введите число: "+key);return v;}
    }
}
