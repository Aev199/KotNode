using System;
using System.IO;
using System.Linq;
using KotNode.Core;

static class AssemblyModelTests
{
    static int checks;
    static void Check(bool result, string name)
    {
        if (!result) throw new Exception(name);
        checks++; Console.WriteLine("PASS assembly: " + name);
    }
    static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, name);
    }
    static bool Near(double a, double b) => Math.Abs(a-b) < 1e-8;
    public static void Run()
    {
        checks = 0;
        var mark = new AssemblyMark();
        Check(mark.Specification().Count == 0, "empty mark");
        var plate = new PartDefinition { Name = "Лист", Position = "1", IncludeInSpec = true,
            Kind = PartKind.RectangularPlate, Plate = new RectangularPlateShape { Length = 500, Width = 200, Thickness = 12 } };
        var pipe = new PartDefinition { Name = "Труба", Kind = PartKind.Pipe,
            Pipe = new PipeShape { OuterDiameter = 820, Thickness = 8, Length = 1000 } };
        mark.Parts.Add(plate); mark.Parts.Add(pipe);
        var first = new PartInstance { PartId = plate.Id };
        mark.Instances.Add(first); mark.Instances.Add(new PartInstance { PartId = pipe.Id });
        for (int i = 1; i < 4; i++) mark.CopyInstance(first.Id).Placement.Origin.X = 500 * i;
        var row = mark.Specification().Single();
        Check(row.Count == 4 && Near(row.UnitMassKg,9.42) && Near(row.TotalMassKg,37.68), "four instances and reference pipe excluded");
        Check(mark.Instances.Select(i => i.Id).Distinct().Count() == 5 && first.Placement.Origin.X == 0, "copy creates independent placement and identity");
        Check(Near(pipe.UnitMassKg(), Math.PI*8*812*1000*7850/1e9), "pipe mass");
        plate.Plate.Thickness = 20;
        Check(Near(mark.Specification().Single().TotalMassKg,62.8), "definition edit updates all instances");
        var g = Splice.Calculate(new SpliceParameters());
        var curved = new PartDefinition { Name = "Накладка", Position = "2", Kind = PartKind.CurvedPlate, IncludeInSpec = true,
            CurvedPlate = new CurvedPlateShape { InnerRadius = g.InnerRadius, Thickness = g.OuterRadius-g.InnerRadius, Length = 950, AngleRadians = g.Angle } };
        mark.Parts.Add(curved);
        Check(Near(curved.UnitMassKg(),g.PlateMass), "curved plate matches legacy mass");
        Check(mark.Specification().Count == 1, "unused definition excluded");
        mark.Instances.Add(new PartInstance { PartId = curved.Id });
        var place = first.Placement;
        place.Origin = new Vector3(10,20,30); place.AxisX = new Vector3(0,1,0); place.AxisY = new Vector3(-1,0,0);
        var point = place.ToWorld(new Vector3(2,3,4));
        Check(Near(point.X,7) && Near(point.Y,22) && Near(point.Z,34), "right handed rotated frame");
        var copy = Json.Copy(mark);
        Check(copy.Id == mark.Id && copy.Parts[0].Id == plate.Id && copy.Instances[0].Id == first.Id && Near(copy.Specification().Sum(r=>r.TotalMassKg), mark.Specification().Sum(r=>r.TotalMassKg)), "JSON roundtrip preserves identity and mass");
        copy.Instances[0].PartId = Guid.NewGuid(); Reject(copy.Validate,"orphan instance");
        copy = Json.Copy(mark); copy.Parts[2].Position = " 1 "; Reject(copy.Validate,"duplicate positions");
        copy = Json.Copy(mark); copy.Instances.Add(Json.Copy(copy.Instances[0])); Reject(copy.Validate,"duplicate instance identity");
        copy = Json.Copy(mark); copy.Parts[1].Pipe.Thickness = 410; Reject(copy.Validate,"invalid pipe thickness");
        copy = Json.Copy(mark); copy.Parts[0].Plate.Width = double.NaN; Reject(copy.Validate,"nonfinite dimensions");
        copy = Json.Copy(mark); copy.Instances[0].Placement.AxisX = new Vector3(2,0,0); Reject(copy.Validate,"scaling rejected");
        copy = Json.Copy(mark); copy.Instances[0].Placement.AxisY = copy.Instances[0].Placement.AxisX; Reject(copy.Validate,"parallel axes rejected");
        copy = Json.Copy(mark); copy.Parts[0].Pipe = new PipeShape(); Reject(copy.Validate,"ambiguous shape rejected");
        copy = Json.Copy(mark); copy.Parts[0].Kind = (PartKind)99; Reject(copy.Validate,"unknown shape rejected");
        copy = Json.Copy(mark); copy.Schema = 2; Reject(copy.Validate,"unknown model schema rejected");
        var edited = new AssemblyMark();
        var definition = new PartDefinition { Name = "Лист", Position = "1", IncludeInSpec = true,
            Kind = PartKind.RectangularPlate, Plate = new RectangularPlateShape { Length = 500, Width = 200, Thickness = 12 } };
        var placement = new RigidPlacement();
        var a = edited.AddPart(definition, placement);
        definition.Plate.Thickness = 99; placement.Origin.X = 999;
        Check(Near(edited.Specification().Single().UnitMassKg,9.42) && a.Placement.Origin.X == 0, "add copies inputs");
        var b = edited.CopyInstance(a.Id);
        var next = new RigidPlacement { Origin = new Vector3(100,200,300) };
        edited.SetPlacement(b.Id,next); next.Origin.X = 999;
        Check(b.Placement.Origin.X == 100 && a.Placement.Origin.X == 0,"move affects one instance and copies input");
        var snapshot = Json.Write(edited);
        Reject(()=>edited.SetPlacement(b.Id,new RigidPlacement { AxisX = new Vector3(2,0,0) }),"invalid move rejected");
        Reject(()=>edited.DetachInstance(b.Id," 1 "),"detach duplicate position rejected");
        Reject(()=>edited.RemoveUnusedDefinition(a.PartId),"used definition cannot be removed");
        Reject(()=>edited.RemoveInstance(Guid.NewGuid()),"missing instance rejected");
        Check(Json.Write(edited) == snapshot,"rejected edits preserve state");
        var detached = edited.DetachInstance(b.Id,"2");
        Check(a.Id != b.Id && b.PartId == detached.Id && a.PartId != b.PartId && edited.Specification().Count == 2,"detach keeps instance identity");
        var replacement = Json.Copy(detached); replacement.Plate.Thickness = 20;
        edited.ReplaceDefinition(detached.Id,replacement);
        Check(Near(edited.Specification().Single(r=>r.Position=="1").UnitMassKg,9.42) && Near(edited.Specification().Single(r=>r.Position=="2").UnitMassKg,15.7),"independent definition edit");
        replacement.Plate.Thickness = 99;
        Check(Near(edited.Specification().Single(r=>r.Position=="2").UnitMassKg,15.7),"replacement copied");
        snapshot = Json.Write(edited);
        replacement = Json.Copy(edited.Parts.Single(p=>p.Id==detached.Id)); replacement.Position = "1";
        Reject(()=>edited.ReplaceDefinition(detached.Id,replacement),"replacement duplicate position rejected");
        Reject(()=>edited.AddPart(Json.Copy(edited.Parts[0]),new RigidPlacement()),"duplicate definition rejected");
        Check(Json.Write(edited)==snapshot,"failed add and replacement atomic");
        edited.RemoveInstance(b.Id); edited.RemoveUnusedDefinition(detached.Id);
        Check(edited.Parts.Count==1 && edited.Instances.Count==1 && edited.Specification().Single().Count==1,"delete instance and unused definition");
        ShapeTests();
        ProjectTests();
        Console.WriteLine(checks + " assembly checks passed");
    }

    static System.Collections.Generic.List<Point2> Ring(params double[] coordinates)
    {
        var points=new System.Collections.Generic.List<Point2>();
        for(int i=0;i<coordinates.Length;i+=2) points.Add(new Point2(coordinates[i],coordinates[i+1]));
        return points;
    }
    static void ShapeTests()
    {
        var polygon=new PolygonPlateShape { Thickness=12, Outer=Ring(0,0,200,0,200,100,100,100,100,200,0,200) };
        polygon.Holes.Add(Ring(20,20,40,20,40,40,20,40));
        polygon.RoundHoles.Add(new RoundHole { Center=new Point2(150,50),Radius=10 });
        double area=30000-400-Math.PI*100;
        Check(Near(polygon.NetAreaMm2(),area),"concave plate with polygon and round holes");
        polygon.Outer.Reverse(); polygon.Holes[0].Reverse();
        Check(Near(polygon.NetAreaMm2(),area),"ring winding independent");
        var part=new PartDefinition { Kind=PartKind.PolygonPlate,Name="Лист",Position="1",IncludeInSpec=true,PolygonPlate=polygon };
        Check(Near(part.UnitMassKg(),area*12*7850/1e9),"net plate mass");
        var copy=Json.Copy(part);
        Check(Near(copy.UnitMassKg(),part.UnitMassKg()),"polygon JSON roundtrip");
        var invalid=Json.Copy(polygon); invalid.Outer=Ring(0,0,100,100,0,100,100,0); Reject(invalid.Validate,"self intersecting outer");
        invalid=Json.Copy(polygon); invalid.Outer=Ring(0,0,100,0,50,0,100,100,0,100); Reject(invalid.Validate,"adjacent backtracking rejected");
        invalid=Json.Copy(polygon); invalid.Holes[0]=Ring(90,90,110,90,110,110,90,110); Reject(invalid.Validate,"hole crosses concave boundary");
        invalid=Json.Copy(polygon); invalid.Holes.Add(Ring(25,25,35,25,35,35,25,35)); Reject(invalid.Validate,"nested holes rejected");
        invalid=Json.Copy(polygon); invalid.RoundHoles[0].Center=new Point2(190,50); Reject(invalid.Validate,"round hole tangent to edge rejected");
        invalid=Json.Copy(polygon); invalid.RoundHoles[0].Center=new Point2(30,30); Reject(invalid.Validate,"round hole overlaps polygon hole");
        invalid=Json.Copy(polygon); invalid.RoundHoles.Add(new RoundHole { Center=new Point2(170,50),Radius=10 }); Reject(invalid.Validate,"round holes tangent rejected");
        invalid=Json.Copy(polygon); invalid.RoundHoles[0].Center=new Point2(150,150); Reject(invalid.Validate,"round hole outside concave plate rejected");
        invalid=Json.Copy(polygon); invalid.Outer.Add(invalid.Outer[0]); Reject(invalid.Validate,"explicit duplicate closure rejected");
        var beam=new PartDefinition { Kind=PartKind.IBeam,Name="Двутавр",Position="2",IncludeInSpec=true,
            IBeam=new IBeamShape { Profile="Test",Height=300,Width=150,WebThickness=8,FlangeThickness=12,Length=1000 }};
        Check(Near(beam.IBeam.AreaMm2(),5808),"idealized I section area");
        Check(Near(beam.UnitMassKg(),45.5928),"I section mass");
        beam.IBeam.CatalogAreaMm2=6000;
        Check(Near(beam.UnitMassKg(),47.1),"catalog area mass");
        var beamBad=Json.Copy(beam); beamBad.IBeam.FlangeThickness=150; Reject(beamBad.Validate,"invalid flange");
        beamBad=Json.Copy(beam); beamBad.IBeam.CatalogAreaMm2=50000; Reject(beamBad.Validate,"catalog area outside envelope");
        var bar=new PartDefinition { Kind=PartKind.Rebar,Name="Стержень",Material="А500С",Position="3",IncludeInSpec=true,
            Rebar=new RebarShape { Diameter=16 }};
        bar.Rebar.Points.Add(new Vector3(0,0,0)); bar.Rebar.Points.Add(new Vector3(100,0,0)); bar.Rebar.Points.Add(new Vector3(100,0,100));
        bar.Rebar.BendRadii.Add(20);
        double length=160+10*Math.PI;
        Check(Near(bar.Rebar.CenterlineLengthMm(),length),"spatial right angle bend length");
        Check(Near(bar.UnitMassKg(),Math.PI*64*length*7850/1e9),"rebar mass");
        var badBar=Json.Copy(bar); badBar.Rebar.BendRadii[0]=110; Reject(badBar.Validate,"oversized bend rejected");
        badBar=Json.Copy(bar); badBar.Rebar.BendRadii[0]=8; Reject(badBar.Validate,"bend below bar radius rejected");
        badBar=Json.Copy(bar); badBar.Rebar.Points[2]=new Vector3(0,0,0); Reject(badBar.Validate,"180 reversal rejected");
        badBar=Json.Copy(bar); badBar.Rebar.Points.Add(new Vector3(200,0,100)); badBar.Rebar.BendRadii[0]=60; badBar.Rebar.BendRadii.Add(60); Reject(badBar.Validate,"adjacent bend overlap rejected");
        var mark=new AssemblyMark();
        mark.AddPart(part,new RigidPlacement()); mark.AddPart(beam,new RigidPlacement()); mark.AddPart(bar,new RigidPlacement());
        Check(Json.Copy(mark).Specification().Count==3 && Near(Json.Copy(mark).Specification().Sum(r=>r.TotalMassKg),mark.Specification().Sum(r=>r.TotalMassKg)),"all new shapes survive assembly roundtrip");
        var ambiguous=Json.Copy(part); ambiguous.IBeam=beam.IBeam; Reject(ambiguous.Validate,"ambiguous new shape rejected");
    }

    static void ProjectTests()
    {
        var legacy=new Project { Code="T",Name="Котлован",Version=5 };
        var old=new NodeMark { Code="УР-1" }; legacy.Marks.Add(old);
        legacy.Uses.Add(new Use { MarkId=old.Id,Count=3,Included=true,Location="Ярус 1" });
        string original=Json.Write(legacy);
        var p=AssemblyProject.FromLegacy(legacy);
        Check(p.Id==legacy.Id && p.Version==0 && p.LegacySourceVersion==5 && p.Marks[0].Id==old.Id && p.Marks[0].DetailId==old.DetailId && p.Uses[0].Id==legacy.Uses[0].Id,"migration preserves source identity");
        Check(Near(p.Materials().Single().TotalMassKg,legacy.Steel().Single().TotalMass),"legacy mass preserved after migration");
        var mark=new AssemblyMark { Code="УР-2" };
        var plate=new PartDefinition { Name="Лист",Position="1",IncludeInSpec=true,Kind=PartKind.RectangularPlate,
            Plate=new RectangularPlateShape { Length=500,Width=200,Thickness=12 }};
        var instance=mark.AddPart(plate,new RigidPlacement()); mark.CopyInstance(instance.Id);
        mark.AddPart(new PartDefinition { Name="Труба",Kind=PartKind.Pipe,Pipe=new PipeShape { OuterDiameter=820,Thickness=8,Length=1000 }},new RigidPlacement());
        p.Assemblies.Add(mark);
        Check(p.Materials().Count==1,"unused assembly adds no project steel");
        p.Uses.Add(new Use { MarkId=mark.Id,Count=4,Included=true,Location="Ярус 2" });
        p.Uses.Add(new Use { MarkId=mark.Id,Count=5,Included=false,Location="Черновик" });
        var row=p.Materials("Ярус 2").Single();
        Check(row.DetailCount==8 && row.PartsPerNode==2 && row.NodeCount==4 && Near(row.TotalMassKg,75.36),"assembly project scope and reference exclusion");
        Check(p.Materials("Черновик").Count==0 && p.Materials().Count==2,"mixed project and draft exclusions");
        Check(Json.Write(legacy)==original,"migration does not mutate legacy");
        var copy=Json.Copy(p);
        Check(Near(copy.Materials().Sum(r=>r.TotalMassKg),p.Materials().Sum(r=>r.TotalMassKg)),"mixed project JSON roundtrip");
        copy.Assemblies[0].Code=" ур-1 "; Reject(copy.Validate,"duplicate cross-format mark code");
        copy=Json.Copy(p); copy.Assemblies[0].Id=old.Id; Reject(copy.Validate,"duplicate cross-format mark id");
        copy=Json.Copy(p); copy.Uses[0].MarkId=Guid.NewGuid(); Reject(copy.Validate,"orphan use in mixed project");
        Reject(()=>Json.Read<Project>(Json.Write(p)).Validate(),"old reader rejects schema 2");
        string dir=Path.Combine(Path.GetTempPath(),"kotnode-assembly-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string oldPath=Path.Combine(dir,"legacy.json"), path=Path.Combine(dir,"mixed.json");
            File.WriteAllText(oldPath,original);
            Reject(()=>new AssemblyProjectStore(oldPath).Save(p),"migration cannot overwrite source");
            Check(File.ReadAllText(oldPath)==original,"source bytes preserved");
            var a=new AssemblyProjectStore(path);
            var saved=a.Save(p);
            Check(saved.Version==1 && p.Version==0,"new project save does not mutate caller");
            var b=new AssemblyProjectStore(path); var stale=b.Load();
            saved.Name="New"; var latest=a.Save(saved);
            bool conflict=false; try { b.Save(stale); } catch(IOException) { conflict=true; }
            Check(conflict,"concurrent schema-2 edit rejected");
            conflict=false; try { a.Save(saved); } catch(IOException) { conflict=true; }
            Check(conflict,"stale candidate rejected even in same store");
            Check(latest.Version==2 && new AssemblyProjectStore(path).Load().Name=="New","winner preserved");
            Check(Json.Read<AssemblyProject>(File.ReadAllText(path+".bak")).Version==1,"schema-2 backup preserved");
            Reject(()=>new ProjectStore(path).Load(),"legacy store rejects schema 2");
        }
        finally { Directory.Delete(dir,true); }
    }
}
