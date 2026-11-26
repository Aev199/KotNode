using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
using Font=System.Drawing.Font;
using FlowDirection=System.Windows.Forms.FlowDirection;
using OpenFileDialog=System.Windows.Forms.OpenFileDialog;
using SaveFileDialog=System.Windows.Forms.SaveFileDialog;
using ModelJson=KotNode.Core.Json;
[assembly: CommandClass(typeof(KotNode.AutoCAD.FamilyCommands))]

namespace KotNode.AutoCAD
{
    // Stage 9: Parametric Family Library browser & template saver (NAFAMILY / NAFAMILYSAVE).
    public sealed class FamilyCommands
    {
        [CommandMethod("NAFAMILY",CommandFlags.Modal)]
        public void InsertFamily()
        {
            Diagnostics.Begin("NAFAMILY");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                if(!doc.Database.TileMode||doc.Database.Insunits!=UnitsValue.Millimeters)
                    throw new InvalidOperationException("Откройте пространство модели (TILEMODE=1), единицы DWG — миллиметры (INSUNITS=4).");
                using(var trCheck=doc.Database.TransactionManager.StartTransaction())
                {
                    if(AssemblyDrawing.Load(doc.Database,trCheck)!=null)
                        throw new InvalidOperationException("В этом DWG уже есть марка. Откройте новый DWG для вставки семейства.");
                    trCheck.Commit();
                }

                NodeFamilyTemplate chosen;
                using(var dlg=new FamilyPickerDialog())
                {
                    if(AcApp.ShowModalDialog(dlg)!=DialogResult.OK)return;
                    chosen=dlg.Result;
                }
                if(chosen==null)return;

                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var mark=chosen.Mark;
                    var refs=new Dictionary<Guid,BlockReference>();
                    var geomByPart=new Dictionary<Guid,ObjectId>();
                    foreach(var part in mark.Parts)
                        geomByPart[part.Id]=AssemblyDrawing.Geometry(doc.Database,tr,part);
                    foreach(var inst in mark.Instances)
                        refs[inst.Id]=AssemblyDrawing.Insert(doc.Database,tr,mark,inst,geomByPart[inst.PartId]);
                    // Normalize placements from inserted BlockReferences BEFORE computing view SourceHash
                    // so newly inserted views immediately match Signature(mark) with zero false [обновить] warnings.
                    AssemblyDrawing.Synchronize(doc.Database,tr,mark);
                    double maxViewX=1500,maxViewY=400;
                    bool hasViewExt=false;
                    if(chosen.Views!=null)
                    {
                        foreach(var vt in chosen.Views)
                        {
                            var data=new AssemblyViews.ViewData
                            {
                                Id=vt.Id,Name=vt.Name,IsSection=vt.IsSection,FullCut=vt.FullCut,
                                Hidden=vt.Hidden,NoHatch=vt.NoHatch,AnnotationScale=vt.AnnotationScale,
                                ParametricPrototype=true,Frame=vt.Frame,
                                FrameAnchorId=vt.FrameAnchorId,
                                RelativeFrame=vt.RelativeFrame,
                                LineOverrides=vt.LineOverrides??new List<ViewLineOverride>(),
                                Welds=vt.Welds??new List<ViewWeldOverlay>(),
                                Dimensions=vt.Dimensions??new List<ViewDimension>(),
                                Leaders=vt.Leaders??new List<ViewLeader>(),
                                Axes=vt.Axes??new List<ViewAxisMark>(),
                                SectionCallouts=vt.SectionCallouts??new List<ViewSectionCallout>()
                            };
                            var vref=AssemblyViews.Create(doc.Database,tr,mark,refs,data,new Point3d(vt.OffsetX,vt.OffsetY,0));
                            FamilyManualGraphics.Import(doc.Database,tr,vref,vt.ManualGraphicsDwg);
                            try
                            {
                                var ext=vref.GeometricExtents;
                                if(!hasViewExt||ext.MaxPoint.X>maxViewX)maxViewX=ext.MaxPoint.X;
                                if(!hasViewExt||ext.MaxPoint.Y>maxViewY)maxViewY=ext.MaxPoint.Y;
                                hasViewExt=true;
                            }
                            catch { if(vt.OffsetX+1200>maxViewX)maxViewX=vt.OffsetX+1200; }
                        }
                    }
                    AssemblyViews.Update(doc.Database,tr,mark,refs);
                    double tableX=maxViewX+600;
                    double nextTableY=maxViewY;
                    var space=AssemblyDrawing.ModelSpace(doc.Database,tr,OpenMode.ForWrite);
                    if(chosen.IncludeSpecTable)
                    {
                        var spec=AssemblyTableDrawing.RenderTable(doc.Database,tr,space,null,new Point3d(tableX,nextTableY,0),20,AssemblyReporting.BuildMarkSpec(mark));
                        AssemblyDrawing.Write((DBDictionary)tr.GetObject(spec.ExtensionDictionary.IsNull?CreateExt(spec):spec.ExtensionDictionary,OpenMode.ForRead),
                            AssemblyTableDrawing.TableKey,new AssemblyTableDrawing.TableTag{MarkId=mark.Id,Kind="spec",AnnotationScale=20,SelfHandle=spec.Handle.ToString()},tr);
                        try { nextTableY=spec.GeometricExtents.MinPoint.Y-350; } catch { nextTableY-=1200; }
                    }
                    if(chosen.IncludeVrsTable)
                    {
                        var vrs=AssemblyTableDrawing.RenderTable(doc.Database,tr,space,null,new Point3d(tableX,nextTableY,0),20,AssemblyReporting.BuildMarkVrs(mark));
                        AssemblyDrawing.Write((DBDictionary)tr.GetObject(vrs.ExtensionDictionary.IsNull?CreateExt(vrs):vrs.ExtensionDictionary,OpenMode.ForRead),
                            AssemblyTableDrawing.TableKey,new AssemblyTableDrawing.TableTag{MarkId=mark.Id,Kind="vrs",AnnotationScale=20,SelfHandle=vrs.Handle.ToString()},tr);
                    }
                    AssemblyDrawing.Save(doc.Database,tr,mark);
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                doc.Editor.Regen();
                AssemblyCommands.RefreshPalette(doc);
                doc.Editor.WriteMessage("\nСемейство «"+chosen.Name+"» (марка "+chosen.Mark.Code+") вставлено с видами и таблицами. Изменяйте параметры через NAPARAMS.");
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nСемейства: "+ex.Message+"\nЖурнал: "+log);
            }
        }
        static ObjectId CreateExt(Entity e){e.CreateExtensionDictionary();return e.ExtensionDictionary;}

