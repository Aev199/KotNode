using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using KotNode.Core;
namespace KotNode.AutoCAD
{
    internal static class WeldContext
    {
        internal sealed class Source {internal AssemblyViews.Entry Owner;internal ViewWeldOverlay Weld;}
        internal sealed class Hit {internal AssemblyViews.Entry Displayed;internal Source Source;internal double Distance;}
        internal static List<Source> Sources(Database db,Transaction tr,AssemblyMark mark,AssemblyViews.Entry current=null)
        {
            var views=AssemblyViews.Find(db,tr,mark).Where(e=>!e.Data.ManualSnapshot&&e.Data.ParametricPrototype).ToList();
            if(current!=null){views.RemoveAll(e=>e.Data.Id==current.Data.Id);views.Add(current);}
            return views.SelectMany(e=>(e.Data.Welds??new List<ViewWeldOverlay>()).Select(w=>new Source{Owner=e,Weld=w})).ToList();
        }
        internal static bool RegisterExisting(Database db,Transaction tr,AssemblyMark mark)
        {
            bool changed=false;
            foreach(var group in Sources(db,tr,mark).GroupBy(s=>s.Owner.Data.Id))
            {
                bool viewChanged=false;
                foreach(var source in group)
                {
                    int count=mark.WeldDefinitions?.Count??0;var old=source.Weld.DefinitionId;
                    WeldCatalog.Register(mark,source.Weld);
                    viewChanged|=old!=source.Weld.DefinitionId;changed|=viewChanged||(mark.WeldDefinitions?.Count??0)!=count;
                }
                if(viewChanged)AssemblyViews.Save(group.First().Owner.Reference,tr,group.First().Owner.Data);
            }
            return changed;
        }
        internal static List<ViewWeldOverlay> Effective(AssemblyMark mark,IEnumerable<Source> sources)
            =>sources.GroupBy(s=>s.Weld.Id).Select(g=>WeldCatalog.Effective(mark,g.First().Weld)).ToList();
        internal static List<WeldCutPatch> Patches(AssemblyMark mark,AssemblyViews.ViewData data,IList<ParametricProjection.Curve2> raw,IEnumerable<ViewWeldOverlay> all)
        {
            if(!data.IsSection)return new List<WeldCutPatch>();
            var patches=WeldSections.Build(mark,AssemblyViews.ResolveFrame(mark,data),all);
            patches.AddRange(WeldSections.BuildLocal(raw,(data.Welds??new List<ViewWeldOverlay>()).Select(w=>WeldCatalog.Effective(mark,w)),out _));
            return patches;
        }
        internal static Hit Pick(Database db,Transaction tr,AssemblyMark mark,Point3d world,double tolerance)
        {
            var sources=Sources(db,tr,mark);var all=Effective(mark,sources);Hit best=null;
            foreach(var view in AssemblyViews.Find(db,tr,mark).Where(v=>v.Data.ParametricPrototype&&!v.Data.ManualSnapshot))
            {
                var local=world.TransformBy(view.Reference.BlockTransform.Inverse());
                var raw=ParametricViewDrawing.ComputeRawCurves(mark,view.Data);
                Action<Guid,double> consider=(id,d)=>{
                    if(d>tolerance||(best!=null&&d>best.Distance))return;
                    var source=sources.FirstOrDefault(s=>s.Owner.Data.Id==view.Data.Id&&s.Weld.Id==id)??sources.FirstOrDefault(s=>s.Weld.Id==id);
                    if(source!=null)best=new Hit{Displayed=view,Source=source,Distance=d};
                };
                foreach(var w in (view.Data.Welds??new List<ViewWeldOverlay>()).Select(w=>WeldCatalog.Effective(mark,w)))
                    foreach(var s in ViewWelds.Build(raw,new[]{w},view.Data.AnnotationScale,out _))
                        consider(w.Id,ViewLineOverrides.DistanceToCurve(new ParametricProjection.Curve2{X0=s.X0,Y0=s.Y0,X1=s.X1,Y1=s.Y1},local.X,local.Y,out _,out _,out _));
                foreach(var patch in Patches(mark,view.Data,raw,all))consider(patch.WeldId,WeldSections.Distance(patch,local.X,local.Y));
            }
            return best;
        }
    }
}
