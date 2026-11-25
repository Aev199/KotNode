using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;
using ModelJson=KotNode.Core.Json;

namespace KotNode.AutoCAD
{
    public sealed class Link
    {
        public int Schema { get; set; } = 1;
        public int DraftRevision { get; set; }
        public DimensionPlan Dimension { get; set; }
        public int[] LeaderLines { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectCode { get; set; }
        public string ProjectPath { get; set; }
        public NodeMark Mark { get; set; }
        public string Role { get; set; }
        public string SelfHandle { get; set; }
        public string ViewHandle { get; set; }
        public Guid SetId { get; set; }
        public double AnnotationScale { get; set; } = 20;
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public long ReportVersion { get; set; }
    }
    public static partial class Drawing
    {
        const string Key = "KOTNODE_PROJECT_1";
        static Point3d P(double x, double y) { return new Point3d(x, y, 0); }
        public static Link Read(Entity e, Transaction tr)
        {
            if (e.ExtensionDictionary.IsNull) return null;
            var dict=(DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead);
            if (!dict.Contains(Key)) return null;
            var xr=(Xrecord)tr.GetObject(dict.GetAt(Key),OpenMode.ForRead);
            byte[] bytes;using(var data=xr.Data){if(data==null)return null;bytes=data.AsArray().Where(v=>v.TypeCode==310).SelectMany(v=>(byte[])v.Value).ToArray();}
            var link=ModelJson.Read<Link>(Encoding.UTF8.GetString(bytes));
            if (link.Schema!=1) throw new InvalidOperationException("Неизвестная схема связи узла.");
            // Ordinary COPY is intentionally detached; it cannot edit the original node.
            return link.SelfHandle==e.Handle.ToString() ? link : null;
        }
        static void Write(Entity e, Link link, Transaction tr)
        {
            link.SelfHandle=e.Handle.ToString();
            if (e.ExtensionDictionary.IsNull) e.CreateExtensionDictionary();
            var dict=(DBDictionary)tr.GetObject(e.ExtensionDictionary,OpenMode.ForWrite);
            Xrecord xr;
            if (dict.Contains(Key)) xr=(Xrecord)tr.GetObject(dict.GetAt(Key),OpenMode.ForWrite);
            else { xr=new Xrecord(); dict.SetAt(Key,xr); tr.AddNewlyCreatedDBObject(xr,true); }
            byte[] bytes=Encoding.UTF8.GetBytes(ModelJson.Write(link)); var values=new List<TypedValue>();
            for(int i=0;i<bytes.Length;i+=127) values.Add(new TypedValue(310,bytes.Skip(i).Take(127).ToArray()));
            using(var data=new ResultBuffer(values.ToArray())) xr.Data=data;
        }
        static ObjectId Add(BlockTableRecord b, Entity e, Transaction tr)
        { var id=b.AppendEntity(e); tr.AddNewlyCreatedDBObject(e,true); return id; }
        static void Line(BlockTableRecord b,Transaction tr,double x,double y,double xx,double yy,string layer="NP_GEOMETRY")
        { Add(b,new Line(P(x,y),P(xx,yy)){Layer=layer},tr); }
        static void Rect(BlockTableRecord b,Transaction tr,double x,double y,double xx,double yy)
        { Line(b,tr,x,y,xx,y);Line(b,tr,xx,y,xx,yy);Line(b,tr,xx,yy,x,yy);Line(b,tr,x,yy,x,y); }
        static void Layers(Database db,Transaction tr)
        {
            var lt=(LayerTable)tr.GetObject(db.LayerTableId,OpenMode.ForRead);
            foreach(var name in new[]{"NP_GEOMETRY","NP_DETAILS","NP_ANNOTATION","NP_AXIS","NP_HIDDEN","NP_WELDS"})
            {
                if(lt.Has(name)) continue;
                if(!lt.IsWriteEnabled)lt.UpgradeOpen();var l=new LayerTableRecord{Name=name,Color=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,(short)(name=="NP_AXIS"?3:7))};lt.Add(l);tr.AddNewlyCreatedDBObject(l,true);
            }
            var styles=(TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead);
            if(!styles.Has("NP_ISOCPEUR")) { styles.UpgradeOpen();var s=new TextStyleTableRecord{Name="NP_ISOCPEUR",FileName="isocpeur.ttf"};styles.Add(s);tr.AddNewlyCreatedDBObject(s,true); }
        }
        static ObjectId DimStyle(Database db, Transaction tr)
        {
            var styles=(DimStyleTable)tr.GetObject(db.DimStyleTableId,OpenMode.ForRead);
            if(styles.Has("NP_DIM_02"))return styles["NP_DIM_02"];
            var blocks=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);ObjectId tick;
            if(blocks.Has("NP_TICK_02"))tick=blocks["NP_TICK_02"];
            else{Upgrade(blocks);var arrow=new BlockTableRecord{Name="NP_TICK_02"};tick=blocks.Add(arrow);tr.AddNewlyCreatedDBObject(arrow,true);Add(arrow,new Line(P(-.5,-.5),P(.5,.5)){LineWeight=LineWeight.LineWeight030},tr);}
            styles.UpgradeOpen();var st=new DimStyleTableRecord{Name="NP_DIM_02",Dimtxsty=TextStyle(db,tr),Dimtxt=2.5,Dimasz=1.875,Dimdec=0,Dimtad=1,Dimexe=1.125,Dimexo=.625,Dimgap=.625,Dimdle=0,Dimtix=false,Dimtmove=2,Dimblk=tick,Dimblk1=tick,Dimblk2=tick,Dimsah=true,Dimtoh=false,Dimtih=false};
            var id=styles.Add(st);tr.AddNewlyCreatedDBObject(st,true);return id;
        }
        static ObjectId TextStyle(Database db,Transaction tr) { return ((TextStyleTable)tr.GetObject(db.TextStyleTableId,OpenMode.ForRead))["NP_ISOCPEUR"]; }
        static void Text(BlockTableRecord b,Transaction tr,Database db,string text,double x,double y,double height)
        { Add(b,new DBText{TextString=text,Position=P(x,y),Height=height,TextStyleId=TextStyle(db,tr),Layer="NP_ANNOTATION"},tr); }
        static double Norm(double a) { return (a%(2*Math.PI)+2*Math.PI)%(2*Math.PI); }
        static Point3d Pt(XY a) { return P(a.X,a.Y); }
        static XY Xy(Point3d p) { return new XY(p.X,p.Y); }
        static Link Child(Link view,string role)
        { var l=ModelJson.Copy(view);l.Role=role;l.ViewHandle=view.SelfHandle;l.Dimension=null;l.LeaderLines=null;return l; }
        static void Upgrade(DBObject e) { if(!e.IsWriteEnabled)e.UpgradeOpen(); }
        static ObjectId Geometry(Database db,Transaction tr,ViewPlan plan)
        {
            var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForWrite);
            var b=new BlockTableRecord{Name="NP_"+Guid.NewGuid().ToString("N"),Units=UnitsValue.Millimeters,Annotative=AnnotativeStates.False};bt.Add(b);tr.AddNewlyCreatedDBObject(b,true);
            foreach(var q in plan.Geometry)
            {
                Entity e;
                switch(q.Kind)
                {
                    case "line":e=new Line(Pt(q.A),Pt(q.B));break;
                    case "circle":e=new Circle(Pt(q.A),Vector3d.ZAxis,q.Radius);break;
                    case "arc":e=new Arc(Pt(q.A),q.Radius,Norm(q.Start),Norm(q.End));break;
                    case "solid":e=new Solid(Pt(q.A),Pt(q.B),Pt(q.C),Pt(q.C));break;
                    case "text":e=new MText{Location=Pt(q.A),Contents=q.Text,TextHeight=q.Height,Width=q.Height*100,TextStyleId=TextStyle(db,tr),Attachment=AttachmentPoint.TopLeft};break;
                    default:throw new InvalidOperationException("Неизвестный элемент оформления: "+q.Kind);
                }
                e.Layer=q.Layer;e.Linetype="Continuous";e.LinetypeScale=1;e.ColorIndex=256;
                e.LineWeight=q.Layer=="NP_HIDDEN"?LineWeight.LineWeight015:q.Layer=="NP_AXIS"?LineWeight.LineWeight018:q.Layer=="NP_DETAILS"||q.Layer=="NP_WELDS"?LineWeight.LineWeight030:LineWeight.LineWeight025;
                Add(b,e,tr);
            }
            return b.ObjectId;
        }
        static Point3d World(BlockReference view,XY p) { return Pt(p).TransformBy(view.BlockTransform); }
        static Dimension MakeDimension(Database db,Transaction tr,BlockReference view,DimensionPlan p)
        {
            var a=World(view,p.A);var b=World(view,p.B);var line=World(view,p.Line);var style=DimStyle(db,tr);
            if(p.Kind=="aligned")return new AlignedDimension(a,b,line,p.Text,style);
            if(p.Kind=="arc")return new ArcDimension(World(view,p.Center),a,b,line,p.Text,style);
            return new Point3AngularDimension(World(view,p.Center),a,b,line,p.Text,style);
        }
        static void DimensionGeometry(Dimension d,BlockReference view,DimensionPlan p,Point3d line)
        {
            if(d is AlignedDimension aligned){aligned.XLine1Point=World(view,p.A);aligned.XLine2Point=World(view,p.B);aligned.DimLinePoint=line;}
            else if(d is ArcDimension arc){arc.CenterPoint=World(view,p.Center);arc.XLine1Point=World(view,p.A);arc.XLine2Point=World(view,p.B);arc.ArcPoint=line;arc.ArcSymbolType=1;}
            else if(d is Point3AngularDimension angle){angle.CenterPoint=World(view,p.Center);angle.XLine1Point=World(view,p.A);angle.XLine2Point=World(view,p.B);angle.ArcPoint=line;}
            else throw new InvalidOperationException("Неизвестный тип размерного объекта.");
        }
        static void SyncDimensions(Database db,Transaction tr,BlockTableRecord space,BlockReference view,Link link,ViewPlan plan,List<Tuple<Entity,Link>> all,XY previousOrigin)
        {
            var children=all.Where(e=>e.Item2.ViewHandle==view.Handle.ToString()&&(e.Item2.Role.StartsWith("dim:")||e.Item2.Role=="dimension")).ToList();
            var keep=new HashSet<ObjectId>();
            foreach(var p in plan.Dimensions)
            {
                Diagnostics.Step("Размеры "+plan.Role+": "+p.Key);
                var item=children.SingleOrDefault(e=>e.Item2.Role=="dim:"+p.Key);
                // Preserve 0.1 main length dimension while replacing its former diameter dimension.
                if(item==null&&p.Key=="length")item=children.SingleOrDefault(e=>e.Item2.Role=="dimension"&&e.Item1 is RotatedDimension);
                Dimension d=item?.Item1 as Dimension;bool newObject=d==null||d is RotatedDimension;
                Point3d line=World(view,p.Line),text=p.TextAt==null?line:World(view,p.TextAt);bool manualText=false;
                if(d!=null)
                {
                    var old=item.Item2.Dimension;var oldOrigin=previousOrigin;
                    XY oldLine=old?.Line??new XY(0,-link.Mark.Parameters.PipeDiameter/2-130);
                    var oldAnchor=old?.A??new XY(-link.Mark.Parameters.PlateLength/2,-link.Mark.Parameters.PipeDiameter/2);
                    Point3d actualAnchor=d is AlignedDimension ad0?ad0.XLine1Point:d is RotatedDimension rd0?rd0.XLine1Point:d is ArcDimension ar0?ar0.XLine1Point:((Point3AngularDimension)d).XLine1Point;
                    var viewDelta=Xy(view.Position)-previousOrigin;
                    // Group MOVE translates native dimensions too. Do not apply that translation twice.
                    if(Xy(actualAnchor).Distance(previousOrigin+oldAnchor+viewDelta)<1e-6)oldOrigin=oldOrigin+viewDelta;
                    Point3d actualLine=d is AlignedDimension ad?ad.DimLinePoint:d is RotatedDimension rd?rd.DimLinePoint:d is ArcDimension ar?ar.ArcPoint:((Point3AngularDimension)d).ArcPoint;
                    line=Pt(Drafting.KeepOffset(Xy(actualLine),oldOrigin+oldLine,Xy(line)));
                    var oldText=old?.TextAt??oldLine;manualText=!d.UsingDefaultTextPosition;
                    if(manualText)text=Pt(Drafting.KeepOffset(Xy(d.TextPosition),oldOrigin+oldText,p.TextAt==null?Xy(World(view,p.Line)):Xy(World(view,p.TextAt))));
                }
                if(newObject){if(d!=null){Upgrade(d);d.Erase();}d=MakeDimension(db,tr,view,p);d.SetDatabaseDefaults(db);Add(space,d,tr);}else Upgrade(d);
                d.DimensionStyle=DimStyle(db,tr);d.Layer="NP_ANNOTATION";d.Dimscale=link.AnnotationScale;d.Dimtxt=2.5;d.Dimasz=1.875;d.Dimlfac=1;d.Dimdec=p.Precision;d.DimensionText=p.Text??"";d.Annotative=AnnotativeStates.False;
                DimensionGeometry(d,view,p,line);
                if(manualText||p.TextAt!=null)d.TextPosition=text;else d.UsingDefaultTextPosition=true;
                d.RecomputeDimensionBlock(true);
                var tag=Child(link,"dim:"+p.Key);tag.Dimension=p;Write(d,tag,tr);keep.Add(d.ObjectId);
            }
            foreach(var child in children)if(!child.Item1.IsErased&&!keep.Contains(child.Item1.ObjectId)){Upgrade(child.Item1);child.Item1.Erase();}
        }
        static void SyncLeaders(Database db,Transaction tr,BlockTableRecord space,BlockReference view,Link link,ViewPlan plan,List<Tuple<Entity,Link>> all)
        {
            var children=all.Where(e=>e.Item2.ViewHandle==view.Handle.ToString()&&(e.Item2.Role.StartsWith("leader:")||e.Item2.Role=="leader")).ToList();var keep=new HashSet<ObjectId>();
            foreach(var p in plan.Leaders)
            {
                Diagnostics.Step("Выноски "+plan.Role+": "+p.Key);
                var item=children.SingleOrDefault(e=>e.Item2.Role=="leader:"+p.Key);
                if(item==null&&p.Key=="weld-main")item=children.SingleOrDefault(e=>e.Item2.Role=="leader");
                var ml=item?.Item1 as MLeader;bool fresh=ml==null;
                if(fresh){ml=new MLeader();ml.SetDatabaseDefaults(db);ml.ContentType=ContentType.MTextContent;ml.Layer="NP_ANNOTATION";ml.Annotative=AnnotativeStates.False;ml.TextHeight=link.AnnotationScale*2.5;ml.ArrowSize=link.AnnotationScale*2.5;ml.DoglegLength=link.AnnotationScale*5;Add(space,ml,tr);}else Upgrade(ml);
                using(var text=fresh?new MText():ml.MText)
                {text.Contents=p.Text;if(fresh){text.TextHeight=link.AnnotationScale*2.5;text.TextStyleId=TextStyle(db,tr);}ml.MText=text;}
                var tag=Child(link,"leader:"+p.Key);var lines=item?.Item2.LeaderLines;
                if(lines==null)lines=ml.GetLeaderIndexes().Cast<int>().SelectMany(i=>ml.GetLeaderLineIndexes(i).Cast<int>()).Take(p.Tips.Count).ToArray();
                var indexes=new List<int>();
                for(int i=0;i<p.Tips.Count;i++)
                {
                    int line;
                    var present=ml.GetLeaderIndexes().Cast<int>().SelectMany(j=>ml.GetLeaderLineIndexes(j).Cast<int>()).ToList();
                    if(i<lines.Length&&present.Contains(lines[i])){line=lines[i];ml.SetFirstVertex(line,World(view,p.Tips[i]));}
                    else {int leader=ml.AddLeader();line=ml.AddLeaderLine(leader);ml.AddFirstVertex(line,World(view,p.Tips[i]));ml.AddLastVertex(line,fresh?World(view,p.Landing):ml.TextLocation);}
                    indexes.Add(line);
                }
                // Only owned arrow branches are updated. Extra user branches keep their own targets.
                if(fresh)ml.TextLocation=World(view,p.Landing);
                tag.LeaderLines=indexes.ToArray();Write(ml,tag,tr);keep.Add(ml.ObjectId);
            }
            foreach(var child in children)if(!keep.Contains(child.Item1.ObjectId)){Upgrade(child.Item1);child.Item1.Erase();}
        }
        public static void Place(Database db,Project project,string path,NodeMark mark,Point3d at,double scale)
        {
            project.Validate();
            using(var tr=db.TransactionManager.StartTransaction())
            {
                Diagnostics.Step("Слои и пространство чертежа");
                Layers(db,tr);var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);Guid set=Guid.NewGuid();Link main=null;
                foreach(var role in new[]{"main","section"})
                {
                    Diagnostics.Step("Геометрия вида: "+role);
                    var plan=Drafting.Build(mark,role,scale);var position=at+(role=="main"?new Vector3d(0,0,0):Pt(Drafting.SectionOffset(mark,scale))-Point3d.Origin);
                    var b=new BlockReference(position,Geometry(db,tr,plan));Add(space,b,tr);
                    var link=new Link{ProjectId=project.Id,ProjectCode=project.Code,ProjectPath=path,Mark=ModelJson.Copy(mark),Role=role,SetId=set,AnnotationScale=scale,OriginX=position.X,OriginY=position.Y,ReportVersion=project.Version,DraftRevision=Drafting.Revision};Write(b,link,tr);
                    SyncDimensions(db,tr,space,b,link,plan,new List<Tuple<Entity,Link>>(),new XY(position.X,position.Y));SyncLeaders(db,tr,space,b,link,plan,new List<Tuple<Entity,Link>>());
                    GroupView(db,tr,space,b);
                    if(role=="main")main=link;
                }
                var point=at+(Pt(Drafting.SpecOffset(mark,scale))-Point3d.Origin);
                Diagnostics.Step("Спецификация: создание");
                var spec=LocalSpec(db,tr,space,project,mark,point,scale,null);Write(spec,Child(main,"spec"),tr);
                Diagnostics.Step("Локальная ведомость: создание");
                var vrs=LocalSteel(db,tr,space,project,mark,point-new Vector3d(0,spec.Height+scale*2.5*5,0),scale,null);Write(vrs,Child(main,"vrs"),tr);
                tr.Commit();
            }
        }
        static IEnumerable<Tuple<Entity,Link>> Linked(BlockTableRecord space,Transaction tr)
        {foreach(ObjectId id in space){var e=tr.GetObject(id,OpenMode.ForRead) as Entity;if(e==null)continue;var l=Read(e,tr);if(l!=null)yield return Tuple.Create(e,l);}}
        static void GroupView(Database db,Transaction tr,BlockTableRecord space,BlockReference view)
        {
            Diagnostics.Step("Группа вида: "+view.Handle);
            var dict=(DBDictionary)tr.GetObject(db.GroupDictionaryId,OpenMode.ForWrite);string name="NP_"+view.Handle;Group group;
            if(dict.Contains(name)){group=(Group)tr.GetObject(dict.GetAt(name),OpenMode.ForWrite);group.Clear();}
            else{group=new Group("Вид узла и размеры",true);dict.SetAt(name,group);tr.AddNewlyCreatedDBObject(group,true);}
            var ids=Linked(space,tr).Where(x=>x.Item2.ViewHandle==view.Handle.ToString()&&x.Item2.Role.StartsWith("dim:")).Select(x=>x.Item1.ObjectId).ToList();ids.Insert(0,view.ObjectId);group.Append(new ObjectIdCollection(ids.ToArray()));
        }
        public static int Update(Database db,Project project)
        {
            project.Validate();int count=0;
            using(var tr=db.TransactionManager.StartTransaction())
            {
                Layers(db,tr);var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
                // Snapshot spaces before adding generated block definitions.
                var spaces=bt.Cast<ObjectId>().Where(id=>((BlockTableRecord)tr.GetObject(id,OpenMode.ForRead)).IsLayout).ToList();
                foreach(var id in spaces)
                {
                    var space=(BlockTableRecord)tr.GetObject(id,OpenMode.ForWrite);var all=Linked(space,tr).Where(e=>e.Item2.ProjectId==project.Id).ToList();
                    foreach(var item in all.Where(e=>e.Item1 is BlockReference&&(e.Item2.Role=="main"||e.Item2.Role=="section")))
                    {
                        var view=(BlockReference)item.Item1;var tag=item.Item2;var mark=project.Marks.SingleOrDefault(m=>m.Id==tag.Mark.Id);if(mark==null)continue;
                        if(Math.Abs(view.ScaleFactors.X-1)>1e-8||Math.Abs(view.ScaleFactors.Y-1)>1e-8||Math.Abs(view.ScaleFactors.Z-1)>1e-8||Math.Abs(view.Rotation)>1e-8||!view.Normal.IsEqualTo(Vector3d.ZAxis)||Math.Abs(view.Position.Z)>1e-8)throw new InvalidOperationException("Верните вид к масштабу 1:1 без поворота. Переносы поддерживаются.");
                        double scale=tag.AnnotationScale;if(scale<1||scale>1000)scale=20;tag.AnnotationScale=scale;
                        var oldOrigin=new XY(tag.OriginX,tag.OriginY);var plan=Drafting.Build(mark,tag.Role,scale);
                        Upgrade(view);view.BlockTableRecord=Geometry(db,tr,plan);
                        SyncDimensions(db,tr,space,view,tag,plan,all,oldOrigin);SyncLeaders(db,tr,space,view,tag,plan,all);
                        tag.Mark=ModelJson.Copy(mark);tag.OriginX=view.Position.X;tag.OriginY=view.Position.Y;tag.DraftRevision=Drafting.Revision;Write(view,tag,tr);
                        // Refresh all child snapshots after applying stable role-based annotations.
                        foreach(var child in Linked(space,tr).Where(e=>e.Item2.ViewHandle==view.Handle.ToString()&&(e.Item2.Role.StartsWith("dim:")||e.Item2.Role.StartsWith("leader:"))).ToList())
                        {Upgrade(child.Item1);child.Item2.Mark=ModelJson.Copy(mark);child.Item2.DraftRevision=Drafting.Revision;Write(child.Item1,child.Item2,tr);}
                        GroupView(db,tr,space,view);
                        count++;
                    }
                    foreach(var item in all.Where(e=>e.Item1 is Table))
                    {
                        var t=(Table)item.Item1;var tag=item.Item2;
                        if(tag.Role=="report"){GlobalSteel(db,tr,space,project,t.Position,tag.AnnotationScale,t);tag.ReportVersion=project.Version;}
                        else if(tag.Role=="spec"||tag.Role=="vrs")
                        {
                            var mark=project.Marks.SingleOrDefault(m=>m.Id==tag.Mark.Id);if(mark==null)continue;
                            if(tag.Role=="spec")LocalSpec(db,tr,space,project,mark,t.Position,tag.AnnotationScale,t);else LocalSteel(db,tr,space,project,mark,t.Position,tag.AnnotationScale,t);tag.Mark=ModelJson.Copy(mark);tag.ReportVersion=project.Version;
                        }
                        else continue;
                        Upgrade(t);tag.DraftRevision=Drafting.Revision;Write(t,tag,tr);
                    }
                }
                tr.Commit();
            }
            return count;
        }
        static long Quantity(Project project,NodeMark mark) { return project.Uses.Where(u=>u.MarkId==mark.Id&&u.Included).Sum(u=>(long)u.Count); }
        // AutoCAD 2021 exposes these flags through the legacy Table properties.
