using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KotNode.Core;
static class WeldLinkingTests
{
    static int count;
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);count++;Console.WriteLine("PASS linked weld: "+name);}
    static double Area(WeldCutPatch p)=>Math.Abs(p.Points.Select((x,i)=>x.X*p.Points[(i+1)%p.Points.Count].Y-x.Y*p.Points[(i+1)%p.Points.Count].X).Sum())/2;
    internal static void Run()
    {
        var mark=new AssemblyMark();
        mark.AddPart(new PartDefinition{Name="Лист",Position="1",Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=100,Width=80,Thickness=10}},new RigidPlacement());
        var frame=new RigidPlacement{Origin=new Vector3(0,0,5)};
        var raw=ParametricProjection.BuildStable(mark,frame,true,true).Curves;
        var edge=raw.First(c=>c.Cut&&!c.Circular&&Math.Abs(c.X1-c.X0)>50);
        var anchor=new ViewAnchor{Kind=ViewAnchorKind.EdgePoint,InstanceId=edge.InstanceId,SourceKey=edge.SourceKey,Param=(edge.SourceFrom+edge.SourceTo)/2};
        var source=new ViewWeldOverlay{InstanceId=edge.InstanceId,SourceKey=edge.SourceKey,LegMm=6};
        WeldCatalog.Register(mark,source);
        Check(mark.Schema==2&&mark.WeldDefinitions.Count==1,"shared definitions promote the mark schema to protect against old readers");
        var section=new ViewWeldOverlay{InstanceId=edge.InstanceId,SourceKey=edge.SourceKey,SectionOnly=true,SectionAnchor=Json.Copy(anchor),Side=ViewWeldSide.Left,DefinitionId=source.DefinitionId};
        WeldCatalog.Register(mark,section);
        Check(mark.WeldDefinitions.Count==1&&source.Id!=section.Id,"view and section share one definition but keep separate presentation IDs");
        WeldCatalog.Set(mark,section.DefinitionId,12,ViewWeldStyle.Site);
        var effective=new[]{WeldCatalog.Effective(mark,source),WeldCatalog.Effective(mark,section)};
        Check(effective.All(w=>w.LegMm==12),"editing through section changes the same leg on the source view");
        var patches=WeldSections.BuildLocal(raw,new[]{effective[1]},out var missing);
        Check(missing==0&&patches.Count==1&&Math.Abs(Area(patches[0])-72)<1e-7,"section-only weld creates a 12 mm fillet triangle without a view row");
        Check(ViewWelds.Build(raw,new[]{effective[1]},20,out missing).Count==0&&missing==0,"section-only weld never invents a visible row on another view");
        var leader=new ViewLeader{Kind=ViewLeaderKind.Weld,Target=new ViewAnchor{Kind=ViewAnchorKind.WeldMidpoint,WeldId=section.Id},LandingOffsetX=80,LandingOffsetY=50};
        var leaders=ViewAnnotations.ResolveLeaders(new[]{leader},mark,raw,effective,20,out missing,patches);
        Check(missing==0&&leaders.Count==1&&leaders[0].Text=="12","leader can attach directly to section triangle and read shared leg");
        var center=new Point2(patches[0].Points.Average(p=>p.X),patches[0].Points.Average(p=>p.Y));
        Check(WeldSections.Distance(patches[0],center.X,center.Y)==0,"triangle interior is pickable for leader and edit commands");
        Check(WeldSections.Distance(patches[0],center.X+1000,center.Y+1000)>100,"triangle picking rejects distant points");
        WeldCatalog.Set(mark,source.DefinitionId,8,ViewWeldStyle.Factory);
        effective=new[]{WeldCatalog.Effective(mark,source),WeldCatalog.Effective(mark,section)};
        patches=WeldSections.BuildLocal(raw,new[]{effective[1]},out missing);
        leaders=ViewAnnotations.ResolveLeaders(new[]{leader},mark,raw,effective,20,out missing,patches);
        Check(leaders[0].Text=="8"&&Math.Abs(Area(patches[0])-32)<1e-7,"editing source view updates triangle and existing section leader together");
        var independent=Json.Copy(section);independent.Id=Guid.NewGuid();independent.DefinitionId=Guid.Empty;independent.LegMm=5;
        WeldCatalog.Register(mark,independent);
        Check(WeldCatalog.Effective(mark,independent).LegMm==5&&mark.WeldDefinitions.Count==2,"independent section weld has its own leg definition");
        mark.Parts[0].Plate.Length=200;
        var resized=ParametricProjection.BuildStable(mark,frame,true,true).Curves;
        var moved=WeldSections.BuildLocal(resized,new[]{WeldCatalog.Effective(mark,section)},out missing);
        Check(missing==0&&Math.Abs(moved[0].Points[0].X-patches[0].Points[0].X-50)<1e-7,"section weld follows its semantic edge point after plate resize");
        var loadedMark=Json.Copy(mark);var loadedSection=Json.Copy(section);
        Check(WeldCatalog.Effective(loadedMark,loadedSection).LegMm==8&&loadedSection.SectionAnchor.SourceKey==anchor.SourceKey,"DWG/family JSON roundtrip retains definition and section anchor");
        var family=new NodeFamilyTemplate{Name="Тест швов",Mark=mark};
        var inserted=NodeFamilyLibrary.Instantiate(family,"W-02","Копия");
        WeldCatalog.Set(inserted.Mark,section.DefinitionId,10,ViewWeldStyle.Site);
        Check(WeldCatalog.Effective(mark,section).LegMm==8&&WeldCatalog.Effective(inserted.Mark,section).LegMm==10,"new family mark owns independent definitions despite preserved internal IDs");
        var oldSchema=Json.Copy(mark);oldSchema.Schema=1;bool rejected=false;
        try{oldSchema.Validate();}catch(InvalidDataException){rejected=true;}
        Check(rejected,"shared definitions cannot be saved under old mark schema");
        rejected=false;try{WeldCatalog.Set(mark,section.DefinitionId,-2,ViewWeldStyle.Site);}catch(InvalidDataException){rejected=true;}
        Check(rejected&&WeldCatalog.Effective(mark,section).LegMm==8,"invalid leg edits preserve current definition");
        Check(ViewAnnotations.ResolveLeaders(new[]{leader},mark,resized,new[]{WeldCatalog.Effective(mark,section)},20,out missing,moved).Count==1&&missing==0,"section leader survives removal of original view presentation");
        var arc=new ParametricProjection.Curve2{InstanceId=anchor.InstanceId,SourceKey=anchor.SourceKey,
            Circular=true,X0=0,Y0=0,Radius=20,Start=Math.PI/2,Sweep=-Math.PI/2,SourceFrom=0,SourceTo=1};
        var arcAnchor=Json.Copy(anchor);arcAnchor.Param=.5;
        Check(WeldSections.TryLocalFrame(arcAnchor,new[]{arc},out var arcRoot,out var tangent)&&tangent.X>0&&tangent.Y<0,
            "clockwise curved contact keeps the correct semantic tangent direction");
        arc.SourceFrom=1;arc.SourceTo=0;
        Check(WeldSections.TryLocalFrame(arcAnchor,new[]{arc},out arcRoot,out tangent)&&tangent.X<0&&tangent.Y>0,
            "reversed source interval reverses the local tangent consistently");
        Console.WriteLine(count+" linked weld checks passed");
    }
}
