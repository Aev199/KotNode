using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using KotNode.Core;

static class ParametricProjectionTests
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception("Parametric projection: "+name);checks++;Console.WriteLine("PASS projection: "+name);}
    static void Near(double actual,double expected,string name)=>Check(Math.Abs(actual-expected)<1e-6,name+" ("+actual+")");
    static PartDefinition Pipe()=>new PartDefinition{Name="Труба 100×10",Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=100,Thickness=10,Length=200}};
    static PartDefinition Plate(double l,double w,double t)=>new PartDefinition{Name="Пластина",Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=l,Width=w,Thickness=t}};
    static RigidPlacement At(double x,double y,double z)=>new RigidPlacement{Origin=new Vector3(x,y,z)};
    static AssemblyMark Mark()=>new AssemblyMark();
    static double ArcSum(ParametricProjection.Result r,double radius,bool hidden=false)=>r.Curves.Where(c=>c.Circular&&Math.Abs(c.Radius-radius)<1e-6&&c.Hidden==hidden).Sum(c=>c.Sweep);
    static void Rejected(Action action,string name){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,name);}
    internal static void Run()
    {
        var m=Mark();m.AddPart(Pipe(),At(0,0,0));
        var axial=ParametricProjection.Build(m,At(0,0,0),false,true);
        Check(axial.Curves.Count==2,"axial hollow pipe has two circular boundaries");
        Near(ArcSum(axial,50),2*Math.PI,"outer circle is complete");Near(ArcSum(axial,40),2*Math.PI,"bore circle is complete");
        Check(!axial.Curves.Any(c=>c.Hidden),"front rim dominates coincident rear rim");
        var front=new RigidPlacement{AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)};
        var side=ParametricProjection.Build(m,front,false,true);
        Near(side.Curves.Where(c=>!c.Hidden).Sum(Length),600,"side visible perimeter");
        Near(side.Curves.Where(c=>c.Hidden).Sum(Length),400,"two hidden inner generatrices");
        m.AddPart(Plate(60,120,10),At(0,-60,220));
        var covered=ParametricProjection.Build(m,At(0,0,0),false,true);
        Near(ArcSum(covered,50),Math.PI,"front plate hides exactly half the outer rim");
        Near(ArcSum(covered,50,true),Math.PI,"covered half is hidden");
        Near(ArcSum(covered,40),Math.PI,"front plate hides exactly half the bore rim");
        var noHidden=ParametricProjection.Build(m,At(0,0,0));
        Check(!noHidden.Curves.Any(c=>c.Hidden),"hidden-lines switch filters obscured parts");
        // Two clipping plates on either side leave a narrow real visible arc.
        var narrow=Mark();narrow.AddPart(Pipe(),At(0,0,0));
        narrow.AddPart(Plate(100,200,10),At(-100,-100,220));
        narrow.AddPart(Plate(100,200,10),At(.01,-100,220));
        var slit=ParametricProjection.Build(narrow,At(0,0,0));
        Near(ArcSum(slit,50),2*Math.Asin(.01/50),"narrow visible slit survives exact boundary splitting");
        var ring=Mark();ring.AddPart(Pipe(),At(0,0,-100));
        ring.AddPart(Plate(10,10,10),At(-5,-5,-40)); // behind plane, inside the hollow core
        ring.AddPart(Plate(20,20,10),At(-10,-10,20)); // in front of plane, must disappear
        var cut=ParametricProjection.Build(ring,At(0,0,0),true);
        Check(cut.Sections.Count==1&&cut.Sections[0].Loops.Count==2,"cross cut is an annular hatch with a hole");
        Near(cut.Curves.Where(c=>!c.Circular&&!c.Cut).Sum(Length),40,"geometry behind section survives in the bore");
        Near(ArcSum(cut,50),2*Math.PI,"complete circular cut boundary");
        var longitudinal=Mark();longitudinal.AddPart(Pipe(),At(0,0,0));var sourceBefore=Json.Write(longitudinal);
        var longCut=ParametricProjection.Build(longitudinal,front,true);
        Check(longCut.Sections.Count==2,"longitudinal cut has two separate pipe walls");
        Near(longCut.Sections.Sum(Area),4000,"longitudinal wall areas are exact");
        var offset=new RigidPlacement{Origin=new Vector3(0,45,0),AxisX=front.AxisX,AxisY=front.AxisY};
        var offsetCut=ParametricProjection.Build(longitudinal,offset,true);
        Check(offsetCut.Sections.Count==1,"offset cut outside bore has one connected strip");
        Near(offsetCut.Sections.Sum(Area),400*Math.Sqrt(2500-2025),"offset section width is analytic");
        var tangent=new RigidPlacement{Origin=new Vector3(0,50,0),AxisX=front.AxisX,AxisY=front.AxisY};
        Check(ParametricProjection.Build(longitudinal,tangent,true).Sections.Count==0,"tangent plane produces no hatch");
        var box=Mark();box.AddPart(Plate(20,30,40),At(-10,-15,-20));
        var boxCut=ParametricProjection.Build(box,At(0,0,0),true);
        Near(boxCut.Sections.Sum(Area),600,"plate cut area");Near(boxCut.Curves.Sum(Length),100,"coincident cut and silhouette are deduplicated");
        var moved=Mark();moved.AddPart(Pipe(),At(1000000,-2000000,3000000));
        var movedView=ParametricProjection.Build(moved,At(1000000,-2000000,3000000));
        Near(ArcSum(movedView,50),2*Math.PI,"large world coordinates removed by view frame");
        var resized=Json.Copy(longitudinal);resized.Parts[0].Pipe.OuterDiameter=120;
        var bigger=ParametricProjection.Build(resized,At(0,0,0));
        Near(ArcSum(bigger,60),2*Math.PI,"parameter change recalculates outer radius");
        Near(ArcSum(bigger,50),2*Math.PI,"wall thickness retained on resize");
        var oblique=new RigidPlacement{AxisX=new Vector3(Math.Sqrt(.5),0,Math.Sqrt(.5)),AxisY=new Vector3(0,1,0)};
        Check(ParametricProjection.Build(longitudinal,oblique,false,true).Curves.Count>0,"oblique pipe view produces contours");
        var rotated=Mark();rotated.AddPart(Plate(20,30,40),new RigidPlacement{Origin=new Vector3(0,0,-20),AxisX=new Vector3(Math.Sqrt(.5),Math.Sqrt(.5),0),AxisY=new Vector3(-Math.Sqrt(.5),Math.Sqrt(.5),0)});
        var rotatedCut=ParametricProjection.Build(rotated,At(0,0,0),true);
        Near(rotatedCut.Sections.Sum(Area),600,"rotated plate section has exact area");
        Near(rotatedCut.Curves.Sum(Length),100,"rotated plate projected perimeter");
        var coveredRotated=Mark();coveredRotated.AddPart(Plate(20,20,2),At(-10,-10,0));
        coveredRotated.AddPart(Plate(100,100,2),new RigidPlacement{Origin=new Vector3(0,-70,10),AxisX=new Vector3(Math.Sqrt(.5),Math.Sqrt(.5),0),AxisY=new Vector3(-Math.Sqrt(.5),Math.Sqrt(.5),0)});
        Check(ParametricProjection.Build(coveredRotated,At(0,0,0),false,true).Curves.Any(c=>c.Hidden),"rotated cover hides rear plate");
        var obliqueCut=ParametricProjection.Build(longitudinal,new RigidPlacement{Origin=new Vector3(0,0,100),AxisX=oblique.AxisX,AxisY=oblique.AxisY},true);
        Check(obliqueCut.Sections.Count==1&&obliqueCut.Sections[0].Loops.Count==2,"oblique pipe section retains bore");
        var bad=Mark();bad.AddPart(Plate(10,10,10),At(0,0,0));bad.AddPart(Plate(10,10,10),At(5,5,5));
        Rejected(()=>ParametricProjection.Build(bad,At(0,0,0)),"penetrating plates rejected");
        var intrusion=Mark();intrusion.AddPart(Pipe(),At(0,0,0));intrusion.AddPart(Plate(10,10,10),At(45,0,10));
        Rejected(()=>ParametricProjection.Build(intrusion,At(0,0,0)),"plate penetrating pipe wall rejected");
        var unsupported=Mark();unsupported.AddPart(new PartDefinition{Name="Криволинейный лист",Kind=PartKind.CurvedPlate,CurvedPlate=new CurvedPlateShape{InnerRadius=50,Thickness=10,Length=100,AngleRadians=1}},At(0,0,0));
        Check(ParametricProjection.Build(unsupported,At(0,0,0)).Curves.Count>0,"curved plate uses common spatial pipeline");
        Check(Json.Write(longitudinal)==sourceBefore,"projection leaves source parameters and identities unchanged");
        foreach(var frame in new[]{
            new RigidPlacement{AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,-1,0)},
            new RigidPlacement{AxisX=new Vector3(-1,0,0),AxisY=new Vector3(0,0,1)},
            new RigidPlacement{AxisX=new Vector3(0,1,0),AxisY=new Vector3(0,0,1)},
            new RigidPlacement{AxisX=new Vector3(0,-1,0),AxisY=new Vector3(0,0,1)}})
        {
            var projection=ParametricProjection.Build(longitudinal,frame,false,true);
            Near(projection.Curves.Where(c=>!c.Hidden).Sum(Length),frame.AxisY.Y==-1?2*Math.PI*90:600,"opposite and side directions retain projected outline");
        }
        var spin=new RigidPlacement{AxisX=new Vector3(Math.Sqrt(.5),Math.Sqrt(.5),0),AxisY=new Vector3(-Math.Sqrt(.5),Math.Sqrt(.5),0)};
        Near(ArcSum(ParametricProjection.Build(longitudinal,spin),50),2*Math.PI,"pipe rotation about its own axis does not alter its shape");
        var touching=Mark();touching.AddPart(Pipe(),At(0,0,0));touching.AddPart(Plate(20,20,10),At(50,-10,10));
        Check(ParametricProjection.Build(touching,At(0,0,0)).Curves.Count>0,"tangent plate contact is supported");
        Console.WriteLine(checks+" parametric projection checks passed");
    }
    static double Length(ParametricProjection.Curve2 c)=>c.Circular?c.Radius*c.Sweep:Math.Sqrt(Math.Pow(c.X1-c.X0,2)+Math.Pow(c.Y1-c.Y0,2));
    static double Area(ParametricProjection.SectionArea a)=>a.Loops.Sum(loop=>loop.Count==1&&loop[0].Circular?0:Math.Abs(loop.Sum(c=>c.X0*c.Y1-c.X1*c.Y0))/2);
    internal static void Export(string path)
    {
        var mark=Mark();mark.AddPart(Pipe(),At(0,0,-100));mark.AddPart(Plate(60,120,10),At(0,-60,120));mark.AddPart(Plate(10,10,10),At(-5,-5,-40));
        var side=new RigidPlacement{AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)};
        var before=Json.Write(mark);
        var scenes=new[]{new{Name="Вид вдоль трубы",Result=ParametricProjection.Build(mark,At(0,0,0),false,true)},
            new{Name="Вид сбоку",Result=ParametricProjection.Build(mark,side,false,true)},
            new{Name="Поперечный разрез: пластина за плоскостью видна в отверстии",Result=ParametricProjection.Build(mark,At(0,0,0),true,true)},
            new{Name="Продольный разрез",Result=ParametricProjection.Build(mark,side,true,true)}};
        if(before!=Json.Write(mark))throw new Exception("Projection mutated source model");
        File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(scenes,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }
}
