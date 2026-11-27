using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
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
        [CommandMethod("NAWELDEDIT",CommandFlags.Modal)]
        public void Edit()
        {
            var doc=AcApp.DocumentManager.MdiActiveDocument;if(doc==null)return;
            Diagnostics.Begin("NAWELDEDIT");
            try
            {
                var pick=doc.Editor.GetPoint("\nУкажите графику существующего шва на исходном виде: ");
                if(pick.Status!=PromptStatus.OK)return;
                var world=pick.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
                double tolerance=Math.Max(3,Convert.ToDouble(AcApp.GetSystemVariable("VIEWSIZE"))*.012);
                using(var tr=doc.Database.TransactionManager.StartTransaction())
                {
                    var mark=AssemblyDrawing.Load(doc.Database,tr);
                    if(mark==null)throw new InvalidOperationException("В DWG нет марки.");
                    var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
                    AssemblyViews.Entry selected=null;ViewWeldOverlay weld=null;double distance=double.PositiveInfinity;
                    foreach(var view in AssemblyViews.Find(doc.Database,tr,mark).Where(v=>v.Data.ParametricPrototype&&!v.Data.ManualSnapshot))
                    {
                        var local=world.TransformBy(view.Reference.BlockTransform.Inverse());
                        var raw=ParametricViewDrawing.ComputeRawCurves(mark,view.Data);
                        foreach(var w in view.Data.Welds??new List<ViewWeldOverlay>())
                            foreach(var stroke in ViewWelds.Build(raw,new[]{w},view.Data.AnnotationScale,out _))
                            {
                                var line=new ParametricProjection.Curve2{X0=stroke.X0,Y0=stroke.Y0,X1=stroke.X1,Y1=stroke.Y1};
                                double d=ViewLineOverrides.DistanceToCurve(line,local.X,local.Y,out _,out _,out _);
                                if(d<=tolerance&&d<distance){selected=view;weld=w;distance=d;}
                            }
                    }
                    if(weld==null)throw new InvalidOperationException("Шов не найден. Укажите ряд крестиков/ресничек на исходном виде, а не линию детали или треугольник разреза.");
                    using(var form=new Fields("Свойства сварного шва"))
                    {
                        form.Number("Катет k, мм","leg",weld.LegMm);
                        form.Choice("Тип графики","style",new Dictionary<string,string>{{"Site","Монтажный"},{"Factory","Заводской"},{"Hidden","Скрытый"}},weld.Style.ToString());
                        form.Choice("Сторона от кромки","side",new Dictionary<string,string>{{"Left","Слева по ходу кромки"},{"Right","Справа по ходу кромки"},{"Both","На кромке / обе стороны"}},weld.Side.ToString());
                        form.Number("Дополнительный отступ графики, мм (+/−)","offset",weld.OffsetMm);
                        form.Check("Показывать срез продольного шва накладки в разрезах","section",weld.ShowInSections!=false);
                        form.ValidateFields=()=>{
                            var candidate=KotNode.Core.Json.Copy(weld);candidate.LegMm=form.Value("leg");candidate.OffsetMm=form.Value("offset");ViewWelds.Validate(candidate);
                        };
                        if(AcApp.ShowModalDialog(form)!=DialogResult.OK)return;
                        Guid group=weld.GroupId==Guid.Empty?weld.Id:weld.GroupId;
                        foreach(var w in selected.Data.Welds.Where(w=>w.Id==group||w.GroupId==group))
                        {
                            w.LegMm=form.Value("leg");w.OffsetMm=form.Value("offset");
                            w.Style=(ViewWeldStyle)Enum.Parse(typeof(ViewWeldStyle),form.Get("style"));
                            w.Side=(ViewWeldSide)Enum.Parse(typeof(ViewWeldSide),form.Get("side"));
                            w.ShowInSections=form.Checked("section");
                        }
                    }
                    AssemblyViews.Rebuild(doc.Database,tr,mark,refs,selected);
                    AssemblyViews.Update(doc.Database,tr,mark,refs);
                    AssemblyDrawing.Save(doc.Database,tr,mark);tr.Commit();
                    if(weld.ShowInSections!=false&&!WeldSections.TryEdge(weld.SourceKey,out _))
                        doc.Editor.WriteMessage("\nЭта кромка не определяет продольный стык накладки. Автоматический срез доступен для начальной/конечной продольной кромки криволинейной накладки; силуэт не определяет физический шов.");
                }
                doc.Editor.Regen();AssemblyCommands.RefreshPalette(doc);
                doc.Editor.WriteMessage("\nШов, его выноска и связанные разрезы обновлены.");
            }
            catch(System.Exception ex)
            {var log=Diagnostics.Write(ex);doc.Editor.WriteMessage("\nСварной шов: "+ex.Message+"\nЖурнал: "+log);}
        }
    }
}
