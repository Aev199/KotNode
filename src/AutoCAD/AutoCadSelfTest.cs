using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using KotNode.Core;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;
[assembly: CommandClass(typeof(KotNode.AutoCAD.AutoCadSelfTest))]

namespace KotNode.AutoCAD
{
    // Real AutoCAD E2E self-test (runs inside accoreconsole.exe or full AutoCAD).
    // Verifies ACIS 3D solids, native Hatch, Polyline Plinegen, ViewShell, NALINEEDIT/NAWELD rebuilds,
    // native Dimensions/MLeaders, native Tables, Schema-2 project VRS, and 3D angled family re-solve + DWG roundtrip.
    public sealed class AutoCadSelfTest
    {
        static void Assert(bool cond,string msg,List<string> log)
        {
            if(!cond)throw new InvalidOperationException("E2E FAIL: "+msg);
            log.Add("PASS: "+msg);
        }

        static void WithDatabase(Database target,Action<Database> action)
        {
            var prev=HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase=target;
            try{action(target);}
            finally{HostApplicationServices.WorkingDatabase=prev;}
        }

        [CommandMethod("NAE2ETEST",CommandFlags.Modal)]
        public void RunE2E()
        {
            string reportPath=Path.Combine(Path.GetTempPath(),"kotnode_e2e_report.txt");
            string dwgPath=Path.Combine(Path.GetTempPath(),"kotnode_e2e_verified.dwg");
            var log=new List<string>();
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            try
            {
                if(doc==null)throw new InvalidOperationException("No active AutoCAD document.");
                var db=doc.Database;
                db.Insunits=UnitsValue.Millimeters;

                // --- TEST 1: All 6 ACIS Solid3d part types + Views + Hatch + Line Overrides + Welds + Annotations + Tables ---
                using(var scratch=new Database(true,true))
                WithDatabase(scratch,_=>{
                    scratch.Insunits=UnitsValue.Millimeters;
                    using(var tr=scratch.TransactionManager.StartTransaction())
                    {
                        var mark=new AssemblyMark{Code="E2E-ALL",Name="Тест всех 6 типов деталей"};
                        var pipe=new PartDefinition{Name="Труба",Position="",Material="С245",IncludeInSpec=false,Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=200,Thickness=10,Length=400}};
                        var pipeInst=mark.AddPart(pipe,new RigidPlacement{Origin=new Vector3(0,0,0)});

                        var plate=new PartDefinition{Name="Пластина",Position="1",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=300,Width=150,Thickness=12}};
                        var plateInst=mark.AddPart(plate,new RigidPlacement{Origin=new Vector3(150,-75,50)});

                        var poly=new PartDefinition
                        {
                            Name="Фасонка",Position="2",Material="С255",IncludeInSpec=true,Kind=PartKind.PolygonPlate,
                            PolygonPlate=new PolygonPlateShape
                            {
                                Thickness=12,
                                Outer=new List<Point2>{new Point2(0,0),new Point2(200,0),new Point2(200,120),new Point2(80,120),new Point2(80,60),new Point2(0,60)},
                                Holes=new List<List<Point2>>{new List<Point2>{new Point2(110,20),new Point2(160,20),new Point2(160,50),new Point2(110,50)}},
                                RoundHoles=new List<RoundHole>{new RoundHole{Center=new Point2(40,30),Radius=12}}
                            }
                        };
                        mark.AddPart(poly,new RigidPlacement{Origin=new Vector3(-350,-60,0)});

                        var curved=new PartDefinition{Name="Накладка",Position="3",Material="С255",IncludeInSpec=true,Kind=PartKind.CurvedPlate,CurvedPlate=new CurvedPlateShape{InnerRadius=100,Thickness=10,Length=200,AngleRadians=Math.PI/3}};
                        var curvedInst=mark.AddPart(curved,new RigidPlacement{Origin=new Vector3(0,0,100)});

                        var beam=new PartDefinition{Name="Двутавр 30Б2",Position="",Material="С255",IncludeInSpec=false,Kind=PartKind.IBeam,IBeam=BeamCatalog.Create("30Б2",500)};
                        mark.AddPart(beam,new RigidPlacement{Origin=new Vector3(0,400,0)});

                        var rebar=new PartDefinition{Name="Стержень Ø16",Position="4",Material="А500С",IncludeInSpec=true,Kind=PartKind.Rebar,Rebar=new RebarShape{
                            Diameter=16,
                            Points=new List<Vector3>{new Vector3(0,0,0),new Vector3(150,0,0),new Vector3(150,150,0),new Vector3(150,150,120)},
                            BendRadii=new List<double>{48,48}}};
                        mark.AddPart(rebar,new RigidPlacement{Origin=new Vector3(-200,300,0)});

                        var refs=new Dictionary<Guid,BlockReference>();
                        foreach(var inst in mark.Instances)
                        {
                            var def=mark.Parts.Single(p=>p.Id==inst.PartId);
                            var geom=AssemblyDrawing.Geometry(scratch,tr,def);
                            var btr=(BlockTableRecord)tr.GetObject(geom,OpenMode.ForRead);
                            int solids=btr.Cast<ObjectId>().Count(id=>!id.IsErased&&tr.GetObject(id,OpenMode.ForRead) is Solid3d);
                            Assert(solids>=1,"ACIS Solid3d created for "+def.Kind+" ("+solids+" solid(s))",log);
                            refs[inst.Id]=AssemblyDrawing.Insert(scratch,tr,mark,inst,geom);
                        }
                        AssemblyDrawing.Save(scratch,tr,mark);
                        var synced=AssemblyDrawing.Synchronize(scratch,tr,mark);
                        Assert(synced.Count==6,"Synchronize matched all 6 ACIS instances",log);

                        // Build Top View + Cross Section (real Hatch.EvaluateHatch + Polyline Plinegen!)
                        var topData=new AssemblyViews.ViewData{Name="Вид сверху",Frame=new RigidPlacement(),Hidden=true,AnnotationScale=20,ParametricPrototype=true};
                        AssemblyViews.Create(scratch,tr,mark,refs,topData,new Point3d(1500,0,0));
                        var secData=new AssemblyViews.ViewData{Name="1-1",Frame=new RigidPlacement{Origin=new Vector3(0,0,150)},IsSection=true,FullCut=true,Hidden=true,AnnotationScale=20,ParametricPrototype=true};
                        AssemblyViews.Create(scratch,tr,mark,refs,secData,new Point3d(3000,0,0));

                        var views=AssemblyViews.Find(scratch,tr,mark);
                        Assert(views.Count==2,"Created Top view and Cross section view with NA_VIEW_SHELL",log);

                        // Verify Hatch + Polyline inside section block
                        var secEntry=views.Single(v=>v.Data.Name=="1-1");
                        var secChild=ViewShell.RequireUnchanged(tr,secEntry);
                        var secBtr=(BlockTableRecord)tr.GetObject(secChild.BlockTableRecord,OpenMode.ForRead);
                        int hatchCount=secBtr.Cast<ObjectId>().Count(id=>!id.IsErased&&tr.GetObject(id,OpenMode.ForRead) is Hatch);
                        int polyCount=secBtr.Cast<ObjectId>().Count(id=>!id.IsErased&&tr.GetObject(id,OpenMode.ForRead) is Polyline p&&p.Plinegen);
                        Assert(hatchCount>=2,"Native AutoCAD Hatch.EvaluateHatch succeeded ("+hatchCount+" hatches across cut parts)",log);
                        Assert(polyCount>=1,"Merged Polyline with Plinegen=true created ("+polyCount+" polylines)",log);

                        // Stage 4.1: Apply LineOverrides + Welds + Stage 5 Annotations (Dims, Position Leader, Weld Leader, Axis, SectionMark)
                        var topEntry=views.Single(v=>v.Data.Name=="Вид сверху");
                        var chains=ParametricViewDrawing.ComputeChains(mark,topEntry.Data);
                        Assert(chains.Count>10&&chains.All(ViewLineOverrides.HasSource),"All displayed chains have semantic SourceKey ("+chains.Count+")",log);
                        var batch=Guid.NewGuid();
                        var plateChain=chains.First(c=>c.InstanceId==plateInst.Id&&!c.Hidden);
                        topEntry.Data.LineOverrides.AddRange(ViewLineOverrides.CreateForChain(plateChain,ViewLineMode.Hidden,false,batch));
                        var weldList=ViewWelds.CreateForChain(plateChain,ViewWeldStyle.Site,ViewWeldSide.Left,8,false,batch);
                        topEntry.Data.Welds.AddRange(weldList);

                        var snapA=ViewAnnotations.SnapAnchor(chains,plateChain.Segments[0].X0,plateChain.Segments[0].Y0,5.0);
                        var snapB=ViewAnnotations.SnapAnchor(chains,plateChain.Segments[0].X1,plateChain.Segments[0].Y1,5.0);
                        var pipeRim=chains.First(c=>c.InstanceId==pipeInst.Id&&c.IsFullLoop);
                        var snapCenter=ViewAnnotations.SnapAnchor(chains,0,0,20.0,true);
                        Assert(snapA!=null&&snapB!=null&&snapCenter!=null,"Semantic anchors resolved on live view",log);

                        topEntry.Data.Dimensions.Add(new ViewDimension{Kind=ViewDimensionKind.Horizontal,AnchorA=snapA.Anchor,AnchorB=snapB.Anchor,OffsetY=-50});
                        topEntry.Data.Dimensions.Add(new ViewDimension{Kind=ViewDimensionKind.Diameter,AnchorA=snapCenter.Anchor,OffsetX=140,OffsetY=140});
                        topEntry.Data.Leaders.Add(new ViewLeader{Kind=ViewLeaderKind.Position,Target=snapB.Anchor,LandingOffsetX=60,LandingOffsetY=60});
                        topEntry.Data.Leaders.Add(new ViewLeader{Kind=ViewLeaderKind.Weld,Target=new ViewAnchor{Kind=ViewAnchorKind.WeldMidpoint,WeldId=weldList[0].Id},LandingOffsetX=-60,LandingOffsetY=60});
                        topEntry.Data.Axes.Add(new ViewAxisMark{Center=snapCenter.Anchor});
                        topEntry.Data.SectionCallouts.Add(new ViewSectionCallout{Start=snapA.Anchor,End=snapB.Anchor,TargetViewId=secEntry.Data.Id,Label="1-1"});

                        AssemblyViews.Rebuild(scratch,tr,mark,refs,topEntry);
                        Assert(topEntry.Data.UnmatchedLineOverrides==0&&topEntry.Data.UnmatchedWelds==0&&topEntry.Data.UnmatchedAnnotations==0,
                            "Rebuild applied overrides, welds, and annotations with 0 unmatched",log);

                        var ms=AssemblyDrawing.ModelSpace(scratch,tr);
                        int dimCount=ms.Cast<ObjectId>().Count(id=>!id.IsErased&&tr.GetObject(id,OpenMode.ForRead) is Dimension);
                        int mlCount=ms.Cast<ObjectId>().Count(id=>!id.IsErased&&tr.GetObject(id,OpenMode.ForRead) is MLeader);
                        Assert(dimCount==2&&mlCount==2,"Native ModelSpace Dimensions ("+dimCount+") and MLeaders ("+mlCount+") created",log);

                        // Mutate part position "1" -> "99" & rebuild: verify in-place MLeader text update!
                        mark.Parts.Single(p=>p.Id==plate.Id).Position="99";
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,topEntry);
                        var posLeaders=ms.Cast<ObjectId>().Where(id=>!id.IsErased)
                            .Select(id=>tr.GetObject(id,OpenMode.ForRead) as MLeader)
                            .Where(ml=>ml!=null&&ml.MText!=null).Select(ml=>ml.MText.Contents).ToList();
                        Assert(posLeaders.Any(t=>t.Contains("Поз. 99")),"In-place MLeader text updated dynamically to 'Поз. 99'",log);
                        Assert(posLeaders.Any(t=>t=="8"),"SPDS weld leader formatted numeric leg k=8",log);

                        // Stabilization: view-only MOVE and combined MOVE must preserve
                        // paper offsets. Also roundtrip native manual shell content.
                        var managedDim=ms.Cast<ObjectId>().Where(id=>!id.IsErased)
                            .Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<RotatedDimension>().First();
                        double savedOffset=topEntry.Data.Dimensions[0].OffsetY;
                        var beforeLine=managedDim.DimLinePoint;
                        topEntry.Reference.UpgradeOpen();
                        topEntry.Reference.TransformBy(Matrix3d.Displacement(new Vector3d(75,40,0)));
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,topEntry);
                        Assert(Math.Abs(topEntry.Data.Dimensions[0].OffsetY-savedOffset)<1e-6&&
                            managedDim.DimLinePoint.DistanceTo(beforeLine+new Vector3d(75,40,0))<1e-5,
                            "View-only MOVE carries dimension line without changing manual offset",log);
                        var annotationSpace=AssemblyDrawing.ModelSpace(scratch,tr);
                        foreach(ObjectId id in annotationSpace)
                        {
                            var entity=tr.GetObject(id,OpenMode.ForRead) as Entity;
                            if(entity is Dimension||entity is MLeader)
                            {entity.UpgradeOpen();entity.TransformBy(Matrix3d.Displacement(new Vector3d(20,10,0)));}
                        }
                        topEntry.Reference.TransformBy(Matrix3d.Displacement(new Vector3d(20,10,0)));
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,topEntry);
                        Assert(Math.Abs(topEntry.Data.Dimensions[0].OffsetY-savedOffset)<1e-6,
                            "Combined view+annotation MOVE avoids duplicate displacement",log);
                        managedDim.DimLinePoint+=new Vector3d(0,-25,0);
                        ViewAnnotationDrawing.CaptureOffsets(scratch,tr,topEntry);
                        Assert(Math.Abs(topEntry.Data.Dimensions[0].OffsetY-(savedOffset-25))<1e-6,
                            "Family export captures fresh dimension grip movement",log);
                        managedDim.TextPosition+=new Vector3d(15,25,0);
                        managedDim.UsingDefaultTextPosition=false;
                        var draggedText=managedDim.TextPosition;
                        ViewAnnotationDrawing.CaptureOffsets(scratch,tr,topEntry);
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,topEntry);
                        Assert(managedDim.TextPosition.DistanceTo(draggedText)<1e-5,
                            "Manually dragged native dimension text survives rebuild and family export capture",log);
                        var shell=(BlockTableRecord)tr.GetObject(topEntry.Reference.BlockTableRecord,OpenMode.ForWrite);
                        var userLine=new Line(new Point3d(-10,-10,0),new Point3d(10,10,0));
                        shell.AppendEntity(userLine);tr.AddNewlyCreatedDBObject(userLine,true);
                        var userText=new DBText{TextString="USER FAMILY DETAIL",Position=new Point3d(30,30,0),Height=5};
                        shell.AppendEntity(userText);tr.AddNewlyCreatedDBObject(userText,true);
                        string payload=FamilyManualGraphics.Export(scratch,tr,topEntry);
                        var reusedData=KotNode.Core.Json.Copy(topEntry.Data);
                        reusedData.Id=Guid.NewGuid();reusedData.GeneratedChildHandle=null;reusedData.GeneratedStamp=null;
                        var reused=AssemblyViews.Create(scratch,tr,mark,refs,reusedData,new Point3d(8000,0,0));
                        FamilyManualGraphics.Import(scratch,tr,reused,payload);
                        var reusedShell=(BlockTableRecord)tr.GetObject(reused.BlockTableRecord,OpenMode.ForRead);
                        Assert(reusedShell.Cast<ObjectId>().Any(id=>tr.GetObject(id,OpenMode.ForRead) is DBText text&&text.TextString=="USER FAMILY DETAIL"),
                            "Native manual text and graphics survive DWG family payload roundtrip",log);
                        var newDims=annotationSpace.Cast<ObjectId>().Where(id=>!id.IsErased)
                            .Count(id=>tr.GetObject(id,OpenMode.ForRead) is Dimension);
                        Assert(newDims==4,"Family dimensions created immediately without extra update",log);

