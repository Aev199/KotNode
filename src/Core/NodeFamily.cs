using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KotNode.Core
{
    // Stage 9: Reusable parametric node family template (.knfamily.json).
    public sealed class FamilyViewTemplate
    {
        public Guid Id { get; set; }=Guid.NewGuid();
        // Native user graphics in local view coordinates, including dependent styles/blocks.
        public string ManualGraphicsDwg { get; set; }
        public string Name { get; set; }="Вид 1";
        public bool IsSection { get; set; }
        public bool FullCut { get; set; }
        public bool Hidden { get; set; }=true;
        public bool NoHatch { get; set; }
        public double AnnotationScale { get; set; }=20;
        public RigidPlacement Frame { get; set; }=new RigidPlacement();
        public Guid? FrameAnchorId { get; set; }
        public RigidPlacement RelativeFrame { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public List<ViewLineOverride> LineOverrides { get; set; }=new List<ViewLineOverride>();
        public List<ViewWeldOverlay> Welds { get; set; }=new List<ViewWeldOverlay>();
        public List<ViewDimension> Dimensions { get; set; }=new List<ViewDimension>();
        public List<ViewLeader> Leaders { get; set; }=new List<ViewLeader>();
        public List<ViewAxisMark> Axes { get; set; }=new List<ViewAxisMark>();
        public List<ViewSectionCallout> SectionCallouts { get; set; }=new List<ViewSectionCallout>();
    }
    public sealed class NodeFamilyTemplate
    {
        public int Schema { get; set; }=1;
        public Guid Id { get; set; }=Guid.NewGuid();
        public string Name { get; set; }="";
        public string Category { get; set; }="Узлы ограждения котлована";
        public string Description { get; set; }="";
        public AssemblyMark Mark { get; set; }=new AssemblyMark();
        public List<FamilyViewTemplate> Views { get; set; }=new List<FamilyViewTemplate>();
        public bool IncludeSpecTable { get; set; }=true;
        public bool IncludeVrsTable { get; set; }=true;

        public void Validate()
        {
            Guard.Require(Schema==1&&Id!=Guid.Empty&&Guard.Text(Name)&&Mark!=null,"Неверные данные шаблона семейства.");
            Mark.Validate();
            if(Views!=null)foreach(var v in Views)
            {
                Guard.Require(v!=null&&Guard.Text(v.Name)&&v.Frame!=null,"Неверные данные вида в шаблоне семейства.");
                v.Frame.Validate();
            }
        }
    }

    public static class NodeFamilyLibrary
    {
        public static void Save(string path,NodeFamilyTemplate template)
        {
            Guard.Require(template!=null,"Шаблон не задан.");
            template.Validate();
            File.WriteAllText(path,Json.Write(template),new UTF8Encoding(false));
        }
        public static NodeFamilyTemplate Load(string path)
        {
            var t=Json.Read<NodeFamilyTemplate>(File.ReadAllText(path,Encoding.UTF8));
            Guard.Require(t!=null,"Пустой файл семейства.");
            t.Validate();
            return t;
        }
        public static NodeFamilyTemplate Instantiate(NodeFamilyTemplate template,string code,string name,IDictionary<string,double> overrides=null)
        {
            Guard.Require(template!=null,"Шаблон не задан.");
            var copy=Json.Copy(template);
            copy.Mark.Id=Guid.NewGuid();
            if(!string.IsNullOrWhiteSpace(code))copy.Mark.Code=code.Trim();
            if(!string.IsNullOrWhiteSpace(name))copy.Mark.Name=name.Trim();
            if(overrides!=null&&copy.Mark.Parameters!=null)
            {
                foreach(var p in copy.Mark.Parameters)
                {
                    if(string.IsNullOrWhiteSpace(p.Formula)&&overrides.TryGetValue(p.Name,out var val))
                        p.Value=val;
                }
            }
            if(copy.Views!=null)
            {
                var viewIds=new Dictionary<Guid,Guid>();
                foreach(var v in copy.Views)
                {
                    if(v.Id!=Guid.Empty)Guard.Require(!viewIds.ContainsKey(v.Id),"Повторный идентификатор вида семейства.");
                    var old=v.Id;v.Id=Guid.NewGuid();
                    if(old!=Guid.Empty)viewIds.Add(old,v.Id);
                }
                foreach(var v in copy.Views)
                    foreach(var callout in v.SectionCallouts??new List<ViewSectionCallout>())
                    {
                        if(callout.TargetViewId==Guid.Empty)continue;
                        if(viewIds.TryGetValue(callout.TargetViewId,out var newId))callout.TargetViewId=newId;
                        else
                        {
                            // Old templates had no view IDs: recover only an unambiguous
                            // explicit name, never guess a destination from list order.
                            var targets=copy.Views.Where(x=>x.Name==callout.Label).ToList();
                            Guard.Require(targets.Count==1,"Старое семейство: невозможно восстановить связь разреза «"+callout.Label+"». Сохраните семейство заново из исходного DWG.");
                            callout.TargetViewId=targets[0].Id;
                        }
                    }
            }
            ParametricEngine.Solve(copy.Mark);
            if(copy.Views!=null)
            {
                foreach(var vt in copy.Views)
                {
                    if(vt.FrameAnchorId.HasValue&&vt.RelativeFrame!=null)
                    {
                        var anchor=copy.Mark.Instances.FirstOrDefault(i=>i.Id==vt.FrameAnchorId.Value);
                        if(anchor!=null)vt.Frame=vt.RelativeFrame.InParent(anchor.Placement);
                    }
                }
            }
            copy.Validate();
            return copy;
        }

        public static List<NodeFamilyTemplate> BuiltIn()
        {
            return new List<NodeFamilyTemplate>
            {
                CreatePipeSpliceFamily(),
                CreatePipeBasePlateFamily(),
                CreateIBeamSpliceFamily(),
                CreateAngledPipeToWalerFamily()
            };
        }

        static NodeFamilyTemplate CreatePipeSpliceFamily()
        {
            var mark=new AssemblyMark{Code="У-01",Name="Стык труб на накладках"};
            var pipe1=new PartDefinition{Name="Труба распорки",Position="",Material="С245",IncludeInSpec=false,Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=820,Thickness=12,Length=1000}};
            var pipeInst1=mark.AddPart(pipe1,new RigidPlacement{Origin=new Vector3(0,0,-1005)});
            var pipeInst2=mark.CopyInstance(pipeInst1.Id);
            pipeInst2.Placement=new RigidPlacement{Origin=new Vector3(0,0,5)};

            var plate=new PartDefinition{Name="Накладка из трубы",Position="1",Material="С255",IncludeInSpec=true,Kind=PartKind.CurvedPlate,CurvedPlate=new CurvedPlateShape{InnerRadius=410,Thickness=12,Length=950,AngleRadians=60*Math.PI/180}};
            var plateInst=mark.AddPart(plate,new RigidPlacement{Origin=new Vector3(0,0,-475)});

            mark.Parameters=new List<MarkParameter>
            {
                new MarkParameter{Name="D",Label="Наружный диаметр трубы",Value=820,Unit="мм"},
                new MarkParameter{Name="t",Label="Толщина стенки трубы",Value=12,Unit="мм"},
                new MarkParameter{Name="L_pipe",Label="Длина участка трубы",Value=1000,Unit="мм"},
                new MarkParameter{Name="gap",Label="Зазор торцов труб",Value=10,Unit="мм"},
                new MarkParameter{Name="L_plate",Label="Длина накладки",Value=950,Unit="мм"},
                new MarkParameter{Name="s_plate",Label="Толщина накладки",Value=12,Unit="мм"},
                new MarkParameter{Name="N_plates",Label="Число накладок",Value=4,Unit="шт."},
                new MarkParameter{Name="Angle",Label="Угол одной накладки",Formula="min(84, max(20, 360 / N_plates - 18))",Unit="град"}
            };
            mark.Bindings=new List<PartBinding>
            {
                new PartBinding{PartId=pipe1.Id,Property="Pipe.OuterDiameter",Formula="D"},
                new PartBinding{PartId=pipe1.Id,Property="Pipe.Thickness",Formula="t"},
                new PartBinding{PartId=pipe1.Id,Property="Pipe.Length",Formula="L_pipe"},
                new PartBinding{PartId=plate.Id,Property="CurvedPlate.InnerRadius",Formula="D / 2"},
                new PartBinding{PartId=plate.Id,Property="CurvedPlate.Thickness",Formula="s_plate"},
                new PartBinding{PartId=plate.Id,Property="CurvedPlate.Length",Formula="L_plate"},
                new PartBinding{PartId=plate.Id,Property="CurvedPlate.AngleDeg",Formula="Angle"}
            };
            mark.PlacementRules=new List<PlacementRule>
            {
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=pipeInst1.Id,ExprZ="-L_pipe - gap / 2"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=pipeInst2.Id,ExprZ="gap / 2"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=plateInst.Id,ExprZ="-L_plate / 2",ExprAngleDeg="-Angle / 2"},
                new PlacementRule{Kind=PlacementRuleKind.CircularArray,TargetInstanceId=plateInst.Id,ExprCount="N_plates",ExprAngleDeg="0"}
            };
            ParametricEngine.Solve(mark);

            return new NodeFamilyTemplate
            {
                Name="Стык труб на накладках",
                Category="Стыки распорок",
                Description="Параметрический стык двух труб с круговым массивом криволинейных накладок, продольным видом и поперечным разрезом 1-1.",
                Mark=mark,
                Views=new List<FamilyViewTemplate>
                {
                    new FamilyViewTemplate{Name="Вид сбоку",Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(0,0,1),AxisY=new Vector3(0,1,0)},Hidden=true,AnnotationScale=20,OffsetX=2000,OffsetY=0},
                    new FamilyViewTemplate{Name="1-1",IsSection=true,FullCut=true,Frame=new RigidPlacement{Origin=new Vector3(0,0,100),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,1,0)},Hidden=false,AnnotationScale=20,OffsetX=4200,OffsetY=0}
                }
            };
        }

        static NodeFamilyTemplate CreatePipeBasePlateFamily()
        {
            var mark=new AssemblyMark{Code="У-02",Name="Опорный узел трубы с фланцем и рёбрами"};
            var pipe=new PartDefinition{Name="Труба распорки",Position="",Material="С245",IncludeInSpec=false,Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=530,Thickness=10,Length=800}};
            var pipeInst=mark.AddPart(pipe,new RigidPlacement{Origin=new Vector3(0,0,20)});
            var basePlate=new PartDefinition{Name="Опорная плита",Position="1",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=850,Width=850,Thickness=20}};
            var baseInst=mark.AddPart(basePlate,new RigidPlacement{Origin=new Vector3(-425,-425,0)});
            var rib=new PartDefinition{Name="Ребро жёсткости",Position="2",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=140,Width=250,Thickness=12}};
            var ribInst=mark.AddPart(rib,new RigidPlacement{Origin=new Vector3(265,-6,20)});

            mark.Parameters=new List<MarkParameter>
            {
                new MarkParameter{Name="D",Label="Диаметр трубы",Value=530,Unit="мм"},
                new MarkParameter{Name="t",Label="Толщина трубы",Value=10,Unit="мм"},
                new MarkParameter{Name="L_pipe",Label="Длина трубы",Value=800,Unit="мм"},
                new MarkParameter{Name="t_base",Label="Толщина опорной плиты",Value=20,Unit="мм"},
                new MarkParameter{Name="L_rib",Label="Вылет ребра",Value=140,Unit="мм"},
                new MarkParameter{Name="H_rib",Label="Высота ребра",Value=250,Unit="мм"},
                new MarkParameter{Name="t_rib",Label="Толщина ребра",Value=12,Unit="мм"},
                new MarkParameter{Name="N_ribs",Label="Число рёбер",Value=4,Unit="шт."},
                new MarkParameter{Name="B_base",Label="Размер опорной плиты",Formula="D + 2 * L_rib + 40",Unit="мм"}
            };
            mark.Bindings=new List<PartBinding>
            {
                new PartBinding{PartId=pipe.Id,Property="Pipe.OuterDiameter",Formula="D"},
                new PartBinding{PartId=pipe.Id,Property="Pipe.Thickness",Formula="t"},
                new PartBinding{PartId=pipe.Id,Property="Pipe.Length",Formula="L_pipe"},
                new PartBinding{PartId=basePlate.Id,Property="Plate.Length",Formula="B_base"},
                new PartBinding{PartId=basePlate.Id,Property="Plate.Width",Formula="B_base"},
                new PartBinding{PartId=basePlate.Id,Property="Plate.Thickness",Formula="t_base"},
                new PartBinding{PartId=rib.Id,Property="Plate.Length",Formula="L_rib"},
                new PartBinding{PartId=rib.Id,Property="Plate.Width",Formula="H_rib"},
                new PartBinding{PartId=rib.Id,Property="Plate.Thickness",Formula="t_rib"}
            };
            mark.PlacementRules=new List<PlacementRule>
            {
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=pipeInst.Id,ExprZ="t_base"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=baseInst.Id,ExprX="-B_base / 2",ExprY="-B_base / 2",ExprZ="0"},
                new PlacementRule{Kind=PlacementRuleKind.OnPipeSurface,TargetInstanceId=ribInst.Id,ReferenceInstanceId=pipeInst.Id,ExprX="0",ExprY="t_rib / 2",ExprZ="0",ExprRotXDeg="90",ExprAngleDeg="0"},
                new PlacementRule{Kind=PlacementRuleKind.CircularArray,TargetInstanceId=ribInst.Id,ReferenceInstanceId=pipeInst.Id,ExprCount="N_ribs",ExprAngleDeg="0"}
            };
            ParametricEngine.Solve(mark);

            return new NodeFamilyTemplate
            {
                Name="Опорный узел трубы с фланцем и рёбрами",
                Category="Опорные узлы",
                Description="Труба с опорным фланцем и круговым массивом рёбер жёсткости (рёбра автоматически следуют за диаметром трубы).",
                Mark=mark,
                Views=new List<FamilyViewTemplate>
                {
                    new FamilyViewTemplate{Name="Вид сверху",Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,1,0)},Hidden=true,AnnotationScale=20,OffsetX=1600,OffsetY=0},
                    new FamilyViewTemplate{Name="Вид спереди",Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)},Hidden=true,AnnotationScale=20,OffsetX=3200,OffsetY=0}
                }
            };
        }

        static NodeFamilyTemplate CreateIBeamSpliceFamily()
        {
            var mark=new AssemblyMark{Code="У-03",Name="Стык двутавра 30Б2 на накладках"};
            var beamDef=new PartDefinition{Name="Двутавр 30Б2",Position="",Material="С255",IncludeInSpec=false,Kind=PartKind.IBeam,IBeam=BeamCatalog.Create("30Б2",1200)};
            var beamInst=mark.AddPart(beamDef,new RigidPlacement{Origin=new Vector3(0,0,-600)});
            var plateDef=new PartDefinition{Name="Накладка полки",Position="1",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=150,Width=600,Thickness=12}};
            var topPlate=mark.AddPart(plateDef,new RigidPlacement{Origin=new Vector3(-75,162,-300),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)});
            var botPlate=mark.CopyInstance(topPlate.Id);
            botPlate.Placement=new RigidPlacement{Origin=new Vector3(-75,-150,-300),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)};

            mark.Parameters=new List<MarkParameter>
            {
                new MarkParameter{Name="L_beam",Label="Длина двутавра",Value=1200,Unit="мм"},
                new MarkParameter{Name="L_plate",Label="Длина накладки",Value=600,Unit="мм"},
                new MarkParameter{Name="W_plate",Label="Ширина накладки",Value=150,Unit="мм"},
                new MarkParameter{Name="t_plate",Label="Толщина накладки",Value=12,Unit="мм"}
            };
            mark.Bindings=new List<PartBinding>
            {
                new PartBinding{PartId=beamDef.Id,Property="IBeam.Length",Formula="L_beam"},
                new PartBinding{PartId=plateDef.Id,Property="Plate.Length",Formula="W_plate"},
                new PartBinding{PartId=plateDef.Id,Property="Plate.Width",Formula="L_plate"},
                new PartBinding{PartId=plateDef.Id,Property="Plate.Thickness",Formula="t_plate"}
            };
            mark.PlacementRules=new List<PlacementRule>
            {
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=beamInst.Id,ExprZ="-L_beam / 2"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=topPlate.Id,ExprX="-W_plate / 2",ExprY="150 + t_plate",ExprZ="-L_plate / 2",ExprRotXDeg="90"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=botPlate.Id,ExprX="-W_plate / 2",ExprY="-150",ExprZ="-L_plate / 2",ExprRotXDeg="90"}
            };
            ParametricEngine.Solve(mark);

            return new NodeFamilyTemplate
            {
                Name="Стык двутавра 30Б2 на накладках",
                Category="Стыки поясов",
                Description="Двутавр ГОСТ 35087-2024 с галтелями и парными накладками по полкам, с поперечным разрезом 1-1.",
                Mark=mark,
                Views=new List<FamilyViewTemplate>
                {
                    new FamilyViewTemplate{Name="Вид сбоку",Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(0,0,1),AxisY=new Vector3(0,1,0)},Hidden=true,AnnotationScale=20,OffsetX=1800,OffsetY=0},
                    new FamilyViewTemplate{Name="1-1",IsSection=true,FullCut=true,Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,1,0)},Hidden=false,AnnotationScale=20,OffsetX=3600,OffsetY=0}
                }
            };
        }

        static NodeFamilyTemplate CreateAngledPipeToWalerFamily()
        {
            var mark=new AssemblyMark{Code="У-04",Name="Примыкание трубы к распредпоясу под углом"};
            // Horizontal waler I-beam (40Ш2: h=390, b=300, s=10, t=16) along WCS X; flanges at Y=±195, Z=±150.
            var walerDef=new PartDefinition{Name="Распредпояс 40Ш2",Position="",Material="С255",IncludeInSpec=false,Kind=PartKind.IBeam,IBeam=BeamCatalog.Create("40Ш2",2400)};
            var walerInst=mark.AddPart(walerDef,new RigidPlacement{Origin=new Vector3(-1200,0,0),AxisX=new Vector3(0,0,-1),AxisY=new Vector3(0,1,0)});

            // Approaching strut pipe angled in BOTH plan (Alpha) and elevation (Beta).
            var pipeDef=new PartDefinition{Name="Труба распорки",Position="",Material="С245",IncludeInSpec=false,Kind=PartKind.Pipe,Pipe=new PipeShape{OuterDiameter=530,Thickness=10,Length=1200}};
            var pipeInst=mark.AddPart(pipeDef,new RigidPlacement{Origin=new Vector3(0,400,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)});

            // Distribution plate flush on front waler flange (Y = 195..195+t_plate).
            var distDef=new PartDefinition{Name="Опорная пластина пояса",Position="1",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=800,Width=300,Thickness=20}};
            var distInst=mark.AddPart(distDef,new RigidPlacement{Origin=new Vector3(-400,215,-150),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)});

            // Parametric beveled gusset plate (PolygonPlate) whose contour morphs with Alpha & D.
            var gussetDef=new PartDefinition
            {
                Name="Фасонка упорная угловая",Position="2",Material="С255",IncludeInSpec=true,Kind=PartKind.PolygonPlate,
                PolygonPlate=new PolygonPlateShape
                {
                    Thickness=12,
                    Outer=new List<Point2>{new Point2(-300,0),new Point2(300,0),new Point2(400,200),new Point2(-200,200)}
                }
            };
            var gussetInst=mark.AddPart(gussetDef,new RigidPlacement{Origin=new Vector3(0,215,-6)});

            // Automatic array of web stiffener ribs inside waler I-beam (real sheet Thickness=12 so VRS groups as Лист t=12).
            var stiffDef=new PartDefinition{Name="Ребро жёсткости пояса",Position="3",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=140,Width=358,Thickness=12}};
            var stiffInst=mark.AddPart(stiffDef,new RigidPlacement{Origin=new Vector3(-348,-179,5),AxisX=new Vector3(0,0,1),AxisY=new Vector3(0,1,0)});

            mark.Parameters=new List<MarkParameter>
            {
                new MarkParameter{Name="Alpha",Label="Угол подхода в плане (XY)",Value=60,Unit="град"},
                new MarkParameter{Name="Beta",Label="Угол наклона в верт. плоскости",Value=10,Unit="град"},
                new MarkParameter{Name="D",Label="Наружный диаметр трубы",Value=530,Unit="мм"},
                new MarkParameter{Name="t",Label="Толщина стенки трубы",Value=10,Unit="мм"},
                new MarkParameter{Name="L_pipe",Label="Длина участка трубы",Value=1200,Unit="мм"},
                new MarkParameter{Name="L_beam",Label="Длина распредпояса",Value=2400,Unit="мм"},
                new MarkParameter{Name="t_plate",Label="Толщина опорной пластины",Value=20,Unit="мм"},
                new MarkParameter{Name="t_gusset",Label="Толщина фасонок и рёбер",Value=12,Unit="мм"},
                new MarkParameter{Name="L_contact",Label="Длина опорной пластины (от Alpha)",Formula="D / sin(Alpha) + 160",Unit="мм"},
                new MarkParameter{Name="W_gusset",Label="Вылет угловой фасонки",Formula="max(180, D / 2 * abs(cos(Alpha)) + 120)",Unit="мм"},
                new MarkParameter{Name="N_stiff",Label="Число рёбер в поясе (от Alpha)",Formula="max(2, ceil(L_contact / 250) + 1)",Unit="шт."},
                new MarkParameter{Name="Step_stiff",Label="Шаг рёбер жёсткости",Formula="(L_contact - 80) / max(1, N_stiff - 1)",Unit="мм"}
            };
            mark.Bindings=new List<PartBinding>
            {
                new PartBinding{PartId=walerDef.Id,Property="IBeam.Length",Formula="L_beam"},
                new PartBinding{PartId=pipeDef.Id,Property="Pipe.OuterDiameter",Formula="D"},
                new PartBinding{PartId=pipeDef.Id,Property="Pipe.Thickness",Formula="t"},
                new PartBinding{PartId=pipeDef.Id,Property="Pipe.Length",Formula="L_pipe"},
                new PartBinding{PartId=distDef.Id,Property="Plate.Length",Formula="L_contact"},
                new PartBinding{PartId=distDef.Id,Property="Plate.Thickness",Formula="t_plate"},
                new PartBinding{PartId=gussetDef.Id,Property="PolygonPlate.Thickness",Formula="t_gusset"},
                new PartBinding{PartId=stiffDef.Id,Property="Plate.Thickness",Formula="t_gusset"}
            };
            mark.PolygonBindings=new List<PolygonBinding>
            {
                new PolygonBinding
                {
                    PartId=gussetDef.Id,
                    Outer=new List<VertexExpr>
                    {
                        new VertexExpr{ExprX="-L_contact / 2 + 20",ExprY="0"},
                        new VertexExpr{ExprX="L_contact / 2 - 20",ExprY="0"},
                        new VertexExpr{ExprX="L_contact / 2 - 20 + W_gusset * cos(Alpha)",ExprY="W_gusset * sin(Alpha)"},
                        new VertexExpr{ExprX="-L_contact / 2 + 20 + W_gusset * cos(Alpha)",ExprY="W_gusset * sin(Alpha)"}
                    }
                }
            };
            mark.PlacementRules=new List<PlacementRule>
            {
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=walerInst.Id,ExprX="-L_beam / 2",ExprRotYDeg="90"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=distInst.Id,ExprX="-L_contact / 2",ExprY="195 + t_plate",ExprZ="-150",ExprRotXDeg="90"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=gussetInst.Id,ExprX="0",ExprY="195 + t_plate",ExprZ="-t_gusset / 2"},
                new PlacementRule
                {
                    Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=pipeInst.Id,
                    ExprX="W_gusset * 0.65 * cos(Alpha)",
                    ExprY="195 + t_plate + W_gusset * 0.65 * sin(Alpha)",
                    ExprZ="0",
                    ExprRotXDeg="-(90 - Beta)",
                    ExprRotZDeg="-(90 - Alpha)"
                },
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=stiffInst.Id,ExprX="-L_contact / 2 + 40 + t_gusset / 2",ExprY="-179",ExprZ="5",ExprRotYDeg="-90"},
                new PlacementRule{Kind=PlacementRuleKind.LinearArray,TargetInstanceId=stiffInst.Id,ExprX="Step_stiff",ExprCount="N_stiff"}
            };
            ParametricEngine.Solve(mark);

            var pipeLocalFrame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,1,0)};
            return new NodeFamilyTemplate
            {
                Name="Примыкание трубы к распредпоясу под углом",
                Category="Узловые примыкания (3D)",
                Description="Узел примыкания трубы к распредпоясу 40Ш2 с заданием углов в 2 плоскостях (Alpha, Beta), адаптивной угловой фасонкой и авто-массивом рёбер жёсткости.",
                Mark=mark,
                Views=new List<FamilyViewTemplate>
                {
                    new FamilyViewTemplate{Name="План узла",Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,1,0)},Hidden=true,AnnotationScale=20,OffsetX=3000,OffsetY=0},
                    new FamilyViewTemplate{Name="Вид спереди",Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(1,0,0),AxisY=new Vector3(0,0,1)},Hidden=true,AnnotationScale=20,OffsetX=6200,OffsetY=0},
                    new FamilyViewTemplate{Name="1-1",IsSection=true,FullCut=true,Frame=new RigidPlacement{Origin=new Vector3(0,0,0),AxisX=new Vector3(0,1,0),AxisY=new Vector3(0,0,1)},Hidden=false,AnnotationScale=20,OffsetX=3000,OffsetY=-2200},
                    new FamilyViewTemplate{Name="Вид вдоль трубы",Frame=pipeLocalFrame.InParent(pipeInst.Placement),FrameAnchorId=pipeInst.Id,RelativeFrame=pipeLocalFrame,Hidden=true,AnnotationScale=20,OffsetX=6200,OffsetY=-2200}
                }
            };
        }
    }
}
