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
        public string Name { get; set; }="Вид 1";
        public bool IsSection { get; set; }
        public bool FullCut { get; set; }
        public bool Hidden { get; set; }=true;
        public bool NoHatch { get; set; }
        public double AnnotationScale { get; set; }=20;
        public RigidPlacement Frame { get; set; }=new RigidPlacement();
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
            ParametricEngine.Solve(copy.Mark);
            copy.Validate();
            return copy;
        }

        public static List<NodeFamilyTemplate> BuiltIn()
        {
            return new List<NodeFamilyTemplate>
            {
                CreatePipeSpliceFamily(),
                CreatePipeBasePlateFamily(),
                CreateIBeamSpliceFamily()
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
            var rib=new PartDefinition{Name="Ребро жёсткости",Position="2",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=140,Width=12,Thickness=250}};
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
                new PartBinding{PartId=rib.Id,Property="Plate.Width",Formula="t_rib"},
                new PartBinding{PartId=rib.Id,Property="Plate.Thickness",Formula="H_rib"}
            };
            mark.PlacementRules=new List<PlacementRule>
            {
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=pipeInst.Id,ExprZ="t_base"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=baseInst.Id,ExprX="-B_base / 2",ExprY="-B_base / 2",ExprZ="0"},
                new PlacementRule{Kind=PlacementRuleKind.OnPipeSurface,TargetInstanceId=ribInst.Id,ReferenceInstanceId=pipeInst.Id,ExprX="0",ExprZ="0",ExprAngleDeg="0"},
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
            var plateDef=new PartDefinition{Name="Накладка полки",Position="1",Material="С255",IncludeInSpec=true,Kind=PartKind.RectangularPlate,Plate=new RectangularPlateShape{Length=150,Width=12,Thickness=600}};
            var topPlate=mark.AddPart(plateDef,new RigidPlacement{Origin=new Vector3(-75,150,-300)});
            var botPlate=mark.CopyInstance(topPlate.Id);
            botPlate.Placement=new RigidPlacement{Origin=new Vector3(-75,-162,-300)};

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
                new PartBinding{PartId=plateDef.Id,Property="Plate.Width",Formula="t_plate"},
                new PartBinding{PartId=plateDef.Id,Property="Plate.Thickness",Formula="L_plate"}
            };
            mark.PlacementRules=new List<PlacementRule>
            {
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=beamInst.Id,ExprZ="-L_beam / 2"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=topPlate.Id,ExprX="-W_plate / 2",ExprY="150",ExprZ="-L_plate / 2"},
                new PlacementRule{Kind=PlacementRuleKind.OffsetFromPart,TargetInstanceId=botPlate.Id,ExprX="-W_plate / 2",ExprY="-150 - t_plate",ExprZ="-L_plate / 2"}
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
    }
}
