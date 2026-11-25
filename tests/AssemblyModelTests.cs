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
        Console.WriteLine(checks + " assembly checks passed");
    }
}
