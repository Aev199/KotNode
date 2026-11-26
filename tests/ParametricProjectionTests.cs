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
        // --- Iteration 4.1.A / Generator 16: Filleted I-beams (ГОСТ 35087-2024) & 3D Rebar ---
        var beamMark=Mark();
        var beam30=BeamCatalog.Create("30Б2",1000);
        beamMark.AddPart(new PartDefinition{Name="Двутавр 30Б2",Kind=PartKind.IBeam,IBeam=beam30},At(0,0,0));
        var beamTop=ParametricProjection.Build(beamMark,At(0,0,0),false,true);
        Check(beamTop.Curves.Count>0&&beamTop.Curves.All(ViewLineOverrides.HasSource),"filleted I-beam 30Б2 axial view has 100% semantic keys");
        var beamSide=ParametricProjection.Build(beamMark,front,false,true);
        Check(beamSide.Curves.Count>0&&beamSide.Curves.All(ViewLineOverrides.HasSource),"filleted I-beam 30Б2 side view has 100% semantic keys");
        var beamOblique=ParametricProjection.Build(beamMark,oblique,false,true);
        Check(beamOblique.Curves.Count>0&&beamOblique.Curves.All(ViewLineOverrides.HasSource),"filleted I-beam 30Б2 oblique view has 100% semantic keys");
        var beamCut=ParametricProjection.Build(beamMark,At(0,0,500),true,true);
        double expectedBeamArea=2*beam30.Width*beam30.FlangeThickness+(beam30.Height-2*beam30.FlangeThickness)*beam30.WebThickness+(4-Math.PI)*beam30.RootRadius*beam30.RootRadius;
        Check(beamCut.Sections.Count==1&&Math.Abs(Area(beamCut.Sections[0])-expectedBeamArea)<3.5,"filleted I-beam cross section area matches nominal geometry ("+Area(beamCut.Sections[0])+" vs "+expectedBeamArea+")");
        Check(beamCut.Curves.All(ViewLineOverrides.HasSource),"filleted I-beam section has 100% semantic keys");
        var beamChains=ViewLineOverrides.Chain(beamTop.Curves);
        var filletChain=beamChains.Single(c=>!c.Hidden&&c.SourceKey.EndsWith(":fillet:0"));
        Check(filletChain.Segments.Count>=3,"I-beam fillet arc merges into single polyline chain");
        var filletRule=ViewLineOverrides.CreateForChain(filletChain,ViewLineMode.Hidden,false,batch);
        var beam40Mark=Json.Copy(beamMark);
        beam40Mark.Parts[0].IBeam=BeamCatalog.Create("40Ш2",1000);
        var beam40Top=ParametricProjection.Build(beam40Mark,At(0,0,0),false,true);
        var afterBeamResize=ViewLineOverrides.Apply(beam40Top.Curves,filletRule,false,out var unmatchedBeam);
        Check(unmatchedBeam==0&&afterBeamResize.Any(c=>c.Hidden),"I-beam fillet override survives profile change 30Б2 -> 40Ш2");
        // Straight, L-bent, U-bent, and 3D spatially-bent rebar
        var straightBarMark=Mark();
        straightBarMark.AddPart(new PartDefinition{Name="Стержень прямой",Kind=PartKind.Rebar,Rebar=new RebarShape{Diameter=16,Points=new List<Vector3>{new Vector3(0,0,0),new Vector3(0,0,200)}}},At(0,0,0));
        var straightBarTop=ParametricProjection.Build(straightBarMark,At(0,0,0),false,true);
        var straightBarSide=ParametricProjection.Build(straightBarMark,front,false,true);
        var straightBarCut=ParametricProjection.Build(straightBarMark,At(0,0,100),true,true);
        Check(straightBarTop.Curves.Count>0&&straightBarTop.Curves.All(ViewLineOverrides.HasSource),"straight rebar axial view has 100% semantic keys");
        Check(straightBarSide.Curves.Count>0&&straightBarSide.Curves.All(ViewLineOverrides.HasSource),"straight rebar side view has 100% semantic keys");
        Check(straightBarCut.Sections.Count==1&&Math.Abs(Area(straightBarCut.Sections[0])-Math.PI*64)<3.0,"straight rebar cross section area matches circle ("+Area(straightBarCut.Sections[0])+")");
        Check(straightBarCut.Curves.All(ViewLineOverrides.HasSource),"straight rebar section has 100% semantic keys");
        var uBarMark=Mark();
        uBarMark.AddPart(new PartDefinition{Name="П-стержень",Kind=PartKind.Rebar,Rebar=new RebarShape{
            Diameter=16,
            Points=new List<Vector3>{new Vector3(0,0,0),new Vector3(0,200,0),new Vector3(200,200,0),new Vector3(200,0,0)},
            BendRadii=new List<double>{40,40}}},At(0,0,0));
        var uBarTop=ParametricProjection.Build(uBarMark,At(0,0,0),false,true);
        Check(uBarTop.Curves.Count>0&&uBarTop.Curves.All(ViewLineOverrides.HasSource),"U-bent rebar top view has 100% semantic keys");
        var uBarCut=ParametricProjection.Build(uBarMark,new RigidPlacement{Origin=new Vector3(0,100,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)},true,true);
        Check(uBarCut.Sections.Count==2&&uBarCut.Curves.All(ViewLineOverrides.HasSource),"section across both legs of U-rebar produces two cut sections with 100% keys");
        var bendChains=ViewLineOverrides.Chain(uBarTop.Curves);
        var bendSil=bendChains.First(c=>!c.Hidden&&c.SourceKey.Contains(":sil:bend:0:"));
        Check(bendSil.Segments.Count>=4,"rebar bend silhouette merges into continuous polyline chain");
        var bendRule=ViewLineOverrides.CreateForChain(bendSil,ViewLineMode.Hidden,false,batch);
        var thickerBar=Json.Copy(uBarMark);thickerBar.Parts[0].Rebar.Diameter=20;
        var thickerView=ParametricProjection.Build(thickerBar,At(0,0,0),false,true);
        var afterBarResize=ViewLineOverrides.Apply(thickerView.Curves,bendRule,false,out var unmatchedBar);
        Check(unmatchedBar==0&&afterBarResize.Any(c=>c.Hidden),"rebar bend override survives diameter change Ø16 -> Ø20");
        var spatialBarMark=Mark();
        spatialBarMark.AddPart(new PartDefinition{Name="3D стержень",Kind=PartKind.Rebar,Rebar=new RebarShape{
            Diameter=16,
            Points=new List<Vector3>{new Vector3(0,0,0),new Vector3(150,0,0),new Vector3(150,150,0),new Vector3(150,150,150)},
            BendRadii=new List<double>{50,50}}},At(0,0,0));
        spatialBarMark.AddPart(Plate(80,80,10),At(110,110,180));
        var spatialBarView=ParametricProjection.Build(spatialBarMark,oblique,false,true);
        Check(spatialBarView.Curves.Count>0&&spatialBarView.Curves.All(ViewLineOverrides.HasSource),"3D spatially bent rebar with occluding plate has 100% semantic keys");
        Check(ParametricProjection.Build(spatialBarMark,At(0,0,0),false,true).Curves.Any(c=>c.Hidden),"plate occludes rebar behind it");
        // --- Iteration 4.1.B / Generator 17: Parametric Weld Overlays (NAWELD) ---
        var weldBatch=Guid.NewGuid();
        var plateMark=Mark();
        plateMark.AddPart(Plate(200,100,12),At(0,0,0));
        var plateView=ParametricProjection.Build(plateMark,At(0,0,0),false,true);
        var plateChains=ViewLineOverrides.Chain(plateView.Curves);
        var bottomEdge=ViewLineOverrides.Pick(plateChains,100,0,5.0).Chain;
        Check(ViewWelds.SideFromPoint(bottomEdge,100,15)==ViewWeldSide.Left,"weld side above horizontal +X edge is Left");
        Check(ViewWelds.SideFromPoint(bottomEdge,100,-15)==ViewWeldSide.Right,"weld side below horizontal +X edge is Right");
        var siteWelds=ViewWelds.CreateForChain(bottomEdge,ViewWeldStyle.Site,ViewWeldSide.Right,8,false,weldBatch);
        var siteStrokes=ViewWelds.Build(plateView.Curves,siteWelds,10,out var unmatchedW);
        Check(unmatchedW==0&&siteStrokes.Count>=2&&siteStrokes.Count%2==0&&siteStrokes.All(s=>!s.Hidden),"site weld produces paired cross strokes on NA_WELD");
        Check(siteStrokes.All(s=>s.Y0<0.5&&s.Y1<0.5),"site weld strokes placed on chosen Right side");
        var factoryWelds=ViewWelds.CreateRangeForChain(bottomEdge,ViewWeldStyle.Factory,ViewWeldSide.Left,6,0.2,0.8,false,weldBatch);
        var factoryStrokes=ViewWelds.Build(plateView.Curves,factoryWelds,10,out unmatchedW);
        Check(unmatchedW==0&&factoryStrokes.Count>=4&&factoryStrokes.All(s=>s.X0>=39&&s.X0<=161&&s.Y1>0),"factory weld ticks cover picked fragment on Left side");
        var hiddenWelds=ViewWelds.CreateForChain(bottomEdge,ViewWeldStyle.Hidden,ViewWeldSide.Both,6,true,weldBatch);
        var hiddenStrokes=ViewWelds.Build(plateView.Curves,hiddenWelds,10,out unmatchedW);
        Check(unmatchedW==0&&hiddenStrokes.Count>=2&&hiddenStrokes.All(s=>s.Hidden),"hidden weld strokes marked hidden");
        // Resizing plate lengthens weld automatically with zero unmatched entries
        var longerPlate=Json.Copy(plateMark);longerPlate.Parts[0].Plate.Length=400;
        var longerPlateView=ParametricProjection.Build(longerPlate,At(0,0,0),false,true);
        var longerSiteStrokes=ViewWelds.Build(longerPlateView.Curves,siteWelds,10,out unmatchedW);
        Check(unmatchedW==0&&longerSiteStrokes.Count>siteStrokes.Count,"weld scales automatically when plate length doubles");
        // Weld along faceted curved plate arc follows resized radius
        var arcWelds=ViewWelds.CreateForChain(outerArcChain,ViewWeldStyle.Factory,ViewWeldSide.Left,6,false,weldBatch);
        var arcStrokes=ViewWelds.Build(resizedCurvedView.Curves,arcWelds,10,out unmatchedW);
        Check(unmatchedW==0&&arcStrokes.Count>=4,"faceted curved arc weld survives radius and facet-count change");
        Rejected(()=>ViewWelds.Validate(new ViewWeldOverlay{InstanceId=Guid.NewGuid(),SourceKey="k",From=0,To=1,LegMm=-2}),"negative weld leg rejected");
        // --- Stage 5: Associative View Annotations (anchors, dims, leaders, axes, section callouts) ---
        var snapEndA=ViewAnnotations.SnapAnchor(plateChains,1,1,5.0);
        var snapEndB=ViewAnnotations.SnapAnchor(plateChains,199,1,5.0);
        Check(snapEndA!=null&&snapEndB!=null&&Math.Abs(snapEndA.Point.X-0)<1e-6&&Math.Abs(snapEndB.Point.X-200)<1e-6,"semantic anchor snaps to plate endpoints");
        var dimPlate=new ViewDimension{Kind=ViewDimensionKind.Horizontal,AnchorA=snapEndA.Anchor,AnchorB=snapEndB.Anchor,OffsetX=0,OffsetY=-40};
        var resDims=ViewAnnotations.ResolveDimensions(new[]{dimPlate},plateView.Curves,null,20,out var unmatchedA);
        Check(unmatchedA==0&&resDims.Count==1&&Math.Abs(resDims[0].B.X-resDims[0].A.X-200)<1e-6,"linear dimension resolves to exact 200 mm");
        var resDimsLonger=ViewAnnotations.ResolveDimensions(new[]{dimPlate},longerPlateView.Curves,null,20,out unmatchedA);
        Check(unmatchedA==0&&resDimsLonger.Count==1&&Math.Abs(resDimsLonger[0].B.X-resDimsLonger[0].A.X-400)<1e-6&&Math.Abs(resDimsLonger[0].LinePoint.Y-(-40))<1e-6,"linear dimension tracks resized plate to 400 mm while keeping Y offset");
        // Diameter dimension & centerline axis on pipe rim
        var pipeChains=ViewLineOverrides.Chain(axial.Curves);
        var snapCenter=ViewAnnotations.SnapAnchor(pipeChains,50,0,5.0,true);
        Check(snapCenter!=null&&snapCenter.Anchor.Kind==ViewAnchorKind.CurveCenter,"diameter/axis snap resolves circular center");
        var diaDim=new ViewDimension{Kind=ViewDimensionKind.Diameter,AnchorA=snapCenter.Anchor,OffsetX=70,OffsetY=70};
        var resDia=ViewAnnotations.ResolveDimensions(new[]{diaDim},newRim.Curves,null,20,out unmatchedA);
        Check(unmatchedA==0&&Math.Abs(resDia[0].Radius-60)<1e-6,"diameter dimension tracks resized pipe radius 50 -> 60");
        var axStrokes=ViewAnnotations.BuildAxes(new[]{new ViewAxisMark{Center=snapCenter.Anchor}},newRim.Curves,20,out unmatchedA);
        Check(unmatchedA==0&&axStrokes.Count==2&&Math.Abs(axStrokes[0].X1-axStrokes[0].X0-(2*(60+50)))<1e-6,"axis cross scales with resized pipe radius + overhang");
        // Position leader tracks PartDefinition.Position change; Weld leader tracks weld leg & midpoint
        plateMark.Parts[0].IncludeInSpec=true;plateMark.Parts[0].Position="7";
        var posLeader=new ViewLeader{Kind=ViewLeaderKind.Position,Target=snapEndB.Anchor,LandingOffsetX=30,LandingOffsetY=30};
        var resPos=ViewAnnotations.ResolveLeaders(new[]{posLeader},plateMark,plateView.Curves,null,20,out unmatchedA);
        Check(unmatchedA==0&&resPos[0].Text=="Поз. 7","position leader pulls PartDefinition.Position dynamically");
        plateMark.Parts[0].Position="12";
        var resPos2=ViewAnnotations.ResolveLeaders(new[]{posLeader},plateMark,longerPlateView.Curves,null,20,out unmatchedA);
        Check(unmatchedA==0&&resPos2[0].Text=="Поз. 12"&&Math.Abs(resPos2[0].Tip.X-400)<1e-6,"position leader updates both text and tip on part edit");
        var weldLeader=new ViewLeader{Kind=ViewLeaderKind.Weld,Target=new ViewAnchor{Kind=ViewAnchorKind.WeldMidpoint,WeldId=siteWelds[0].Id},LandingOffsetX=20,LandingOffsetY=-30};
        var resWL=ViewAnnotations.ResolveLeaders(new[]{weldLeader},plateMark,plateView.Curves,siteWelds,10,out unmatchedA);
        Check(unmatchedA==0&&resWL[0].Text.Contains("8")&&resWL[0].Text.Contains("монт."),"weld leader formats GOST leg and site label");
        // Section callout links to section view name dynamically
        var secId=Guid.NewGuid();
        var callout=new ViewSectionCallout{Start=snapEndA.Anchor,End=snapEndB.Anchor,TargetViewId=secId,Label="1-1"};
        var cg=ViewAnnotations.BuildSectionCallouts(new[]{callout},plateView.Curves,id=>id==secId?"2-2":null,20,out unmatchedA);
        Check(unmatchedA==0&&cg.ThickLines.Count==2&&cg.Arrows.Count==2&&cg.Labels.All(l=>l.Item3=="2"),"section callout resolves dynamic linked section view name");
        // --- Stages 6 & 7: Universal Spec, Local VRS, Project VRS & CSV ---
        var repMark=new AssemblyMark{Code="УР-10",Name="Узел крепления"};
        repMark.AddPart(new PartDefinition{Name="Труба распорки",Position="",Material="С245",IncludeInSpec=false,Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=820,Thickness=12,Length=1000}},At(0,0,0));
        repMark.AddPart(new PartDefinition{Name="Фасонка",Position="1",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=500,Width=200,Thickness=12}},At(450,0,0));
        var plateInstId=repMark.Instances.Last().Id;
        repMark.CopyInstance(plateInstId);
        repMark.AddPart(new PartDefinition{Name="Арматура",Position="2",Material="А500С",IncludeInSpec=true,Kind=PartKind.Rebar,Rebar=new RebarShape{Diameter=16,Points=new List<Vector3>{new Vector3(0,0,0),new Vector3(500,0,0)}}},At(0,0,100));
        var specPlan=AssemblyReporting.BuildMarkSpec(repMark);
        Check(specPlan.Rows.Count==6&&specPlan.Rows.Any(r=>r.Length>1&&r[1].Contains("Лист 12×200×500"))&&specPlan.Rows.Any(r=>r.Length>1&&r[1].Contains("Ø16 А500С")),"universal mark spec includes only specifiable parts with GOST designations");
        Check(specPlan.Rows.Last()[0].Contains("исключено: 1"),"non-spec pipe excluded and noted in spec footer");
        var vrsPlan=AssemblyReporting.BuildMarkVrs(repMark,3);
        Check(Math.Abs(vrsPlan.TotalMassKg-specPlan.TotalMassKg*3)<1e-6,"mark VRS multiplies mass by node count");
        var proj2=new AssemblyProject{Code="ПР-2026",Name="Котлован"};
        var legacyNode=new NodeMark{Code="У-01",Name="Стык труб"};
        proj2.Marks.Add(legacyNode);
        proj2.Assemblies.Add(repMark);
        proj2.Uses.Add(new Use{MarkId=legacyNode.Id,Location="Ярус 1",Count=4,Included=true});
        proj2.Uses.Add(new Use{MarkId=repMark.Id,Location="Ярус 1",Count=2,Included=true});
        proj2.Uses.Add(new Use{MarkId=repMark.Id,Location="Ярус 2",Count=5,Included=true});
        var projVrsAll=AssemblyReporting.BuildProjectVrs(proj2);
        var projVrsTier1=AssemblyReporting.BuildProjectVrs(proj2,"Ярус 1");
        Check(projVrsAll.TotalMassKg>projVrsTier1.TotalMassKg&&projVrsTier1.TotalMassKg>0,"schema-2 project VRS combines legacy and universal marks and filters by scope");
        var csvText=AssemblyReporting.ToCsv(projVrsAll);
        Check(csvText.Contains("ПР-2026")&&csvText.Contains(";"),"CSV export produces valid semicolon table");
        // --- Stage 8: Safe Formula Engine, DAG Parameter Solver, Surface & Array Constraints ---
        var vars=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase){{"D",820},{"N",4}};
        Near(FormulaEngine.Evaluate("D / 2 + sqrt(16) * 10",vars),450,"formula engine arithmetic and sqrt");
        Near(FormulaEngine.Evaluate("if(N > 3, min(84, 360 / N - 18), 90)",vars),72,"formula engine conditional and min");
        Rejected(()=>FormulaEngine.Evaluate("10 / (N - 4)",vars),"formula engine rejects division by zero");
        var cyclic=new AssemblyMark{Parameters=new List<MarkParameter>{
            new MarkParameter{Name="A",Formula="B + 1"},new MarkParameter{Name="B",Formula="A + 1"}}};
        Rejected(()=>ParametricEngine.Solve(cyclic),"cyclic parameter dependency rejected");
        // --- Stage 9: Built-in Parametric Families, Live Re-Solve & Roundtrip Persistence ---
        var families=NodeFamilyLibrary.BuiltIn();
        Check(families.Count==4,"four starter parametric families built in (including 3D angled pipe-to-waler)");
        // Test 3D multi-plane junction angles (Alpha in plan + Beta in elevation) driving plate length, gusset polygon contour, and stiffener count
        var waler90=NodeFamilyLibrary.Instantiate(families[3],"У-04-90","Узел 90°",new Dictionary<string,double>{{"Alpha",90},{"Beta",0}});
        var waler45=NodeFamilyLibrary.Instantiate(families[3],"У-04-45","Узел 45°/20°",new Dictionary<string,double>{{"Alpha",45},{"Beta",20}});
        var dist90=waler90.Mark.Parts.Single(p=>p.Position=="1").Plate.Length;
        var dist45=waler45.Mark.Parts.Single(p=>p.Position=="1").Plate.Length;
        Check(dist45>dist90+150,"sharper junction angle Alpha=45 extends distribution plate L_contact ("+dist90+" -> "+dist45+")");
        var stiffPart90=waler90.Mark.Parts.Single(p=>p.Position=="3").Id;
        var stiffPart45=waler45.Mark.Parts.Single(p=>p.Position=="3").Id;
        int stiffCount90=waler90.Mark.Instances.Count(i=>i.PartId==stiffPart90);
        int stiffCount45=waler45.Mark.Instances.Count(i=>i.PartId==stiffPart45);
        Check(stiffCount45>stiffCount90,"sharper junction angle automatically increases stiffener count ("+stiffCount90+" -> "+stiffCount45+")");
        var pipeAxis45=waler45.Mark.Instances.Single(i=>waler45.Mark.Parts.Single(p=>p.Id==i.PartId).Kind==PartKind.Pipe).Placement;
        var pipeEnd45=pipeAxis45.ToWorld(new Vector3(0,0,1));
        var pipeDir45=new Vector3(pipeEnd45.X-pipeAxis45.Origin.X,pipeEnd45.Y-pipeAxis45.Origin.Y,pipeEnd45.Z-pipeAxis45.Origin.Z);
        Near(pipeDir45.Z,Math.Sin(20*Math.PI/180),"pipe axis tilts in vertical plane by Beta=20°");
        Near(pipeDir45.X,Math.Cos(20*Math.PI/180)*Math.Cos(45*Math.PI/180),"pipe axis turns in horizontal plane by Alpha=45°");
        var gusset90=waler90.Mark.Parts.Single(p=>p.Position=="2").PolygonPlate;
        var gusset45=waler45.Mark.Parts.Single(p=>p.Position=="2").PolygonPlate;
        Check(Math.Abs(gusset45.Outer[2].X-gusset45.Outer[1].X)>50&&Math.Abs(gusset90.Outer[2].X-gusset90.Outer[1].X)<1e-6,"beveled polygon gusset morphs dynamically with junction angle Alpha");
        Check(waler45.Views.Any(v=>v.FrameAnchorId.HasValue),"angled junction family includes pipe-anchored 3D view");
        var spliceFam=NodeFamilyLibrary.Instantiate(families[0],"У-101","Стык 1020",new Dictionary<string,double>{{"D",1020},{"N_plates",6}});
        Check(spliceFam.Mark.Parts.Single(p=>p.Kind==PartKind.Pipe).Pipe.OuterDiameter==1020,"family instantiation updates pipe diameter to 1020");
        Check(spliceFam.Mark.Parts.Single(p=>p.Kind==PartKind.CurvedPlate).CurvedPlate.InnerRadius==510,"curved cover plate inner radius follows D/2 = 510");
        Check(spliceFam.Mark.Instances.Count(i=>i.PartId==spliceFam.Mark.Parts.Single(p=>p.Kind==PartKind.CurvedPlate).Id)==6,"circular array expands cover plates from 4 to 6");
        // Shrink array from 6 -> 3 and verify stable cleanup
        spliceFam.Mark.Parameters.Single(p=>p.Name=="N_plates").Value=3;
        var shrinkReport=ParametricEngine.Solve(spliceFam.Mark);
        Check(shrinkReport.RemovedInstances.Count==3&&spliceFam.Mark.Instances.Count(i=>i.PartId==spliceFam.Mark.Parts.Single(p=>p.Kind==PartKind.CurvedPlate).Id)==3,"circular array shrinks cleanly from 6 to 3");
        // Pipe base plate family: OnPipeSurface ribs follow diameter change + views project cleanly!
        var baseFam=NodeFamilyLibrary.Instantiate(families[1],"У-202","Опора 720",new Dictionary<string,double>{{"D",720},{"N_ribs",6}});
        var basePlateShape=baseFam.Mark.Parts.Single(p=>p.Position=="1").Plate;
        Near(basePlateShape.Length,720+2*140+40,"base plate formula B_base = D + 2*L_rib + 40");
        var ribPartId=baseFam.Mark.Parts.Single(p=>p.Position=="2").Id;
        var ribInsts=baseFam.Mark.Instances.Where(i=>i.PartId==ribPartId).ToList();
        Check(ribInsts.Count==6&&ribInsts.All(r=>{var mid=r.Placement.ToWorld(new Vector3(0,0,6));return Math.Abs(Math.Sqrt(mid.X*mid.X+mid.Y*mid.Y)-360)<1e-5;}),"all 6 OnPipeSurface ribs sit flush on R=360 pipe wall");
        foreach(var fam in families)
        {
            var instFam=NodeFamilyLibrary.Instantiate(fam,"Т-"+fam.Mark.Code,fam.Name);
            foreach(var vt in instFam.Views)
            {
                var proj=ParametricProjection.Build(instFam.Mark,vt.Frame,vt.IsSection,true);
                Check(proj.Curves.Count>0&&proj.Curves.All(ViewLineOverrides.HasSource),"family '"+fam.Name+"' view '"+vt.Name+"' builds with 100% semantic keys");
            }
            Check(AssemblyReporting.BuildMarkSpec(instFam.Mark).TotalMassKg>0,"family '"+fam.Name+"' produces positive spec mass");
        }
        // Save & Load .knfamily.json roundtrip
        var famDir=Path.Combine(Path.GetTempPath(),"kotnode-fam-"+Guid.NewGuid());Directory.CreateDirectory(famDir);
        try
        {
            var famPath=Path.Combine(famDir,"splice.knfamily.json");
            NodeFamilyLibrary.Save(famPath,spliceFam);
            var reloaded=NodeFamilyLibrary.Load(famPath);
            Check(reloaded.Name==spliceFam.Name&&reloaded.Views.Count==2&&reloaded.Mark.Parameters.Count==8,"family .knfamily.json roundtrips cleanly");
        }
        finally{Directory.Delete(famDir,true);}
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
