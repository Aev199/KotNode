using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.AnnotationCommands))]

namespace KotNode.AutoCAD
{
    // Stage 5: Associative view annotations (dimensions, position/weld leaders, axes, section marks).
    public sealed class AnnotationCommands
    {
        sealed class ViewSnapContext
        {
            public Guid ViewId;
            public string Name;
            public double AnnotationScale;
            public Matrix3d ToWorld;
            public Matrix3d ToLocal;
            public List<CurveChain> Chains;
            public List<ParametricProjection.Curve2> RawCurves;
            public List<ViewWeldOverlay> Welds;
        }
        static double PickTol()
        {
            try
            {
                double vs=Convert.ToDouble(AcApp.GetSystemVariable("VIEWSIZE"));
                if(vs>0&&!double.IsNaN(vs)&&!double.IsInfinity(vs))return Math.Max(3.0,vs*0.02);
            }
            catch { }
            return 15.0;
        }
        static List<ViewSnapContext> PrepareViews(Document doc)
        {
            var list=new List<ViewSnapContext>();
            bool upgraded=false;
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var m=AssemblyDrawing.Load(doc.Database,tr);
                if(m==null)throw new InvalidOperationException("Создайте марку командой NANEW.");
                var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                var sig=AssemblyViews.Signature(m);
                foreach(var entry in AssemblyViews.Find(doc.Database,tr,m))
                {
                    if(entry.Data.ManualSnapshot||string.IsNullOrEmpty(entry.Data.GeneratedChildHandle))continue;
                    if(entry.Data.SourceHash!=sig)continue;
                    if(entry.Data.GeneratorRevision!=AssemblyViews.Revision)
                    {
                        try{AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);upgraded=true;}
                        catch{continue;}
                    }
                    var world=entry.Reference.BlockTransform;
                    list.Add(new ViewSnapContext
                    {
                        ViewId=entry.Data.Id,Name=entry.Data.Name,
                        AnnotationScale=entry.Data.AnnotationScale>0?entry.Data.AnnotationScale:20,
                        ToWorld=world,ToLocal=world.Inverse(),
                        RawCurves=ParametricViewDrawing.ComputeRawCurves(m,entry.Data),
                        Chains=ParametricViewDrawing.ComputeChains(m,entry.Data),
                        Welds=entry.Data.Welds??new List<ViewWeldOverlay>()
                    });
                }
                if(upgraded)AssemblyDrawing.Save(doc.Database,tr,m);
                tr.Commit();
            }
            if(upgraded){doc.TransactionManager.QueueForGraphicsFlush();doc.Editor.Regen();}
            if(list.Count==0)throw new InvalidOperationException("Нет актуальных параметрических видов. Создайте или обновите вид (NAUPDATEVIEWS).");
            return list;
        }
        static bool PickAnchor(Editor ed,List<ViewSnapContext> views,string prompt,bool preferCenter,ref ViewSnapContext locked,out AnchorSnap snap)
        {
            snap=null;
            var p=ed.GetPoint("\n"+prompt);
            if(p.Status!=PromptStatus.OK)return false;
            var world=p.Value.TransformBy(ed.CurrentUserCoordinateSystem);
            double tol=PickTol();
            ViewSnapContext bestView=null;AnchorSnap bestSnap=null;
            foreach(var v in views)
            {
                if(locked!=null&&v.ViewId!=locked.ViewId)continue;
                var local=world.TransformBy(v.ToLocal);
                var s=ViewAnnotations.SnapAnchor(v.Chains,local.X,local.Y,tol,preferCenter);
                if(s!=null&&(bestSnap==null||s.Distance<bestSnap.Distance)){bestSnap=s;bestView=v;}
            }
            if(bestSnap==null){ed.WriteMessage("\nРядом с точкой не найдено привязки на виде.");return false;}
            locked=bestView;snap=bestSnap;
            ed.WriteMessage("\nПривязка: "+snap.Label+" (вид «"+locked.Name+"»)");
            return true;
        }
        static void CommitViewMutation(Document doc,Guid viewId,Action<AssemblyViews.ViewData> mutate)
        {
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var m=AssemblyDrawing.Load(doc.Database,tr);
                if(m==null)throw new InvalidOperationException("Создайте марку командой NANEW.");
                var refs=AssemblyDrawing.Synchronize(doc.Database,tr,m);
                var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Data.Id==viewId);
                if(entry==null)throw new InvalidOperationException("Представление не найдено.");
                mutate(entry.Data);
                AssemblyViews.Rebuild(doc.Database,tr,m,refs,entry);
                AssemblyDrawing.Save(doc.Database,tr,m);
                tr.Commit();
            }
            doc.TransactionManager.QueueForGraphicsFlush();
            doc.Editor.Regen();
            AssemblyCommands.RefreshPalette(doc);
        }
        static void RunSafe(string cmd,Action<Document> body)
        {
            Diagnostics.Begin(cmd);
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            try
            {
                if(!doc.Database.TileMode||doc.Database.Insunits!=UnitsValue.Millimeters)
                    throw new InvalidOperationException("Откройте пространство модели (TILEMODE=1), единицы DWG — миллиметры (INSUNITS=4).");
                body(doc);
            }
            catch(System.Exception ex)
            {
                var log=Diagnostics.Write(ex);
                doc.Editor.WriteMessage("\nОформление: "+ex.Message+"\nЖурнал: "+log);
            }
        }
        [CommandMethod("NADIM",CommandFlags.Modal)]
        public void AddDimension()=>RunSafe("NADIM",doc=>{
            var views=PrepareViews(doc);
            var ed=doc.Editor;
            var opt=new PromptKeywordOptions("\nТип размера [Horizontal/Vertical/Aligned/Diameter/Radius] <Horizontal>: "){AllowNone=true};
            foreach(var k in new[]{"Horizontal","Vertical","Aligned","Diameter","Radius"})opt.Keywords.Add(k);
            opt.Keywords.Default="Horizontal";
            var ans=ed.GetKeywords(opt);
            if(ans.Status!=PromptStatus.OK&&ans.Status!=PromptStatus.None)return;
            var kind=ans.StringResult=="Vertical"?ViewDimensionKind.Vertical:ans.StringResult=="Aligned"?ViewDimensionKind.Aligned:ans.StringResult=="Diameter"?ViewDimensionKind.Diameter:ans.StringResult=="Radius"?ViewDimensionKind.Radius:ViewDimensionKind.Horizontal;

            ViewSnapContext view=null;
            if(kind==ViewDimensionKind.Diameter||kind==ViewDimensionKind.Radius)
            {
                if(!PickAnchor(ed,views,"Укажите окружность или центр трубы/отверстия на виде: ",true,ref view,out var snap))return;
                var lp=ed.GetPoint("\nПоложение размерной линии: ");
                if(lp.Status!=PromptStatus.OK)return;
                var local=lp.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(view.ToLocal);
                var dim=new ViewDimension{Kind=kind,AnchorA=snap.Anchor,OffsetX=local.X-snap.Point.X,OffsetY=local.Y-snap.Point.Y};
                CommitViewMutation(doc,view.ViewId,d=>{if(d.Dimensions==null)d.Dimensions=new List<ViewDimension>();d.Dimensions.Add(dim);});
                ed.WriteMessage("\nАссоциативный размер добавлен.");
                return;
            }
            if(!PickAnchor(ed,views,"Первая точка размера на виде: ",false,ref view,out var snapA))return;
            if(!PickAnchor(ed,views,"Вторая точка размера на виде: ",false,ref view,out var snapB))return;
            var linePt=ed.GetPoint("\nПоложение размерной линии: ");
            if(linePt.Status!=PromptStatus.OK)return;
            var locLine=linePt.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(view.ToLocal);
            double midX=(snapA.Point.X+snapB.Point.X)/2,midY=(snapA.Point.Y+snapB.Point.Y)/2;
            var linear=new ViewDimension{Kind=kind,AnchorA=snapA.Anchor,AnchorB=snapB.Anchor,OffsetX=locLine.X-midX,OffsetY=locLine.Y-midY};
            CommitViewMutation(doc,view.ViewId,d=>{if(d.Dimensions==null)d.Dimensions=new List<ViewDimension>();d.Dimensions.Add(linear);});
            ed.WriteMessage("\nАссоциативный размер добавлен.");
        });
        [CommandMethod("NAPOS",CommandFlags.Modal)]
        public void AddPositionLeader()=>RunSafe("NAPOS",doc=>{
            var views=PrepareViews(doc);
            var ed=doc.Editor;
            ViewSnapContext view=null;
            if(!PickAnchor(ed,views,"Укажите кромку детали на виде для выноски позиции: ",false,ref view,out var snap))return;
            var land=ed.GetPoint("\nПоложение полки выноски: ");
            if(land.Status!=PromptStatus.OK)return;
            var local=land.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(view.ToLocal);
            var leader=new ViewLeader{Kind=ViewLeaderKind.Position,Target=snap.Anchor,LandingOffsetX=local.X-snap.Point.X,LandingOffsetY=local.Y-snap.Point.Y};
            CommitViewMutation(doc,view.ViewId,d=>{if(d.Leaders==null)d.Leaders=new List<ViewLeader>();d.Leaders.Add(leader);});
            ed.WriteMessage("\nАссоциативная выноска позиции добавлена.");
        });
        [CommandMethod("NAWELDLEADER",CommandFlags.Modal)]
        public void AddWeldLeader()=>RunSafe("NAWELDLEADER",doc=>{
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var mark=AssemblyDrawing.Load(doc.Database,tr);
                if(mark==null)throw new InvalidOperationException("В DWG нет марки.");
                var refs=AssemblyDrawing.Synchronize(doc.Database,tr,mark);
                if(WeldContext.RegisterExisting(doc.Database,tr,mark))AssemblyViews.Update(doc.Database,tr,mark,refs);
                var p=doc.Editor.GetPoint("\nУкажите ряд шва на виде или треугольник шва на сечении: ");
                if(p.Status!=PromptStatus.OK)return;
                var hit=WeldContext.Pick(doc.Database,tr,mark,p.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem),PickTol());
                if(hit==null)throw new InvalidOperationException("Шов не найден. Создайте его через NAWELD или NAWELDSECTION.");
                var entry=hit.Displayed;var raw=ParametricViewDrawing.ComputeRawCurves(mark,entry.Data);
                var all=WeldContext.Effective(mark,WeldContext.Sources(doc.Database,tr,mark,entry));
                var patches=WeldContext.Patches(mark,entry.Data,raw,all);
                var anchor=new ViewAnchor{Kind=ViewAnchorKind.WeldMidpoint,WeldId=hit.Source.Weld.Id};
                var at=ViewAnnotations.ResolveAnchor(anchor,raw,all,entry.Data.AnnotationScale,out _,patches);
                if(at==null)throw new InvalidOperationException("Не удалось разрешить привязку шва.");
                var point=doc.Editor.GetPoint("\nПоложение полки катета: ");
                if(point.Status!=PromptStatus.OK)return;
                var local=point.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem).TransformBy(entry.Reference.BlockTransform.Inverse());
                if(entry.Data.Leaders==null)entry.Data.Leaders=new List<ViewLeader>();
                entry.Data.Leaders.Add(new ViewLeader{Kind=ViewLeaderKind.Weld,Target=anchor,LandingOffsetX=local.X-at.X,LandingOffsetY=local.Y-at.Y});
                AssemblyViews.Rebuild(doc.Database,tr,mark,refs,entry);
                AssemblyDrawing.Save(doc.Database,tr,mark);tr.Commit();
            }
            doc.TransactionManager.QueueForGraphicsFlush();doc.Editor.Regen();AssemblyCommands.RefreshPalette(doc);
            doc.Editor.WriteMessage("\nАссоциативная выноска общего катета добавлена.");
        });
        [CommandMethod("NAAXIS",CommandFlags.Modal)]
        public void AddAxis()=>RunSafe("NAAXIS",doc=>{
            var views=PrepareViews(doc);
            var ed=doc.Editor;
            var opt=new PromptKeywordOptions("\nТип оси [Cross/Horizontal/Vertical] <Cross>: "){AllowNone=true};
            foreach(var k in new[]{"Cross","Horizontal","Vertical"})opt.Keywords.Add(k);
            opt.Keywords.Default="Cross";
            var ans=ed.GetKeywords(opt);
            if(ans.Status!=PromptStatus.OK&&ans.Status!=PromptStatus.None)return;
            bool h=ans.StringResult!="Vertical",v=ans.StringResult!="Horizontal";
            ViewSnapContext view=null;
            if(!PickAnchor(ed,views,"Укажите центр трубы, отверстия или середину кромки на виде: ",true,ref view,out var snap))return;
            var axis=new ViewAxisMark{Center=snap.Anchor,Horizontal=h,Vertical=v};
            CommitViewMutation(doc,view.ViewId,d=>{if(d.Axes==null)d.Axes=new List<ViewAxisMark>();d.Axes.Add(axis);});
            ed.WriteMessage("\nАссоциативная ось добавлена.");
        });
        [CommandMethod("NASECTIONMARK",CommandFlags.Modal)]
        public void AddSectionMark()=>RunSafe("NASECTIONMARK",doc=>{
            var views=PrepareViews(doc);
            var ed=doc.Editor;
            ViewSnapContext view=null;
            if(!PickAnchor(ed,views,"Начальная точка линии разреза на виде: ",false,ref view,out var snapA))return;
            if(!PickAnchor(ed,views,"Конечная точка линии разреза на виде: ",false,ref view,out var snapB))return;
            var sidePt=ed.GetPoint("\nУкажите направление взгляда разреза (сторона стрелок): ");
            if(sidePt.Status!=PromptStatus.OK)return;
            var local=sidePt.Value.TransformBy(ed.CurrentUserCoordinateSystem).TransformBy(view.ToLocal);
            double dx=snapB.Point.X-snapA.Point.X,dy=snapB.Point.Y-snapA.Point.Y;
            double cross=dx*(local.Y-snapA.Point.Y)-dy*(local.X-snapA.Point.X);
            int dir=cross>=0?1:-1;
            var lblOpt=new PromptStringOptions("\nОбозначение разреза (или Enter — «1»): "){AllowSpaces=true};
            var lblRes=ed.GetString(lblOpt);
            if(lblRes.Status!=PromptStatus.OK&&lblRes.Status!=PromptStatus.None)return;
            string label=string.IsNullOrWhiteSpace(lblRes.StringResult)?"1":lblRes.StringResult.Trim();
            Guid targetViewId=Guid.Empty;
            var matchedSection=views.FirstOrDefault(v=>v.ViewId!=view.ViewId&&string.Equals(v.Name.Trim(),label,StringComparison.OrdinalIgnoreCase));
            if(matchedSection!=null)targetViewId=matchedSection.ViewId;
            var callout=new ViewSectionCallout{Start=snapA.Anchor,End=snapB.Anchor,DirectionSign=dir,Label=label,TargetViewId=targetViewId};
            CommitViewMutation(doc,view.ViewId,d=>{if(d.SectionCallouts==null)d.SectionCallouts=new List<ViewSectionCallout>();d.SectionCallouts.Add(callout);});
            ed.WriteMessage("\nОбозначение разреза «"+label+"» добавлено.");
        });
        [CommandMethod("NAANNORESET",CommandFlags.Modal)]
        public void ResetAnnotations()=>RunSafe("NAANNORESET",doc=>{
            var ed=doc.Editor;
            var picked=ed.GetEntity("\nВыберите вид для очистки ассоциативного оформления: ");
            if(picked.Status!=PromptStatus.OK)return;
            var opt=new PromptKeywordOptions("\nОчистить [All/Dimensions/Leaders/Axes/Callouts] <All>: "){AllowNone=true};
            foreach(var k in new[]{"All","Dimensions","Leaders","Axes","Callouts"})opt.Keywords.Add(k);
            opt.Keywords.Default="All";
            var ans=ed.GetKeywords(opt);
            if(ans.Status!=PromptStatus.OK&&ans.Status!=PromptStatus.None)return;
            string mode=string.IsNullOrEmpty(ans.StringResult)?"All":ans.StringResult;
            Guid viewId;
            using(var tr=doc.Database.TransactionManager.StartTransaction())
            {
                var m=AssemblyDrawing.Load(doc.Database,tr);
                if(m==null)throw new InvalidOperationException("Создайте марку командой NANEW.");
                var entry=AssemblyViews.Find(doc.Database,tr,m).SingleOrDefault(e=>e.Reference.ObjectId==picked.ObjectId);
                if(entry==null||entry.Data.ManualSnapshot)throw new InvalidOperationException("Выберите параметрический вид.");
                viewId=entry.Data.Id;
                tr.Commit();
            }
            CommitViewMutation(doc,viewId,d=>{
                if(mode=="All"||mode=="Dimensions")d.Dimensions?.Clear();
                if(mode=="All"||mode=="Leaders")d.Leaders?.Clear();
                if(mode=="All"||mode=="Axes")d.Axes?.Clear();
                if(mode=="All"||mode=="Callouts")d.SectionCallouts?.Clear();
            });
            ed.WriteMessage("\nАссоциативное оформление очищено ("+mode+").");
        });
    }
}
