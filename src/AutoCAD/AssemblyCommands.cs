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
        static System.Windows.Forms.TreeView tree;
        static Label treeStatus;
        static Document treeDocument;
        static Document watchedDocument;
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
                RefreshTree(doc);
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
        static void RefreshTree(Document doc)
        {
            if(tree==null)return;
            try
            {
                tree.BeginUpdate();
                string previous=treeDocument==doc?tree.SelectedNode?.Tag as string:null;
                tree.Nodes.Clear();treeDocument=doc;
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var m=AssemblyDrawing.Load(doc.Database,tr);
                    if(m==null){treeStatus.Text="В текущем DWG нет марки.";return;}
                    AssemblyDrawing.Synchronize(doc.Database,tr,m);
                    var root=tree.Nodes.Add(m.Code+" — "+m.Name);
                    System.Windows.Forms.TreeNode selected=null;
                    foreach(var part in m.Parts)
                    {
                        var instances=m.Instances.Where(i=>i.PartId==part.Id).ToList();
                        var node=root.Nodes.Add((part.IncludeInSpec?"Поз. "+part.Position+" • ":"[Справ.] ")+part.Name+" × "+instances.Count);
                        node.Tag=part.Id.ToString("D");
                        node.ToolTipText=part.Material+" | "+part.UnitMassKg().ToString("0.###")+" кг/шт.";
                        if((string)node.Tag==previous)selected=node;
                        for(int i=0;i<instances.Count;i++)
                        {
                            var child=node.Nodes.Add("Экземпляр "+(i+1));child.Tag=instances[i].Id.ToString("D");
                            if((string)child.Tag==previous)selected=child;
                        }
                    }
                    var groups=AssemblyDrawing.AuxiliaryGroups(doc.Database,tr,m);
                    if(groups.Count>0)
                    {
                        var aux=root.Nodes.Add("Вспомогательные чертежи");
                        for(int i=0;i<groups.Count;i++)
                        {
                            var node=aux.Nodes.Add("Чертёж "+(i+1)+" • "+groups[i].Item2.Length+" объектов");
                            node.Tag=groups[i].Item1;
                            if((string)node.Tag==previous)selected=node;
                        }
                    }
                    root.Expand();if(selected!=null){selected.EnsureVisible();tree.SelectedNode=selected;}
                    treeStatus.Text="Деталей: "+m.Instances.Count+" | В спецификации: "+m.Specification().Sum(r=>r.TotalMassKg).ToString("0.###")+" кг";
                    tr.Commit();
                }
            }
            catch(System.Exception ex){treeStatus.Text="Не удалось обновить состав: "+ex.Message;}
            finally{tree.EndUpdate();}
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
            =>AcApp.DocumentManager.MdiActiveDocument?.SendStringToExecute(command+"\n",true,false,false);
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
                ids=m.Instances.Where(i=>i.Id==id||i.PartId==id).Select(i=>refs[i.Id].ObjectId).ToArray();
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
            var key=Text(doc.Editor,"Идентификатор позиции или экземпляра: ");
            if(key==null)return;
            if(!Guid.TryParse(key,out var id))throw new InvalidOperationException("Выберите конструктивную деталь в дереве.");
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
                var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(5)};
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var header=new Label {Text="Конструктор • одна марка на DWG",AutoSize=true};layout.Controls.Add(header,0,0);
                tree=new System.Windows.Forms.TreeView {Dock=DockStyle.Fill,HideSelection=false,ShowNodeToolTips=true};layout.Controls.Add(tree,0,1);
                treeStatus=new Label {AutoSize=true,MaximumSize=new Size(300,0)};layout.Controls.Add(treeStatus,0,2);
                var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoSize=true,FlowDirection=System.Windows.Forms.FlowDirection.LeftToRight,WrapContents=true,Padding=new Padding(1)};
                foreach(var pair in new[]{new[]{"Новая марка","NANEW"},new[]{"Труба","NAPIPE"},new[]{"Лист","NAPLATE"},new[]{"Лист по контуру","NAPOLYPLATE"},new[]{"Кривой лист","NACURVED"},new[]{"Двутавр","NABEAM"},new[]{"Арматура","NAREBAR"},new[]{"Вспом. чертёж","NAAUX"},new[]{"Свойства","NAEDIT"},new[]{"Копировать","NACOPY"},new[]{"Линейный массив","NAARRAY"},new[]{"Отделить","NADETACH"},new[]{"Удалить","NADELETE"},new[]{"Синхронизировать","NASYNC"},new[]{"Состав и масса","NAINFO"},new[]{"Восстановить","NAREPAIR"},new[]{"Обновить дерево","NAREFRESH"}})
                {
                    var cmd=pair[1];var button=new Button {Text=pair[0],AutoSize=true};
                    button.Click+=(s,e)=>Queue(cmd);panel.Controls.Add(button);
                }
                var select=new Button {Text="Выбрать на чертеже",AutoSize=true};
                Action pick=()=>{
                    if(treeDocument!=AcApp.DocumentManager.MdiActiveDocument){Queue("NAREFRESH");return;}
                    if(tree.SelectedNode?.Tag is string key)Queue("NASELECT "+key);
                };
                select.Click+=(s,e)=>pick();tree.NodeMouseDoubleClick+=(s,e)=>pick();panel.Controls.Add(select);
                var remove=new Button {Text="Удалить из дерева",AutoSize=true};
                remove.Click+=(s,e)=>{
                    if(treeDocument!=AcApp.DocumentManager.MdiActiveDocument){Queue("NAREFRESH");return;}
                    if(tree.SelectedNode?.Tag is string key && Guid.TryParse(key,out _))Queue("NATREEDELETE "+key);
                };
                panel.Controls.Add(remove);
                layout.Controls.Add(panel,0,3);
                palette=new PaletteSet("Узлы — конструктор",new Guid("e51c3c38-b277-4753-9b27-03ed12d5c50f")){Size=new Size(340,650),MinimumSize=new Size(300,420),Style=PaletteSetStyles.ShowCloseButton|PaletteSetStyles.ShowAutoHideButton};
                palette.Add("Конструктор",layout);
                AcApp.DocumentManager.DocumentActivated+=(s,e)=>{
                    if(tree!=null){tree.Nodes.Clear();treeDocument=null;treeStatus.Text="Обновите дерево для текущего DWG.";}
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
