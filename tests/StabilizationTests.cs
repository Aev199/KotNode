using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KotNode.Core;

static class StabilizationTests
{
    static int checks;
    static void Check(bool yes,string msg){if(!yes)throw new Exception(msg);checks++;Console.WriteLine("PASS stabilization: "+msg);}
    static void Reject(Action action,string msg){try{action();}catch(InvalidDataException){Check(true,msg);return;}throw new Exception(msg);}
    static AssemblyMark Pair(out Guid a,out Guid b)
    {
        var m=new AssemblyMark();
        var i=m.AddPart(new PartDefinition{Name="Лист",Position="1",Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=100,Width=50,Thickness=10}},new RigidPlacement());
        a=i.Id;b=m.CopyInstance(a).Id;return m;
    }
    internal static void Run()
    {
        var m=Pair(out var a,out var b);
        m.PlacementRules.Add(new PlacementRule{TargetInstanceId=b,ReferenceInstanceId=a,ExprX="10"});
        m.PlacementRules.Add(new PlacementRule{TargetInstanceId=a,ExprX="100"});
        ParametricEngine.Solve(m);
        Check(m.Instances.Single(i=>i.Id==b).Placement.Origin.X==110,"reverse dependency order resolves in one pass");
        var once=Json.Write(m);ParametricEngine.Solve(m);
        Check(once==Json.Write(m),"repeated placement solve is idempotent");
        m.PlacementRules[1].ReferenceInstanceId=b;
        var before=Json.Write(m);
        Reject(()=>ParametricEngine.Solve(m),"placement cycles rejected");
        Check(before==Json.Write(m),"failed solve leaves model unchanged");
        m.PlacementRules[1].ReferenceInstanceId=Guid.NewGuid();
        Reject(()=>ParametricEngine.Solve(m),"missing reference never becomes WCS");
        var f=NodeFamilyLibrary.BuiltIn()[0];
        var small=NodeFamilyLibrary.Instantiate(f,"SMALL","Малая труба",new Dictionary<string,double>{{"D",20},{"t",1}});
        Check(small.Mark.Parts.Single(p=>p.Kind==PartKind.Pipe).Pipe.Thickness==1,"simultaneous pipe diameter and wall reduction accepted");
        small.Mark.Parameters.Single(p=>p.Name=="t").Value=12;
        before=Json.Write(small.Mark);
        Reject(()=>ParametricEngine.Solve(small.Mark),"invalid final pipe dimensions rejected");
        Check(before==Json.Write(small.Mark),"invalid dimensions preserve previous model");
        var baseFamily=NodeFamilyLibrary.BuiltIn()[1];
        var rib=baseFamily.Mark.Parts.Single(p=>p.Position=="2");
        Check(AssemblyReporting.VrsProfile(rib)=="Лист t=12","U02 ribs grouped by actual 12 mm stock thickness");
        var ribInstance=baseFamily.Mark.Instances.First(i=>i.PartId==rib.Id);
        var zTop=ribInstance.Placement.ToWorld(new Vector3(0,250,0)).Z;
        Check(Math.Abs(zTop-270)<1e-8,"U02 rib height follows local sheet width after rotation");
        var array=Pair(out a,out b);array.Instances.RemoveAll(i=>i.Id==b);
        array.Instances[0].Placement.Origin=new Vector3(10,0,0);
        array.PlacementRules.Add(new PlacementRule{Kind=PlacementRuleKind.CircularArray,TargetInstanceId=a,ExprCount="4",ExprRotZDeg="30"});
        ParametricEngine.Solve(array);
        var child=array.Instances[1].Placement.Origin;
        Check(Math.Abs(Math.Atan2(child.Y,child.X)*180/Math.PI-30)<1e-8,"circular array respects GUI Rz angle");
        var last=new Point2(100,200);var vd=new Point2(50,60);
        var off=AnnotationPlacement.Capture(new Point2(70,170),new Point2(50,140),last,vd);
        Check(Math.Abs(off.X-20)<1e-8&&Math.Abs(off.Y-30)<1e-8,"annotations follow view-only MOVE");
        off=AnnotationPlacement.Capture(new Point2(120,230),last,last,vd);
        Check(off.X==20&&off.Y==30,"moving view and annotations together applies no duplicate displacement");
        off=AnnotationPlacement.Capture(new Point2(140,250),last,last,new Point2(0,0));
        Check(off.X==40&&off.Y==50,"manual dimension line and landing offsets preserved");
        var template=NodeFamilyLibrary.BuiltIn()[0];
        var oldTarget=template.Views[1].Id;
        template.Views[0].SectionCallouts.Add(new ViewSectionCallout{TargetViewId=oldTarget,Label="1-1"});
        template.Views[0].ManualGraphicsDwg="payload-roundtrip";
        var inserted=NodeFamilyLibrary.Instantiate(template,"REUSE","Повторная вставка");
        Check(inserted.Views[1].Id!=oldTarget&&inserted.Views[0].SectionCallouts[0].TargetViewId==inserted.Views[1].Id,"family callouts remapped to new view IDs");
        Check(template.Views[0].SectionCallouts[0].TargetViewId==oldTarget,"family instantiation preserves original template links");
        Check(Json.Copy(inserted).Views[0].ManualGraphicsDwg=="payload-roundtrip","native manual graphics payload survives JSON roundtrip");
        var scene=new AssemblyMark();
        var pipe=scene.AddPart(new PartDefinition{Name="Труба",Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=100,Thickness=5,Length=200}},new RigidPlacement());
        var frame=new RigidPlacement();
        var oldRaw=ParametricProjection.Build(scene,frame,false,true).Curves;
        var raw=ParametricProjection.BuildStable(scene,frame,false,true).Curves;
        var legacyRim=oldRaw.Single(c=>c.Radius==50);
        var legacyEdit=ViewLineOverrides.Create(legacyRim,ViewLineMode.Hidden,true,Guid.NewGuid());
        var migrated=ViewReferenceMigration.Lines(new[]{legacyEdit},oldRaw,raw);
        var applied=ViewLineOverrides.Apply(raw,migrated,true,out var missing);
        Check(missing==0&&applied.Any(c=>c.Hidden),"legacy analytic rim migrates to stable physical edge");
        var rimAnchor=new ViewAnchor{Kind=ViewAnchorKind.EdgePoint,InstanceId=pipe.Id,SourceKey=legacyRim.SourceKey,Param=.25};
        ViewReferenceMigration.Anchor(rimAnchor,oldRaw,raw);
        var point=ViewAnnotations.ResolveAnchor(rimAnchor,raw,null,20,out _);
        Check(point!=null&&Math.Abs(point.Y-50)<.11&&Math.Abs(point.X)<.11,"legacy rim anchor retains physical point after migration");
        var oldKeys=raw.Where(c=>c.InstanceId==pipe.Id).Select(c=>c.SourceKey).Distinct().ToList();
        scene.AddPart(new PartDefinition{Name="Накладка",Position="2",Kind=PartKind.CurvedPlate,CurvedPlate=new CurvedPlateShape{InnerRadius=20,Thickness=3,Length=40,AngleRadians=1}},new RigidPlacement{Origin=new Vector3(500,500,0)});
        var newRaw=ParametricProjection.BuildStable(scene,frame,false,true).Curves;
        Check(oldKeys.All(k=>newRaw.Any(c=>c.InstanceId==pipe.Id&&c.SourceKey==k)),"adding unrelated curved plate preserves all pipe source identities");
        ViewLineOverrides.Apply(newRaw,migrated,true,out missing);
        Check(missing==0,"migrated edits remain attached after assembly composition change");
        var oldWeld=new ViewWeldOverlay{InstanceId=pipe.Id,SourceKey=legacyRim.SourceKey};
        var migratedWeld=ViewReferenceMigration.Welds(new[]{oldWeld},oldRaw,raw);
        ViewWelds.Build(newRaw,migratedWeld,20,out missing);
        Check(missing==0,"migrated weld survives unrelated shape addition");
        var rotation=new RigidPlacement{AxisX=new Vector3(Math.Sqrt(.5),0,-Math.Sqrt(.5)),AxisY=new Vector3(0,1,0)};
        scene.Instances.Single(i=>i.Id==pipe.Id).Placement=rotation;
        var rotated=ParametricProjection.BuildStable(scene,frame,false,true).Curves;
        ViewLineOverrides.Apply(rotated,migrated,true,out missing);
        Check(missing==0,"physical rim override survives oblique rotation");
        var fragment=ViewLineOverrides.Create(legacyRim,ViewLineMode.Hidden,true,Guid.NewGuid());fragment.From=.2;fragment.To=.4;
        var fragmentMigration=ViewReferenceMigration.Lines(new[]{fragment},oldRaw,raw);
        Check(fragmentMigration.All(x=>x.From>=.2-1e-6&&x.To<=.4+1e-6),"legacy partial rim migration retains normalized interval");
        var unsupported=Json.Copy(legacyEdit);unsupported.SourceKey="analytic:missing";
        Reject(()=>ViewReferenceMigration.Lines(new[]{unsupported},oldRaw,raw),"unresolvable legacy reference rejected instead of silently discarded");
        var plateScene=Pair(out a,out b);plateScene.Instances.RemoveAll(i=>i.Id==b);
        var plateOld=ParametricProjection.Build(plateScene,frame,false,true).Curves;
        var plateNew=ParametricProjection.BuildStable(plateScene,frame,false,true).Curves;
        var edits=plateOld.Select(c=>ViewLineOverrides.Create(c,ViewLineMode.Hidden,true,Guid.NewGuid())).ToList();
        var plateMigrated=ViewReferenceMigration.Lines(edits,plateOld,plateNew);
        ViewLineOverrides.Apply(plateNew,plateMigrated,true,out missing);
        Check(missing==0,"all old plate outline edges migrate to stable source keys");
        var thin=new AssemblyMark();thin.AddPart(new PartDefinition{Name="Тонкая труба",Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=100,Thickness=.01,Length=200}},frame);
        var thinOld=ParametricProjection.Build(thin,frame,false,true).Curves;
        var thinNew=ParametricProjection.BuildStable(thin,frame,false,true).Curves;
        var thinRule=ViewLineOverrides.Create(thinOld.Single(c=>c.Radius==50),ViewLineMode.Hidden,true,Guid.NewGuid());
        var thinMigrated=ViewReferenceMigration.Lines(new[]{thinRule},thinOld,thinNew);
        Check(thinMigrated.All(r=>r.SourceKey.EndsWith("rim:0")),"legacy outer rim never migrates to nearby inner rim below sagitta tolerance");
        var exactStable=ParametricProjection.BuildStable(new AssemblyMark{Parts=thin.Parts,Instances=thin.Instances},frame,false,true);
        Check(exactStable.Curves.Any(c=>c.Circular&&c.Radius==50&&Math.Abs(c.Sweep-2*Math.PI)<1e-7),"stable physical keys retain exact full circular pipe rims");
        var slitScene=new AssemblyMark();
        slitScene.AddPart(new PartDefinition{Name="Труба",Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=100,Thickness=5,Length=200}},frame);
        slitScene.AddPart(new PartDefinition{Name="Лист 1",Position="1",Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=100,Width=200,Thickness=10}},new RigidPlacement{Origin=new Vector3(-100,-100,220)});
        slitScene.AddPart(new PartDefinition{Name="Лист 2",Position="2",Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=100,Width=200,Thickness=10}},new RigidPlacement{Origin=new Vector3(.01,-100,220)});
        var preciseSlit=ParametricProjection.BuildStable(slitScene,frame).Curves.Where(c=>c.Circular&&Math.Abs(c.Radius-50)<1e-6).Sum(c=>c.Sweep);
        Check(Math.Abs(preciseSlit-2*Math.Asin(.01/50))<1e-7,"stable identity pipeline preserves narrow exact visible slit");
        Console.WriteLine(checks+" stabilization checks passed");
    }
}
