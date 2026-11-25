using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
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
[assembly: CommandClass(typeof(KotNode.AutoCAD.AssemblyCommands))]

namespace KotNode.AutoCAD
{
    public sealed class AssemblyCommands
    {
        static PaletteSet palette;
        static void Run(string command,Action<Document,Transaction> action)
        {
            Diagnostics.Begin(command);
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                if(!doc.Database.TileMode || doc.Database.Insunits!=UnitsValue.Millimeters)
                    throw new InvalidOperationException("Откройте пространство модели (TILEMODE=1), единицы DWG — миллиметры (INSUNITS=4).");
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    action(doc,tr);
                    tr.Commit();
                }
                doc.Editor.Regen();
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nКонструктор: "+ex.Message+"\nЖурнал: "+log);
            }
        }
        static AssemblyMark Need(Document doc,Transaction tr)
        {
            var m=AssemblyDrawing.Load(doc.Database,tr);
            if(m==null)throw new InvalidOperationException("Создайте марку командой NANEW.");
            return m;
        }
        static string Text(Editor ed,string prompt)
        {
            var r=ed.GetString(new PromptStringOptions("\n"+prompt){AllowSpaces=true});
            return r.Status==PromptStatus.OK?r.StringResult.Trim():null;
        }
        static RigidPlacement At(Document doc,string prompt)
        {
            var p=doc.Editor.GetPoint("\n"+prompt);
            if(p.Status!=PromptStatus.OK)return null;
            var ucs=doc.Editor.CurrentUserCoordinateSystem;
            var at=p.Value.TransformBy(ucs);
            var x=Vector3d.XAxis.TransformBy(ucs);var y=Vector3d.YAxis.TransformBy(ucs);
            return new RigidPlacement { Origin=new Vector3(at.X,at.Y,at.Z),AxisX=new Vector3(x.X,x.Y,x.Z),AxisY=new Vector3(y.X,y.Y,y.Z) };
        }
        static Guid? Select(Document doc,System.Collections.Generic.Dictionary<Guid,BlockReference> refs)
        {
            var p=doc.Editor.GetEntity("\nВыберите деталь марки: ");
            if(p.Status!=PromptStatus.OK)return null;
            foreach(var pair in refs)if(pair.Value.ObjectId==p.ObjectId)return pair.Key;
            throw new InvalidOperationException("Выбран неуправляемый объект. Для копирования детали используйте NACOPY.");
        }
        static bool EditForm(PartDefinition part)
        {
            using(var f=new Fields("Свойства детали"))
            {
                f.TextField("Наименование","name",part.Name);
                f.TextField("Позиция","position",part.Position);
                f.TextField("Материал","material",part.Material);
                f.Check("Включить в спецификацию","spec",part.IncludeInSpec);
                if(part.Kind==PartKind.Pipe)
                {
                    f.Number("Наружный диаметр, мм","d",part.Pipe.OuterDiameter);
                    f.Number("Толщина стенки, мм","t",part.Pipe.Thickness);
                    f.Number("Длина, мм","l",part.Pipe.Length);
                }
                else if(part.Kind==PartKind.RectangularPlate)
                {
                    f.Number("Длина, мм","l",part.Plate.Length);
                    f.Number("Ширина, мм","w",part.Plate.Width);
                    f.Number("Толщина, мм","t",part.Plate.Thickness);
                }
                else if(part.Kind==PartKind.PolygonPlate)
                    f.Number("Толщина, мм","t",part.PolygonPlate.Thickness);
                else if(part.Kind==PartKind.CurvedPlate)
                {
                    f.Number("Внутренний радиус, мм","r",part.CurvedPlate.InnerRadius);
                    f.Number("Толщина, мм","t",part.CurvedPlate.Thickness);
                    f.Number("Длина, мм","l",part.CurvedPlate.Length);
                    f.Number("Угол сектора, град","a",part.CurvedPlate.AngleRadians*180/Math.PI);
                }
                else if(part.Kind==PartKind.IBeam)
                {
                    var b=part.IBeam;
                    f.TextField("Обозначение профиля","profile",b.Profile);
                    f.Number("Высота, мм","h",b.Height);f.Number("Ширина, мм","w",b.Width);
                    f.Number("Толщина стенки, мм","s",b.WebThickness);f.Number("Толщина полки, мм","t",b.FlangeThickness);
                    f.Number("Длина, мм","l",b.Length);f.Number("Площадь сортамента, мм² (0 — расчёт)","area",b.CatalogAreaMm2??0);
                }
                else if(part.Kind==PartKind.Rebar)
                {
                    f.Number("Диаметр, мм","d",part.Rebar.Diameter);
                    for(int i=0;i<part.Rebar.BendRadii.Count;i++)
                        f.Number("Радиус по оси гиба "+(i+1)+", мм","bend"+i,part.Rebar.BendRadii[i]);
                }
                else throw new NotSupportedException("Этот тип детали пока не редактируется в AutoCAD.");
                f.ValidateFields=()=>{
                    part.Name=f.Get("name").Trim();part.Position=f.Get("position").Trim();
                    part.Material=f.Get("material").Trim();part.IncludeInSpec=f.Checked("spec");
                    if(part.Kind==PartKind.Pipe){part.Pipe.OuterDiameter=f.Value("d");part.Pipe.Thickness=f.Value("t");part.Pipe.Length=f.Value("l");}
                    else if(part.Kind==PartKind.RectangularPlate) {part.Plate.Length=f.Value("l");part.Plate.Width=f.Value("w");part.Plate.Thickness=f.Value("t");}
                    else if(part.Kind==PartKind.PolygonPlate)part.PolygonPlate.Thickness=f.Value("t");
                    else if(part.Kind==PartKind.CurvedPlate)
                    {var p=part.CurvedPlate;p.InnerRadius=f.Value("r");p.Thickness=f.Value("t");p.Length=f.Value("l");p.AngleRadians=f.Value("a")*Math.PI/180;}
                    else if(part.Kind==PartKind.IBeam)
                    {var b=part.IBeam;b.Profile=f.Get("profile").Trim();b.Height=f.Value("h");b.Width=f.Value("w");b.WebThickness=f.Value("s");b.FlangeThickness=f.Value("t");b.Length=f.Value("l");double a=f.Value("area");b.CatalogAreaMm2=a==0?(double?)null:a;}
                    else if(part.Kind==PartKind.Rebar)
                    {
                        part.Rebar.Diameter=f.Value("d");
                        for(int i=0;i<part.Rebar.BendRadii.Count;i++)part.Rebar.BendRadii[i]=f.Value("bend"+i);
                    }
                    part.Validate();
                };
                return AcApp.ShowModalDialog(f)==DialogResult.OK;
            }
        }
        static string NextPosition(AssemblyMark m)
        {
            int n=1;
            while(m.Parts.Any(p=>string.Equals((p.Position??"").Trim(),n.ToString(),StringComparison.OrdinalIgnoreCase)))n++;
            return n.ToString();
        }
        static void Add(Document doc,Transaction tr,bool pipe)
        {
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var part=new PartDefinition {Name=pipe?"Труба":"Лист",Position=NextPosition(m),IncludeInSpec=!pipe,Kind=pipe?PartKind.Pipe:PartKind.RectangularPlate};
            if(pipe)part.Pipe=new PipeShape {OuterDiameter=820,Thickness=12,Length=1000};
            else part.Plate=new RectangularPlateShape {Length=500,Width=200,Thickness=12};
            if(!EditForm(part))return;
            var at=At(doc,pipe?"Точка начала трубы (ось вдоль Z текущей ПСК): ":"Угол листа (плоскость XY текущей ПСК): ");
            if(at==null)return;
            var instance=m.AddPart(part,at);
            Diagnostics.Step("Построение тела");
            var geometry=AssemblyDrawing.Geometry(doc.Database,tr,part);
            AssemblyDrawing.Insert(doc.Database,tr,m,instance,geometry);
            AssemblyDrawing.Save(doc.Database,tr,m);
        }
        static void AddPrepared(Document doc,Transaction tr,AssemblyMark m,PartDefinition part,RigidPlacement at)
        {
            var instance=m.AddPart(part,at);
            Diagnostics.Step("Построение тела "+part.Kind);
            var geometry=AssemblyDrawing.Geometry(doc.Database,tr,part);
            AssemblyDrawing.Insert(doc.Database,tr,m,instance,geometry);
            AssemblyDrawing.Save(doc.Database,tr,m);
        }
        static List<KotNode.Core.Point2> ReadRing(Polyline line,Matrix3d toLocal)
        {
            if(!line.Closed || line.NumberOfVertices<3)throw new InvalidOperationException("Требуется замкнутая плоская полилиния.");
            var points=new List<KotNode.Core.Point2>();
            for(int i=0;i<line.NumberOfVertices;i++)
            {
                if(Math.Abs(line.GetBulgeAt(i))>1e-12)throw new InvalidOperationException("Дуги контура пока не поддерживаются.");
                var p=line.GetPoint3dAt(i).TransformBy(toLocal);
                if(Math.Abs(p.Z)>1e-6)throw new InvalidOperationException("Контуры и отверстия должны лежать в одной плоскости, параллельной XY текущей ПСК.");
                points.Add(new KotNode.Core.Point2(p.X,p.Y));
            }
            return points;
        }
        [CommandMethod("NAPOLYPLATE",CommandFlags.Modal)]
        public void PolygonPlate()=>Run("NAPOLYPLATE",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var ed=doc.Editor;
            var picked=ed.GetEntity("\nВыберите внешнюю замкнутую полилинию листа: ");
            if(picked.Status!=PromptStatus.OK)return;
            var outer=tr.GetObject(picked.ObjectId,OpenMode.ForRead) as Polyline;
            if(outer==null || outer.NumberOfVertices<3)throw new InvalidOperationException("Выберите замкнутую 2D полилинию.");
            var ucs=ed.CurrentUserCoordinateSystem;
            var origin=outer.GetPoint3dAt(0);
            var x=Vector3d.XAxis.TransformBy(ucs);var y=Vector3d.YAxis.TransformBy(ucs);var z=x.CrossProduct(y);
            var toLocal=Matrix3d.AlignCoordinateSystem(origin,x,y,z,Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis);
            var p=new PolygonPlateShape {Thickness=12,Outer=ReadRing(outer,toLocal)};
            var option=new PromptKeywordOptions("\nЕсть отверстия? [Yes/No] <No>: ");
            option.Keywords.Add("Yes");option.Keywords.Add("No");option.Keywords.Default="No";option.AllowNone=true;
            var choice=ed.GetKeywords(option);
            if(choice.Status!=PromptStatus.OK && choice.Status!=PromptStatus.None)return;
            if(choice.StringResult=="Yes")
            {
                var selection=ed.GetSelection(new PromptSelectionOptions {MessageForAdding="\nВыберите замкнутые полилинии и окружности отверстий: "});
                if(selection.Status!=PromptStatus.OK)return;
                foreach(var id in selection.Value.GetObjectIds())
                {
                    if(id==picked.ObjectId)throw new InvalidOperationException("Внешний контур не может быть отверстием.");
                    var e=tr.GetObject(id,OpenMode.ForRead);
                    if(e is Polyline ring)p.Holes.Add(ReadRing(ring,toLocal));
                    else if(e is Circle circle)
                    {
                        var center=circle.Center.TransformBy(toLocal);
                        if(Math.Abs(center.Z)>1e-6 || Math.Abs(Math.Abs(circle.Normal.DotProduct(z))-1)>1e-8)
                            throw new InvalidOperationException("Окружность должна лежать в плоскости листа.");
                        p.RoundHoles.Add(new RoundHole {Center=new KotNode.Core.Point2(center.X,center.Y),Radius=circle.Radius});
                    }
                    else throw new InvalidOperationException("Отверстия задаются полилиниями или окружностями.");
                }
            }
            var part=new PartDefinition {Name="Лист",Position=NextPosition(m),IncludeInSpec=true,Kind=PartKind.PolygonPlate,PolygonPlate=p};
            if(!EditForm(part))return;
            var at=new RigidPlacement {Origin=new Vector3(origin.X,origin.Y,origin.Z),AxisX=new Vector3(x.X,x.Y,x.Z),AxisY=new Vector3(y.X,y.Y,y.Z)};
            AddPrepared(doc,tr,m,part,at);
            ed.WriteMessage("\nЛист создан. Исходные контуры оставлены без изменений.");
        });
        [CommandMethod("NACURVED",CommandFlags.Modal)]
        public void CurvedPlate()=>Run("NACURVED",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var part=new PartDefinition {Name="Криволинейный лист",Position=NextPosition(m),IncludeInSpec=true,Kind=PartKind.CurvedPlate,
                CurvedPlate=new CurvedPlateShape {InnerRadius=410,Thickness=12,Length=500,AngleRadians=Math.PI/3}};
            if(!EditForm(part))return;
            var at=At(doc,"Центр сектора у начала детали (ось Z текущей ПСК): ");if(at==null)return;
            AddPrepared(doc,tr,m,part,at);
        });
        [CommandMethod("NABEAM",CommandFlags.Modal)]
        public void Beam()=>Run("NABEAM",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var part=new PartDefinition {Name="Двутавр",Position=NextPosition(m),IncludeInSpec=false,Kind=PartKind.IBeam,
                IBeam=new IBeamShape {Height=300,Width=150,WebThickness=8,FlangeThickness=12,Length=1000}};
            if(!EditForm(part))return;
            var at=At(doc,"Центр сечения двутавра (ось Z текущей ПСК): ");if(at==null)return;
            AddPrepared(doc,tr,m,part,at);
        });
        [CommandMethod("NAREBAR",CommandFlags.Modal)]
        public void Rebar()=>Run("NAREBAR",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var ed=doc.Editor;var ucs=ed.CurrentUserCoordinateSystem;
            var first=ed.GetPoint("\nНачало оси стержня: ");if(first.Status!=PromptStatus.OK)return;
            var origin=first.Value.TransformBy(ucs);
            var x=Vector3d.XAxis.TransformBy(ucs);var y=Vector3d.YAxis.TransformBy(ucs);var z=x.CrossProduct(y);
            var toLocal=Matrix3d.AlignCoordinateSystem(origin,x,y,z,Point3d.Origin,Vector3d.XAxis,Vector3d.YAxis,Vector3d.ZAxis);
            var shape=new RebarShape {Diameter=16};shape.Points.Add(new Vector3(0,0,0));
            var previous=first.Value;
            while(shape.Points.Count<32)
            {
                var options=new PromptPointOptions("\nСледующая точка оси (Enter — завершить): ") {AllowNone=true,UseBasePoint=true,BasePoint=previous};
                var next=ed.GetPoint(options);
                if(next.Status==PromptStatus.None)break;
                if(next.Status!=PromptStatus.OK)return;
                var p=next.Value.TransformBy(ucs).TransformBy(toLocal);
                shape.Points.Add(new Vector3(p.X,p.Y,p.Z));previous=next.Value;
            }
            if(shape.Points.Count<2)throw new InvalidOperationException("Укажите не менее двух точек стержня.");
            if(shape.Points.Count==32)ed.WriteMessage("\nДостигнут предел прототипа: 32 точки.");
            for(int i=1;i<shape.Points.Count-1;i++)shape.BendRadii.Add(64);
            var part=new PartDefinition {Name="Арматурный стержень",Material="А500С",Position=NextPosition(m),IncludeInSpec=true,Kind=PartKind.Rebar,Rebar=shape};
            if(!EditForm(part))return;
            var at=new RigidPlacement {Origin=new Vector3(origin.X,origin.Y,origin.Z),AxisX=new Vector3(x.X,x.Y,x.Z),AxisY=new Vector3(y.X,y.Y,y.Z)};
            AddPrepared(doc,tr,m,part,at);
        });
        [CommandMethod("NACONSTRUCTOR",CommandFlags.Modal)]
        public void Open()
        {
            if(palette==null)
            {
                var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=System.Windows.Forms.FlowDirection.TopDown,WrapContents=false,Padding=new Padding(8)};
                panel.Controls.Add(new Label {Text="Экспериментальный конструктор\nОдна марка на DWG • модель 1:1",AutoSize=true});
                foreach(var pair in new[]{new[]{"Новая марка","NANEW"},new[]{"Добавить трубу","NAPIPE"},new[]{"Добавить лист","NAPLATE"},new[]{"Лист по контуру","NAPOLYPLATE"},new[]{"Криволинейный лист","NACURVED"},new[]{"Двутавр","NABEAM"},new[]{"Арматурный стержень","NAREBAR"},new[]{"Свойства детали","NAEDIT"},new[]{"Копировать экземпляр","NACOPY"},new[]{"Отделить позицию","NADETACH"},new[]{"Удалить экземпляр","NADELETE"},new[]{"Синхронизировать","NASYNC"},new[]{"Состав и масса","NAINFO"},new[]{"Восстановить удалённое","NAREPAIR"}})
                {
                    var cmd=pair[1];
                    var button=new Button {Text=pair[0],Width=210,Height=32};
                    button.Click+=(s,e)=>AcApp.DocumentManager.MdiActiveDocument?.SendStringToExecute(cmd+"\n",true,false,false);
                    panel.Controls.Add(button);
                }
                panel.Controls.Add(new Label {Text="MOVE / ROTATE → NASYNC\nCOPY и ERASE — только через команды NA.",AutoSize=true});
                palette=new PaletteSet("Узлы — конструктор",new Guid("e51c3c38-b277-4753-9b27-03ed12d5c50f")){Size=new Size(270,460),MinimumSize=new Size(240,300),Style=PaletteSetStyles.ShowCloseButton|PaletteSetStyles.ShowAutoHideButton};
                palette.Add("Конструктор",panel);
            }
            palette.Visible=true;
        }
        [CommandMethod("NANEW",CommandFlags.Modal)]
        public void NewMark()=>Run("NANEW",(doc,tr)=>{
            if(AssemblyDrawing.Load(doc.Database,tr)!=null)throw new InvalidOperationException("В этом DWG уже есть марка. Создайте новый DWG.");
            var code=Text(doc.Editor,"Шифр марки: ");if(code==null)return;
            var name=Text(doc.Editor,"Название марки: ");if(name==null)return;
            AssemblyDrawing.Save(doc.Database,tr,new AssemblyMark { Code=code,Name=name });
            doc.Editor.WriteMessage("\nМарка создана.");
        });
        [CommandMethod("NAPIPE",CommandFlags.Modal)]
        public void Pipe()=>Run("NAPIPE",(doc,tr)=>Add(doc,tr,true));
        [CommandMethod("NAPLATE",CommandFlags.Modal)]
        public void Plate()=>Run("NAPLATE",(doc,tr)=>Add(doc,tr,false));
        [CommandMethod("NASYNC",CommandFlags.Modal)]
        public void Sync()=>Run("NASYNC",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);AssemblyDrawing.Save(doc.Database,tr,m);
            doc.Editor.WriteMessage("\nПоложения деталей синхронизированы.");
        });
        [CommandMethod("NAEDIT",CommandFlags.Modal)]
        public void Edit()=>Run("NAEDIT",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var id=Select(doc,refs);if(!id.HasValue)return;
            var instance=m.Instances.Single(i=>i.Id==id.Value);
            var part=ModelJson.Copy(m.Parts.Single(p=>p.Id==instance.PartId));
            if(!EditForm(part))return;
            m.ReplaceDefinition(part.Id,part);
            var geometry=AssemblyDrawing.Geometry(doc.Database,tr,part);
            foreach(var i in m.Instances.Where(i=>i.PartId==part.Id))AssemblyDrawing.Replace(refs[i.Id],geometry);
            AssemblyDrawing.Save(doc.Database,tr,m);
        });
        [CommandMethod("NACOPY",CommandFlags.Modal)]
        public void Copy()=>Run("NACOPY",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var id=Select(doc,refs);if(!id.HasValue)return;
            var at=At(doc,"Новое положение начала детали: ");if(at==null)return;
            var copy=m.CopyInstance(id.Value);
            copy.Placement.Origin=at.Origin; // preserve orientation of the source
            AssemblyDrawing.Insert(doc.Database,tr,m,copy,refs[id.Value].BlockTableRecord);
            AssemblyDrawing.Save(doc.Database,tr,m);
        });
        [CommandMethod("NADETACH",CommandFlags.Modal)]
        public void Detach()=>Run("NADETACH",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var id=Select(doc,refs);if(!id.HasValue)return;
            var instance=m.Instances.Single(i=>i.Id==id.Value);
            var old=instance.PartId;
            if(m.Instances.Count(i=>i.PartId==old)==1)throw new InvalidOperationException("Деталь уже имеет самостоятельное определение.");
            var position=Text(doc.Editor,"Новая позиция: ");if(position==null)return;
            var part=m.DetachInstance(id.Value,position);
            AssemblyDrawing.Replace(refs[id.Value],AssemblyDrawing.Geometry(doc.Database,tr,part));
            AssemblyDrawing.Save(doc.Database,tr,m);
        });
        [CommandMethod("NADELETE",CommandFlags.Modal)]
        public void Delete()=>Run("NADELETE",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var id=Select(doc,refs);if(!id.HasValue)return;
            var part=m.Instances.Single(i=>i.Id==id.Value).PartId;
            m.RemoveInstance(id.Value);
            if(!m.Instances.Any(i=>i.PartId==part))m.RemoveUnusedDefinition(part);
            var reference=refs[id.Value];reference.UpgradeOpen();reference.Erase();
            AssemblyDrawing.Save(doc.Database,tr,m);
        });
        [CommandMethod("NAREPAIR",CommandFlags.Modal)]
        public void Repair()=>Run("NAREPAIR",(doc,tr)=>{
            var m=Need(doc,tr);
            var options=new PromptKeywordOptions("\nВосстановить отсутствующие детали из сохраненной модели? [Yes/No] <No>: ");
            options.Keywords.Add("Yes");options.Keywords.Add("No");options.Keywords.Default="No";
            var answer=doc.Editor.GetKeywords(options);
            if(answer.Status!=PromptStatus.OK||answer.StringResult!="Yes")return;
            AssemblyDrawing.Synchronize(doc.Database,tr,m,true);AssemblyDrawing.Save(doc.Database,tr,m);
            doc.Editor.WriteMessage("\nПроверка и восстановление завершены.");
        });
        [CommandMethod("NAINFO",CommandFlags.Modal)]
        public void Info()=>Run("NAINFO",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var rows=m.Specification();
            doc.Editor.WriteMessage("\nМарка "+m.Code+" — "+m.Name+" | объектов: "+m.Instances.Count);
            foreach(var r in rows)doc.Editor.WriteMessage("\nПоз. "+r.Position+" "+r.Name+" "+r.Material+" | "+r.Count+" шт. × "+r.UnitMassKg.ToString("0.###")+" кг");
            doc.Editor.WriteMessage("\nИтого: "+rows.Sum(r=>r.TotalMassKg).ToString("0.###")+" кг");
        });
    }
}
