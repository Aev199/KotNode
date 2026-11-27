using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KotNode.Core;
static class WeldEditingTests
{
    static int count;
    static void Check(bool yes,string name){if(!yes)throw new Exception(name);count++;Console.WriteLine("PASS weld editing: "+name);}
    static double Area(WeldCutPatch p)=>Math.Abs(p.Points.Select((x,i)=>x.X*p.Points[(i+1)%p.Points.Count].Y-x.Y*p.Points[(i+1)%p.Points.Count].X).Sum())/2;
    internal static void Run()
    {
        var mark=new AssemblyMark();
        var support=mark.AddPart(new PartDefinition{Name="Труба",IncludeInSpec=false,Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=820,Thickness=12,Length=2000}},new RigidPlacement{Origin=new Vector3(0,0,-1000)});
        var inst=mark.AddPart(new PartDefinition{Name="Накладка",Position="1",Kind=PartKind.CurvedPlate,CurvedPlate=new CurvedPlateShape{InnerRadius=410,Thickness=12,Length=1000,AngleRadians=72*Math.PI/180}},new RigidPlacement{Origin=new Vector3(0,0,-500)});
        var w=new ViewWeldOverlay{InstanceId=inst.Id,SourceKey="spatial:CurvedPlate:inner-start",LegMm=10,Side=ViewWeldSide.Right};
        var raw=new[]{new ParametricProjection.Curve2{InstanceId=inst.Id,SourceKey=w.SourceKey,X0=-500,Y0=0,X1=500,Y1=0}};
        var before=ViewWelds.Build(raw,new[]{w},20,out _);
        w.OffsetMm=37;
        var after=ViewWelds.Build(raw,new[]{w},20,out var missing);
        Check(missing==0&&before.Count==after.Count&&before.Zip(after,(a,b)=>Math.Abs(b.Y0-a.Y0-37)<1e-8&&Math.Abs(b.X0-a.X0)<1e-8).All(x=>x),"signed offset moves graphics without changing edge identity");
        var section=WeldSections.Build(mark,new RigidPlacement(),new[]{w});
        Check(section.Count==1&&section[0].Points.Count==3,"longitudinal cover weld appears as one triangle in cross section");
        Check(Math.Abs(Area(section[0])-50)<1e-8,"section triangle uses true 10 mm leg");
        var first=section[0].Points;
        w.OffsetMm=-100;
        var movedGraphicSection=WeldSections.Build(mark,new RigidPlacement(),new[]{w});
        Check(movedGraphicSection[0].Points.All(p=>first.Any(q=>Math.Abs(p.X-q.X)<1e-8&&Math.Abs(p.Y-q.Y)<1e-8)),"drawing offset never moves physical weld section");
        var id=w.Id;w.LegMm=14;
        section=WeldSections.Build(mark,new RigidPlacement(),new[]{w});
        Check(w.Id==id&&Math.Abs(Area(section[0])-98)<1e-8,"editing leg updates same weld and cross-section area");
        var leader=new ViewLeader{Kind=ViewLeaderKind.Weld,Target=new ViewAnchor{Kind=ViewAnchorKind.WeldMidpoint,WeldId=id}};
        Check(ViewAnnotations.FormatLeaderText(leader,mark,new[]{w})=="14","SPDS numeric leg updates with source weld");
        w.From=.6;w.To=.8;
        Check(WeldSections.Build(mark,new RigidPlacement(),new[]{w}).Count==0,"plane outside partial weld interval draws no triangle");
        Check(WeldSections.Build(mark,new RigidPlacement{Origin=new Vector3(0,0,200)},new[]{w}).Count==1,"plane inside partial weld interval draws triangle");
        w.From=0;w.To=1;w.ShowInSections=false;
        Check(WeldSections.Build(mark,new RigidPlacement(),new[]{w}).Count==0,"section display can be disabled independently");
        w.ShowInSections=null;
        Check(WeldSections.Build(mark,new RigidPlacement(),new[]{w}).Count==1,"legacy absent section option enables known physical joints");
        w.SourceKey="spatial:CurvedPlate:sil:outer:pos";
        Check(WeldSections.Build(mark,new RigidPlacement(),new[]{w}).Count==0,"smooth silhouette never invents a physical joint");
        w.SourceKey="spatial:CurvedPlate:inner-start";
        inst.Placement=new RigidPlacement{Origin=new Vector3(100,200,-500),AxisX=new Vector3(0,1,0),AxisY=new Vector3(-1,0,0)};
        support.Placement.Origin=new Vector3(100,200,-1000);
        section=WeldSections.Build(mark,new RigidPlacement(),new[]{w});
        Check(section.Count==1&&section[0].Points.Any(p=>Math.Abs(p.X-100)<1e-8&&Math.Abs(p.Y-610)<1e-8),"section follows rotated and translated plate instance");
        mark.Instances.Remove(support);
        Check(WeldSections.Build(mark,new RigidPlacement(),new[]{w}).Count==0,"free cover edge without a supporting pipe never invents a welded joint");
        mark.Instances.Add(support);
        mark.Parts.Single(p=>p.Kind==PartKind.Pipe).Pipe.Length=990;
        mark.Instances.Add(new PartInstance{PartId=support.PartId,Placement=new RigidPlacement{Origin=new Vector3(100,200,10)}});
        Check(WeldSections.Build(mark,new RigidPlacement(),new[]{w}).Count==0,"gap between pipe ends is not filled with an invented weld section");
        Check(WeldSections.Build(mark,new RigidPlacement{Origin=new Vector3(0,0,100)},new[]{w}).Count==1,"weld section remains visible on the adjacent supported pipe segment");
        var persisted=Json.Copy(w);
        Check(persisted.OffsetMm==w.OffsetMm&&persisted.LegMm==14&&persisted.Id==w.Id,"edited weld properties survive JSON roundtrip");
        persisted.OffsetMm=double.NaN;bool rejected=false;
        try{ViewWelds.Validate(persisted);}catch(InvalidDataException){rejected=true;}
        Check(rejected,"non-finite drawing offset rejected");
        var fam=NodeFamilyLibrary.BuiltIn()[0].Mark;
        fam.Parameters.Add(new MarkParameter{Name="C_gap",Label="Между накладками в свету",Unit="мм",Value=50});
        fam.Parameters.Single(p=>p.Name=="Angle").Formula="360 / N_plates - 2 * asin(C_gap / D)";
        ParametricEngine.Solve(fam);
        var pipe=fam.Parts.Single(p=>p.Kind==PartKind.Pipe).Pipe;
        var cover=fam.Parts.Single(p=>p.Kind==PartKind.CurvedPlate).CurvedPlate;
        double n=fam.Parameters.Single(p=>p.Name=="N_plates").Value;
        double chord=pipe.OuterDiameter*Math.Sin((2*Math.PI/n-cover.AngleRadians)/2);
        Check(Math.Abs(chord-50)<1e-8,"GUI tutorial formula produces actual 50 mm clear chord between covers");
        fam.Parameters.Single(p=>p.Name=="D").Value=1020;ParametricEngine.Solve(fam);
        cover=fam.Parts.Single(p=>p.Kind==PartKind.CurvedPlate).CurvedPlate;
        Check(Math.Abs(1020*Math.Sin((2*Math.PI/n-cover.AngleRadians)/2)-50)<1e-8,"clear spacing remains fixed after pipe diameter changes");
        Console.WriteLine(count+" weld editing checks passed");
    }
}
