using System;
using System.IO;
using System.Linq;
using KotNode.Core;

static class MemberEndCutTests
{
    static int count;
    static void Check(bool ok,string msg){if(!ok)throw new Exception(msg);count++;Console.WriteLine("PASS end cut: "+msg);}
    static void Reject(Action action,string msg){bool fail=false;try{action();}catch(InvalidDataException){fail=true;}Check(fail,msg);}
    static PartDefinition Pipe()=>new PartDefinition{Name="Труба",Position="1",IncludeInSpec=true,Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=200,Thickness=10,Length=1000}};
    static void RunShape(PartDefinition part)
    {
        double mass=part.UnitMassKg();
        var mark=new AssemblyMark{Schema=3};var inst=mark.AddPart(part,new RigidPlacement());part=mark.Parts.Single();
        part.EndCuts=new MemberEndTreatment{Start=new PlanarEndCut{AngleXDeg=20,AngleYDeg=10,OffsetMm=5},End=new PlanarEndCut{AngleXDeg=-30,OffsetMm=8}};
        part.Validate();
        Check(part.UnitMassKg()==mass,part.Kind+" nominal mass unchanged by both end cuts");
        Check(AssemblyReporting.BuildMarkSpec(mark).TotalMassKg==mass&&AssemblyReporting.BuildMarkVrs(mark).TotalMassKg==mass,part.Kind+" specification and VRS use uncut stock");
        Check(AssemblyReporting.Designation(part).Contains("1000"),part.Kind+" designation retains stock length");
        var side=new RigidPlacement{AxisX=new Vector3(0,0,1),AxisY=new Vector3(1,0,0)};
        var projected=ParametricProjection.BuildStable(mark,side,false,true);
        Check(projected.Curves.Count>0&&projected.Curves.All(c=>c.SourceKey!=null),part.Kind+" cut members project with semantic edge identities");
        var top=projected.Curves.Where(c=>c.SourceKey.Contains(":top:")).ToList();
        Check(top.Count>0&&top.All(c=>Math.Abs(c.X0-MemberEndCuts.Z(part,false,c.Y0,0))<1e-5&&Math.Abs(c.X1-MemberEndCuts.Z(part,false,c.Y1,0))<1e-5),part.Kind+" visible end rim lies on oblique plane rather than original flat cap");
        var legacy=ParametricProjection.Build(mark,side,false,true);
        Check(Math.Abs(legacy.Curves.Max(c=>Math.Max(c.X0,c.X1))-projected.Curves.Max(c=>Math.Max(c.X0,c.X1)))<1e-5,part.Kind+" legacy entry point also honors cuts");
        var cut=new RigidPlacement{Origin=new Vector3(0,0,500)};
        var middle=ParametricProjection.BuildStable(mark,cut,true,true);
        Check(middle.Sections.Count>0&&middle.Curves.Any(c=>c.Cut),part.Kind+" section through retained stock remains closed and hatched");
        var oldEnd=ParametricProjection.BuildStable(mark,new RigidPlacement{Origin=new Vector3(0,0,999)},true,true);
        Check(oldEnd.Sections.Count==0,part.Kind+" section beyond retained end has no invented material");
        var sectionNear=ParametricProjection.BuildStable(mark,new RigidPlacement{Origin=new Vector3(0,0,MemberEndCuts.Intercept(part,false))},true,true);
        Check(sectionNear.Sections.Count>0,part.Kind+" section intersecting inclined cap has material patches");
        var keys=projected.Curves.Select(c=>c.SourceKey).Distinct().OrderBy(k=>k).ToArray();
        if(part.Kind==PartKind.Pipe)part.Pipe.Length=1200;else part.IBeam.Length=1200;
        var resized=ParametricProjection.BuildStable(mark,side,false,true);
        Check(keys.SequenceEqual(resized.Curves.Select(c=>c.SourceKey).Distinct().OrderBy(k=>k)),part.Kind+" resizing retains semantic keys");
        Check(Math.Abs(part.UnitMassKg()-mass*1.2)<1e-8,part.Kind+" resize mass follows stock length only");
        var template=new NodeFamilyTemplate{Name="Подрезки",Mark=mark};
        var inserted=NodeFamilyLibrary.Instantiate(Json.Copy(template),"CUT-02","Копия");
        Check(inserted.Mark.Schema==3&&inserted.Mark.Parts[0].EndCuts.End.AngleXDeg==-30,part.Kind+" JSON and family insertion preserve cut definition");
        var project=new AssemblyProject{Code="CUT",Name="Подрезки"};project.Assemblies.Add(mark);project.Uses.Add(new Use{MarkId=mark.Id,Count=3,Location="Ярус",Included=true});
        Check(Math.Abs(AssemblyReporting.SummarizeProjectVrs(project).Sum(r=>r.MassKg)-part.UnitMassKg()*3)<1e-7,part.Kind+" project VRS counts uncut stock and multiplicity");
        part.IncludeInSpec=false;
        Check(AssemblyReporting.BuildMarkSpec(mark).TotalMassKg==0&&AssemblyReporting.BuildMarkVrs(mark).TotalMassKg==0,part.Kind+" reference member stays excluded");
    }
    internal static void Run()
    {
        RunShape(Pipe());
        var beam=new PartDefinition{Name="Двутавр",Position="1",IncludeInSpec=true,Kind=PartKind.IBeam,IBeam=BeamCatalog.Create("30Б2",1000)};
        RunShape(beam);
        var p=Pipe();p.EndCuts=new MemberEndTreatment{Start=new PlanarEndCut{AngleXDeg=30,AngleYDeg=-15}};
        double r=100;
        Check(Enumerable.Range(0,360).All(i=>MemberEndCuts.Z(p,true,r*Math.Cos(i*Math.PI/180),r*Math.Sin(i*Math.PI/180))>=-1e-7),"entire start rim remains within stock envelope");
        var placement=RigidPlacement.FromThreePoints(new Vector3(40,50,60),new Vector3(40,50,61),new Vector3(41,50,60));
        double intercept=MemberEndCuts.Intercept(p,true);
        var local=new RigidPlacement{Origin=new Vector3(0,0,intercept),AxisX=new Vector3(Math.Cos(Math.PI/6),0,Math.Sin(Math.PI/6)),AxisY=new Vector3(0,1,0)};
        var from=MemberEndCuts.FromPlane(Pipe(),placement,local.InParent(placement),true);
        Check(Math.Abs(from.AngleXDeg-30)<1e-8&&Math.Abs(from.AngleYDeg)<1e-8,"three-point world plane converts to local slopes in rotated member");
        var reversed=new RigidPlacement{Origin=local.Origin,AxisX=local.AxisX,AxisY=new Vector3(0,-1,0)};
        var fromReverse=MemberEndCuts.FromPlane(Pipe(),placement,reversed.InParent(placement),true);
        Check(Math.Abs(from.AngleXDeg-fromReverse.AngleXDeg)<1e-8,"reversing picked plane normal does not reverse retained member");
        var invalid=Json.Copy(p);invalid.EndCuts.Start.OffsetMm=-1;Reject(invalid.Validate,"negative setback rejected");
        invalid=Json.Copy(p);invalid.EndCuts.Start.AngleXDeg=90;Reject(invalid.Validate,"longitudinal plane rejected");
        invalid=Json.Copy(p);invalid.Pipe.Length=10;Reject(invalid.Validate,"crossing ends rejected");
        invalid=Json.Copy(p);invalid.EndCuts.Start.AngleXDeg=double.NaN;Reject(invalid.Validate,"NaN slope rejected");
        Reject(()=>MemberEndCuts.FromPlane(Pipe(),new RigidPlacement(),new RigidPlacement{AxisX=new Vector3(0,0,1),AxisY=new Vector3(0,1,0)},true),"three-point longitudinal cut rejected");
        Reject(()=>MemberEndCuts.FromPlane(Pipe(),new RigidPlacement(),new RigidPlacement{Origin=new Vector3(0,0,-1)},true),"plane outside stock rejected");
        var mark=new AssemblyMark{Schema=3};mark.AddPart(p,new RigidPlacement());
        var old=Json.Copy(mark);old.Schema=2;Reject(old.Validate,"old model schema cannot silently drop end cuts");
        WeldCatalog.Register(mark,new ViewWeldOverlay{InstanceId=mark.Instances[0].Id,SourceKey="spatial:Pipe:rim:0"});
        Check(mark.Schema==3,"registering shared weld cannot downgrade cut model schema");
        var formulaMark=new AssemblyMark();var part=Pipe();formulaMark.AddPart(part,new RigidPlacement());
        formulaMark.Parameters.Add(new MarkParameter{Name="A",Value=25});
        formulaMark.Bindings.Add(new PartBinding{PartId=part.Id,Property="EndCuts.End.AngleXDeg",Formula="A"});
        ParametricEngine.Solve(formulaMark);
        Check(formulaMark.Schema==3&&formulaMark.Parts[0].EndCuts.End.AngleXDeg==25,"formula creates and persists parametrically driven end cut");
        formulaMark.Parameters[0].Value=90;var bad=Json.Write(formulaMark);
        Reject(()=>ParametricEngine.Solve(formulaMark),"invalid cut formula rejected");
        Check(Json.Write(formulaMark)==bad,"failed cut solve is atomic");
        var contact=new AssemblyMark{Schema=3};var contactPipe=Pipe();
        contactPipe.EndCuts=new MemberEndTreatment{Start=new PlanarEndCut{AngleXDeg=20,OffsetMm=100}};
        contact.AddPart(contactPipe,new RigidPlacement());
        var cover=new PartDefinition{Name="Накладка",Kind=PartKind.CurvedPlate,CurvedPlate=new CurvedPlateShape{InnerRadius=100,Thickness=10,Length=400,AngleRadians=Math.PI/3}};
        var coverInst=contact.AddPart(cover,new RigidPlacement());
        var weld=new ViewWeldOverlay{InstanceId=coverInst.Id,SourceKey="spatial:CurvedPlate:inner-start",LegMm=6};
        Check(WeldSections.Build(contact,new RigidPlacement{Origin=new Vector3(0,0,20)},new[]{weld}).Count==0,"no weld triangle is invented against removed pipe material");
        Check(WeldSections.Build(contact,new RigidPlacement{Origin=new Vector3(0,0,250)},new[]{weld}).Count==1,"weld section remains visible where cut pipe actually supports the cover");
        Console.WriteLine(count+" end cut checks passed");
    }
}
