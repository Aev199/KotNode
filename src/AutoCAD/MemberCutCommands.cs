using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.MemberCutCommands))]
namespace KotNode.AutoCAD
{
    public sealed class MemberCutCommands
    {
        static string Choice(Editor ed,string message,string[] values,string initial)
        {
            var opt=new PromptKeywordOptions("\n"+message){AllowNone=true};
            foreach(var v in values)opt.Keywords.Add(v);
            opt.Keywords.Default=initial;
            var r=ed.GetKeywords(opt);
            return r.Status==PromptStatus.None?initial:r.Status==PromptStatus.OK?r.StringResult:null;
        }
        static Vector3 Point(Document doc,string prompt)
        {
            var r=doc.Editor.GetPoint("\n"+prompt);
            if(r.Status!=PromptStatus.OK)return null;
            var p=r.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
            return new Vector3(p.X,p.Y,p.Z);
        }
        static void Set(PartDefinition part,bool start,PlanarEndCut cut)
        {
            if(part.EndCuts==null)part.EndCuts=new MemberEndTreatment();
            if(start)part.EndCuts.Start=cut;else part.EndCuts.End=cut;
            if(!MemberEndCuts.HasCuts(part))part.EndCuts=null;
        }
        [CommandMethod("NATRIM",CommandFlags.Modal)]
        public void Trim()
        {
            var doc=AcApp.DocumentManager.MdiActiveDocument;if(doc==null)return;
            Diagnostics.Begin("NATRIM");Diagnostics.EnableLiveTrace("NATRIM");
            try
            {
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var mark=AssemblyDrawing.Load(doc.Database,tr);
                    if(mark==null)throw new InvalidOperationException("Создайте или вставьте марку.");
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
                    var pick=doc.Editor.GetEntity("\nВыберите исходную 3D-деталь — трубу или двутавр: ");
                    if(pick.Status!=PromptStatus.OK)return;
                    var pair=refs.FirstOrDefault(x=>x.Value.ObjectId==pick.ObjectId);
                    if(pair.Value==null)throw new InvalidOperationException("Выберите исходный блок детали, а не её 2D-вид.");
                    var inst=mark.Instances.Single(i=>i.Id==pair.Key);
                    var part=KotNode.Core.Json.Copy(mark.Parts.Single(p=>p.Id==inst.PartId));
                    if(part.Kind!=PartKind.Pipe&&part.Kind!=PartKind.IBeam)throw new InvalidOperationException("Подрезка доступна только для труб и двутавров.");
                    string end=Choice(doc.Editor,"Торец [Start/End] <End>: ",new[]{"Start","End"},"End");
                    if(end==null)return;bool start=end=="Start";
                    string mode=Choice(doc.Editor,"Задать плоскость [ThreePoints/Angles/Clear] <ThreePoints>: ",new[]{"ThreePoints","Angles","Clear"},"ThreePoints");
                    if(mode==null)return;
                    if(mode=="Clear")Set(part,start,null);
                    else if(mode=="ThreePoints")
                    {
                        var a=Point(doc,"Первая точка плоскости подрезки: ");if(a==null)return;
                        var b=Point(doc,"Вторая точка плоскости: ");if(b==null)return;
                        var c=Point(doc,"Третья точка плоскости: ");if(c==null)return;
                        Set(part,start,MemberEndCuts.FromPlane(part,inst.Placement,RigidPlacement.FromThreePoints(a,b,c),start));
                    }
                    else
                    {
                        var existing=start?part.EndCuts?.Start:part.EndCuts?.End;
                        using(var f=new Fields(start?"Подрезка начала (локальная Z=0)":"Подрезка конца (локальная Z=L)"))
                        {
                            f.Number("Наклон в локальной плоскости XZ, град","x",existing?.AngleXDeg??0);
                            f.Number("Наклон в локальной плоскости YZ, град","y",existing?.AngleYDeg??0);
                            f.Number("Отступ крайней точки внутрь заготовки, мм","offset",existing?.OffsetMm??0);
                            f.ValidateFields=()=>{
                                Set(part,start,new PlanarEndCut{AngleXDeg=f.Value("x"),AngleYDeg=f.Value("y"),OffsetMm=f.Value("offset")});part.Validate();
                            };
                            if(AcApp.ShowModalDialog(AcApp.MainWindow.Handle,f,false)!=DialogResult.OK)return;
                        }
                    }
                    part.Validate();
                    if(MemberEndCuts.HasCuts(part))mark.Schema=Math.Max(mark.Schema,3);
                    mark.ReplaceDefinition(part.Id,part);
                    Diagnostics.Step("Построение подрезанного тела");
                    var geometry=AssemblyDrawing.Geometry(doc.Database,tr,part);
                    foreach(var i in mark.Instances.Where(i=>i.PartId==part.Id))AssemblyDrawing.Replace(refs[i.Id],geometry);
                    Diagnostics.Step("Перестроение видов и таблиц");
                    AssemblyViews.Update(doc.Database,tr,mark,refs);
                    AssemblyTableDrawing.UpdateMarkTables(doc.Database,tr,mark);
                    AssemblyDrawing.Save(doc.Database,tr,mark);tr.Commit();
                    doc.Editor.WriteMessage("\nТорец обновлён для всех экземпляров этой позиции. Для независимой детали используйте NADETACH. Длина и масса в ведомостях — по заготовке без вычета подрезки.");
                }
                doc.TransactionManager.QueueForGraphicsFlush();doc.Editor.Regen();AssemblyCommands.RefreshPalette(doc);
            }
            catch(System.Exception ex){var log=Diagnostics.Write(ex);doc.Editor.WriteMessage("\nПодрезка: "+ex.Message+"\nЖурнал: "+log);}
            finally{Diagnostics.EndLiveTrace();}
        }
    }
}
