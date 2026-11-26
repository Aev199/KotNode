using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;

namespace KotNode.AutoCAD
{
    internal sealed class ConstructorPanel : UserControl
    {
        enum NodeKind { None, Root, Part, Instance, View, LegacyView, ManualView, AuxGroup }
        sealed class NodeTag
        {
            public NodeKind Kind;
            public string Key;
        }

        readonly Label markTitle;
        readonly Label statusLabel;
        readonly Button newMarkBannerButton;
        readonly TreeView tree;
        readonly Button selectButton;
        readonly Button deleteButton;
        readonly Button quickActionButton;
        readonly ToolTip tips=new ToolTip{AutoPopDelay=6000,InitialDelay=350,ReshowDelay=150,ShowAlways=true};
        readonly ContextMenuStrip moreMenu=new ContextMenuStrip();
        readonly ContextMenuStrip treeMenu=new ContextMenuStrip();
        Document treeDocument;

        public ConstructorPanel()
        {
            AutoScaleMode=AutoScaleMode.Dpi;
            Font=new Font("Segoe UI",9f,FontStyle.Regular,GraphicsUnit.Point);
            Dock=DockStyle.Fill;
            BackColor=SystemColors.Control;
            Padding=new Padding(6);

            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Margin=new Padding(0)};
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 0: Header + quick top actions
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 1: Action tabs
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 2: Tree header label
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));  // 3: TreeView
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 4: Selection context bar
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 5: Status footer

            // --- 0. Header bar ---
            var headerPanel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,AutoSize=true,Margin=new Padding(0,0,0,6)};
            headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            markTitle=new Label
            {
                Text="Марка не создана",
                Font=new Font("Segoe UI",9.5f,FontStyle.Bold,GraphicsUnit.Point),
                AutoSize=true,
                Anchor=AnchorStyles.Left,
                Margin=new Padding(0,5,6,0)
            };
            headerPanel.Controls.Add(markTitle,0,0);

            var topButtons=new FlowLayoutPanel{AutoSize=true,WrapContents=false,FlowDirection=FlowDirection.LeftToRight,Margin=new Padding(0)};
            newMarkBannerButton=ActionButton("+ Марка","NANEW","Создать новую марку в текущем DWG (NANEW)",26);
            var refreshBtn=ActionButton("Обновить","NAREFRESH","Обновить дерево состава и синхронизировать удаления (NAREFRESH)",26);
            var infoBtn=ActionButton("Инфо","NAINFO","Вывести состав и массу марки в командную строку (NAINFO)",26);
            var moreBtn=new Button{Text="Ещё ▾",AutoSize=true,Height=26,Margin=new Padding(2,0,0,0),FlatStyle=FlatStyle.System};
            tips.SetToolTip(moreBtn,"Редкие и служебные команды марки");

            moreMenu.Items.Add("Новая марка (NANEW)",null,(s,e)=>Queue("NANEW"));
            moreMenu.Items.Add("Библиотека семейств (NAFAMILY)",null,(s,e)=>Queue("NAFAMILY"));
            moreMenu.Items.Add("Сохранить как семейство (NAFAMILYSAVE)",null,(s,e)=>Queue("NAFAMILYSAVE"));
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("Параметры и формулы (NAPARAMS)",null,(s,e)=>Queue("NAPARAMS"));
            moreMenu.Items.Add("Пересчитать параметры (NASOLVE)",null,(s,e)=>Queue("NASOLVE"));
            moreMenu.Items.Add("Выгрузить CSV (NAEXPORTCSV)",null,(s,e)=>Queue("NAEXPORTCSV"));
            moreMenu.Items.Add(new ToolStripSeparator());
            moreMenu.Items.Add("Синхронизировать положения (NASYNC)",null,(s,e)=>Queue("NASYNC"));
            moreMenu.Items.Add("Перевести старый вид (NAMIGRATEVIEW)",null,(s,e)=>Queue("NAMIGRATEVIEW"));
            moreMenu.Items.Add("Восстановить пропавшие детали (NAREPAIR)",null,(s,e)=>Queue("NAREPAIR"));
            moreBtn.Click+=(s,e)=>moreMenu.Show(moreBtn,new Point(0,moreBtn.Height));

            topButtons.Controls.Add(newMarkBannerButton);
            topButtons.Controls.Add(refreshBtn);
            topButtons.Controls.Add(infoBtn);
            topButtons.Controls.Add(moreBtn);
            headerPanel.Controls.Add(topButtons,1,0);
            root.Controls.Add(headerPanel,0,0);

            // --- 1. Tabbed actions (clean functional tabs instead of flat button wall) ---
            var tabs=new TabControl{Dock=DockStyle.Fill,Height=152,Margin=new Padding(0,0,0,6)};
            tabs.TabPages.Add(BuildPartsTab());
            tabs.TabPages.Add(BuildEditTab());
            tabs.TabPages.Add(BuildViewsTab());
            tabs.TabPages.Add(BuildDraftingTab());
            root.Controls.Add(tabs,0,1);

            // --- 2. Tree caption ---
            var treeCaption=new Label
            {
                Text="Состав марки и виды (двойной клик — выбрать, ПКМ — меню)",
                ForeColor=SystemColors.GrayText,
                Font=new Font("Segoe UI",8.25f,FontStyle.Regular,GraphicsUnit.Point),
                AutoSize=true,
                Margin=new Padding(1,0,0,3)
            };
            root.Controls.Add(treeCaption,0,2);

            // --- 3. TreeView ---
            tree=new TreeView
            {
                Dock=DockStyle.Fill,
                HideSelection=false,
                ShowNodeToolTips=true,
                FullRowSelect=true,
                ItemHeight=20,
                BorderStyle=BorderStyle.FixedSingle,
                Margin=new Padding(0,0,0,6)
            };
            tree.NodeMouseDoubleClick+=(s,e)=>PickSelected();
            tree.AfterSelect+=(s,e)=>UpdateSelectionControls();
            tree.NodeMouseClick+=(s,e)=>{
                if(e.Button==MouseButtons.Right)
                {
                    tree.SelectedNode=e.Node;
                    ShowNodeContextMenu(e.Node,e.Location);
                }
            };
            root.Controls.Add(tree,0,3);

            // --- 4. Contextual bottom bar for selected tree item ---
            var selBar=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,AutoSize=true,Margin=new Padding(0,0,0,4)};
            selBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,34));
            selBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,34));
            selBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,32));

            selectButton=new Button{Text="Выбрать",Dock=DockStyle.Fill,Height=26,Margin=new Padding(0,0,3,0),Enabled=false,FlatStyle=FlatStyle.System};
            selectButton.Click+=(s,e)=>PickSelected();
            tips.SetToolTip(selectButton,"Выделить выбранный объект на чертеже (NASELECT)");

            quickActionButton=new Button{Text="Линии вида",Dock=DockStyle.Fill,Height=26,Margin=new Padding(2,0,2,0),Enabled=false,FlatStyle=FlatStyle.System};
            quickActionButton.Click+=(s,e)=>RunQuickAction();
            tips.SetToolTip(quickActionButton,"Быстрое действие для выбранного элемента дерева");

            deleteButton=new Button{Text="Удалить",Dock=DockStyle.Fill,Height=26,Margin=new Padding(3,0,0,0),Enabled=false,FlatStyle=FlatStyle.System};
            deleteButton.Click+=(s,e)=>DeleteSelected();
            tips.SetToolTip(deleteButton,"Удалить выбранную позицию, экземпляр или вид (NATREEDELETE)");

            selBar.Controls.Add(selectButton,0,0);
            selBar.Controls.Add(quickActionButton,1,0);
            selBar.Controls.Add(deleteButton,2,0);
            root.Controls.Add(selBar,0,4);

            // --- 5. Status footer ---
            statusLabel=new Label
            {
                Text="Откройте DWG и нажмите «Обновить»",
                AutoSize=true,
                Dock=DockStyle.Fill,
                ForeColor=SystemColors.ControlDarkDark,
                Font=new Font("Segoe UI",8.5f,FontStyle.Regular,GraphicsUnit.Point),
                Padding=new Padding(2,3,2,1)
            };
            root.Controls.Add(statusLabel,0,5);

            Controls.Add(root);
        }

        TabPage BuildPartsTab()
        {
            var page=new TabPage("Детали"){Padding=new Padding(4)};
            var grid=MakeGrid(3,3);
            AddGridButton(grid,0,0,"Труба","NAPIPE","Добавить круглую трубу (NAPIPE)");
            AddGridButton(grid,1,0,"Лист","NAPLATE","Добавить прямоугольный лист (NAPLATE)");
            AddGridButton(grid,2,0,"По контуру","NAPOLYPLATE","Лист по замкнутой полилинии с отверстиями (NAPOLYPLATE)");
            AddGridButton(grid,0,1,"Кривой лист","NACURVED","Криволинейная накладка — сектор цилиндра (NACURVED)");
            AddGridButton(grid,1,1,"Двутавр","NABEAM","Добавить двутавр по сортаменту (NABEAM)");
            AddGridButton(grid,2,1,"Арматура","NAREBAR","Арматурный стержень по точкам с гибами (NAREBAR)");
            var aux=GridButton("+ Вспомогательный чертёж","NAAUX","Добавить выбранные примитивы как вспомогательную графику без массы (NAAUX)");
            grid.Controls.Add(aux,0,2);
            grid.SetColumnSpan(aux,3);
            page.Controls.Add(grid);
            return page;
        }

        TabPage BuildEditTab()
        {
            var page=new TabPage("Правка"){Padding=new Padding(4)};
            var grid=MakeGrid(3,3);
            AddGridButton(grid,0,0,"Свойства","NAEDIT","Редактировать свойства определения детали (NAEDIT)");
            AddGridButton(grid,1,0,"Копировать","NACOPY","Создать копии детали с общей позицией (NACOPY)");
            AddGridButton(grid,2,0,"Массив","NAARRAY","Линейный массив детали (NAARRAY)");
            AddGridButton(grid,0,1,"Отделить поз.","NADETACH","Выделить экземпляр в отдельную позицию (NADETACH)");
            AddGridButton(grid,1,1,"Удалить","NADELETE","Удалить деталь с чертежа и из марки (NADELETE)");
            AddGridButton(grid,2,1,"Синхрон.","NASYNC","Записать положения после MOVE/ROTATE (NASYNC)");
            var paramBtn=GridButton("Параметры / формулы","NAPARAMS","Редактор параметров и формул семейства (NAPARAMS)");
            paramBtn.Font=new Font(Font,FontStyle.Bold);
            grid.Controls.Add(paramBtn,0,2);grid.SetColumnSpan(paramBtn,2);
            AddGridButton(grid,2,2,"Семейства","NAFAMILY","Вставить готовое параметрическое семейство (NAFAMILY)");
            page.Controls.Add(grid);
            return page;
        }

        TabPage BuildViewsTab()
        {
            var page=new TabPage("Виды"){Padding=new Padding(4)};
            var grid=MakeGrid(3,4);
            AddGridButton(grid,0,0,"Создать вид","NAVIEW","Построить параметрический вид (NAVIEW)");
            AddGridButton(grid,1,0,"Разрез","NASECTION","Построить параметрический разрез (NASECTION)");
            AddGridButton(grid,2,0,"Сечение","NACROSS","Построить чистое сечение без заднего плана (NACROSS)");
            AddGridButton(grid,0,1,"Обновить все","NAUPDATEVIEWS","Перестроить все параметрические виды марки (NAUPDATEVIEWS)");
            AddGridButton(grid,1,1,"Обновить вид","NAUPDATEVIEW","Перестроить один выбранный вид (NAUPDATEVIEW)");
            AddGridButton(grid,2,1,"Свойства вида","NAEDITVIEW","Изменить параметры, плоскость или привязку вида (NAEDITVIEW)");
            var lineEdit=GridButton("Редактировать линии","NALINEEDIT","Скрыть, показать или переключить тип линии/участка вида (NALINEEDIT)");
            lineEdit.Font=new Font(Font,FontStyle.Bold);
            grid.Controls.Add(lineEdit,0,2);
            grid.SetColumnSpan(lineEdit,2);
            AddGridButton(grid,2,2,"Сброс линий","NALINERESET","Отменить ручные правки линий вида (NALINERESET)");
            var weldBtn=GridButton("Сварные швы","NAWELD","Нанести параметрические сварные швы по кромкам вида (NAWELD)");
            grid.Controls.Add(weldBtn,0,3);
            grid.SetColumnSpan(weldBtn,2);
            AddGridButton(grid,2,3,"Сброс швов","NAWELDRESET","Удалить параметрические сварные швы вида (NAWELDRESET)");
            page.Controls.Add(grid);
            return page;
        }

        TabPage BuildDraftingTab()
        {
            var page=new TabPage("Оформление"){Padding=new Padding(4)};
            var grid=MakeGrid(3,3);
            AddGridButton(grid,0,0,"Размер","NADIM","Ассоциативный размер на виде (NADIM)");
            AddGridButton(grid,1,0,"Позиция","NAPOS","Ассоциативная выноска позиции детали (NAPOS)");
            AddGridButton(grid,2,0,"Выноска шва","NAWELDLEADER","Выноска параметрического сварного шва (NAWELDLEADER)");
            AddGridButton(grid,0,1,"Ось","NAAXIS","Ассоциативная осевая линия центра трубы/отверстия (NAAXIS)");
            AddGridButton(grid,1,1,"Разрез 1-1","NASECTIONMARK","Обозначение линии разреза со стрелками (NASECTIONMARK)");
            AddGridButton(grid,2,1,"Очистить","NAANNORESET","Очистить ассоциативное оформление вида (NAANNORESET)");
            AddGridButton(grid,0,2,"Спецификация","NASPEC","Вставить таблицу спецификации марки (NASPEC)");
            AddGridButton(grid,1,2,"ВРС марки","NAVRS","Вставить ведомость расхода стали марки (NAVRS)");
            AddGridButton(grid,2,2,"Проект","NPROJECT2","Открыть диспетчер проекта Schema 2 (NPROJECT2)");
            page.Controls.Add(grid);
            return page;
        }

        static TableLayoutPanel MakeGrid(int cols,int rows)
        {
            var g=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=cols,RowCount=rows,Margin=new Padding(0)};
            for(int c=0;c<cols;c++)g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/cols));
            for(int r=0;r<rows;r++)g.RowStyles.Add(new RowStyle(SizeType.Percent,100f/rows));
            return g;
        }

        Button ActionButton(string text,string command,string tooltip,int height)
        {
            var b=new Button{Text=text,AutoSize=true,Height=height,Margin=new Padding(2,0,0,0),FlatStyle=FlatStyle.System};
            b.Click+=(s,e)=>Queue(command);
            tips.SetToolTip(b,tooltip);
            return b;
        }

        Button GridButton(string text,string command,string tooltip)
        {
            var b=new Button{Text=text,Dock=DockStyle.Fill,Margin=new Padding(2),FlatStyle=FlatStyle.System};
            b.Click+=(s,e)=>Queue(command);
            tips.SetToolTip(b,tooltip);
            return b;
        }

        void AddGridButton(TableLayoutPanel grid,int col,int row,string text,string command,string tooltip)
            =>grid.Controls.Add(GridButton(text,command,tooltip),col,row);

        void ShowNodeContextMenu(TreeNode node,Point location)
        {
            treeMenu.Items.Clear();
            var tag=node?.Tag as NodeTag;
            if(tag==null||tag.Kind==NodeKind.None)return;
            if(tag.Kind==NodeKind.Root)
            {
                treeMenu.Items.Add("Обновить дерево (NAREFRESH)",null,(s,e)=>Queue("NAREFRESH"));
                treeMenu.Items.Add("Состав и масса (NAINFO)",null,(s,e)=>Queue("NAINFO"));
                treeMenu.Items.Add("Обновить все виды (NAUPDATEVIEWS)",null,(s,e)=>Queue("NAUPDATEVIEWS"));
            }
            else if(tag.Kind==NodeKind.Part||tag.Kind==NodeKind.Instance)
            {
                treeMenu.Items.Add("Выбрать на чертеже",null,(s,e)=>PickSelected());
                treeMenu.Items.Add("Свойства детали (NAEDIT)",null,(s,e)=>Queue("NAEDIT"));
                treeMenu.Items.Add(new ToolStripSeparator());
                treeMenu.Items.Add("Удалить из марки",null,(s,e)=>DeleteSelected());
            }
            else if(tag.Kind==NodeKind.View||tag.Kind==NodeKind.LegacyView||tag.Kind==NodeKind.ManualView)
            {
                treeMenu.Items.Add("Выбрать на чертеже",null,(s,e)=>PickSelected());
                if(tag.Kind==NodeKind.View)
                {
                    treeMenu.Items.Add("Редактировать линии (NALINEEDIT)",null,(s,e)=>Queue("NALINEEDIT"));
                    treeMenu.Items.Add("Сбросить линии (NALINERESET)",null,(s,e)=>Queue("NALINERESET"));
                    treeMenu.Items.Add(new ToolStripSeparator());
                    treeMenu.Items.Add("Сварные швы (NAWELD)",null,(s,e)=>Queue("NAWELD"));
                    treeMenu.Items.Add("Сбросить швы (NAWELDRESET)",null,(s,e)=>Queue("NAWELDRESET"));
                    treeMenu.Items.Add(new ToolStripSeparator());
                    treeMenu.Items.Add("Обновить вид (NAUPDATEVIEW)",null,(s,e)=>Queue("NAUPDATEVIEW"));
                    treeMenu.Items.Add("Свойства вида (NAEDITVIEW)",null,(s,e)=>Queue("NAEDITVIEW"));
                }
                else if(tag.Kind==NodeKind.LegacyView)
                {
                    treeMenu.Items.Add("Перевести в новый формат (NAMIGRATEVIEW)",null,(s,e)=>Queue("NAMIGRATEVIEW"));
                }
                treeMenu.Items.Add(new ToolStripSeparator());
                treeMenu.Items.Add("Удалить представление",null,(s,e)=>DeleteSelected());
            }
            else if(tag.Kind==NodeKind.AuxGroup)
            {
                treeMenu.Items.Add("Выбрать на чертеже",null,(s,e)=>PickSelected());
            }
            if(treeMenu.Items.Count>0)treeMenu.Show(tree,location);
        }

        void UpdateSelectionControls()
        {
            var tag=tree.SelectedNode?.Tag as NodeTag;
            bool selectable=tag!=null&&tag.Kind!=NodeKind.None&&tag.Kind!=NodeKind.Root&&!string.IsNullOrEmpty(tag.Key);
            bool deletable=tag!=null&&(tag.Kind==NodeKind.Part||tag.Kind==NodeKind.Instance||tag.Kind==NodeKind.View||tag.Kind==NodeKind.LegacyView||tag.Kind==NodeKind.ManualView);
            selectButton.Enabled=selectable;
            deleteButton.Enabled=deletable;
            if(tag!=null&&tag.Kind==NodeKind.View)
            {
                quickActionButton.Text="Линии вида";
                quickActionButton.Enabled=true;
            }
            else if(tag!=null&&tag.Kind==NodeKind.LegacyView)
            {
                quickActionButton.Text="Перевести вид";
                quickActionButton.Enabled=true;
            }
            else if(tag!=null&&(tag.Kind==NodeKind.Part||tag.Kind==NodeKind.Instance))
            {
                quickActionButton.Text="Свойства";
                quickActionButton.Enabled=true;
            }
            else
            {
                quickActionButton.Text="Линии вида";
                quickActionButton.Enabled=false;
            }
        }

        void RunQuickAction()
        {
            var tag=tree.SelectedNode?.Tag as NodeTag;
            if(tag==null)return;
            if(tag.Kind==NodeKind.View)Queue("NALINEEDIT");
            else if(tag.Kind==NodeKind.LegacyView)Queue("NAMIGRATEVIEW");
            else if(tag.Kind==NodeKind.Part||tag.Kind==NodeKind.Instance)Queue("NAEDIT");
        }

        void PickSelected()
        {
            if(treeDocument!=AcApp.DocumentManager.MdiActiveDocument){Queue("NAREFRESH");return;}
            var tag=tree.SelectedNode?.Tag as NodeTag;
            if(tag!=null&&!string.IsNullOrEmpty(tag.Key)&&tag.Kind!=NodeKind.Root)
                Queue("NASELECT "+tag.Key);
        }

        void DeleteSelected()
        {
            if(treeDocument!=AcApp.DocumentManager.MdiActiveDocument){Queue("NAREFRESH");return;}
            var tag=tree.SelectedNode?.Tag as NodeTag;
            if(tag!=null&&!string.IsNullOrEmpty(tag.Key)&&Guid.TryParse(tag.Key,out _))
                Queue("NATREEDELETE "+tag.Key);
        }

        static void Queue(string command)
            =>AcApp.DocumentManager.MdiActiveDocument?.SendStringToExecute(command+"\n",true,false,false);

        internal void ClearTree(string status)
        {
            tree.Nodes.Clear();
            treeDocument=null;
            markTitle.Text="Документ не синхронизирован";
            newMarkBannerButton.Visible=false;
            statusLabel.Text=status;
            statusLabel.ForeColor=SystemColors.ControlDarkDark;
            UpdateSelectionControls();
        }

        internal void RefreshTree(Document doc)
        {
            try
            {
                tree.BeginUpdate();
                var prevTag=treeDocument==doc?tree.SelectedNode?.Tag as NodeTag:null;
                string previous=prevTag?.Key;
                tree.Nodes.Clear();
                treeDocument=doc;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)
                    {
                        markTitle.Text="В DWG нет марки";
                        newMarkBannerButton.Visible=true;
                        statusLabel.Text="Нажмите «+ Марка» (NANEW), чтобы начать сборку узла.";
                        statusLabel.ForeColor=SystemColors.ControlDarkDark;
                        UpdateSelectionControls();
                        return;
                    }
                    AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    markTitle.Text=m.Code+" — "+m.Name;
                    newMarkBannerButton.Visible=false;
                    var root=tree.Nodes.Add(m.Code+" — "+m.Name);
                    root.Tag=new NodeTag{Kind=NodeKind.Root};
                    TreeNode selected=null;
                    foreach(var part in m.Parts)
                    {
                        var instances=m.Instances.Where(i=>i.PartId==part.Id).ToList();
                        var key=part.Id.ToString("D");
                        var node=root.Nodes.Add((part.IncludeInSpec?"Поз. "+part.Position+" • ":"[Справ.] ")+part.Name+" × "+instances.Count);
                        node.Tag=new NodeTag{Kind=NodeKind.Part,Key=key};
                        node.ToolTipText=part.Material+" | "+part.UnitMassKg().ToString("0.###")+" кг/шт.";
                        if(key==previous)selected=node;
                        for(int i=0;i<instances.Count;i++)
                        {
                            var instKey=instances[i].Id.ToString("D");
                            var child=node.Nodes.Add("Экземпляр "+(i+1));
                            child.Tag=new NodeTag{Kind=NodeKind.Instance,Key=instKey};
                            if(instKey==previous)selected=child;
                        }
                    }
                    var views=AssemblyViews.Find(doc.Database,tr,m);
                    int staleCount=0,unmatchedCount=0;
                    if(views.Count>0)
                    {
                        var branch=root.Nodes.Add("Виды и разрезы ("+views.Count+")");
                        branch.Tag=new NodeTag{Kind=NodeKind.None};
                        var hash=AssemblyViews.Signature(m);
                        foreach(var view in views)
                        {
                            var stale=AssemblyViews.IsStale(view.Data,hash);
                            if(stale)staleCount++;
                            unmatchedCount+=view.Data.UnmatchedLineOverrides+view.Data.UnmatchedWelds;
                            bool legacy=!view.Data.ManualSnapshot&&string.IsNullOrEmpty(view.Data.GeneratedChildHandle);
                            int overrideCount=view.Data.LineOverrides?.Count??0;
                            int weldCount=view.Data.Welds?.Count??0;
                            var label=(view.Data.FullCut?"Разрез: ":view.Data.IsSection?"Сечение: ":"Вид: ")+view.Data.Name
                                +(view.Data.ManualSnapshot?" [FLATSHOT]":legacy?" [старый формат]":"")
                                +(overrideCount>0?" [правок: "+overrideCount+"]":"")
                                +(weldCount>0?" [швов: "+weldCount+"]":"")
                                +(stale?" [обновить]":"")
                                +(view.Data.UnmatchedLineOverrides+view.Data.UnmatchedWelds>0?" [неприменённых: "+(view.Data.UnmatchedLineOverrides+view.Data.UnmatchedWelds)+"]":"");
                            var key=view.Data.Id.ToString("D");
                            var node=branch.Nodes.Add(label);
                            node.Tag=new NodeTag{Kind=view.Data.ManualSnapshot?NodeKind.ManualView:legacy?NodeKind.LegacyView:NodeKind.View,Key=key};
                            node.ToolTipText=(stale?"Требуется перестроение. ":"Актуально. ")+"Масштаб 1:"+(view.Data.AnnotationScale>0?view.Data.AnnotationScale:20);
                            if(stale||view.Data.UnmatchedLineOverrides>0||view.Data.UnmatchedWelds>0)node.ForeColor=Color.DarkRed;
                            if(key==previous)selected=node;
                        }
                        branch.Expand();
                    }
                    var groups=AssemblyDrawing.AuxiliaryGroups(doc.Database,tr,m);
                    if(groups.Count>0)
                    {
                        var aux=root.Nodes.Add("Вспомогательные чертежи ("+groups.Count+")");
                        aux.Tag=new NodeTag{Kind=NodeKind.None};
                        for(int i=0;i<groups.Count;i++)
                        {
                            var key=groups[i].Item1;
                            var node=aux.Nodes.Add("Чертёж "+(i+1)+" • "+groups[i].Item2.Length+" объектов");
                            node.Tag=new NodeTag{Kind=NodeKind.AuxGroup,Key=key};
                            if(key==previous)selected=node;
                        }
                    }
                    root.Expand();
                    if(selected!=null){selected.EnsureVisible();tree.SelectedNode=selected;}
                    var mass=m.Specification().Sum(r=>r.TotalMassKg).ToString("0.###");
                    statusLabel.Text="Деталей: "+m.Instances.Count+" • Масса: "+mass+" кг"
                        +(staleCount>0?" • Обновить видов: "+staleCount:"")
                        +(unmatchedCount>0?" • Неприм. правок: "+unmatchedCount:"");
                    statusLabel.ForeColor=staleCount>0||unmatchedCount>0?Color.DarkRed:SystemColors.ControlDarkDark;
                    tr.Commit();
                }
                UpdateSelectionControls();
            }
            catch(Exception ex)
            {
                statusLabel.Text="Не удалось обновить состав: "+ex.Message;
                statusLabel.ForeColor=Color.DarkRed;
            }
            finally{tree.EndUpdate();}
        }
    }
}