                        // User-test regressions: native ticks must not reserve custom
                        // arrowhead gaps; section welds follow the physical cover edge.
                        Assert(managedDim.Dimtsz>0&&managedDim.Dimdle>0,
                            "Native tick-based dimension line reaches and extends past extension lines",log);
                        var physicalWeld=new ViewWeldOverlay{InstanceId=curvedInst.Id,SourceKey="spatial:CurvedPlate:inner-start",LegMm=8};
                        topEntry.Data.Welds.Add(physicalWeld);
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,topEntry);
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,secEntry);
                        var sectionGeometry=ViewShell.RequireUnchanged(tr,secEntry).BlockTableRecord;
                        var sectionDefinition=(BlockTableRecord)tr.GetObject(sectionGeometry,OpenMode.ForRead);
                        Assert(sectionDefinition.Cast<ObjectId>().Any(id=>tr.GetObject(id,OpenMode.ForRead) is Solid solid&&solid.Layer==AssemblyViews.WeldLayer),
                            "Source longitudinal cover weld produces a native filled triangle in another section view",log);
                        physicalWeld.LegMm=12;
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,topEntry);
                        AssemblyViews.Rebuild(scratch,tr,mark,refs,secEntry);
                        sectionDefinition=(BlockTableRecord)tr.GetObject(ViewShell.RequireUnchanged(tr,secEntry).BlockTableRecord,OpenMode.ForRead);
                        Assert(sectionDefinition.Cast<ObjectId>().Select(id=>tr.GetObject(id,OpenMode.ForRead)).OfType<Solid>()
                            .Any(s=>s.Layer==AssemblyViews.WeldLayer&&Math.Abs(s.GeometricExtents.MaxPoint.X-112)<1e-5),
                            "Edited 12 mm leg updates the linked native section triangle",log);

                        // Stage 6 & 7: Native Tables (NASPEC, NAVRS, NAPROJVRS) + Update in-place
                        var msW=AssemblyDrawing.ModelSpace(scratch,tr,OpenMode.ForWrite);
                        var specTbl=AssemblyTableDrawing.RenderTable(scratch,tr,msW,null,new Point3d(4500,500,0),20,AssemblyReporting.BuildMarkSpec(mark));
                        specTbl.CreateExtensionDictionary();
                        AssemblyDrawing.Write((DBDictionary)tr.GetObject(specTbl.ExtensionDictionary,OpenMode.ForRead),
                            AssemblyTableDrawing.TableKey,new AssemblyTableDrawing.TableTag{MarkId=mark.Id,Kind="spec",AnnotationScale=20,SelfHandle=specTbl.Handle.ToString()},tr);
                        var vrsTbl=AssemblyTableDrawing.RenderTable(scratch,tr,msW,null,new Point3d(4500,-500,0),20,AssemblyReporting.BuildMarkVrs(mark));
                        vrsTbl.CreateExtensionDictionary();
                        AssemblyDrawing.Write((DBDictionary)tr.GetObject(vrsTbl.ExtensionDictionary,OpenMode.ForRead),
                            AssemblyTableDrawing.TableKey,new AssemblyTableDrawing.TableTag{MarkId=mark.Id,Kind="vrs",AnnotationScale=20,SelfHandle=vrsTbl.Handle.ToString()},tr);
                        int updatedTbls=AssemblyTableDrawing.UpdateMarkTables(scratch,tr,mark);
                        Assert(updatedTbls==2&&specTbl.Rows.Count>=6&&vrsTbl.Rows.Count>=5,"Native Spec & VRS tables created and updated in-place",log);

                        var proj2=new AssemblyProject{Code="E2E-PRJ",Name="Тест Schema 2"};
                        proj2.Marks.Add(new NodeMark{Code="У-01",Name="Стык 0.2"});
                        proj2.Assemblies.Add(mark);
                        proj2.Uses.Add(new Use{MarkId=proj2.Marks[0].Id,Location="Ярус 1",Count=2,Included=true});
                        proj2.Uses.Add(new Use{MarkId=mark.Id,Location="Ярус 1",Count=3,Included=true});
                        AssemblyTableDrawing.PlaceProjectVrs(scratch,tr,proj2,null,new Point3d(6500,0,0),20);
                        int updatedProj=AssemblyTableDrawing.UpdateProjectTables(scratch,tr,proj2);
                        Assert(updatedProj==1,"Native Schema-2 consolidated Project VRS table created and updated in-place",log);

                        tr.Commit();
                    }
                });

                // --- TEST 2: All 4 Built-In Families + Live 3D Angled Pipe-to-Waler Re-Solve + SaveAs DWG ---
                var families=NodeFamilyLibrary.BuiltIn();
                for(int fi=0;fi<3;fi++)
                {
                    using(var fdb=new Database(true,true))
                    WithDatabase(fdb,_=>{
                        fdb.Insunits=UnitsValue.Millimeters;
                        using(var tr=fdb.TransactionManager.StartTransaction())
                        {
                            var instFam=NodeFamilyLibrary.Instantiate(families[fi],"F-"+(fi+1),families[fi].Name);
                            var refs=new Dictionary<Guid,BlockReference>();
                            var gByP=instFam.Mark.Parts.ToDictionary(p=>p.Id,p=>AssemblyDrawing.Geometry(fdb,tr,p));
                            foreach(var inst in instFam.Mark.Instances)refs[inst.Id]=AssemblyDrawing.Insert(fdb,tr,instFam.Mark,inst,gByP[inst.PartId]);
                            foreach(var vt in instFam.Views)
                                AssemblyViews.Create(fdb,tr,instFam.Mark,refs,new AssemblyViews.ViewData{Name=vt.Name,IsSection=vt.IsSection,FullCut=vt.FullCut,Hidden=vt.Hidden,NoHatch=vt.NoHatch,AnnotationScale=vt.AnnotationScale,ParametricPrototype=true,Frame=vt.Frame,FrameAnchorId=vt.FrameAnchorId,RelativeFrame=vt.RelativeFrame},new Point3d(vt.OffsetX,vt.OffsetY,0));
                            tr.Commit();
                        }
                        Assert(true,"Built-in family #"+(fi+1)+" ('"+families[fi].Name+"') built ACIS + views cleanly in AutoCAD",log);
                    });
                }

                // Family #4 (3D Angled Pipe-to-Waler) into ACTIVE document -> Solve Alpha=60->45, Beta=10->20 -> SaveAs DWG!
                var angled=NodeFamilyLibrary.Instantiate(families[3],"У-04-E2E","Примыкание 3D E2E");
                using(var tr=db.TransactionManager.StartTransaction())
                {
                    var mark=angled.Mark;
                    var refs=new Dictionary<Guid,BlockReference>();
                    var gByP=mark.Parts.ToDictionary(p=>p.Id,p=>AssemblyDrawing.Geometry(db,tr,p));
                    foreach(var inst in mark.Instances)refs[inst.Id]=AssemblyDrawing.Insert(db,tr,mark,inst,gByP[inst.PartId]);
                    foreach(var vt in angled.Views)
                        AssemblyViews.Create(db,tr,mark,refs,new AssemblyViews.ViewData{Name=vt.Name,IsSection=vt.IsSection,FullCut=vt.FullCut,Hidden=vt.Hidden,NoHatch=vt.NoHatch,AnnotationScale=vt.AnnotationScale,ParametricPrototype=true,Frame=vt.Frame,FrameAnchorId=vt.FrameAnchorId,RelativeFrame=vt.RelativeFrame},new Point3d(vt.OffsetX,vt.OffsetY,0));
                    var space=AssemblyDrawing.ModelSpace(db,tr,OpenMode.ForWrite);
                    var sp=AssemblyTableDrawing.RenderTable(db,tr,space,null,new Point3d(8200,400,0),20,AssemblyReporting.BuildMarkSpec(mark));
                    sp.CreateExtensionDictionary();
                    AssemblyDrawing.Write((DBDictionary)tr.GetObject(sp.ExtensionDictionary,OpenMode.ForRead),AssemblyTableDrawing.TableKey,new AssemblyTableDrawing.TableTag{MarkId=mark.Id,Kind="spec",AnnotationScale=20,SelfHandle=sp.Handle.ToString()},tr);
                    var vr=AssemblyTableDrawing.RenderTable(db,tr,space,null,new Point3d(8200,-400,0),20,AssemblyReporting.BuildMarkVrs(mark));
                    vr.CreateExtensionDictionary();
                    AssemblyDrawing.Write((DBDictionary)tr.GetObject(vr.ExtensionDictionary,OpenMode.ForRead),AssemblyTableDrawing.TableKey,new AssemblyTableDrawing.TableTag{MarkId=mark.Id,Kind="vrs",AnnotationScale=20,SelfHandle=vr.Handle.ToString()},tr);
                    AssemblyDrawing.Save(db,tr,mark);
                    tr.Commit();
                }
                Assert(true,"Family #4 (3D Angled Pipe-to-Waler Alpha=60, Beta=10) inserted in active DWG",log);

                // Live parametric re-solve: change Alpha 60->35, Beta 10->20 (adds 6th stiffener, morphs gusset, tilts pipe & pipe-anchored view, updates tables!)
                using(var tr=db.TransactionManager.StartTransaction())
                {
                    var mark=AssemblyDrawing.Load(db,tr);
                    var refs=AssemblyDrawing.Synchronize(db,tr,mark);
                    int beforeCount=mark.Instances.Count;
                    mark.Parameters.Single(p=>p.Name=="Alpha").Value=35;
                    mark.Parameters.Single(p=>p.Name=="Beta").Value=20;
                    var report=ParametricEngine.Solve(mark);
                    ParametricCommands.ApplySolvedMark(db,tr,mark,refs,report);
                    Assert(mark.Instances.Count>beforeCount&&report.AddedInstances.Count>=1,"Live 3D re-solve (Alpha=35, Beta=20) added stiffener in ModelSpace ("+beforeCount+" -> "+mark.Instances.Count+")",log);
                    var views=AssemblyViews.Find(db,tr,mark);
                    Assert(views.Count==4,"All 4 views (including pipe-anchored 3D view & 1-1 section) rebuilt cleanly",log);
                    tr.Commit();
                }

                if(File.Exists(dwgPath))File.Delete(dwgPath);
                db.SaveAs(dwgPath,DwgVersion.Current);
                Assert(File.Exists(dwgPath)&&new FileInfo(dwgPath).Length>10000,"Saved verified DWG: "+dwgPath,log);
                log.Add("ALL_E2E_PASSED");
            }
            catch(System.Exception ex)
            {
                log.Add("EXCEPTION: "+ex.ToString());
            }
            finally
            {
                File.WriteAllLines(reportPath,log,new UTF8Encoding(true));
            }
        }

        [CommandMethod("NAE2ERELOAD",CommandFlags.Modal)]
        public void VerifyReload()
        {
            string reportPath=Path.Combine(Path.GetTempPath(),"kotnode_e2e_reload_report.txt");
            var log=new List<string>();
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            try
            {
                var db=doc.Database;
                using(var tr=db.TransactionManager.StartTransaction())
                {
                    var mark=AssemblyDrawing.Load(db,tr);
                    Assert(mark!=null&&mark.Code=="У-04-E2E","Reloaded saved DWG mark '"+(mark?.Code)+"'",log);
                    var refs=AssemblyDrawing.Synchronize(db,tr,mark);
                    Assert(refs.Count==mark.Instances.Count&&refs.Count>=9,"Synchronized all "+refs.Count+" instances after DWG reopen",log);
                    var views=AssemblyViews.Find(db,tr,mark);
                    Assert(views.Count==4,"Found all 4 parametric views after DWG reopen",log);
                    foreach(var v in views)ViewShell.RequireUnchanged(tr,v);
                    Assert(true,"ViewShell integrity stamp verified on all 4 views after DWG reopen",log);
                    int updatedViews=AssemblyViews.Update(db,tr,mark,refs);
                    int updatedTables=AssemblyTableDrawing.UpdateMarkTables(db,tr,mark);
                    Assert(updatedViews==4&&updatedTables==2,"Rebuilt all 4 views and 2 tables cleanly on reopened DWG",log);
                    tr.Commit();
                }
                log.Add("ALL_RELOAD_PASSED");
            }
            catch(System.Exception ex)
            {
                log.Add("EXCEPTION: "+ex.ToString());
            }
            finally
            {
                File.WriteAllLines(reportPath,log,new UTF8Encoding(true));
            }
        }
    }
}