        [CommandMethod("NAFAMILYSAVE",CommandFlags.Modal)]
        public void SaveFamily()
        {
            Diagnostics.Begin("NAFAMILYSAVE");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                NodeFamilyTemplate template;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null)throw new InvalidOperationException("В текущем DWG нет марки для сохранения в семейство.");
                    AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    var entries=AssemblyViews.Find(doc.Database,tr,m)
                        .Where(e=>!e.Data.ManualSnapshot&&!string.IsNullOrEmpty(e.Data.GeneratedChildHandle)).ToList();
                    foreach(var entry in entries)ViewAnnotationDrawing.CaptureOffsets(doc.Database,tr,entry);
                    var views=entries.Select(e=>new FamilyViewTemplate
                    {
                        Id=e.Data.Id,
                        ManualGraphicsDwg=FamilyManualGraphics.Export(doc.Database,tr,e),
                        Name=e.Data.Name,IsSection=e.Data.IsSection,FullCut=e.Data.FullCut,
                        Hidden=e.Data.Hidden,NoHatch=e.Data.NoHatch,
                        AnnotationScale=e.Data.AnnotationScale>0?e.Data.AnnotationScale:20,
                        Frame=ModelJson.Copy(e.Data.Frame),FrameAnchorId=e.Data.FrameAnchorId,
                        RelativeFrame=e.Data.RelativeFrame==null?null:ModelJson.Copy(e.Data.RelativeFrame),
                        OffsetX=e.Reference.Position.X,OffsetY=e.Reference.Position.Y,
                        LineOverrides=ModelJson.Copy(e.Data.LineOverrides??new List<ViewLineOverride>()),
                        Welds=ModelJson.Copy(e.Data.Welds??new List<ViewWeldOverlay>()),
                        Dimensions=ModelJson.Copy(e.Data.Dimensions??new List<ViewDimension>()),
                        Leaders=ModelJson.Copy(e.Data.Leaders??new List<ViewLeader>()),
                        Axes=ModelJson.Copy(e.Data.Axes??new List<ViewAxisMark>()),
                        SectionCallouts=ModelJson.Copy(e.Data.SectionCallouts??new List<ViewSectionCallout>())
                    }).ToList();
                    template=new NodeFamilyTemplate
                    {
                        Name=m.Name,Category="Пользовательские семейства",
                        Description="Сохранено из марки "+m.Code,
                        Mark=ModelJson.Copy(m),Views=views
                    };
                    tr.Commit();
                }
                using(var f=new Fields("Сохранить параметрическое семейство"))
                {
                    f.TextField("Название семейства","name",template.Name);
                    f.TextField("Категория","cat",template.Category);
                    f.TextField("Описание","desc",template.Description);
                    f.ValidateFields=()=>{if(string.IsNullOrWhiteSpace(f.Get("name")))throw new InvalidOperationException("Введите название семейства.");};
                    if(AcApp.ShowModalDialog(f)!=DialogResult.OK)return;
                    template.Name=f.Get("name").Trim();
                    template.Category=f.Get("cat").Trim();
                    template.Description=f.Get("desc").Trim();
                }
                using(var dlg=new SaveFileDialog{Filter="Семейство KotNode (*.knfamily.json)|*.knfamily.json",FileName=template.Mark.Code+".knfamily.json",OverwritePrompt=true})
                {
                    if(dlg.ShowDialog()!=DialogResult.OK)return;
                    NodeFamilyLibrary.Save(dlg.FileName,template);
                    doc.Editor.WriteMessage("\nСемейство «"+template.Name+"» сохранено (видов: "+template.Views.Count+"): "+dlg.FileName);
                }
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nСемейства: "+ex.Message+"\nЖурнал: "+log);
            }
        }
    }

    internal sealed class FamilyPickerDialog : Form
    {
        readonly List<NodeFamilyTemplate> items;
        readonly ListBox list=new ListBox{Dock=DockStyle.Fill};
        readonly Label desc=new Label{Dock=DockStyle.Top,Height=48,AutoEllipsis=true};
        readonly TextBox codeBox=new TextBox{Width=120};
        readonly TextBox nameBox=new TextBox{Width=260};
        readonly DataGridView paramGrid=new DataGridView
        {
            Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,BackgroundColor=Color.White
        };
        public NodeFamilyTemplate Result { get; private set; }

        public FamilyPickerDialog()
        {
            items=NodeFamilyLibrary.BuiltIn();
            AutoScaleMode=AutoScaleMode.Dpi;
            Text="Библиотека параметрических семейств KotNode";
            Font=new Font("Segoe UI",9f);
            Width=980;Height=580;MinimumSize=new Size(820,480);StartPosition=FormStartPosition.CenterScreen;
            Padding=new Padding(8);

            // DPI-safe proportional 2-column layout (avoids SplitContainer.SplitterDistance DPI collapse)
            var main=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,42));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,58));
            main.RowStyles.Add(new RowStyle(SizeType.Percent,100));

            var left=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0,0,8,0)};
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var loadBtn=new Button{Text="Открыть .knfamily.json...",AutoSize=true,Height=28,FlatStyle=FlatStyle.System,Margin=new Padding(0,0,0,6)};
            loadBtn.Click+=(s,e)=>LoadFromFile();
            list.IntegralHeight=false;
            list.Margin=new Padding(0);
            left.Controls.Add(loadBtn,0,0);
            left.Controls.Add(list,0,1);

            var right=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=new Padding(0)};
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent,100));

            desc.Dock=DockStyle.Fill;desc.AutoSize=true;desc.MaximumSize=new Size(520,0);desc.Margin=new Padding(0,0,0,8);

            var meta=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,AutoSize=true,Margin=new Padding(0,0,0,8)};
            meta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            meta.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,95));
            meta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            meta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            codeBox.Dock=DockStyle.Fill;nameBox.Dock=DockStyle.Fill;
            meta.Controls.Add(new Label{Text="Шифр:",AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(0,4,4,0)},0,0);
            meta.Controls.Add(codeBox,1,0);
            meta.Controls.Add(new Label{Text="Название:",AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(10,4,4,0)},2,0);
            meta.Controls.Add(nameBox,3,0);

            paramGrid.Columns.Add("Name","Параметр");paramGrid.Columns[0].ReadOnly=true;paramGrid.Columns[0].FillWeight=20;
            paramGrid.Columns.Add("Label","Описание");paramGrid.Columns[1].ReadOnly=true;paramGrid.Columns[1].FillWeight=46;
            paramGrid.Columns.Add("Value","Значение");paramGrid.Columns[2].FillWeight=20;
            paramGrid.Columns.Add("Unit","Ед.");paramGrid.Columns[3].ReadOnly=true;paramGrid.Columns[3].FillWeight=14;

            right.Controls.Add(desc,0,0);
            right.Controls.Add(meta,0,1);
            right.Controls.Add(paramGrid,0,2);

            main.Controls.Add(left,0,0);
            main.Controls.Add(right,1,0);

            var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,8,0,0)};
            var ok=new Button{Text="Вставить семейство",AutoSize=true,Height=30,FlatStyle=FlatStyle.System};
            var cancel=new Button{Text="Отмена",DialogResult=DialogResult.Cancel,AutoSize=true,Height=30,FlatStyle=FlatStyle.System};
            ok.Click+=(s,e)=>Confirm();
            footer.Controls.Add(ok);footer.Controls.Add(cancel);

            Controls.Add(main);Controls.Add(footer);
            AcceptButton=ok;CancelButton=cancel;

            list.SelectedIndexChanged+=(s,e)=>SelectCurrent();
            RefreshList();
            if(list.Items.Count>0)list.SelectedIndex=0;
        }

        void RefreshList()
        {
            list.Items.Clear();
            foreach(var f in items)list.Items.Add("["+f.Category+"] "+f.Name);
        }
        void LoadFromFile()
        {
            using(var dlg=new OpenFileDialog{Filter="Семейство KotNode (*.knfamily.json)|*.knfamily.json|JSON (*.json)|*.json"})
            {
                if(dlg.ShowDialog()!=DialogResult.OK)return;
                try
                {
                    var loaded=NodeFamilyLibrary.Load(dlg.FileName);
                    items.Add(loaded);
                    RefreshList();
                    list.SelectedIndex=items.Count-1;
                }
                catch(System.Exception ex){MessageBox.Show(ex.Message,"Семейство");}
            }
        }
        void SelectCurrent()
        {
            if(list.SelectedIndex<0||list.SelectedIndex>=items.Count)return;
            var f=items[list.SelectedIndex];
            desc.Text=f.Description;
            codeBox.Text=f.Mark.Code;
            nameBox.Text=f.Mark.Name;
            paramGrid.Rows.Clear();
            if(f.Mark.Parameters!=null)
            {
                foreach(var p in f.Mark.Parameters)
                {
                    int row=paramGrid.Rows.Add(p.Name,p.Label,p.Value.ToString("0.###",CultureInfo.InvariantCulture),p.Unit);
                    if(!string.IsNullOrWhiteSpace(p.Formula))
                    {
                        paramGrid.Rows[row].Cells[2].ReadOnly=true;
                        paramGrid.Rows[row].Cells[2].Style.BackColor=SystemColors.Control;
                        paramGrid.Rows[row].Cells[1].Value=p.Label+" (= "+p.Formula+")";
                    }
                }
            }
        }
        void Confirm()
        {
            if(list.SelectedIndex<0||list.SelectedIndex>=items.Count)return;
            try
            {
                var template=items[list.SelectedIndex];
                var overrides=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
                foreach(DataGridViewRow row in paramGrid.Rows)
                {
                    if(row.Cells[2].ReadOnly)continue;
                    string key=Convert.ToString(row.Cells[0].Value);
                    string s=(Convert.ToString(row.Cells[2].Value)??"").Replace(',','.');
                    if(!double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var val))
                        throw new InvalidOperationException("Неверное число для параметра "+key);
                    overrides[key]=val;
                }
                Result=NodeFamilyLibrary.Instantiate(template,codeBox.Text,nameBox.Text,overrides);
                DialogResult=DialogResult.OK;Close();
            }
            catch(System.Exception ex){MessageBox.Show(ex.Message,"Параметры семейства",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
    }
}
