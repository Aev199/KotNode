using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.WeldCommands))]
namespace KotNode.AutoCAD
{
    public sealed class WeldCommands
    {
        static double Tolerance()=>Math.Max(3,Convert.ToDouble(AcApp.GetSystemVariable("VIEWSIZE"))*.012);
        static AssemblyMark Load(Document doc,Transaction tr,out Dictionary<Guid,BlockReference> refs)
        {
            var mark=AssemblyDrawing.Load(doc.Database,tr);
            if(mark==null)throw new InvalidOperationException("В DWG нет марки.");
            refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
            if(WeldContext.RegisterExisting(doc.Database,tr,mark))AssemblyViews.Update(doc.Database,tr,mark,refs);
            return mark;
        }
        static void Finish(Document doc)
        {doc.TransactionManager.QueueForGraphicsFlush();doc.Editor.Regen();AssemblyCommands.RefreshPalette(doc);}
        [CommandMethod("NAWELDEDIT",CommandFlags.Modal)]
        public void Edit()
        {
            var doc=AcApp.DocumentManager.MdiActiveDocument;if(doc==null)return;
            Diagnostics.Begin("NAWELDEDIT");
            try
            {
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var mark=Load(doc,tr,out var refs);
                    var pick=doc.Editor.GetPoint("\nУкажите ряд шва на виде или заполненный треугольник на сечении: ");
                    if(pick.Status!=PromptStatus.OK)return;
                    var hit=WeldContext.Pick(doc.Database,tr,mark,pick.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem),Tolerance());
                    if(hit==null)throw new InvalidOperationException("Шов не найден. Укажите его графику, при необходимости обновите виды.");
                    var weld=hit.Source.Weld;var effective=WeldCatalog.Effective(mark,weld);
                    using(var form=new Fields("Сварной шов — общий катет для вида и сечения"))
                    {
                        form.Number("Общий катет k, мм","leg",effective.LegMm);
                        form.Choice("Общий тип шва","style",new Dictionary<string,string>{{"Site","Монтажный"},{"Factory","Заводской"},{"Hidden","Скрытый"}},effective.Style.ToString());
                        form.Choice("Сторона графики этого изображения","side",new Dictionary<string,string>{{"Left","Слева по ходу кромки"},{"Right","Справа по ходу кромки"},{"Both","Обе стороны"}},weld.Side.ToString());
                        form.Number("Отступ графики исходного изображения, мм (+/−)","offset",weld.OffsetMm);
                        form.Check("Автоматические срезы продольного шва накладки","section",weld.ShowInSections!=false);
                        form.EnabledField("section",!weld.SectionOnly&&WeldSections.TryEdge(weld.SourceKey,out _));
                        form.ValidateFields=()=>{
                            var candidate=KotNode.Core.Json.Copy(weld);candidate.LegMm=form.Value("leg");candidate.OffsetMm=form.Value("offset");ViewWelds.Validate(candidate);
                        };
                        if(AcApp.ShowModalDialog(AcApp.MainWindow.Handle,form,false)!=DialogResult.OK)return;
                        var style=(ViewWeldStyle)Enum.Parse(typeof(ViewWeldStyle),form.Get("style"));
                        WeldCatalog.Set(mark,weld.DefinitionId,form.Value("leg"),style);
                        Guid group=weld.GroupId==Guid.Empty?weld.Id:weld.GroupId;
                        foreach(var w in hit.Source.Owner.Data.Welds.Where(w=>w.Id==group||w.GroupId==group))
                        {
                            w.LegMm=form.Value("leg");w.Style=style;w.OffsetMm=form.Value("offset");
                            w.Side=(ViewWeldSide)Enum.Parse(typeof(ViewWeldSide),form.Get("side"));
                            w.ShowInSections=form.Checked("section");
                        }
                    }
                    AssemblyViews.Rebuild(doc.Database,tr,mark,refs,hit.Source.Owner);
                    AssemblyViews.Update(doc.Database,tr,mark,refs);
                    AssemblyDrawing.Save(doc.Database,tr,mark);tr.Commit();
                }
                Finish(doc);doc.Editor.WriteMessage("\nОбщий катет, связанные изображения и выноски обновлены.");
            }
            catch(System.Exception ex)
            {var log=Diagnostics.Write(ex);doc.Editor.WriteMessage("\nСварной шов: "+ex.Message+"\nЖурнал: "+log);}
        }
        [CommandMethod("NAWELDSECTION",CommandFlags.Modal)]
        public void CreateSectionWeld()
        {
            var doc=AcApp.DocumentManager.MdiActiveDocument;if(doc==null)return;
            Diagnostics.Begin("NAWELDSECTION");
            try
            {
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var mark=Load(doc,tr,out var refs);
                    var point=doc.Editor.GetPoint("\nУкажите кромку/угол контакта на сечении для углового шва: ");
                    if(point.Status!=PromptStatus.OK)return;
                    var world=point.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
                    AssemblyViews.Entry selected=null;AnchorSnap snap=null;List<ParametricProjection.Curve2> raw=null;
                    foreach(var view in AssemblyViews.Find(doc.Database,tr,mark).Where(v=>v.Data.IsSection&&v.Data.ParametricPrototype&&!v.Data.ManualSnapshot))
                    {
                        if(AssemblyViews.IsStale(view.Data,AssemblyViews.Signature(mark)))continue;
                        var local=world.TransformBy(view.Reference.BlockTransform.Inverse());
                        var curves=ParametricViewDrawing.ComputeRawCurves(mark,view.Data);
                        var candidate=ViewAnnotations.SnapAnchor(ViewLineOverrides.Chain(curves.Where(c=>c.Cut)),local.X,local.Y,Tolerance());
                        if(candidate!=null&&candidate.Anchor.Kind==ViewAnchorKind.EdgePoint&&(snap==null||candidate.Distance<snap.Distance))
                        {selected=view;snap=candidate;raw=curves;}
                    }
                    if(selected==null)throw new InvalidOperationException("Кромка среза не найдена. Укажите точку на актуальном разрезе/сечении; при необходимости выполните NAUPDATEVIEWS.");
                    if(!WeldSections.TryLocalFrame(snap.Anchor,raw,out var origin,out var tangent))throw new InvalidOperationException("Не определено направление кромки.");
                    var along=doc.Editor.GetPoint("\nУкажите направление первой стороны катета вдоль выбранной кромки: ");
                    if(along.Status!=PromptStatus.OK)return;
                    var a=along.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem).TransformBy(selected.Reference.BlockTransform.Inverse());
                    double dot=(a.X-origin.X)*tangent.X+(a.Y-origin.Y)*tangent.Y;
                    if(Math.Abs(dot)<1e-6)throw new InvalidOperationException("Укажите точку вдоль кромки, отличную от точки контакта.");
                    int sign=dot>0?1:-1;
                    var side=doc.Editor.GetPoint("\nУкажите сторону размещения треугольника от кромки: ");
                    if(side.Status!=PromptStatus.OK)return;
                    var b=side.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem).TransformBy(selected.Reference.BlockTransform.Inverse());
                    double cross=tangent.X*sign*(b.Y-origin.Y)-tangent.Y*sign*(b.X-origin.X);
                    if(Math.Abs(cross)<1e-6)throw new InvalidOperationException("Укажите точку сбоку от выбранной кромки.");
                    var mode=new PromptKeywordOptions("\nКатет [New/Existing] <New>: "){AllowNone=true};
                    mode.Keywords.Add("New");mode.Keywords.Add("Existing");mode.Keywords.Default="New";
                    var choice=doc.Editor.GetKeywords(mode);
                    if(choice.Status!=PromptStatus.OK&&choice.Status!=PromptStatus.None)return;
                    var weld=new ViewWeldOverlay{InstanceId=snap.Anchor.InstanceId,SourceKey=snap.Anchor.SourceKey,SectionOnly=true,
                        SectionAnchor=KotNode.Core.Json.Copy(snap.Anchor),SectionAlongSign=sign,Side=cross>0?ViewWeldSide.Left:ViewWeldSide.Right,BatchId=Guid.NewGuid()};
                    if(choice.StringResult=="Existing")
                    {
                        var sourcePoint=doc.Editor.GetPoint("\nУкажите существующий шов на виде или сечении для общего катета: ");
                        if(sourcePoint.Status!=PromptStatus.OK)return;
                        var source=WeldContext.Pick(doc.Database,tr,mark,sourcePoint.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem),Tolerance());
                        if(source==null)throw new InvalidOperationException("Исходный шов не найден.");
                        var effective=WeldCatalog.Effective(mark,source.Source.Weld);
                        weld.DefinitionId=effective.DefinitionId;weld.LegMm=effective.LegMm;weld.Style=effective.Style;
                    }
                    else
                    {
                        using(var form=new Fields("Новый шов на сечении"))
                        {
                            form.Number("Катет k, мм","leg",6);
                            form.Choice("Тип шва","style",new Dictionary<string,string>{{"Site","Монтажный"},{"Factory","Заводской"}},"Site");
                            form.ValidateFields=()=>{weld.LegMm=form.Value("leg");ViewWelds.Validate(weld);};
                            if(AcApp.ShowModalDialog(AcApp.MainWindow.Handle,form,false)!=DialogResult.OK)return;
                            weld.LegMm=form.Value("leg");weld.Style=(ViewWeldStyle)Enum.Parse(typeof(ViewWeldStyle),form.Get("style"));
                        }
                    }
                    WeldCatalog.Register(mark,weld);
                    var patch=WeldSections.BuildLocal(raw,new[]{WeldCatalog.Effective(mark,weld)},out _).First();
                    var landing=doc.Editor.GetPoint(new PromptPointOptions("\nПоложение полки катета (Enter — без выноски): "){AllowNone=true});
                    if(landing.Status!=PromptStatus.OK&&landing.Status!=PromptStatus.None)return;
                    if(selected.Data.Welds==null)selected.Data.Welds=new List<ViewWeldOverlay>();
                    selected.Data.Welds.Add(weld);
                    if(landing.Status==PromptStatus.OK)
                    {
                        var local=landing.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem).TransformBy(selected.Reference.BlockTransform.Inverse());
                        if(selected.Data.Leaders==null)selected.Data.Leaders=new List<ViewLeader>();
                        selected.Data.Leaders.Add(new ViewLeader{Kind=ViewLeaderKind.Weld,Target=new ViewAnchor{Kind=ViewAnchorKind.WeldMidpoint,WeldId=weld.Id},
                            LandingOffsetX=local.X-patch.Points.Average(p=>p.X),LandingOffsetY=local.Y-patch.Points.Average(p=>p.Y)});
                    }
                    AssemblyViews.Rebuild(doc.Database,tr,mark,refs,selected);
                    AssemblyViews.Update(doc.Database,tr,mark,refs);
                    AssemblyDrawing.Save(doc.Database,tr,mark);tr.Commit();
                }
                Finish(doc);doc.Editor.WriteMessage("\nШов на сечении создан. Катет изменяется через NAWELDEDIT, выноска — NAWELDLEADER.");
            }
            catch(System.Exception ex)
            {var log=Diagnostics.Write(ex);doc.Editor.WriteMessage("\nШов на сечении: "+ex.Message+"\nЖурнал: "+log);}
        }
    }
}
