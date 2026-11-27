using System;
using System.IO;
using System.Linq;
using KotNode.Core;
static class DrawingNodesTests
{
    static int count;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS drawing nodes: "+name);}
    static void Reject(Action action,string name){bool bad=false;try{action();}catch(InvalidDataException){bad=true;}Check(bad,name);}
    internal static void Run()
    {
        var family=NodeFamilyLibrary.BuiltIn()[0];
        var a=NodeFamilyLibrary.Instantiate(family,"У-01","Первый");
        var b=NodeFamilyLibrary.Instantiate(family,"У-02","Второй");
        var other=NodeFamilyLibrary.Instantiate(NodeFamilyLibrary.BuiltIn()[1],"У-03","Другой тип");
        var nodes=new DrawingNodes();nodes.Put(a.Mark);nodes.Put(b.Mark);nodes.Put(other.Mark);
        Check(nodes.Nodes.Count==3&&nodes.ActiveId==other.Mark.Id,"same and different families coexist with a distinct active node");
        Check(a.Mark.Parts[0].Id==b.Mark.Parts[0].Id&&a.Mark.Id!=b.Mark.Id,"same family internal IDs are scoped by owning node");
        nodes.Activate(b.Mark.Id);string savedA=Json.Write(nodes.Nodes.Single(n=>n.Id==a.Mark.Id));
        var edit=Json.Copy(nodes.Active());edit.Parameters.First(p=>p.Name=="D").Value=900;ParametricEngine.Solve(edit);nodes.Put(edit);
        Check(Json.Write(nodes.Nodes.Single(n=>n.Id==a.Mark.Id))==savedA,"editing second family instance leaves first byte-for-byte unchanged");
        edit.Parts[0].Name="Outside mutation";
        Check(nodes.Active().Parts[0].Name!="Outside mutation","persisted node does not alias caller's part definitions");
        var duplicate=Json.Copy(a.Mark);duplicate.Id=Guid.NewGuid();string before=Json.Write(nodes);
        Reject(()=>nodes.Put(duplicate),"duplicate node code rejected");Check(Json.Write(nodes)==before,"rejected addition is atomic");
        Check(nodes.UniqueCode("У-01")=="У-01-02","repeated family gets an available code");
        Reject(()=>nodes.Activate(Guid.NewGuid()),"foreign active ID rejected");
        var roundtrip=Json.Copy(nodes);Check(roundtrip.Nodes.Count==3&&roundtrip.ActiveId==b.Mark.Id,"DWG JSON roundtrip retains all nodes and selection");
        var corrupted=Json.Copy(nodes);corrupted.ActiveId=Guid.NewGuid();Reject(corrupted.Validate,"missing active node is not silently replaced");
        nodes.Remove(b.Mark.Id);Check(nodes.Nodes.Count==2&&nodes.Nodes.All(n=>n.Id!=b.Mark.Id)&&nodes.ActiveId==a.Mark.Id,"removing active node retains others and selects a survivor");
        nodes.Remove(a.Mark.Id);nodes.Remove(other.Mark.Id);Check(nodes.Active()==null,"empty drawing has no active node");
        var placement=new Vector3(12000,8000,0);var beforePart=Json.Copy(a.Mark.Instances[0].Placement.Origin);
        NodePlacement.PlaceFamily(a,placement);
        Check(a.Mark.Schema==4&&a.Mark.LayoutOrigin.X==12000,"placement is stored independently from family formula coordinates");
        Check(Math.Abs(a.Mark.Instances[0].Placement.Origin.X-beforePart.X-12000)<1e-8,"model moves to chosen insertion point");
        var placedFrame=Json.Copy(a.Views[0].Frame.Origin);var placedViewX=a.Views[0].OffsetX;
        var original=Json.Copy(a.Mark.Instances[0].Placement.Origin);ParametricEngine.Solve(a.Mark);
        Check(Math.Abs(a.Mark.Instances[0].Placement.Origin.X-original.X)<1e-8&&Math.Abs(a.Mark.Instances[0].Placement.Origin.Y-original.Y)<1e-8,"solve does not pull placed node back onto the first node");
        a.Mark.Parameters.First(p=>p.Name=="D").Value=1000;ParametricEngine.Solve(a.Mark);
        Check(Math.Abs(a.Mark.Instances[0].Placement.Origin.X-12000)<1e-8,"dimension changes retain drawing placement");
        var template=Json.Copy(a);var again=NodeFamilyLibrary.Instantiate(template,"У-04","Повторная вставка");
        NodePlacement.PlaceFamily(again,new Vector3(30000,0,0));ParametricEngine.Solve(again.Mark);
        Check(Math.Abs(again.Mark.Instances[0].Placement.Origin.X-30000)<1e-8&&Math.Abs(again.Mark.Instances[0].Placement.Origin.Y)<1e-8,"saved placed family is rebased to its new insertion point");
        Check(Math.Abs(again.Views[0].OffsetX-placedViewX-18000)<1e-8&&Math.Abs(again.Views[0].Frame.Origin.Y-placedFrame.Y+8000)<1e-8,"views and world projection frames move together");
        var prior=Json.Write(again.Mark);again.Mark.Parameters.First(p=>p.Name=="D").Value=-1;var badState=Json.Write(again.Mark);
        Reject(()=>ParametricEngine.Solve(again.Mark),"invalid solve on a placed node rejected");Check(Json.Write(again.Mark)==badState,"failed solve does not leak local coordinates into drawing placement");
        var moved=Json.Copy(a.Mark);NodePlacement.TranslateMark(moved,new Vector3(300,400,0));ParametricEngine.Solve(moved);
        Check(Math.Abs(moved.Instances[0].Placement.Origin.X-12300)<1e-8&&Math.Abs(moved.Instances[0].Placement.Origin.Y-8400)<1e-8,"moving entire node remains stable through solve");
        var old=Json.Copy(moved);old.Schema=3;Reject(old.Validate,"old model readers cannot discard drawing origin");
        Reject(()=>NodePlacement.PlaceFamily(Json.Copy(a),new Vector3(0,0,10)),"non-XY insertion rejected");
        var angled=NodeFamilyLibrary.Instantiate(NodeFamilyLibrary.BuiltIn()[3],"У-05","Угол");
        NodePlacement.PlaceFamily(angled,new Vector3(-8000,20000,0));var anchored=angled.Views.First(v=>v.FrameAnchorId.HasValue);
        ParametricEngine.Solve(angled.Mark);
        var inst=angled.Mark.Instances.Single(i=>i.Id==anchored.FrameAnchorId.Value);var frame=anchored.RelativeFrame.InParent(inst.Placement);
        Check(Math.Abs(frame.Origin.X-anchored.Frame.Origin.X)<1e-7&&Math.Abs(frame.Origin.Y-anchored.Frame.Origin.Y)<1e-7,"3D anchored view frame follows rebased family correctly");
        Console.WriteLine(count+" drawing node checks passed");
    }
}
