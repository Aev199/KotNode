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
        var a3=new Vector3(1000,2000,3000);
        var b3=new Vector3(1010,2000,3010);
        var c3=new Vector3(1005,2020,3005);
        var frame3=RigidPlacement.FromThreePoints(a3,b3,c3);
        Near(frame3.AxisX.X,Math.Sqrt(.5),"three point plane X");
        Near(frame3.AxisX.Z,Math.Sqrt(.5),"three point plane inclination");
        Near(frame3.AxisY.Y,1,"three point plane orthogonalization");
        Near(frame3.ToWorld(new Vector3(0,0,1)).X-a3.X,-Math.Sqrt(.5),"observer normal");
        var localFrame=frame3.RelativeTo(new RigidPlacement{Origin=new Vector3(1000,2000,3000)});
        var movedFrame=localFrame.InParent(new RigidPlacement{Origin=new Vector3(10,20,30),AxisX=new Vector3(0,1,0),AxisY=new Vector3(-1,0,0)});
        Near(movedFrame.Origin.X,10,"attached plane follows translation");
        Near(movedFrame.AxisX.Y,Math.Sqrt(.5),"attached plane follows rotation");
        Near(movedFrame.AxisY.X,-1,"attached plane maintains local orientation");
        var rev3=RigidPlacement.FromThreePoints(a3,b3,c3,true);
        Near(rev3.ToWorld(new Vector3(0,0,1)).X-a3.X,Math.Sqrt(.5),"reverse observer normal");
        Rejected(()=>RigidPlacement.FromThreePoints(a3,a3,c3),"coincident plane points rejected");
        Rejected(()=>RigidPlacement.FromThreePoints(a3,b3,new Vector3(1020,2000,3020)),"collinear plane points rejected");
        var m=Mark();m.AddPart(Pipe(),At(0,0,0));
        var axial=ParametricProjection.Build(m,At(0,0,0),false,true);
        Check(axial.Curves.Count==2,"axial hollow pipe has two circular boundaries");
        var rim=axial.Curves.Single(c=>Math.Abs(c.Radius-50)<1e-6);
        Check(ViewLineOverrides.HasSource(rim),"analytic rim has semantic identity");
        var batch=Guid.NewGuid();
        var line=new ParametricProjection.Curve2{InstanceId=rim.InstanceId,SourceKey="test-line",X0=0,Y0=0,X1=100,Y1=0,SourceFrom=.2,SourceTo=.8};
        var a=ViewLineOverrides.ParameterAt(line,25,2);var b=ViewLineOverrides.ParameterAt(line,75,-2);
        var range=ViewLineOverrides.CreateRange(line,ViewLineMode.Suppressed,a,b,false,batch);
        Near(range.Single().From,.35,"picked fragment start mapped to source");
        Near(range.Single().To,.65,"picked fragment end mapped to source");
        var remain=ViewLineOverrides.Apply(new[]{line},range,true,out var missing);
        Near(remain.Sum(Length),50,"partial deletion retains both ends");
        Check(remain.Count==2&&missing==0,"partial deletion splits curve");
        var shorter=ViewLineOverrides.CreateRange(rim,ViewLineMode.Hidden,.9,.1,false,batch);
        var longer=ViewLineOverrides.CreateRange(rim,ViewLineMode.Hidden,.9,.1,true,batch);
        Near(shorter.Sum(x=>x.To-x.From),.2,"circle shorter range crosses seam");
        Near(longer.Sum(x=>x.To-x.From),.8,"circle longer range");
        Near(ViewLineOverrides.HiddenLineScale(60,20,2,4),2.5,"short line dash pattern fits three periods");
        Near(ViewLineOverrides.HiddenLineScale(1000,20,2,4),10,"long line nominal dash scale");
        var suppress=ViewLineOverrides.Create(rim,ViewLineMode.Suppressed,true,batch);
        var resizeRim=Json.Copy(m);resizeRim.Parts[0].Pipe.OuterDiameter=120;
        var newRim=ParametricProjection.Build(resizeRim,At(0,0,0),false,true);
        var filtered=ViewLineOverrides.Apply(newRim.Curves,new[]{suppress},true,out var unmatched);
        Check(unmatched==0&&filtered.Count==1&&Math.Abs(filtered[0].Radius-50)<1e-6,"suppressed outer rim follows resized pipe, not equal radius inner rim");
        var fragment=ViewLineOverrides.Create(rim,ViewLineMode.Hidden,false,batch);
        fragment.From=.25;fragment.To=.75;
        var changed=ViewLineOverrides.Apply(newRim.Curves,new[]{fragment},false,out unmatched);
        Near(changed.Where(c=>c.Hidden).Sum(c=>c.Sweep),Math.PI,"manual hidden interval retained with hidden switch off");
        Near(changed.Where(c=>!c.Hidden&&c.Radius==60).Sum(c=>c.Sweep),Math.PI,"remaining rim visible");
        var restored=ViewLineOverrides.Create(rim,ViewLineMode.Auto,true,batch);
        changed=ViewLineOverrides.Apply(newRim.Curves,new[]{fragment,restored},false,out unmatched);
        Check(!changed.Any(c=>c.Hidden),"later auto rule restores default");
        var replacement=Mark();replacement.AddPart(Pipe(),At(0,0,0));
        var other=ViewLineOverrides.Apply(ParametricProjection.Build(replacement,At(0,0,0)).Curves,new[]{suppress},true,out unmatched);
        Check(unmatched==1&&other.Count==2,"correction never migrates to another instance");
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
        // --- Stage 4.1 / Generator 15: 100% spatial SourceKey coverage, chaining, geometric picking, faceted fragments ---
        var polyMark=Mark();
        var polyDef=new PartDefinition
        {
            Name="Фасонка с отверстиями",Kind=PartKind.PolygonPlate,
            PolygonPlate=new PolygonPlateShape
            {
                Thickness=12,
                Outer=new List<Point2>{new Point2(0,0),new Point2(200,0),new Point2(200,120),new Point2(80,120),new Point2(80,60),new Point2(0,60)},
                Holes=new List<List<Point2>>{new List<Point2>{new Point2(110,20),new Point2(160,20),new Point2(160,50),new Point2(110,50)}},
                RoundHoles=new List<RoundHole>{new RoundHole{Center=new Point2(40,30),Radius=12}}
            }
        };
        polyMark.AddPart(polyDef,At(0,0,-6));
        var polyTop=ParametricProjection.Build(polyMark,At(0,0,0),false,true);
        Check(polyTop.Curves.Count>0&&polyTop.Curves.All(ViewLineOverrides.HasSource),"polygon plate with holes has 100% semantic keys");
        var polyOblique=ParametricProjection.Build(polyMark,oblique,false,true);
        Check(polyOblique.Curves.Count>0&&polyOblique.Curves.All(ViewLineOverrides.HasSource),"oblique polygon plate has 100% semantic keys");
        var polyCut=ParametricProjection.Build(polyMark,At(0,0,0),true,true);
        Check(polyCut.Curves.Count>0&&polyCut.Curves.All(ViewLineOverrides.HasSource),"polygon plate section cut has 100% semantic keys");
        var obliquePipe=ParametricProjection.Build(longitudinal,oblique,false,true);
        Check(obliquePipe.Curves.Count>0&&obliquePipe.Curves.All(ViewLineOverrides.HasSource),"oblique pipe including smooth silhouettes has 100% semantic keys");
        var obliquePipeCut=ParametricProjection.Build(longitudinal,new RigidPlacement{Origin=new Vector3(0,0,100),AxisX=oblique.AxisX,AxisY=oblique.AxisY},true,true);
        Check(obliquePipeCut.Curves.Count>0&&obliquePipeCut.Curves.All(ViewLineOverrides.HasSource),"oblique pipe section has 100% semantic keys");
        var curvedMark=Mark();
        curvedMark.AddPart(new PartDefinition{Name="Накладка",Kind=PartKind.CurvedPlate,CurvedPlate=new CurvedPlateShape{InnerRadius=100,Thickness=12,Length=200,AngleRadians=Math.PI/2}},At(0,0,0));
        var curvedTop=ParametricProjection.Build(curvedMark,At(0,0,0),false,true);
        Check(curvedTop.Curves.Count>0&&curvedTop.Curves.All(ViewLineOverrides.HasSource),"curved plate top view has 100% semantic keys");
        var curvedSide=ParametricProjection.Build(curvedMark,front,false,true);
        Check(curvedSide.Curves.Count>0&&curvedSide.Curves.All(ViewLineOverrides.HasSource),"curved plate side view has 100% semantic keys");
        // Chaining faceted curves & geometric picking + Fragment across multiple facets
        var curvedChains=ViewLineOverrides.Chain(curvedTop.Curves);
        var outerArcChain=curvedChains.Single(c=>!c.Hidden&&c.SourceKey.EndsWith(":outer")&&c.Segments.Count>2);
        Check(Math.Abs(outerArcChain.TotalLength-112*Math.PI/2)<0.1,"faceted outer arc merges into one full-length chain");
        var hitArc=ViewLineOverrides.Pick(curvedChains,112*Math.Cos(Math.PI/4),112*Math.Sin(Math.PI/4),5.0);
        Check(hitArc!=null&&hitArc.Chain==outerArcChain,"geometric pick hits merged faceted arc chain");
        var u1=ViewLineOverrides.ParameterAt(outerArcChain,112*Math.Cos(Math.PI/8),112*Math.Sin(Math.PI/8));
        var u2=ViewLineOverrides.ParameterAt(outerArcChain,112*Math.Cos(3*Math.PI/8),112*Math.Sin(3*Math.PI/8));
        var facetedPreview=new List<ParametricProjection.Curve2>();
        var facetedRules=ViewLineOverrides.CreateRangeForChain(outerArcChain,ViewLineMode.Hidden,u1,u2,false,batch,facetedPreview);
        Check(facetedRules.Count==1&&facetedPreview.Count>=2,"fragment on faceted arc spans multiple segments as single source interval");
        var afterFacetedHidden=ViewLineOverrides.Apply(curvedTop.Curves,facetedRules,false,out var unmatchedFaceted);
        Check(unmatchedFaceted==0&&afterFacetedHidden.Any(c=>c.Hidden),"manual hidden fragment on faceted arc is preserved even when showHidden=false");
        // Resizing curved plate (which changes facet count!) must keep the override matched
        var resizedCurved=Json.Copy(curvedMark);resizedCurved.Parts[0].CurvedPlate.InnerRadius=160;
        var resizedCurvedView=ParametricProjection.Build(resizedCurved,At(0,0,0),false,true);
        var afterResizeFaceted=ViewLineOverrides.Apply(resizedCurvedView.Curves,facetedRules,false,out unmatchedFaceted);
        Check(unmatchedFaceted==0&&afterResizeFaceted.Any(c=>c.Hidden),"faceted override survives radius change and facet-count change");
        // Closed faceted loop (round hole) chains into full loop and supports Short/Long wrap-around fragment
        var holeChains=ViewLineOverrides.Chain(polyTop.Curves);
        var holeLoop=holeChains.Single(c=>c.SourceKey.Contains("roundhole:0")&&!c.Hidden);
        Check(holeLoop.Closed&&holeLoop.IsFullLoop&&holeLoop.Segments.Count>=12,"faceted round hole merges into closed full loop");
        var holeShort=ViewLineOverrides.CreateRangeForChain(holeLoop,ViewLineMode.Suppressed,0.9,0.1,false,batch);
        Near(holeShort.Sum(r=>r.To-r.From),0.2,"closed faceted loop shorter range wraps seam");
        var holeLong=ViewLineOverrides.CreateRangeForChain(holeLoop,ViewLineMode.Suppressed,0.9,0.1,true,batch);
        Near(holeLong.Sum(r=>r.To-r.From),0.8,"closed faceted loop longer range");
        // Coincident overlapping lines detected together by geometric pick
        var cA=new ParametricProjection.Curve2{InstanceId=Guid.NewGuid(),SourceKey="k1",X0=0,Y0=0,X1=100,Y1=0,SourceFrom=0,SourceTo=1,Hidden=false};
        var cB=new ParametricProjection.Curve2{InstanceId=Guid.NewGuid(),SourceKey="k2",X0=0,Y0=0.02,X1=100,Y1=0.02,SourceFrom=0,SourceTo=1,Hidden=false};
        var overlapHit=ViewLineOverrides.Pick(ViewLineOverrides.Chain(new[]{cA,cB}),50,0.01,5.0);
        Check(overlapHit!=null&&overlapHit.Coincident.Count==2,"coincident near-overlapping edges grouped on pick");
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
