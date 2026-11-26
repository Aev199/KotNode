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
        Console.WriteLine(checks+" stabilization checks passed");
    }
}