#pragma warning disable CS0618
        static void ShowHeaders(Table t){t.IsTitleSuppressed=false;t.IsHeaderSuppressed=false;}
#pragma warning restore CS0618
        static Table FillTable(Database db,Transaction tr,BlockTableRecord space,Table old,Point3d at,double scale,List<string[]> rows,double[] widths,int[] mergedRows)
        {
            Diagnostics.Step("Таблица: структура");
            bool fresh=old==null;var t=old??new Table();int cols=widths.Length;
            if(fresh){t.SetDatabaseDefaults(db);t.TableStyle=db.Tablestyle;ShowHeaders(t);t.Position=at;t.Layer="NP_ANNOTATION";t.SetSize(rows.Count,cols);}
            else {Upgrade(t);Unmerge(t);if(t.Rows.Count!=rows.Count||t.Columns.Count!=cols)t.SetSize(rows.Count,cols);}
            double h=2.5*scale;
            if(fresh){t.SetRowHeight(h*2.5);for(int c=0;c<cols;c++)t.Columns[c].Width=widths[c]*h;}
            // Recompute generated merges after row/column changes; table position and widths remain intact.
            Diagnostics.Step("Таблица: объединения ячеек");
            Unmerge(t);
            foreach(int row in mergedRows)t.MergeCells(CellRange.Create(t,row,0,row,cols-1));
            for(int row=0;row<rows.Count;row++)for(int col=0;col<cols;col++)
            {
                if(mergedRows.Contains(row)&&col>0)continue;
                t.Cells[row,col].TextString=col<rows[row].Length?rows[row][col]:"";
                if(fresh){t.Cells[row,col].TextHeight=h;t.Cells[row,col].TextStyleId=TextStyle(db,tr);}
            }
            if(fresh){t.Rows[0].Height=h*3;t.Cells[0,0].TextHeight=h*1.2;Add(space,t,tr);}
            Diagnostics.Step("Таблица: GenerateLayout");
            t.GenerateLayout();return t;
        }
        static Table LocalSpec(Database db,Transaction tr,BlockTableRecord space,Project project,NodeMark mark,Point3d at,double scale,Table old)
        {
            var p=mark.Parameters;var g=Splice.Calculate(p);long n=Quantity(project,mark);double perNode=g.PlateMass*p.PlateCount,total=perNode*n;
            string designation="Труба %%c"+Drafting.F(p.DonorDiameter)+"x"+Drafting.F(p.PlateThickness)+"x"+Drafting.F(p.PlateLength)+" ГОСТ 10704-91 / "+p.Material;
            var rows=new List<string[]>{new[]{"Спецификация "+mark.Code+" / ред. "+mark.Revision},new[]{"Поз.","Обозначение","Наименование","Кол.","Масса ед., кг","Масса, кг"},new[]{"","",mark.Name,n.ToString(),perNode.ToString("0.00"),total.ToString("0.00")},new[]{mark.DetailCode,"",designation,p.PlateCount.ToString(),g.PlateMass.ToString("0.00"),perNode.ToString("0.00")},new[]{"","","Итого по применениям:","","",total.ToString("0.00")},new[]{"Детали: на один узел; узлов в проекте: "+n+". Основные трубы в расход не включены."}};
            var table=FillTable(db,tr,space,old,at,scale,rows,new[]{5.0,20,24,5,7,8},new[]{0,5});
            if(old==null){table.Rows[1].Height=scale*2.5*3.5;table.Rows[3].Height=scale*2.5*4;table.Rows[5].Height=scale*2.5*3;table.GenerateLayout();}return table;
        }
        static Table LocalSteel(Database db,Transaction tr,BlockTableRecord space,Project project,NodeMark mark,Point3d at,double scale,Table old)
        {
            var p=mark.Parameters;double total=Splice.Calculate(p).PlateMass*p.PlateCount*Quantity(project,mark);
            var rows=new List<string[]>{new[]{"Ведомость расхода стали — "+mark.Code+" (чистая масса накладок)"},new[]{"Труба-заготовка","Сталь","Масса, кг","Масса, т"},new[]{"%%c"+Drafting.F(p.DonorDiameter)+"x"+Drafting.F(p.PlateThickness),p.Material,total.ToString("0.00"),(total/1000).ToString("0.00000")},new[]{"Без металла швов, припусков и отходов. Количество — из включенных применений проекта."}};
            return FillTable(db,tr,space,old,at,scale,rows,new[]{34.0,8,13,14},new[]{0,3});
        }
        static Table GlobalSteel(Database db,Transaction tr,BlockTableRecord space,Project project,Point3d at,double scale,Table old)
        {
            var steel=project.Steel();var rows=new List<string[]>{new[]{"Расход стали — "+project.Code+" / версия "+project.Version},new[]{"Марка / ред.","Сталь / s, мм","Узлов","Накладок","Масса, кг"}};
            foreach(var r in steel)rows.Add(new[]{r.Mark+" / "+r.Revision,r.Material+" / "+r.Thickness,r.NodeCount.ToString(),r.DetailCount.ToString(),r.TotalMass.ToString("0.00")});
            rows.Add(new[]{"Итого","","","",steel.Sum(r=>r.TotalMass).ToString("0.00")});int header=rows.Count;rows.Add(new[]{"Итого по материалам и толщине"});
            foreach(var group in steel.GroupBy(r=>new{r.Material,r.Thickness}))rows.Add(new[]{group.Key.Material,"s="+group.Key.Thickness,"","",group.Sum(r=>r.TotalMass).ToString("0.00")});
            return FillTable(db,tr,space,old,at,scale,rows,new[]{14.0,15,8,9,12},new[]{0,header});
        }
        public static void SpecTable(Database db,Project project,string path,NodeMark mark,Point3d point)
        {
            using(var tr=db.TransactionManager.StartTransaction())
            {Layers(db,tr);var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);var t=LocalSpec(db,tr,space,project,mark,point,20,null);Write(t,new Link{ProjectId=project.Id,ProjectCode=project.Code,ProjectPath=path,Mark=ModelJson.Copy(mark),Role="spec",AnnotationScale=20,ReportVersion=project.Version,DraftRevision=Drafting.Revision},tr);tr.Commit();}
        }
        public static void SteelTable(Database db,Project project,string path,Point3d point)
        {
            using(var tr=db.TransactionManager.StartTransaction())
            {Layers(db,tr);var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);var t=GlobalSteel(db,tr,space,project,point,20,null);Write(t,new Link{ProjectId=project.Id,ProjectCode=project.Code,ProjectPath=path,Role="report",AnnotationScale=20,ReportVersion=project.Version,DraftRevision=Drafting.Revision},tr);tr.Commit();}
        }
        public static string Status(Database db,Project p)
        {
            using(var tr=db.TransactionManager.StartTransaction())
            {
                int views=0,old=0,reports=0;var bt=(BlockTable)tr.GetObject(db.BlockTableId,OpenMode.ForRead);
                foreach(ObjectId id in bt)
                {
                    var space=(BlockTableRecord)tr.GetObject(id,OpenMode.ForRead);if(!space.IsLayout)continue;
                    foreach(var item in Linked(space,tr).Where(x=>x.Item2.ProjectId==p.Id))
                    {
                        var l=item.Item2;
                        if(item.Item1 is Table)
                        {var m=l.Mark==null?null:p.Marks.SingleOrDefault(x=>x.Id==l.Mark.Id);if(l.DraftRevision!=Drafting.Revision||l.ReportVersion!=p.Version||(l.Mark!=null&&(m==null||m.Revision!=l.Mark.Revision)))reports++;continue;}
                        if(l.Role!="main"&&l.Role!="section")continue;views++;var mark=p.Marks.SingleOrDefault(x=>x.Id==l.Mark.Id);if(mark==null||mark.Revision!=l.Mark.Revision||l.DraftRevision!=Drafting.Revision)old++;
                    }
                }
                return "В текущем DWG видов: "+views+"; устаревших: "+old+"; устаревших таблиц: "+reports;
            }
        }
    }
}
