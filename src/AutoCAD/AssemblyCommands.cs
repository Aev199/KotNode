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
using AcGi=Autodesk.AutoCAD.GraphicsInterface;
using ModelJson=KotNode.Core.Json;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.AssemblyCommands))]

namespace KotNode.AutoCAD
{
    public sealed class AssemblyCommands
    {
        static PaletteSet palette;
        static ConstructorPanel constructorPanel;
        static Document watchedDocument;
        static void Run(string command,Action<Document,Transaction> action)
        {
            Diagnostics.Begin(command);
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            var trace=Diagnostics.EnableLiveTrace(command);
            if(trace!=null)doc.Editor.WriteMessage("\nЖурнал "+command+": "+trace);
            try
            {
                Diagnostics.Step("Проверка пространства модели и единиц");
                if(!doc.Database.TileMode || doc.Database.Insunits!=UnitsValue.Millimeters)
                    throw new InvalidOperationException("Откройте пространство модели (TILEMODE=1), единицы DWG — миллиметры (INSUNITS=4).");
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    if(new[]{"NAEDIT","NADETACH","NADELETE","NATREEDELETE","NAPIPE","NAPLATE","NAPOLYPLATE","NACURVED","NABEAM","NAREBAR","NACOPY","NAARRAY","NAEDITVIEW"}.Contains(command))
                    {
                        Diagnostics.Step("Чтение марки перед командой");
                        var current=AssemblyDrawing.Load(doc.Database,tr);
                        if(current!=null)
                        {
                            Diagnostics.Step("Синхронизация перед командой");
                            var refs=AssemblyDrawing.Synchronize(doc.Database,tr,current);
                            Diagnostics.Step("Обновление старых привязок перед командой");
                            AssemblyViews.UpgradeReferences(doc.Database,tr,current,refs);
                        }
                    }
                    Diagnostics.Step("Выполнение обработчика "+command);
                    action(doc,tr);
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                doc.Editor.Regen();
                RefreshTree(doc);
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nКонструктор: "+ex.Message+"\nЖурнал: "+log);
            }
            finally{Diagnostics.EndLiveTrace();}
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
            Diagnostics.Step("Ожидание выбора детали");
            var p=doc.Editor.GetEntity("\nВыберите деталь марки: ");
            Diagnostics.Step("Выбор детали завершён: "+p.Status);
            if(p.Status!=PromptStatus.OK)return null;
            foreach(var pair in refs)if(pair.Value.ObjectId==p.ObjectId)return pair.Key;
            throw new InvalidOperationException("Выбран неуправляемый объект. Для копирования детали используйте NACOPY.");
        }
        static bool EditForm(PartDefinition part)
        {
            Diagnostics.Step("Создание формы свойств детали");
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
                    var choices=BeamCatalog.Profiles().ToDictionary(x=>x,x=>x+" — "+BeamCatalog.Standard);
                    string selected=choices.ContainsKey(b.Profile)?b.Profile:"30Б2";
                    // Old manually defined profiles require an explicit catalog choice on edit.
                    if(!choices.ContainsKey(b.Profile))
                    {
                        choices.Add("","Выберите профиль (старые размеры не являются сортаментом)");
                        selected="";
                    }
                    f.Choice("Профиль (выборка ГОСТ)","profile",choices,selected);
                    f.Number("Длина, мм","l",b.Length);
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
                    {part.IBeam=BeamCatalog.Create(f.Get("profile"),f.Value("l"));}
                    else if(part.Kind==PartKind.Rebar)
                    {
                        part.Rebar.Diameter=f.Value("d");
                        for(int i=0;i<part.Rebar.BendRadii.Count;i++)part.Rebar.BendRadii[i]=f.Value("bend"+i);
                    }
                    part.Validate();
                };
                Diagnostics.Step("Показ формы свойств детали");
                return AcApp.ShowModalDialog(AcApp.MainWindow.Handle,f,false)==DialogResult.OK;
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
        static List<Point3d> ReadContour(DBObject obj,Transaction tr)
        {
            var points=new List<Point3d>();
            bool closed;
            if(obj is Polyline line)
            {
                closed=line.Closed;
                for(int i=0;i<line.NumberOfVertices;i++)
                {
                    if(Math.Abs(line.GetBulgeAt(i))>1e-12)throw new InvalidOperationException("Дуги внешнего контура пока не поддерживаются.");
                    points.Add(line.GetPoint3dAt(i));
                }
            }
            else if(obj is Polyline2d old)
            {
                closed=old.Closed;
                if(old.PolyType!=Poly2dType.SimplePoly)throw new InvalidOperationException("Сглаженная полилиния не поддерживается.");
                foreach(ObjectId id in old)
                {
                    var vertex=(Vertex2d)tr.GetObject(id,OpenMode.ForRead);
                    if(Math.Abs(vertex.Bulge)>1e-12)throw new InvalidOperationException("Дуги контура пока не поддерживаются.");
                    points.Add(old.VertexPosition(vertex));
                }
            }
            else if(obj is Polyline3d spatial)
            {
                closed=spatial.Closed;
                if(spatial.PolyType!=Poly3dType.SimplePoly)throw new InvalidOperationException("Сглаженная полилиния не поддерживается.");
                foreach(ObjectId id in spatial)points.Add(((PolylineVertex3d)tr.GetObject(id,OpenMode.ForRead)).Position);
            }
            else throw new InvalidOperationException("Контур: полилиния либо замкнутая цепочка отрезков. Выбран тип: "+obj.GetType().Name);
            bool coincident=points.Count>1 && points[0].DistanceTo(points[points.Count-1])<1e-6;
            if(coincident)points.RemoveAt(points.Count-1);
            if((!closed&&!coincident)||points.Count<3)throw new InvalidOperationException("Контур не замкнут или содержит менее трёх вершин.");
            return points;
        }
        static List<Point3d> ReadOuter(ObjectId[] ids,Transaction tr)
        {
            if(ids.Length==1)return ReadContour(tr.GetObject(ids[0],OpenMode.ForRead),tr);
            var segments=ids.Select(id=>tr.GetObject(id,OpenMode.ForRead) as Line).ToList();
            if(segments.Count<3||segments.Any(l=>l==null))throw new InvalidOperationException("Выберите одну полилинию или все отрезки одного замкнутого контура.");
            var points=new List<Point3d>{segments[0].StartPoint,segments[0].EndPoint};segments.RemoveAt(0);
            while(segments.Count>0)
            {
                var tail=points[points.Count-1];
                var next=segments.Where(l=>l.StartPoint.DistanceTo(tail)<1e-6||l.EndPoint.DistanceTo(tail)<1e-6).ToList();
                if(next.Count!=1)throw new InvalidOperationException("Цепочка отрезков имеет разрыв или ветвление.");
                var line=next[0];segments.Remove(line);
                points.Add(line.StartPoint.DistanceTo(tail)<1e-6?line.EndPoint:line.StartPoint);
            }
            if(points[0].DistanceTo(points[points.Count-1])>=1e-6)throw new InvalidOperationException("Цепочка отрезков не замкнута.");
            points.RemoveAt(points.Count-1);return points;
        }
        static List<KotNode.Core.Point2> ReadRing(List<Point3d> points,Matrix3d toLocal)
        {
            return points.Select(point=>{
                var p=point.TransformBy(toLocal);
                if(Math.Abs(p.Z)>1e-6)throw new InvalidOperationException("Контур и отверстия должны лежать в одной плоскости.");
                return new KotNode.Core.Point2(p.X,p.Y);
            }).ToList();
        }
        [CommandMethod("NAPOLYPLATE",CommandFlags.Modal)]
        public void PolygonPlate()=>Run("NAPOLYPLATE",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var ed=doc.Editor;
            var picked=ed.GetSelection(new PromptSelectionOptions {MessageForAdding="\nВыберите внешнюю полилинию или все отрезки контура: "});
            if(picked.Status!=PromptStatus.OK)return;
            var outerIds=picked.Value.GetObjectIds();
            var outer=ReadOuter(outerIds,tr);
            var preferred=Vector3d.ZAxis.TransformBy(ed.CurrentUserCoordinateSystem);
            if(outerIds.Length==1)
            {
                var entity=tr.GetObject(outerIds[0],OpenMode.ForRead);
                if(entity is Polyline poly)preferred=poly.Normal;
                else if(entity is Polyline2d old)preferred=old.Normal;
            }
            var at=ContourPlane.Fit(outer.Select(v=>new Vector3(v.X,v.Y,v.Z)).ToList(),new Vector3(preferred.X,preferred.Y,preferred.Z));
            var origin=new Point3d(at.Origin.X,at.Origin.Y,at.Origin.Z);
            var x=new Vector3d(at.AxisX.X,at.AxisX.Y,at.AxisX.Z);
            var y=new Vector3d(at.AxisY.X,at.AxisY.Y,at.AxisY.Z);var z=x.CrossProduct(y);
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
                    if(outerIds.Contains(id))throw new InvalidOperationException("Внешний контур не может быть отверстием.");
                    var e=tr.GetObject(id,OpenMode.ForRead);
                    if(e is Polyline || e is Polyline2d || e is Polyline3d)p.Holes.Add(ReadRing(ReadContour(e,tr),toLocal));
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
                IBeam=BeamCatalog.Create("30Б2",1000)};
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
        // Source directions are independent of the insertion plane (always WCS XY).
        internal static RigidPlacement ViewFrame(Document doc,AssemblyMark mark,bool section)
        {
            var ed=doc.Editor;
            var options=new PromptKeywordOptions("\nПлоскость представления [Top/Bottom/Front/Back/Right/Left/UCS/ThreePoints] <Top>: ");
            foreach(var keyword in new[]{"Top","Bottom","Front","Back","Right","Left","UCS","ThreePoints"})options.Keywords.Add(keyword);
            options.Keywords.Default="Top";options.AllowNone=true;
            var answer=ed.GetKeywords(options);
            if(answer.Status!=PromptStatus.OK&&answer.Status!=PromptStatus.None)return null;
            if(answer.StringResult=="ThreePoints")
            {
                var points=new List<Vector3>();
                foreach(var prompt in new[]{"\nНачало плоскости: ","\nТочка в направлении вправо (+X): ","\nТочка в верхней полуплоскости (+Y): "})
                {
                    var p=ed.GetPoint(prompt);
                    if(p.Status!=PromptStatus.OK)return null;
                    var w=p.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                    points.Add(new Vector3(w.X,w.Y,w.Z));
                }
                var frame3=RigidPlacement.FromThreePoints(points[0],points[1],points[2]);
                var side=new PromptKeywordOptions("\nСторона наблюдения [Normal/Reverse] <Normal>: "){AllowNone=true};
                side.Keywords.Add("Normal");side.Keywords.Add("Reverse");side.Keywords.Default="Normal";
                var choice=ed.GetKeywords(side);
                if(choice.Status!=PromptStatus.OK&&choice.Status!=PromptStatus.None)return null;
                return choice.StringResult=="Reverse"?RigidPlacement.FromThreePoints(points[0],points[1],points[2],true):frame3;
            }
            Vector3d x=Vector3d.XAxis,y=Vector3d.YAxis;
            switch(answer.StringResult)
            {
                case "Bottom": y=-Vector3d.YAxis;break;
                case "Front": y=Vector3d.ZAxis;break;
                case "Back": x=-Vector3d.XAxis;y=Vector3d.ZAxis;break;
                case "Right": x=Vector3d.YAxis;y=Vector3d.ZAxis;break;
                case "Left": x=-Vector3d.YAxis;y=Vector3d.ZAxis;break;
                case "UCS":
                    x=Vector3d.XAxis.TransformBy(ed.CurrentUserCoordinateSystem);
                    y=Vector3d.YAxis.TransformBy(ed.CurrentUserCoordinateSystem);break;
            }
            Vector3 origin;
            if(section)
            {
                var p=ed.GetPoint("\nУкажите точку секущей плоскости на модели: ");
                if(p.Status!=PromptStatus.OK)return null;
                var w=p.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                origin=new Vector3(w.X,w.Y,w.Z);
            }
            else
            {
                if(mark.Instances.Count==0)throw new InvalidOperationException("В марке нет деталей.");
                origin=ModelJson.Copy(mark.Instances[0].Placement.Origin);
            }
            var frame=new RigidPlacement {Origin=origin,AxisX=new Vector3(x.X,x.Y,x.Z),AxisY=new Vector3(y.X,y.Y,y.Z)};
            frame.Validate();return frame;
        }
        static void AddView(Document doc,Transaction tr,bool section,bool fullCut=false,bool parametric=false)
        {
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            if(refs.Count==0)throw new InvalidOperationException("В марке нет деталей.");
            if(!parametric&&(!section||fullCut))NativeHlr.Require();
            var frame=ViewFrame(doc,m,section);if(frame==null)return;
            var name=Text(doc.Editor,fullCut?"Название разреза (например, 1-1): ":section?"Название сечения (например, 1-1): ":"Название вида (например, Вид А): ");
            if(name==null)return;
            if(string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Укажите название представления.");
            bool hidden=false;
            if(!section||fullCut)
            {
                var option=new PromptKeywordOptions("\nПоказывать невидимые линии? [Yes/No] <No>: ");
                option.Keywords.Add("Yes");option.Keywords.Add("No");option.Keywords.Default="No";option.AllowNone=true;
                var answer=doc.Editor.GetKeywords(option);
                if(answer.Status!=PromptStatus.OK&&answer.Status!=PromptStatus.None)return;
                hidden=answer.StringResult=="Yes";
            }
            bool noHatch=false;
            if(section)
            {
                var option=new PromptKeywordOptions("\nШтриховать сечение? [Yes/No] <Yes>: ");
                option.Keywords.Add("Yes");option.Keywords.Add("No");option.Keywords.Default="Yes";option.AllowNone=true;
                var answer=doc.Editor.GetKeywords(option);
                if(answer.Status!=PromptStatus.OK&&answer.Status!=PromptStatus.None)return;
                noHatch=answer.StringResult=="No";
            }
            var scaleOptions=new PromptDoubleOptions("\nМасштаб оформления 1 <20>: ") {AllowNone=true,AllowNegative=false,AllowZero=false,DefaultValue=20,UseDefaultValue=true};
            var scale=doc.Editor.GetDouble(scaleOptions);
            if(scale.Status!=PromptStatus.OK&&scale.Status!=PromptStatus.None)return;
            double annotationScale=scale.Status==PromptStatus.None?20:scale.Value;
            if(double.IsNaN(annotationScale)||double.IsInfinity(annotationScale)||annotationScale<=0||annotationScale>1000)throw new InvalidOperationException("Масштаб оформления должен быть положительным и не более 1000.");
            var p=doc.Editor.GetPoint("\nТочка размещения плоского изображения (XY мировой системы): ");
            if(p.Status!=PromptStatus.OK)return;
            var location=p.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
            Diagnostics.Step(parametric?"Проекция из параметров деталей":section?"Построение точного сечения":"Проекция тел AutoCAD");
            AssemblyViews.Create(doc.Database,tr,m,refs,new AssemblyViews.ViewData{Name=name,Frame=frame,IsSection=section,FullCut=fullCut,Hidden=hidden,NoHatch=noHatch,AnnotationScale=annotationScale,ParametricPrototype=parametric},location);
            AssemblyDrawing.Save(doc.Database,tr,m);
            doc.Editor.WriteMessage("\nПредставление создано. Геометрия 1:1; масштаб печати задаётся на листе.");
        }
        [CommandMethod("NAIMPORTVIEW",CommandFlags.Modal)]
        public void ImportView()=>Run("NAIMPORTVIEW",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var picked=doc.Editor.GetEntity("\nВыберите готовый плоский блок FLATSHOT (проверьте масштаб и направление): ");
            if(picked.Status!=PromptStatus.OK)return;
            var reference=tr.GetObject(picked.ObjectId,OpenMode.ForRead) as BlockReference;
            if(reference==null)throw new InvalidOperationException("Требуется блок FLATSHOT.");
            var frame=ViewFrame(doc,m,false);if(frame==null)return;
            var name=Text(doc.Editor,"Название импортированного вида: ");if(name==null)return;
            if(string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Укажите название.");
            AssemblyViews.AttachSnapshot(doc.Database,tr,m,reference,new AssemblyViews.ViewData{Name=name,Frame=frame,AnnotationScale=20});
            AssemblyDrawing.Save(doc.Database,tr,m);
            doc.Editor.WriteMessage("\nРучной снимок добавлен в дерево. При изменении модели его нужно перестроить или заменить.");
        });
        [CommandMethod("NAPDEMO",CommandFlags.Modal)]
        public void ParametricDemo()=>Run("NAPDEMO",(doc,tr)=>{
            if(AssemblyDrawing.Load(doc.Database,tr)!=null)
                throw new InvalidOperationException("Для примера откройте новый DWG без марки. Существующая марка не изменена.");
            var mark=new AssemblyMark{Code="ПРОБА-01",Name="Проверка параметрических видов"};
            mark.AddPart(new PartDefinition{Name="Труба 100×10",Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=100,Thickness=10,Length=200}},
                new RigidPlacement{Origin=new Vector3(0,0,-100)});
            mark.AddPart(new PartDefinition{Name="Передняя пластина",Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=60,Width=120,Thickness=10}},
                new RigidPlacement{Origin=new Vector3(0,-60,120)});
            mark.AddPart(new PartDefinition{Name="Пластина внутри полости",Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=10,Width=10,Thickness=10}},
                new RigidPlacement{Origin=new Vector3(-5,-5,-40)});
            var refs=new Dictionary<Guid,BlockReference>();
            foreach(var instance in mark.Instances)
            {
                var definition=mark.Parts.Single(p=>p.Id==instance.PartId);
                var geometry=AssemblyDrawing.Geometry(doc.Database,tr,definition);
                var reference=AssemblyDrawing.Insert(doc.Database,tr,mark,instance,geometry);
                refs.Add(instance.Id,reference);
            }
            var frames=new[]{new RigidPlacement(),new RigidPlacement{AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)}};
            for(int i=0;i<4;i++)
            {
                string name=new[]{"Вид вдоль трубы","Вид сбоку","Поперечный разрез","Продольный разрез"}[i];
                double x=250+(i%2)*250,y=i<2?0:-300;
                AssemblyViews.Create(doc.Database,tr,mark,refs,new AssemblyViews.ViewData{
                    Name=name,Frame=frames[i%2],IsSection=i>=2,FullCut=i>=2,Hidden=true,AnnotationScale=1,ParametricPrototype=true},new Point3d(x,y,0));
                var label=new DBText{TextString=name,Height=5,Position=new Point3d(x-50,y-130,0)};
                label.SetDatabaseDefaults(doc.Database);
                AssemblyDrawing.ModelSpace(doc.Database,tr,OpenMode.ForWrite).AppendEntity(label);tr.AddNewlyCreatedDBObject(label,true);
            }
            AssemblyDrawing.Save(doc.Database,tr,mark);
            doc.Editor.WriteMessage("\nСозданы труба, две пластины, два вида и два разреза. Выполните _ZOOM _Extents. Для обновления — NAUPDATEVIEWS.");
        });
        [CommandMethod("NAPVIEW",CommandFlags.Modal)]
        public void ParametricView()=>Run("NAPVIEW",(doc,tr)=>AddView(doc,tr,false,false,true));
        [CommandMethod("NAPSECTION",CommandFlags.Modal)]
        public void ParametricSection()=>Run("NAPSECTION",(doc,tr)=>AddView(doc,tr,true,true,true));
        [CommandMethod("NAVIEW",CommandFlags.Modal)]
        public void View()=>Run("NAVIEW",(doc,tr)=>AddView(doc,tr,false,false,true));
        [CommandMethod("NASECTION",CommandFlags.Modal)]
        public void SectionView()=>Run("NASECTION",(doc,tr)=>AddView(doc,tr,true,true,true));
        [CommandMethod("NAFLATPREP",CommandFlags.Modal)]
        public void FlatPrep()=>Run("NAFLATPREP",(doc,tr)=>{
            var options=new PromptKeywordOptions("\nПодготовить [View/Section] <View>: "){AllowNone=true};
            options.Keywords.Add("View");options.Keywords.Add("Section");options.Keywords.Default="View";
            var result=doc.Editor.GetKeywords(options);
            if(result.Status!=PromptStatus.OK&&result.Status!=PromptStatus.None)return;
            FlatshotWorkflow.Prepare(doc,tr,result.StringResult=="Section");
        });
        [CommandMethod("NAFLATFINISH",CommandFlags.Modal)]
        public void FlatFinish()=>Run("NAFLATFINISH",(doc,tr)=>FlatshotWorkflow.Finish(doc,tr));
        [CommandMethod("NACROSS",CommandFlags.Modal)]
        public void CrossView()=>Run("NACROSS",(doc,tr)=>AddView(doc,tr,true));
        [CommandMethod("NAHLRSTATUS",CommandFlags.Modal)]
        public void HlrStatus()=>Run("NAHLRSTATUS",(doc,tr)=>
            doc.Editor.WriteMessage("\nВиды и разрезы строятся из параметров марки: NAVIEW и NASECTION."));
        [CommandMethod("NAEXPORTCUT",CommandFlags.Modal)]
        public void ExportCut()=>Run("NAEXPORTCUT",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var frame=ViewFrame(doc,m,true);if(frame==null)return;
            using(var dialog=new System.Windows.Forms.SaveFileDialog{Filter="AutoCAD DWG (*.dwg)|*.dwg",DefaultExt="dwg",AddExtension=true,OverwritePrompt=true,FileName="KotNode-cut.dwg"})
            {
                if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
                AssemblyViews.ExportCut(tr,refs,frame,dialog.FileName);
                doc.Editor.WriteMessage("\nСоздан отдельный DWG с оставшейся за плоскостью моделью. Откройте его, установите вид Top и выполните FLATSHOT.");
            }
        });
        [CommandMethod("NAEDITVIEW",CommandFlags.Modal)]
        public void EditView()=>Run("NAEDITVIEW",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var picked=doc.Editor.GetEntity("\nВыберите вид или сечение: ");
            if(picked.Status!=PromptStatus.OK)return;
            var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Reference.ObjectId==picked.ObjectId);
            if(entry==null)throw new InvalidOperationException("Выберите представление марки.");
            var data=entry.Data;
            if(data.ManualSnapshot)throw new InvalidOperationException("Старый снимок не связан с параметрами. Создайте новый вид командой NAVIEW или NASECTION.");
            if(string.IsNullOrEmpty(data.GeneratedChildHandle))
                throw new InvalidOperationException("Сначала переведите старый вид командой NAMIGRATEVIEW.");
            using(var form=new Fields("Свойства представления"))
            {
                form.TextField("Название","name",data.Name);
                form.Number("Масштаб оформления 1:","scale",data.AnnotationScale>0?data.AnnotationScale:20);
                if(data.IsSection)form.Check("Штриховать сечение","hatch",!data.NoHatch);
                if(!data.IsSection||data.FullCut)form.Check("Невидимые линии","hidden",data.Hidden);
                form.Check("Задать плоскость заново","plane",false);
                form.Check("Привязать плоскость к выбранной детали (заново)","anchor",false);
                form.Check("Снять привязку к детали","detach",false);
                form.ValidateFields=()=>{
                    if(form.Checked("anchor")&&form.Checked("detach"))throw new InvalidOperationException("Выберите только одну операцию с привязкой.");
                    var name=form.Get("name").Trim();var scale=form.Value("scale");
                    if(string.IsNullOrWhiteSpace(name)||double.IsNaN(scale)||double.IsInfinity(scale)||scale<=0||scale>1000)
                        throw new InvalidOperationException("Введите название и масштаб от 0 до 1000.");
                };
                if(AcApp.ShowModalDialog(form)!=DialogResult.OK)return;
                data.Name=form.Get("name").Trim();data.AnnotationScale=form.Value("scale");
                if(data.IsSection)data.NoHatch=!form.Checked("hatch");
                if(!data.IsSection||data.FullCut)data.Hidden=form.Checked("hidden");
                if(form.Checked("plane")&&((data.LineOverrides?.Count??0)>0||(data.Welds?.Count??0)>0))
                    throw new InvalidOperationException("Сначала снимите корректировки линий (NALINERESET) и швов (NAWELDRESET), затем изменяйте плоскость.");
                if(form.Checked("plane")||form.Checked("anchor")||form.Checked("detach"))
                {
                    var oldAnchor=m.Instances.SingleOrDefault(i=>data.FrameAnchorId.HasValue&&i.Id==data.FrameAnchorId.Value);
                    var frame=oldAnchor!=null?AssemblyViews.ResolveFrame(m,data):data.Frame;
                    if(form.Checked("plane"))
                    {
                        frame=ViewFrame(doc,m,data.IsSection);if(frame==null)return;
                    }
                    if(form.Checked("detach")) {data.FrameAnchorId=null;data.RelativeFrame=null;}
                    else if(form.Checked("anchor"))
                    {
                        var id=Select(doc,refs);if(!id.HasValue)return;
                        data.FrameAnchorId=id;oldAnchor=m.Instances.Single(i=>i.Id==id.Value);
                    }
                    if(data.FrameAnchorId.HasValue)
                    {
                        if(oldAnchor==null)throw new InvalidOperationException("Выберите новую деталь привязки или снимите привязку.");
                        data.RelativeFrame=frame.RelativeTo(oldAnchor.Placement);
                    }
                    data.Frame=frame;
                }
            }
            AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
            AssemblyDrawing.Save(doc.Database,tr,m);
        });
        sealed class EditableViewCache
        {
            public Guid Id;
            public string Name;
            public bool ModelStale;
            public double AnnotationScale;
            public Matrix3d ToWorld;
            public Matrix3d ToLocal;
            public List<CurveChain> Chains;
            public List<ParametricProjection.Curve2> RawCurves;
        }
        sealed class LineEditStep
        {
            public List<ViewLineOverride> Rules=new List<ViewLineOverride>();
            public List<AcGi.Drawable> Transients=new List<AcGi.Drawable>();
        }
        sealed class WeldEditStep
        {
            public List<ViewWeldOverlay> Welds=new List<ViewWeldOverlay>();
            public List<AcGi.Drawable> Transients=new List<AcGi.Drawable>();
        }
        static string ModeLabel(ViewLineMode mode)=>mode==ViewLineMode.Visible?"Видимая":mode==ViewLineMode.Hidden?"Скрытая":mode==ViewLineMode.Suppressed?"Удалить":"Авто";
        static string WeldStyleLabel(ViewWeldStyle style)=>style==ViewWeldStyle.Site?"Монтажный (×)":style==ViewWeldStyle.Hidden?"Невидимый":"Заводской (|)";
        static string ScopeLabel(bool whole,bool partial)=>whole?"Кромка":partial?"Участок":"Сегмент";
        static short ModeColor(ViewLineMode mode)=>mode==ViewLineMode.Visible?(short)3:mode==ViewLineMode.Hidden?(short)2:mode==ViewLineMode.Suppressed?(short)1:(short)4;
        static List<AcGi.Drawable> AddWeldPreviewTransients(IEnumerable<WeldStroke> strokes,Matrix3d toWorld)
        {
            var added=new List<AcGi.Drawable>();
            var tm=AcGi.TransientManager.CurrentTransientManager;
            if(tm==null)return added;
            var vps=new IntegerCollection();
            foreach(var s in strokes)
            {
                try
                {
                    var d=new Line(new Point3d(s.X0,s.Y0,0),new Point3d(s.X1,s.Y1,0));
                    d.TransformBy(toWorld);
                    d.ColorIndex=6; // Magenta
                    d.LineWeight=LineWeight.LineWeight035;
                    tm.AddTransient(d,AcGi.TransientDrawingMode.DirectTopmost,128,vps);
                    added.Add(d);
                }
                catch { }
            }
            return added;
        }
        static List<AcGi.Drawable> AddPreviewTransients(IEnumerable<ParametricProjection.Curve2> curves,Matrix3d toWorld,ViewLineMode mode)
        {
            var added=new List<AcGi.Drawable>();
            var tm=AcGi.TransientManager.CurrentTransientManager;
            if(tm==null)return added;
            short color=ModeColor(mode);
            var vps=new IntegerCollection();
            foreach(var c in curves)
            {
                try
                {
                    var d=ParametricViewDrawing.CreateEntity(c);
                    d.TransformBy(toWorld);
                    d.ColorIndex=color;
                    d.LineWeight=LineWeight.LineWeight050;
                    tm.AddTransient(d,AcGi.TransientDrawingMode.DirectTopmost,128,vps);
                    added.Add(d);
                }
                catch { }
            }
            return added;
        }
        static void ClearTransients(List<AcGi.Drawable> drawables)
        {
            var tm=AcGi.TransientManager.CurrentTransientManager;
            var vps=new IntegerCollection();
            foreach(var d in drawables)
            {
                try {tm?.EraseTransient(d,vps);}catch { }
                try {d.Dispose();}catch { }
            }
            drawables.Clear();
        }
        static double PickTolerance()
        {
            try
            {
                double viewSize=Convert.ToDouble(AcApp.GetSystemVariable("VIEWSIZE"));
                if(viewSize>0&&!double.IsNaN(viewSize)&&!double.IsInfinity(viewSize))
                    return Math.Max(2.0,viewSize*0.018);
            }
            catch { }
            return 15.0;
        }
        [CommandMethod("NALINEEDIT",CommandFlags.Modal)]
        public void EditLines()
        {
            Diagnostics.Begin("NALINEEDIT");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            var ed=doc.Editor;
            var steps=new List<LineEditStep>();
            var activeTemp=new List<AcGi.Drawable>();
            try
            {
                if(!doc.Database.TileMode||doc.Database.Insunits!=UnitsValue.Millimeters)
                    throw new InvalidOperationException("Откройте пространство модели (TILEMODE=1), единицы DWG — миллиметры (INSUNITS=4).");

                // Phase 1: short read/auto-upgrade transaction (closed BEFORE interactive prompts).
                var cache=new List<EditableViewCache>();
                bool upgradedAny=false;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=Need(doc,tr);
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    var entries=AssemblyViews.Find(doc.Database,tr,m);
                    var signature=AssemblyViews.Signature(m);
                    foreach(var entry in entries)
                    {
                        if(entry.Data.ManualSnapshot||string.IsNullOrEmpty(entry.Data.GeneratedChildHandle))continue;
                        bool modelStale=entry.Data.SourceHash!=signature;
                        if(!modelStale&&entry.Data.GeneratorRevision!=AssemblyViews.Revision)
                        {
                            try
                            {
                                AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
                                upgradedAny=true;
                            }
                            catch(System.Exception ex)
                            {
                                ed.WriteMessage("\nВид «"+entry.Data.Name+"» не обновлён автоматически: "+ex.Message);
                                continue;
                            }
                        }
                        var world=entry.Reference.BlockTransform;
                        var chains=modelStale?new List<CurveChain>():ParametricViewDrawing.ComputeChains(m,entry.Data);
                        cache.Add(new EditableViewCache
                        {
                            Id=entry.Data.Id,Name=entry.Data.Name,ModelStale=modelStale,
                            ToWorld=world,ToLocal=world.Inverse(),Chains=chains
                        });
                    }
                    if(upgradedAny)AssemblyDrawing.Save(doc.Database,tr,m);
                    tr.Commit();
                }
                if(upgradedAny)
                {
                    doc.TransactionManager.QueueForGraphicsFlush();
                    ed.Regen();
                    RefreshTree(doc);
                }
                if(cache.Count==0)
                {
                    ed.WriteMessage("\nНет параметрических видов нового формата. Создайте вид (NAVIEW/NASECTION) или переведите старый (NAMIGRATEVIEW).");
                    return;
                }

                // Phase 2: transaction-free geometric picking with live TransientManager highlight.
                var action=new PromptKeywordOptions("\nРежим линий [Visible/Hidden/Suppress/Auto] <Hidden>: "){AllowNone=true};
                foreach(var k in new[]{"Visible","Hidden","Suppress","Auto"})action.Keywords.Add(k);
                action.Keywords.Default="Hidden";
                var answer=ed.GetKeywords(action);
                if(answer.Status!=PromptStatus.OK&&answer.Status!=PromptStatus.None)return;
                var mode=answer.StringResult=="Visible"?ViewLineMode.Visible:answer.StringResult=="Suppress"?ViewLineMode.Suppressed:answer.StringResult=="Auto"?ViewLineMode.Auto:ViewLineMode.Hidden;

                var scope=new PromptKeywordOptions("\nОбласть [Segment/Fragment/Edge] <Segment>: "){AllowNone=true};
                foreach(var k in new[]{"Segment","Fragment","Edge"})scope.Keywords.Add(k);
                scope.Keywords.Default="Segment";
                var chosen=ed.GetKeywords(scope);
                if(chosen.Status!=PromptStatus.OK&&chosen.Status!=PromptStatus.None)return;
                bool whole=chosen.StringResult=="Edge",partial=chosen.StringResult=="Fragment";

                var batch=Guid.NewGuid();
                EditableViewCache selected=null;
                while(true)
                {
                    var prompt="\n["+ModeLabel(mode)+" | "+ScopeLabel(whole,partial)+"] Укажите линию вида (Enter — применить, Esc — отменить) [Visible/Hidden/Suppress/Auto/Segment/Fragment/Edge/Undo]: ";
                    var opt=new PromptPointOptions(prompt){AllowNone=true};
                    foreach(var k in new[]{"Visible","Hidden","Suppress","Auto","Segment","Fragment","Edge","Undo"})opt.Keywords.Add(k);
                    var hitPt=ed.GetPoint(opt);
                    if(hitPt.Status==PromptStatus.None)break;
                    if(hitPt.Status==PromptStatus.Keyword)
                    {
                        switch(hitPt.StringResult)
                        {
                            case "Visible": mode=ViewLineMode.Visible;break;
                            case "Hidden": mode=ViewLineMode.Hidden;break;
                            case "Suppress": mode=ViewLineMode.Suppressed;break;
                            case "Auto": mode=ViewLineMode.Auto;break;
                            case "Segment": whole=false;partial=false;break;
                            case "Fragment": whole=false;partial=true;break;
                            case "Edge": whole=true;partial=false;break;
                            case "Undo":
                                if(steps.Count>0)
                                {
                                    var last=steps[steps.Count-1];
                                    ClearTransients(last.Transients);
                                    steps.RemoveAt(steps.Count-1);
                                    if(steps.Count==0)selected=null;
                                    ed.WriteMessage("\nПоследний выбор отменён. Подготовлено шагов: "+steps.Count);
                                }
                                else ed.WriteMessage("\nНет шагов для отмены.");
                                break;
                        }
                        continue;
                    }
                    if(hitPt.Status!=PromptStatus.OK)return;

                    var worldPt=hitPt.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                    double tol=PickTolerance();
                    EditableViewCache hitView=null;
                    PickHit bestHit=null;
                    foreach(var v in cache)
                    {
                        if(selected!=null&&v.Id!=selected.Id)continue;
                        if(v.ModelStale)continue;
                        var local=worldPt.TransformBy(v.ToLocal);
                        var candidate=ViewLineOverrides.Pick(v.Chains,local.X,local.Y,tol);
                        if(candidate!=null&&(bestHit==null||candidate.Distance<bestHit.Distance))
                        {
                            bestHit=candidate;hitView=v;
                        }
                    }
                    if(bestHit==null)
                    {
                        if(selected==null&&cache.Any(v=>v.ModelStale))
                            ed.WriteMessage("\nЛиния не найдена. Если нужный вид устарел после изменения модели, сначала выполните NAUPDATEVIEW.");
                        else if(selected!=null)
                            ed.WriteMessage("\nРядом с точкой нет линий вида «"+selected.Name+"» (за одну операцию редактируется один вид).");
                        else
                            ed.WriteMessage("\nРядом с указанной точкой не найдено линий параметрического вида. Кликните ближе к линии.");
                        continue;
                    }

                    var step=new LineEditStep();
                    var previewCurves=new List<ParametricProjection.Curve2>();
                    if(partial)
                    {
                        activeTemp.AddRange(AddPreviewTransients(bestHit.Chain.Segments,hitView.ToWorld,mode));
                        var first=ed.GetPoint("\nПервая граница участка на выбранной линии: ");
                        if(first.Status!=PromptStatus.OK){ClearTransients(activeTemp);return;}
                        var second=ed.GetPoint(new PromptPointOptions("\nВторая граница участка на выбранной линии: "){UseBasePoint=true,BasePoint=first.Value});
                        if(second.Status!=PromptStatus.OK){ClearTransients(activeTemp);return;}
                        var u=first.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(hitView.ToLocal);
                        var v=second.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(hitView.ToLocal);
                        bool longer=false;
                        if(bestHit.Chain.IsFullLoop)
                        {
                            var arc=new PromptKeywordOptions("\nДуга между точками [Short/Long] <Short>: "){AllowNone=true};
                            arc.Keywords.Add("Short");arc.Keywords.Add("Long");arc.Keywords.Default="Short";
                            var pick=ed.GetKeywords(arc);
                            if(pick.Status!=PromptStatus.OK&&pick.Status!=PromptStatus.None){ClearTransients(activeTemp);return;}
                            longer=pick.StringResult=="Long";
                        }
                        ClearTransients(activeTemp);
                        try
                        {
                            foreach(var ch in bestHit.Coincident)
                            {
                                var a=ViewLineOverrides.ParameterAt(ch,u.X,u.Y);
                                var b=ViewLineOverrides.ParameterAt(ch,v.X,v.Y);
                                step.Rules.AddRange(ViewLineOverrides.CreateRangeForChain(ch,mode,a,b,longer,batch,ch==bestHit.Chain?previewCurves:null));
                            }
                        }
                        catch(System.IO.InvalidDataException ex){ed.WriteMessage("\n"+ex.Message);continue;}
                    }
                    else if(whole)
                    {
                        var targets=bestHit.Coincident;
                        foreach(var ch in targets)
                            step.Rules.AddRange(ViewLineOverrides.CreateForChain(ch,mode,true,batch));
                        foreach(var c in hitView.Chains.Where(x=>targets.Any(t=>t.InstanceId==x.InstanceId&&t.SourceKey==x.SourceKey)))
                            previewCurves.AddRange(c.Segments);
                    }
                    else
                    {
                        foreach(var ch in bestHit.Coincident)
                            step.Rules.AddRange(ViewLineOverrides.CreateForChain(ch,mode,false,batch,ch==bestHit.Chain?previewCurves:null));
                    }
                    step.Transients.AddRange(AddPreviewTransients(previewCurves,hitView.ToWorld,mode));
                    steps.Add(step);
                    selected=hitView;
                    int totalRules=steps.Sum(s=>s.Rules.Count);
                    string overlapNote=bestHit.Coincident.Count>1?" (совпало линий: "+bestHit.Coincident.Count+")":"";
                    ed.WriteMessage("\n["+ModeLabel(mode)+" | "+ScopeLabel(whole,partial)+"] Добавлено"+overlapNote+". Всего корректировок: "+totalRules);
                }

                var allRules=steps.SelectMany(s=>s.Rules).ToList();
                if(selected==null||allRules.Count==0)return;

                // Phase 3: atomic commit transaction.
                int unmatched;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=Need(doc,tr);
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Data.Id==selected.Id);
                    if(entry==null)throw new InvalidOperationException("Представление не найдено.");
                    if(entry.Data.LineOverrides==null)entry.Data.LineOverrides=new List<ViewLineOverride>();
                    entry.Data.LineOverrides.AddRange(allRules);
                    AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
                    AssemblyDrawing.Save(doc.Database,tr,m);
                    unmatched=entry.Data.UnmatchedLineOverrides;
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                ed.Regen();
                RefreshTree(doc);
                ed.WriteMessage("\nКорректировки применены ("+allRules.Count+"); без соответствия: "+unmatched);
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                ed.WriteMessage("\nКонструктор: "+ex.Message+"\nЖурнал: "+log);
            }
            finally
            {
                ClearTransients(activeTemp);
                foreach(var s in steps)ClearTransients(s.Transients);
            }
        }
        [CommandMethod("NALINERESET",CommandFlags.Modal)]
        public void ResetLines()=>Run("NALINERESET",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var ed=doc.Editor;var picked=ed.GetEntity("\nВыберите вид для восстановления линий: ");
            if(picked.Status!=PromptStatus.OK)return;
            var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Reference.ObjectId==picked.ObjectId);
            if(entry==null||entry.Data.ManualSnapshot)throw new InvalidOperationException("Выберите параметрический вид.");
            var journal=entry.Data.LineOverrides;
            if(journal==null||journal.Count==0){ed.WriteMessage("\nНет ручных корректировок.");return;}
            var choice=new PromptKeywordOptions("\nВосстановить [Last/All/Number] <Last>: "){AllowNone=true};
            foreach(var k in new[]{"Last","All","Number"})choice.Keywords.Add(k);
            choice.Keywords.Default="Last";
            var answer=ed.GetKeywords(choice);
            if(answer.Status!=PromptStatus.OK&&answer.Status!=PromptStatus.None)return;
            if(answer.StringResult=="All")journal.Clear();
            else if(answer.StringResult=="Number")
            {
                for(int i=0;i<journal.Count;i++)
                    ed.WriteMessage("\n"+(i+1)+": "+journal[i].Mode+" "+journal[i].SourceKey+" ["+journal[i].From.ToString("0.###")+"–"+journal[i].To.ToString("0.###")+"]");
                var opt=new PromptIntegerOptions("\nНомер корректировки: "){LowerLimit=1,UpperLimit=journal.Count};
                var number=ed.GetInteger(opt);if(number.Status!=PromptStatus.OK)return;
                journal.RemoveAt(number.Value-1);
            }
            else
            {
                var batch=journal.Last().BatchId;
                journal.RemoveAll(r=>r.BatchId==batch);
            }
            AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
            AssemblyDrawing.Save(doc.Database,tr,m);
            ed.WriteMessage("\nОсталось корректировок: "+journal.Count);
        });
        [CommandMethod("NAWELD",CommandFlags.Modal)]
        public void EditWelds()
        {
            Diagnostics.Begin("NAWELD");
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            var ed=doc.Editor;
            var steps=new List<WeldEditStep>();
            var activeTemp=new List<AcGi.Drawable>();
            try
            {
                if(!doc.Database.TileMode||doc.Database.Insunits!=UnitsValue.Millimeters)
                    throw new InvalidOperationException("Откройте пространство модели (TILEMODE=1), единицы DWG — миллиметры (INSUNITS=4).");

                var cache=new List<EditableViewCache>();
                bool upgradedAny=false;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=Need(doc,tr);
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    var entries=AssemblyViews.Find(doc.Database,tr,m);
                    var signature=AssemblyViews.Signature(m);
                    foreach(var entry in entries)
                    {
                        if(entry.Data.ManualSnapshot||string.IsNullOrEmpty(entry.Data.GeneratedChildHandle))continue;
                        bool modelStale=entry.Data.SourceHash!=signature;
                        if(!modelStale&&entry.Data.GeneratorRevision!=AssemblyViews.Revision)
                        {
                            try
                            {
                                AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
                                upgradedAny=true;
                            }
                            catch(System.Exception ex)
                            {
                                ed.WriteMessage("\nВид «"+entry.Data.Name+"» не обновлён автоматически: "+ex.Message);
                                continue;
                            }
                        }
                        var world=entry.Reference.BlockTransform;
                        var raw=modelStale?new List<ParametricProjection.Curve2>():ParametricViewDrawing.ComputeRawCurves(m,entry.Data);
                        var chains=modelStale?new List<CurveChain>():ParametricViewDrawing.ComputeChains(m,entry.Data);
                        cache.Add(new EditableViewCache
                        {
                            Id=entry.Data.Id,Name=entry.Data.Name,ModelStale=modelStale,
                            AnnotationScale=entry.Data.AnnotationScale>0?entry.Data.AnnotationScale:20,
                            ToWorld=world,ToLocal=world.Inverse(),Chains=chains,RawCurves=raw
                        });
                    }
                    if(upgradedAny)AssemblyDrawing.Save(doc.Database,tr,m);
                    tr.Commit();
                }
                if(upgradedAny)
                {
                    doc.TransactionManager.QueueForGraphicsFlush();
                    ed.Regen();
                    RefreshTree(doc);
                }
                if(cache.Count==0)
                {
                    ed.WriteMessage("\nНет параметрических видов нового формата. Создайте вид (NAVIEW/NASECTION) или переведите старый (NAMIGRATEVIEW).");
                    return;
                }

                var styleOpt=new PromptKeywordOptions("\nТип шва [Site/Factory/Hidden] <Site>: "){AllowNone=true};
                foreach(var k in new[]{"Site","Factory","Hidden"})styleOpt.Keywords.Add(k);
                styleOpt.Keywords.Default="Site";
                var styleAns=ed.GetKeywords(styleOpt);
                if(styleAns.Status!=PromptStatus.OK&&styleAns.Status!=PromptStatus.None)return;
                var style=styleAns.StringResult=="Factory"?ViewWeldStyle.Factory:styleAns.StringResult=="Hidden"?ViewWeldStyle.Hidden:ViewWeldStyle.Site;

                var scopeOpt=new PromptKeywordOptions("\nОбласть [Segment/Fragment/Edge] <Segment>: "){AllowNone=true};
                foreach(var k in new[]{"Segment","Fragment","Edge"})scopeOpt.Keywords.Add(k);
                scopeOpt.Keywords.Default="Segment";
                var scopeAns=ed.GetKeywords(scopeOpt);
                if(scopeAns.Status!=PromptStatus.OK&&scopeAns.Status!=PromptStatus.None)return;
                bool whole=scopeAns.StringResult=="Edge",partial=scopeAns.StringResult=="Fragment";

                var legOpt=new PromptDoubleOptions("\nКатет шва k, мм <6>: "){AllowNone=true,AllowNegative=false,AllowZero=false,DefaultValue=6,UseDefaultValue=true};
                var legAns=ed.GetDouble(legOpt);
                if(legAns.Status!=PromptStatus.OK&&legAns.Status!=PromptStatus.None)return;
                double legMm=legAns.Status==PromptStatus.None?6:legAns.Value;
                if(double.IsNaN(legMm)||double.IsInfinity(legMm)||legMm<=0||legMm>100)
                    throw new InvalidOperationException("Катет шва должен быть от 0 до 100 мм.");

                var batch=Guid.NewGuid();
                EditableViewCache selected=null;
                while(true)
                {
                    var prompt="\n["+WeldStyleLabel(style)+" k="+legMm.ToString("0.#")+" | "+ScopeLabel(whole,partial)+"] Укажите кромку вида (Enter — применить, Esc — отменить) [Site/Factory/Hidden/Segment/Fragment/Edge/Leg/Undo]: ";
                    var opt=new PromptPointOptions(prompt){AllowNone=true};
                    foreach(var k in new[]{"Site","Factory","Hidden","Segment","Fragment","Edge","Leg","Undo"})opt.Keywords.Add(k);
                    var hitPt=ed.GetPoint(opt);
                    if(hitPt.Status==PromptStatus.None)break;
                    if(hitPt.Status==PromptStatus.Keyword)
                    {
                        switch(hitPt.StringResult)
                        {
                            case "Site": style=ViewWeldStyle.Site;break;
                            case "Factory": style=ViewWeldStyle.Factory;break;
                            case "Hidden": style=ViewWeldStyle.Hidden;break;
                            case "Segment": whole=false;partial=false;break;
                            case "Fragment": whole=false;partial=true;break;
                            case "Edge": whole=true;partial=false;break;
                            case "Leg":
                                var lo=new PromptDoubleOptions("\nНовый катет шва k, мм <"+legMm.ToString("0.#")+">: "){AllowNone=true,AllowNegative=false,AllowZero=false,DefaultValue=legMm,UseDefaultValue=true};
                                var la=ed.GetDouble(lo);
                                if(la.Status==PromptStatus.OK&&la.Value>0&&la.Value<=100)legMm=la.Value;
                                break;
                            case "Undo":
                                if(steps.Count>0)
                                {
                                    var last=steps[steps.Count-1];
                                    ClearTransients(last.Transients);
                                    steps.RemoveAt(steps.Count-1);
                                    if(steps.Count==0)selected=null;
                                    ed.WriteMessage("\nПоследний шов отменён. Подготовлено шагов: "+steps.Count);
                                }
                                else ed.WriteMessage("\nНет швов для отмены.");
                                break;
                        }
                        continue;
                    }
                    if(hitPt.Status!=PromptStatus.OK)return;

                    var worldPt=hitPt.Value.TransformBy(ed.CurrentUserCoordinateSystem);
                    double tol=PickTolerance();
                    EditableViewCache hitView=null;
                    PickHit bestHit=null;
                    foreach(var v in cache)
                    {
                        if(selected!=null&&v.Id!=selected.Id)continue;
                        if(v.ModelStale)continue;
                        var local=worldPt.TransformBy(v.ToLocal);
                        var candidate=ViewLineOverrides.Pick(v.Chains,local.X,local.Y,tol);
                        if(candidate!=null&&(bestHit==null||candidate.Distance<bestHit.Distance))
                        {
                            bestHit=candidate;hitView=v;
                        }
                    }
                    if(bestHit==null)
                    {
                        if(selected==null&&cache.Any(v=>v.ModelStale))
                            ed.WriteMessage("\nЛиния не найдена. Если нужный вид устарел, сначала выполните NAUPDATEVIEW.");
                        else
                            ed.WriteMessage("\nРядом с указанной точкой не найдено линий вида.");
                        continue;
                    }

                    activeTemp.AddRange(AddPreviewTransients(bestHit.Chain.Segments,hitView.ToWorld,ViewLineMode.Auto));
                    double rangeA=0,rangeB=1;bool longer=false;
                    if(partial)
                    {
                        var first=ed.GetPoint("\nПервая граница шва на линии: ");
                        if(first.Status!=PromptStatus.OK){ClearTransients(activeTemp);return;}
                        var second=ed.GetPoint(new PromptPointOptions("\nВторая граница шва на линии: "){UseBasePoint=true,BasePoint=first.Value});
                        if(second.Status!=PromptStatus.OK){ClearTransients(activeTemp);return;}
                        var u=first.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(hitView.ToLocal);
                        var v=second.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(hitView.ToLocal);
                        if(bestHit.Chain.IsFullLoop)
                        {
                            var arc=new PromptKeywordOptions("\nДуга между точками [Short/Long] <Short>: "){AllowNone=true};
                            arc.Keywords.Add("Short");arc.Keywords.Add("Long");arc.Keywords.Default="Short";
                            var pick=ed.GetKeywords(arc);
                            if(pick.Status!=PromptStatus.OK&&pick.Status!=PromptStatus.None){ClearTransients(activeTemp);return;}
                            longer=pick.StringResult=="Long";
                        }
                        try
                        {
                            rangeA=ViewLineOverrides.ParameterAt(bestHit.Chain,u.X,u.Y);
                            rangeB=ViewLineOverrides.ParameterAt(bestHit.Chain,v.X,v.Y);
                        }
                        catch(System.IO.InvalidDataException ex){ClearTransients(activeTemp);ed.WriteMessage("\n"+ex.Message);continue;}
                    }

                    var sideOpt=new PromptPointOptions("\nУкажите сторону шва на чертеже (Enter — по обе стороны) [Both]: "){AllowNone=true};
                    sideOpt.Keywords.Add("Both");
                    var sideAns=ed.GetPoint(sideOpt);
                    ClearTransients(activeTemp);
                    ViewWeldSide side;
                    if(sideAns.Status==PromptStatus.None||(sideAns.Status==PromptStatus.Keyword&&sideAns.StringResult=="Both"))
                        side=ViewWeldSide.Both;
                    else if(sideAns.Status==PromptStatus.OK)
                    {
                        var sp=sideAns.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(hitView.ToLocal);
                        side=ViewWelds.SideFromPoint(bestHit.Chain,sp.X,sp.Y);
                    }
                    else return;

                    var step=new WeldEditStep();
                    try
                    {
                        if(partial)
                            step.Welds.AddRange(ViewWelds.CreateRangeForChain(bestHit.Chain,style,side,legMm,rangeA,rangeB,longer,batch));
                        else
                            step.Welds.AddRange(ViewWelds.CreateForChain(bestHit.Chain,style,side,legMm,whole,batch));
                    }
                    catch(System.IO.InvalidDataException ex){ed.WriteMessage("\n"+ex.Message);continue;}

                    var previewStrokes=ViewWelds.Build(hitView.RawCurves,step.Welds,hitView.AnnotationScale,out _);
                    step.Transients.AddRange(AddWeldPreviewTransients(previewStrokes,hitView.ToWorld));
                    steps.Add(step);
                    selected=hitView;
                    ed.WriteMessage("\n["+WeldStyleLabel(style)+" k="+legMm.ToString("0.#")+"] Добавлен шов. Всего швов в серии: "+steps.Sum(s=>s.Welds.Count));
                }

                var allWelds=steps.SelectMany(s=>s.Welds).ToList();
                if(selected==null||allWelds.Count==0)return;

                int unmatched;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=Need(doc,tr);
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    WeldContext.RegisterExisting(doc.Database,tr,m);
                    var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Data.Id==selected.Id);
                    if(entry==null)throw new InvalidOperationException("Представление не найдено.");
                    if(entry.Data.Welds==null)entry.Data.Welds=new List<ViewWeldOverlay>();
                    foreach(var w in allWelds)WeldCatalog.Register(m,w);
                    entry.Data.Welds.AddRange(allWelds);
                    AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
                    AssemblyViews.Update(doc.Database,tr,m,refs);
                    AssemblyDrawing.Save(doc.Database,tr,m);
                    unmatched=entry.Data.UnmatchedWelds;
                    tr.Commit();
                }
                doc.TransactionManager.QueueForGraphicsFlush();
                ed.Regen();
                RefreshTree(doc);
                ed.WriteMessage("\nСварные швы сохранены ("+allWelds.Count+"); без соответствия: "+unmatched);
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                ed.WriteMessage("\nКонструктор: "+ex.Message+"\nЖурнал: "+log);
            }
            finally
            {
                ClearTransients(activeTemp);
                foreach(var s in steps)ClearTransients(s.Transients);
            }
        }
        [CommandMethod("NAWELDRESET",CommandFlags.Modal)]
        public void ResetWelds()=>Run("NAWELDRESET",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var ed=doc.Editor;var picked=ed.GetEntity("\nВыберите вид для удаления сварных швов: ");
            if(picked.Status!=PromptStatus.OK)return;
            var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Reference.ObjectId==picked.ObjectId);
            if(entry==null||entry.Data.ManualSnapshot)throw new InvalidOperationException("Выберите параметрический вид.");
            var welds=entry.Data.Welds;
            if(welds==null||welds.Count==0){ed.WriteMessage("\nНа виде нет параметрических сварных швов.");return;}
            var choice=new PromptKeywordOptions("\nУдалить швы [Last/All/Number] <Last>: "){AllowNone=true};
            foreach(var k in new[]{"Last","All","Number"})choice.Keywords.Add(k);
            choice.Keywords.Default="Last";
            var answer=ed.GetKeywords(choice);
            if(answer.Status!=PromptStatus.OK&&answer.Status!=PromptStatus.None)return;
            if(answer.StringResult=="All")welds.Clear();
            else if(answer.StringResult=="Number")
            {
                for(int i=0;i<welds.Count;i++)
                    ed.WriteMessage("\n"+(i+1)+": "+WeldStyleLabel(welds[i].Style)+" k="+welds[i].LegMm.ToString("0.#")+" "+welds[i].SourceKey+" ["+welds[i].From.ToString("0.###")+"–"+welds[i].To.ToString("0.###")+"]");
                var opt=new PromptIntegerOptions("\nНомер шва: "){LowerLimit=1,UpperLimit=welds.Count};
                var number=ed.GetInteger(opt);if(number.Status!=PromptStatus.OK)return;
                welds.RemoveAt(number.Value-1);
            }
            else
            {
                var batch=welds.Last().BatchId;
                welds.RemoveAll(r=>r.BatchId==batch);
            }
            AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
            AssemblyViews.Update(doc.Database,tr,m,refs);
            AssemblyDrawing.Save(doc.Database,tr,m);
            ed.WriteMessage("\nОсталось сварных швов: "+welds.Count);
        });
        [CommandMethod("NAUPDATEVIEWS",CommandFlags.Modal)]
        public void UpdateViews()=>Run("NAUPDATEVIEWS",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var count=AssemblyViews.Update(doc.Database,tr,m,refs);
            var tables=AssemblyTableDrawing.UpdateMarkTables(doc.Database,tr,m);
            if(count>0)AssemblyDrawing.Save(doc.Database,tr,m);
            var manual=AssemblyViews.Find(doc.Database,tr,m).Count(e=>e.Data.ManualSnapshot);
            var legacy=AssemblyViews.Find(doc.Database,tr,m).Count(e=>!e.Data.ManualSnapshot&&string.IsNullOrEmpty(e.Data.GeneratedChildHandle));
            doc.Editor.WriteMessage("\nАвтоматически обновлено видов: "+count+"; таблиц: "+tables+"; старых снимков: "+manual+"; видов старого формата (NAMIGRATEVIEW): "+legacy);
        });
        [CommandMethod("NAMIGRATEVIEW",CommandFlags.Modal)]
        public void MigrateView()=>Run("NAMIGRATEVIEW",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var picked=doc.Editor.GetEntity("\nВыберите старый автоматически построенный вид: ");
            if(picked.Status!=PromptStatus.OK)return;
            var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Reference.ObjectId==picked.ObjectId);
            if(entry==null||entry.Data.ManualSnapshot||!string.IsNullOrEmpty(entry.Data.GeneratedChildHandle))
                throw new InvalidOperationException("Требуется автоматически построенный вид старого формата.");
            var p=doc.Editor.GetPoint("\nТочка размещения резервной копии старого вида: ");
            if(p.Status!=PromptStatus.OK)return;
            var at=p.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
            if(entry.Reference.Position.DistanceTo(new Point3d(at.X,at.Y,0))<1e-6)
                throw new InvalidOperationException("Разместите резервную копию отдельно от исходного вида.");
            AssemblyViews.Migrate(doc.Database,tr,m,refs,entry,at);
            AssemblyDrawing.Save(doc.Database,tr,m);
            doc.Editor.WriteMessage("\nВид переведён в новый формат. Старое оформление сохранено в отдельном блоке; перенесите нужную графику во внешний блок вида.");
        });
        [CommandMethod("NAUPDATEVIEW",CommandFlags.Modal)]
        public void UpdateView()=>Run("NAUPDATEVIEW",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var picked=doc.Editor.GetEntity("\nВыберите блок вида или сечения: ");
            if(picked.Status!=PromptStatus.OK)return;
            var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Reference.ObjectId==picked.ObjectId);
            if(entry==null)throw new InvalidOperationException("Выберите представление марки.");
            AssemblyViews.Update(doc.Database,tr,m,refs,entry.Data.Id);
            AssemblyDrawing.Save(doc.Database,tr,m);
        });
        internal static void RefreshPalette(Document doc)=>RefreshTree(doc);
        static void RefreshTree(Document doc)
        {
            if(constructorPanel==null||doc==null)return;
            constructorPanel.RefreshTree(doc);
        }
        static void Watch(Document doc)
        {
            if(watchedDocument==doc)return;
            if(watchedDocument!=null)watchedDocument.CommandEnded-=NativeEnded;
            watchedDocument=doc;
            if(doc!=null)doc.CommandEnded+=NativeEnded;
        }
        static void NativeEnded(object sender,CommandEventArgs args)
        {
            var doc=sender as Document;
            if(doc==null || doc!=AcApp.DocumentManager.MdiActiveDocument || palette==null || !palette.Visible)return;
            string name=(args.GlobalCommandName??"").ToUpperInvariant();
            if(name=="ERASE"||name=="U"||name=="UNDO"||name=="REDO"||name=="OOPS"||name=="MOVE"||name=="ROTATE"||name=="ROTATE3D"||name=="EXPLODE")
                RefreshTree(doc);
        }
        static void Queue(string command)
            =>PaletteCommands.Queue(command);
        [CommandMethod("NAREFRESH",CommandFlags.Modal)]
        public void Refresh()=>Run("NAREFRESH",(doc,tr)=>{
            var m=AssemblyDrawing.Load(doc.Database,tr);if(m==null)return;
            var before=m.Instances.Count;
            AssemblyDrawing.Synchronize(doc.Database,tr,m);
            if(before!=m.Instances.Count)AssemblyDrawing.Save(doc.Database,tr,m);
        });
        [CommandMethod("NASELECT",CommandFlags.Modal|CommandFlags.UsePickSet)]
        public void SelectInDrawing()=>Run("NASELECT",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var key=Text(doc.Editor,"Идентификатор детали или вспомогательного чертежа: ");if(key==null)return;
            ObjectId[] ids;
            if(Guid.TryParse(key,out var id))
            {
                ids=m.Instances.Where(i=>i.Id==id||i.PartId==id).Select(i=>refs[i.Id].ObjectId).ToArray();
                if(ids.Length==0)ids=AssemblyViews.Find(doc.Database,tr,m).Where(e=>e.Data.Id==id).Select(e=>e.Reference.ObjectId).ToArray();
            }
            else
            {
                var aux=AssemblyDrawing.AuxiliaryGroups(doc.Database,tr,m).SingleOrDefault(g=>g.Item1==key);
                ids=aux==null?new ObjectId[0]:aux.Item2;
            }
            if(ids.Length==0)throw new InvalidOperationException("В текущей марке объект не найден.");
            doc.Editor.SetImpliedSelection(ids);
        });
        [CommandMethod("NATREEDELETE",CommandFlags.Modal)]
        public void DeleteFromTree()=>Run("NATREEDELETE",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var key=Text(doc.Editor,"Идентификатор позиции, экземпляра или вида: ");
            if(key==null)return;
            if(!Guid.TryParse(key,out var id))throw new InvalidOperationException("Выберите конструктивную деталь в дереве.");
            var view=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Data.Id==id);
            if(view!=null)
            {
                var confirm=new PromptKeywordOptions("\nУдалить представление? [Yes/No] <No>: ");
                confirm.Keywords.Add("Yes");confirm.Keywords.Add("No");confirm.Keywords.Default="No";
                var choice=doc.Editor.GetKeywords(confirm);
                if(choice.Status!=PromptStatus.OK||choice.StringResult!="Yes")return;
                view.Reference.UpgradeOpen();view.Reference.Erase();
                return;
            }
            var instances=m.Instances.Where(i=>i.Id==id||i.PartId==id).ToList();
            if(instances.Count==0)
            {
                AssemblyDrawing.Save(doc.Database,tr,m);
                doc.Editor.WriteMessage("\nОтсутствующие экземпляры исключены из марки.");
                return;
            }
            var option=new PromptKeywordOptions("\nУдалить "+instances.Count+" экземпляров? [Yes/No] <No>: ");
            option.Keywords.Add("Yes");option.Keywords.Add("No");option.Keywords.Default="No";
            var answer=doc.Editor.GetKeywords(option);
            if(answer.Status!=PromptStatus.OK||answer.StringResult!="Yes")return;
            foreach(var instance in instances)
            {
                var block=refs[instance.Id];block.UpgradeOpen();block.Erase();
                m.RemoveInstance(instance.Id);
            }
            foreach(var partId in instances.Select(i=>i.PartId).Distinct())
                if(!m.Instances.Any(i=>i.PartId==partId))m.RemoveUnusedDefinition(partId);
            AssemblyDrawing.Save(doc.Database,tr,m);
        });
        [CommandMethod("NAAUX",CommandFlags.Modal)]
        public void Auxiliary()=>Run("NAAUX",(doc,tr)=>{
            var m=Need(doc,tr);AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var selected=doc.Editor.GetSelection(new PromptSelectionOptions {MessageForAdding="\nВыберите вспомогательные примитивы AutoCAD: "});
            if(selected.Status!=PromptStatus.OK)return;
            AssemblyDrawing.AddAuxiliary(doc.Database,tr,m,selected.Value.GetObjectIds());
            doc.Editor.WriteMessage("\nВспомогательная графика добавлена в состав марки без учёта массы.");
        });
        [CommandMethod("NACONSTRUCTOR",CommandFlags.Modal)]
        public void Open()
        {
            if(palette==null)
            {
                constructorPanel=new ConstructorPanel();
                palette=new PaletteSet("Узлы — конструктор",new Guid("e51c3c38-b277-4753-9b27-03ed12d5c50f")){Size=new Size(360,660),MinimumSize=new Size(320,460),KeepFocus=false,Style=PaletteSetStyles.ShowCloseButton|PaletteSetStyles.ShowAutoHideButton};
                palette.Add("Конструктор",constructorPanel);
                AcApp.DocumentManager.DocumentActivated+=(s,e)=>{
                    constructorPanel?.ClearTree("Нажмите «Обновить» для текущего DWG.");
                    Watch(e.Document);
                };
            }
            var doc=AcApp.DocumentManager.MdiActiveDocument;Watch(doc);if(doc!=null)RefreshTree(doc);
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
            var ed=doc.Editor;
            var ucs=ed.CurrentUserCoordinateSystem;
            var origins=new List<Vector3>();
            while(origins.Count<500)
            {
                var option=new PromptPointOptions("\nНовое положение начала детали (Enter — завершить, Esc — отменить): ") {AllowNone=true};
                var point=ed.GetPoint(option);
                if(point.Status==PromptStatus.None)break;
                if(point.Status!=PromptStatus.OK)return;
                var w=point.Value.TransformBy(ucs);
                origins.Add(new Vector3(w.X,w.Y,w.Z));
            }
            if(origins.Count==0)return;
            var copies=m.CopyInstances(id.Value,origins);
            foreach(var copy in copies)AssemblyDrawing.Insert(doc.Database,tr,m,copy,refs[id.Value].BlockTableRecord);
            AssemblyDrawing.Save(doc.Database,tr,m);
            ed.WriteMessage("\nСоздано дополнительных экземпляров: "+copies.Count);
        });
        [CommandMethod("NAARRAY",CommandFlags.Modal)]
        public void LinearArray()=>Run("NAARRAY",(doc,tr)=>{
            var m=Need(doc,tr);var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
            var id=Select(doc,refs);if(!id.HasValue)return;
            var ed=doc.Editor;
            var count=ed.GetInteger(new PromptIntegerOptions("\nКоличество дополнительных копий (без исходной детали): ") {LowerLimit=1,UpperLimit=500,AllowZero=false,AllowNegative=false});
            if(count.Status!=PromptStatus.OK)return;
            var ucs=ed.CurrentUserCoordinateSystem;
            var a=ed.GetPoint("\nПервая точка вектора шага: ");if(a.Status!=PromptStatus.OK)return;
            var b=ed.GetPoint(new PromptPointOptions("\nВторая точка вектора шага: ") {UseBasePoint=true,BasePoint=a.Value});
            if(b.Status!=PromptStatus.OK)return;
            var step=b.Value.TransformBy(ucs)-a.Value.TransformBy(ucs);
            var copies=m.LinearCopies(id.Value,new Vector3(step.X,step.Y,step.Z),count.Value);
            foreach(var copy in copies)AssemblyDrawing.Insert(doc.Database,tr,m,copy,refs[id.Value].BlockTableRecord);
            AssemblyDrawing.Save(doc.Database,tr,m);
            ed.WriteMessage("\nСоздано дополнительных экземпляров: "+copies.Count);
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
