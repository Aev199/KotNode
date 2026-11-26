using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KotNode.Core
{
    // Versioned, CAD-independent draft model. Not yet embedded in schema-1 Project.
    public sealed class AssemblyMark
    {
        public int Schema { get; set; } = 1;
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "У-01";
        public string Name { get; set; } = "Произвольный узел";
        public List<PartDefinition> Parts { get; set; } = new List<PartDefinition>();
        public List<PartInstance> Instances { get; set; } = new List<PartInstance>();
        public List<MarkParameter> Parameters { get; set; } = new List<MarkParameter>();
        public List<PartBinding> Bindings { get; set; } = new List<PartBinding>();
        public List<PlacementRule> PlacementRules { get; set; } = new List<PlacementRule>();

        public void Validate()
        {
            Guard.Require(Schema == 1 && Id != Guid.Empty && Guard.Text(Code) && Guard.Text(Name), "Неверная марка или версия модели.");
            Guard.Require(Parts != null && Instances != null, "Отсутствует состав марки.");
            var ids = new HashSet<Guid>();
            var positions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Parts)
            {
                Guard.Require(p != null, "Пустая деталь.");
                p.Validate();
                Guard.Require(ids.Add(p.Id), "Дубли идентификаторов деталей.");
                if (p.IncludeInSpec) Guard.Require(positions.Add(p.Position.Trim()), "Дубли позиций спецификации.");
            }
            var instanceIds = new HashSet<Guid>();
            foreach (var i in Instances)
            {
                Guard.Require(i != null && i.Id != Guid.Empty && instanceIds.Add(i.Id), "Неверный или повторный экземпляр.");
                Guard.Require(ids.Contains(i.PartId), "Экземпляр ссылается на отсутствующую деталь.");
                Guard.Require(i.Placement != null, "Отсутствует положение детали.");
                i.Placement.Validate();
            }
        }

        public List<AssemblySpecRow> Specification()
        {
            Validate();
            var counts = Instances.GroupBy(i => i.PartId).ToDictionary(g => g.Key, g => g.Count());
            var rows = new List<AssemblySpecRow>();
            foreach (var p in Parts.Where(p => p.IncludeInSpec && counts.ContainsKey(p.Id)))
            {
                double mass = p.UnitMassKg();
                double total = mass * counts[p.Id];
                Guard.Require(Guard.Finite(total), "Переполнение массы.");
                rows.Add(new AssemblySpecRow { PartId = p.Id, Position = p.Position, Name = p.Name, Kind = p.Kind,
                    Material = p.Material, Count = counts[p.Id], UnitMassKg = mass, TotalMassKg = total });
            }
            return rows;
        }

        // Commands validate before mutation; failed edits leave the mark unchanged.
        public PartInstance AddPart(PartDefinition definition, RigidPlacement placement)
        {
            Validate();
            Guard.Require(definition != null && placement != null, "Не задана деталь или положение.");
            var part = Json.Copy(definition);
            var instance = new PartInstance { PartId = part.Id, Placement = Json.Copy(placement) };
            var draft = Json.Copy(this);
            draft.Parts.Add(part);
            draft.Instances.Add(instance);
            draft.Validate();
            Parts.Add(part);
            Instances.Add(instance);
            return instance;
        }

        public void SetPlacement(Guid instanceId, RigidPlacement placement)
        {
            Validate();
            var instance = FindInstance(instanceId);
            Guard.Require(placement != null, "Не задано положение.");
            placement.Validate();
            instance.Placement = Json.Copy(placement);
        }

        public void ReplaceDefinition(Guid partId, PartDefinition replacement)
        {
            Validate();
            Guard.Require(replacement != null && replacement.Id == partId, "Нельзя менять идентификатор детали.");
            int index = Parts.FindIndex(p => p.Id == partId);
            Guard.Require(index >= 0, "Деталь не найдена.");
            var copy = Json.Copy(replacement);
            var draft = Json.Copy(this);
            draft.Parts[index] = copy;
            draft.Validate();
            Parts[index] = copy;
        }

        // Detach a single instance; the caller supplies a new position for a specifiable part.
        public PartDefinition DetachInstance(Guid instanceId, string newPosition)
        {
            Validate();
            var instance = FindInstance(instanceId);
            var copy = Json.Copy(Parts.Single(p => p.Id == instance.PartId));
            copy.Id = Guid.NewGuid();
            copy.Position = newPosition ?? "";
            var draft = Json.Copy(this);
            draft.Parts.Add(copy);
            draft.Instances.Single(i => i.Id == instanceId).PartId = copy.Id;
            draft.Validate();
            Parts.Add(copy);
            instance.PartId = copy.Id;
            return copy;
        }

        public void RemoveInstance(Guid instanceId)
        {
            Validate();
            Instances.Remove(FindInstance(instanceId));
        }

        // Only definitions whose last instance disappeared are removed.
        public int RemoveMissingInstances(IEnumerable<Guid> liveIds)
        {
            Validate();
            if(liveIds==null)throw new ArgumentNullException(nameof(liveIds));
            var live=new HashSet<Guid>(liveIds);
            Guard.Require(live.All(id=>Instances.Any(i=>i.Id==id)), "Неизвестный экземпляр на чертеже.");
            var removed=Instances.Where(i=>!live.Contains(i.Id)).ToList();
            var affected=new HashSet<Guid>(removed.Select(i=>i.PartId));
            Instances.RemoveAll(i=>!live.Contains(i.Id));
            Parts.RemoveAll(p=>affected.Contains(p.Id)&&!Instances.Any(i=>i.PartId==p.Id));
            return removed.Count;
        }

        public void RemoveUnusedDefinition(Guid partId)
        {
            Validate();
            var part = Parts.SingleOrDefault(p => p.Id == partId);
            Guard.Require(part != null, "Деталь не найдена.");
            Guard.Require(!Instances.Any(i => i.PartId == partId), "Деталь используется в узле.");
            Parts.Remove(part);
        }

        private PartInstance FindInstance(Guid id)
        {
            var instance = Instances.SingleOrDefault(i => i.Id == id);
            Guard.Require(instance != null, "Экземпляр не найден.");
            return instance;
        }

        // Batch copies share the same definition but own their placement and UUID.
        public List<PartInstance> CopyInstances(Guid id, IEnumerable<Vector3> origins)
        {
            Validate();
            var source=FindInstance(id);
            Guard.Require(origins!=null,"Не заданы точки копирования.");
            var points=origins.Take(501).ToList();
            Guard.Require(points.Count>=1&&points.Count<=500,"Допустимо от 1 до 500 дополнительных экземпляров.");
            var result=new List<PartInstance>();
            foreach(var point in points)
            {
                Guard.Require(point!=null&&point.IsFinite(),"Неверная точка копирования.");
                var copy=Json.Copy(source);
                copy.Id=Guid.NewGuid();
                copy.Placement.Origin=new Vector3(point.X,point.Y,point.Z);
                copy.Placement.Validate();
                result.Add(copy);
            }
            Instances.AddRange(result);
            return result;
        }

        public List<PartInstance> LinearCopies(Guid id,Vector3 step,int count)
        {
            Validate();
            var source=FindInstance(id);
            Guard.Require(count>=1&&count<=500&&step!=null&&step.IsFinite()&&Guard.Finite(step.Dot(step))&&step.Dot(step)>1e-12,"Неверный шаг или число дополнительных экземпляров.");
            var o=source.Placement.Origin;
            return CopyInstances(id,Enumerable.Range(1,count).Select(i=>new Vector3(o.X+i*step.X,o.Y+i*step.Y,o.Z+i*step.Z)));
        }

        public PartInstance CopyInstance(Guid id)
        {
            Validate();
            var source = FindInstance(id);
            var copy = Json.Copy(source);
            copy.Id = Guid.NewGuid();
            Instances.Add(copy);
            return copy;
        }
    }

    public enum PartKind { Pipe = 1, RectangularPlate = 2, CurvedPlate = 3, PolygonPlate = 4, IBeam = 5, Rebar = 6 }

    // Explicit tagged union: only the active shape may be present, for safe JSON round trips.
    public sealed class PartDefinition
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Position { get; set; } = "";
        public string Name { get; set; } = "";
        public string Material { get; set; } = "С245";
        public double DensityKgM3 { get; set; } = 7850;
        public bool IncludeInSpec { get; set; }
        public PartKind Kind { get; set; }
        public PipeShape Pipe { get; set; }
        public RectangularPlateShape Plate { get; set; }
        public CurvedPlateShape CurvedPlate { get; set; }
        public PolygonPlateShape PolygonPlate { get; set; }
        public IBeamShape IBeam { get; set; }
        public RebarShape Rebar { get; set; }

        public void Validate()
        {
            Guard.Require(Id != Guid.Empty && Guard.Text(Name) && Guard.Text(Material), "Неверные свойства детали.");
            Guard.Require(!IncludeInSpec || Guard.Text(Position), "Учитываемая деталь должна иметь позицию.");
            Guard.Require(Guard.Positive(DensityKgM3), "Неверная плотность материала.");
            Guard.Require(new object[] { Pipe, Plate, CurvedPlate, PolygonPlate, IBeam, Rebar }.Count(s => s != null) == 1, "Должна быть задана ровно одна геометрия детали.");
            switch (Kind)
            {
                case PartKind.Pipe:
                    Guard.Require(Pipe != null && Plate == null && CurvedPlate == null, "Неверная геометрия трубы."); Pipe.Validate(); break;
                case PartKind.RectangularPlate:
                    Guard.Require(Plate != null && Pipe == null && CurvedPlate == null, "Неверная геометрия листа."); Plate.Validate(); break;
                case PartKind.CurvedPlate:
                    Guard.Require(CurvedPlate != null && Pipe == null && Plate == null, "Неверная геометрия криволинейного листа."); CurvedPlate.Validate(); break;
                case PartKind.PolygonPlate: Guard.Require(PolygonPlate != null, "Не задан контур листа."); PolygonPlate.Validate(); break;
                case PartKind.IBeam: Guard.Require(IBeam != null, "Не задан двутавр."); IBeam.Validate(); break;
                case PartKind.Rebar: Guard.Require(Rebar != null, "Не задан стержень."); Rebar.Validate(); break;
                default: throw new InvalidDataException("Неизвестный тип детали.");
            }
        }
        public double UnitMassKg()
        {
            Validate();
            double volume;
            switch (Kind)
            {
                case PartKind.Pipe: volume = Math.PI * Pipe.Thickness * (Pipe.OuterDiameter - Pipe.Thickness) * Pipe.Length; break;
                case PartKind.RectangularPlate: volume = Plate.Length * Plate.Width * Plate.Thickness; break;
                case PartKind.CurvedPlate: volume = CurvedPlate.AngleRadians * CurvedPlate.Thickness * (CurvedPlate.InnerRadius + CurvedPlate.Thickness / 2) * CurvedPlate.Length; break;
                case PartKind.PolygonPlate: volume = PolygonPlate.NetAreaMm2() * PolygonPlate.Thickness; break;
                case PartKind.IBeam:
                    if(IBeam.CatalogMassKgPerM.HasValue) { double nominal=IBeam.CatalogMassKgPerM.Value*IBeam.Length/1000; Guard.Require(Guard.Positive(nominal), "Неверная масса двутавра."); return nominal; }
                    volume = IBeam.AreaMm2() * IBeam.Length; break;
                case PartKind.Rebar: volume = Math.PI * Rebar.Diameter * Rebar.Diameter / 4 * Rebar.CenterlineLengthMm(); break;
                default: throw new InvalidDataException("Неизвестный тип детали.");
            }
            double mass = volume * (DensityKgM3 / 1e9);
            Guard.Require(Guard.Positive(mass), "Неверная масса детали.");
            return mass;
        }
    }

    // Dimensions in mm. Pipe and curved plate axes run along local Z;
    // rectangular plate occupies local [0,L] x [0,W] x [0,t].
    // Curved plate starts at local +X and sweeps counterclockwise about +Z.
    public sealed class PipeShape
    {
        public double OuterDiameter { get; set; }
        public double Thickness { get; set; }
        public double Length { get; set; }
        public void Validate() { Guard.Require(Guard.Positive(OuterDiameter) && Guard.Positive(Thickness) && Guard.Positive(Length) && Thickness < OuterDiameter / 2, "Неверные размеры трубы."); }
    }
    public sealed class RectangularPlateShape
    {
        public double Length { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        public void Validate() { Guard.Require(Guard.Positive(Length) && Guard.Positive(Width) && Guard.Positive(Thickness), "Неверные размеры листа."); }
    }
    public sealed class CurvedPlateShape
    {
        public double InnerRadius { get; set; }
        public double Thickness { get; set; }
        public double Length { get; set; }
        public double AngleRadians { get; set; }
        public void Validate() { Guard.Require(Guard.Positive(InnerRadius) && Guard.Positive(Thickness) && Guard.Positive(Length) && Guard.Positive(AngleRadians) && AngleRadians <= 2 * Math.PI, "Неверные размеры криволинейного листа."); }
    }
    public sealed class Point2
    {
        public double X { get; set; }
        public double Y { get; set; }
        public Point2() { }
        public Point2(double x, double y) { X = x; Y = y; }
    }

    // Straight-edged contours in local XY, extruded from Z=0 to Z=Thickness.
    // Rings are implicitly closed; winding is immaterial. Holes must be strictly internal.
    public sealed class PolygonPlateShape
    {
        public double Thickness { get; set; }
        public List<Point2> Outer { get; set; } = new List<Point2>();
        public List<List<Point2>> Holes { get; set; } = new List<List<Point2>>();
        public List<RoundHole> RoundHoles { get; set; } = new List<RoundHole>();

        public void Validate() { NetAreaMm2(); }

        public double NetAreaMm2()
        {
            Guard.Require(Guard.Positive(Thickness) && Holes != null && RoundHoles != null, "Неверные параметры листа.");
            double area = PolygonChecks.ValidateRing(Outer);
            for (int i = 0; i < Holes.Count; i++)
            {
                var hole = Holes[i];
                area -= PolygonChecks.ValidateRing(hole);
                Guard.Require(PolygonChecks.Inside(hole[0], Outer) && !PolygonChecks.RingsTouch(hole, Outer), "Отверстие выходит за контур листа.");
                for (int j = 0; j < i; j++)
                    Guard.Require(!PolygonChecks.RingsTouch(hole, Holes[j]) && !PolygonChecks.Inside(hole[0], Holes[j]) && !PolygonChecks.Inside(Holes[j][0], hole), "Отверстия пересекаются или вложены.");
            }
            for (int i = 0; i < RoundHoles.Count; i++)
            {
                var h = RoundHoles[i];
                Guard.Require(h != null && PolygonChecks.ValidPoint(h.Center) && Guard.Positive(h.Radius), "Неверное круглое отверстие.");
                Guard.Require(PolygonChecks.Inside(h.Center, Outer) && PolygonChecks.DistanceToRing(h.Center, Outer) > h.Radius + PolygonChecks.Eps, "Круглое отверстие выходит за контур.");
                foreach (var ring in Holes)
                    Guard.Require(!PolygonChecks.Inside(h.Center, ring) && PolygonChecks.DistanceToRing(h.Center, ring) > h.Radius + PolygonChecks.Eps, "Отверстия пересекаются.");
                for (int j = 0; j < i; j++)
                    Guard.Require(PolygonChecks.Distance(h.Center, RoundHoles[j].Center) > h.Radius + RoundHoles[j].Radius + PolygonChecks.Eps, "Круглые отверстия пересекаются.");
                area -= Math.PI * h.Radius * h.Radius;
            }
            Guard.Require(Guard.Positive(area), "Неверная площадь листа.");
            return area;
        }
    }
    public sealed class RoundHole
    {
        public Point2 Center { get; set; } = new Point2();
        public double Radius { get; set; }
    }
    internal static class PolygonChecks
    {
        internal const double Eps = 1e-7; // mm
        internal static bool ValidPoint(Point2 p) => p != null && Guard.Finite(p.X) && Guard.Finite(p.Y) && Math.Abs(p.X) <= 1e7 && Math.Abs(p.Y) <= 1e7;
        internal static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        private static double Cross(Point2 a, Point2 b, Point2 c) => (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        private static int Side(Point2 a, Point2 b, Point2 c)
        {
            double x = Cross(a,b,c), tolerance = Eps * Distance(a,b);
            return x > tolerance ? 1 : x < -tolerance ? -1 : 0;
        }
        private static double SegmentDistance(Point2 p, Point2 a, Point2 b)
        {
            double x=b.X-a.X, y=b.Y-a.Y;
            double t=Math.Max(0,Math.Min(1,((p.X-a.X)*x+(p.Y-a.Y)*y)/(x*x+y*y)));
            return Distance(p,new Point2(a.X+t*x,a.Y+t*y));
        }
        private static bool Touch(Point2 a, Point2 b, Point2 c, Point2 d)
        {
            if (Side(a,b,c)*Side(a,b,d)<0 && Side(c,d,a)*Side(c,d,b)<0) return true;
            return SegmentDistance(a,c,d)<=Eps || SegmentDistance(b,c,d)<=Eps || SegmentDistance(c,a,b)<=Eps || SegmentDistance(d,a,b)<=Eps;
        }
        internal static double ValidateRing(List<Point2> ring)
        {
            Guard.Require(ring != null && ring.Count >= 3 && ring.Count <= 4096 && ring.All(ValidPoint), "Неверные вершины контура.");
            int n=ring.Count;
            double twice=0;
            for(int i=0;i<n;i++)
            {
                var a=ring[i]; var b=ring[(i+1)%n]; var c=ring[(i+2)%n];
                Guard.Require(Distance(a,b)>Eps, "Нулевая сторона контура.");
                twice+=Cross(ring[0],a,b);
                // Reject overlapping adjacent segments (backtracking).
                if (Side(a,b,c)==0)
                    Guard.Require((b.X-a.X)*(c.X-b.X)+(b.Y-a.Y)*(c.Y-b.Y)>0, "Наложение сторон контура.");
            }
            for(int i=0;i<n;i++)
                for(int j=i+2;j<n;j++)
                    if (!(i==0 && j==n-1))
                        Guard.Require(!Touch(ring[i],ring[(i+1)%n],ring[j],ring[(j+1)%n]), "Самопересечение контура.");
            double area=Math.Abs(twice)/2;
            Guard.Require(Guard.Finite(area) && area>Eps*Eps, "Нулевая площадь контура.");
            return area;
        }
        internal static bool RingsTouch(List<Point2> a,List<Point2> b)
        {
            for(int i=0;i<a.Count;i++)
                for(int j=0;j<b.Count;j++)
                    if(Touch(a[i],a[(i+1)%a.Count],b[j],b[(j+1)%b.Count])) return true;
            return false;
        }
        internal static double DistanceToRing(Point2 p,List<Point2> ring)
        {
            double d=double.PositiveInfinity;
            for(int i=0;i<ring.Count;i++) d=Math.Min(d,SegmentDistance(p,ring[i],ring[(i+1)%ring.Count]));
            return d;
        }
        internal static bool Inside(Point2 p,List<Point2> ring)
        {
            bool inside=false;
            for(int i=0;i<ring.Count;i++)
            {
                var a=ring[i]; var b=ring[(i+1)%ring.Count];
                if ((a.Y>p.Y)!=(b.Y>p.Y) && p.X<(b.X-a.X)*(p.Y-a.Y)/(b.Y-a.Y)+a.X) inside=!inside;
            }
            return inside;
        }
    }

    // Parallel-flange symmetric section, local Z axis. Optional tabulated area
    // overrides the idealized rectangular section for material takeoff only.
    public sealed class IBeamShape
    {
        public string Profile { get; set; } = "";
        public double Height { get; set; }
        public double Width { get; set; }
        public double WebThickness { get; set; }
        public double FlangeThickness { get; set; }
        public double Length { get; set; }
        public double? CatalogAreaMm2 { get; set; }
        public double RootRadius { get; set; }
        public double? CatalogMassKgPerM { get; set; }
        public string CatalogStandard { get; set; }
        public void Validate()
        {
            Guard.Require(Guard.Positive(Height) && Guard.Positive(Width) && Guard.Positive(WebThickness) && Guard.Positive(FlangeThickness) && Guard.Positive(Length)
                && WebThickness < Width && FlangeThickness < Height/2, "Неверные размеры двутавра.");
            Guard.Require(Guard.Finite(RootRadius)&&RootRadius>=0&&RootRadius<=(Width-WebThickness)/2&&2*RootRadius<=Height-2*FlangeThickness,"Неверный радиус сопряжения.");
            Guard.Require(!CatalogMassKgPerM.HasValue || (Guard.Positive(CatalogMassKgPerM.Value)&&Guard.Text(CatalogStandard)&&CatalogAreaMm2.HasValue),"Неверная масса сортамента.");
            Guard.Require(!CatalogAreaMm2.HasValue || (Guard.Text(Profile) && Guard.Positive(CatalogAreaMm2.Value) && CatalogAreaMm2.Value <= Height*Width), "Неверная площадь сортамента.");
            Guard.Require(Guard.Positive(CatalogAreaMm2 ?? (2*Width*FlangeThickness+(Height-2*FlangeThickness)*WebThickness+(4-Math.PI)*RootRadius*RootRadius)), "Неверная площадь двутавра.");
        }
        public double AreaMm2() { Validate(); return CatalogAreaMm2 ?? (2*Width*FlangeThickness+(Height-2*FlangeThickness)*WebThickness+(4-Math.PI)*RootRadius*RootRadius); }
    }

    // Centerline polyline with a radius at each intermediate vertex (0 is allowed only for straight continuation).
    // Tangent circular fillets are accounted for in developed length.
    public sealed class RebarShape
    {
        public double Diameter { get; set; }
        public List<Vector3> Points { get; set; } = new List<Vector3>();
        public List<double> BendRadii { get; set; } = new List<double>();
        public void Validate() { CenterlineLengthMm(); }
        public double CenterlineLengthMm()
        {
            Guard.Require(Guard.Positive(Diameter) && Points != null && Points.Count >= 2 && BendRadii != null && BendRadii.Count == Points.Count-2 && Points.All(p=>p!=null && p.IsFinite()), "Неверная траектория стержня.");
            int n=Points.Count;
            var lengths=new double[n-1]; var dirs=new Vector3[n-1]; var trim=new double[n]; double sum=0, arcs=0;
            for(int i=0;i<n-1;i++)
            {
                var a=Points[i]; var b=Points[i+1]; var d=new Vector3(b.X-a.X,b.Y-a.Y,b.Z-a.Z);
                double l=Math.Sqrt(d.Dot(d));
                Guard.Require(Guard.Positive(l) && l>1e-7, "Нулевая длина участка стержня.");
                lengths[i]=l; dirs[i]=new Vector3(d.X/l,d.Y/l,d.Z/l); sum+=l;
            }
            for(int i=1;i<n-1;i++)
            {
                double r=BendRadii[i-1];
                Guard.Require(Guard.Finite(r) && r>=0, "Неверный радиус гиба.");
                double angle=Math.Acos(Math.Max(-1,Math.Min(1,dirs[i-1].Dot(dirs[i]))));
                Guard.Require(angle<Math.PI-1e-8, "Разворот стержня на 180 градусов не поддерживается.");
                if(angle>1e-8)
                {
                    Guard.Require(r>Diameter/2, "Радиус по оси гиба должен превышать радиус стержня.");
                    trim[i]=r*Math.Tan(angle/2); arcs+=r*angle;
                }
            }
            for(int i=0;i<n-1;i++)
                Guard.Require(trim[i]+trim[i+1]<=lengths[i], "Гибы не помещаются на участке стержня.");
            double result=sum-2*trim.Sum()+arcs;
            Guard.Require(Guard.Positive(result), "Неверная длина стержня.");
            return result;
        }
    }

    public sealed class PartInstance
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PartId { get; set; }
        public RigidPlacement Placement { get; set; } = new RigidPlacement();
    }
    public sealed class Vector3
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public Vector3() { }
        public Vector3(double x, double y, double z) { X = x; Y = y; Z = z; }
        internal bool IsFinite() => Guard.Finite(X) && Guard.Finite(Y) && Guard.Finite(Z);
        internal double Dot(Vector3 b) => X * b.X + Y * b.Y + Z * b.Z;
        internal Vector3 Cross(Vector3 b) => new Vector3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    }
    public sealed class RigidPlacement
    {
        public Vector3 Origin { get; set; } = new Vector3();
        public Vector3 AxisX { get; set; } = new Vector3(1, 0, 0);
        public Vector3 AxisY { get; set; } = new Vector3(0, 1, 0);
        public void Validate()
        {
            const double eps = 1e-9;
            Guard.Require(Origin != null && AxisX != null && AxisY != null && Origin.IsFinite() && AxisX.IsFinite() && AxisY.IsFinite(), "Некорректные координаты детали.");
            Guard.Require(Math.Abs(AxisX.Dot(AxisX) - 1) <= eps && Math.Abs(AxisY.Dot(AxisY) - 1) <= eps && Math.Abs(AxisX.Dot(AxisY)) <= eps, "Оси должны быть ортонормированными.");
        }
        public static RigidPlacement FromThreePoints(Vector3 origin,Vector3 right,Vector3 upper,bool reverse=false)
        {
            Guard.Require(origin!=null&&right!=null&&upper!=null&&origin.IsFinite()&&right.IsFinite()&&upper.IsFinite(),"Некорректные точки плоскости.");
            var a=new Vector3(right.X-origin.X,right.Y-origin.Y,right.Z-origin.Z);
            var b=new Vector3(upper.X-origin.X,upper.Y-origin.Y,upper.Z-origin.Z);
            double l=Math.Sqrt(a.Dot(a)),m=Math.Sqrt(b.Dot(b));
            Guard.Require(Guard.Finite(l)&&Guard.Finite(m)&&l>1e-6&&m>1e-6,"Точки плоскости должны различаться.");
            var x=new Vector3(a.X/l,a.Y/l,a.Z/l);
            var n=x.Cross(new Vector3(b.X/m,b.Y/m,b.Z/m));
            double h=Math.Sqrt(n.Dot(n));
            Guard.Require(Guard.Finite(h)&&h>1e-9&&m*h>1e-6,"Точки плоскости не должны лежать на одной прямой.");
            var z=new Vector3(n.X/h,n.Y/h,n.Z/h);
            var y=z.Cross(x);
            if(reverse)y=new Vector3(-y.X,-y.Y,-y.Z);
            var frame=new RigidPlacement{Origin=new Vector3(origin.X,origin.Y,origin.Z),AxisX=x,AxisY=y};
            frame.Validate();return frame;
        }
        public RigidPlacement RelativeTo(RigidPlacement parent)
        {
            Validate();parent.Validate();
            var z=parent.AxisX.Cross(parent.AxisY);
            Func<Vector3,Vector3> local=v=>new Vector3(v.Dot(parent.AxisX),v.Dot(parent.AxisY),v.Dot(z));
            var d=new Vector3(Origin.X-parent.Origin.X,Origin.Y-parent.Origin.Y,Origin.Z-parent.Origin.Z);
            var result=new RigidPlacement{Origin=local(d),AxisX=local(AxisX),AxisY=local(AxisY)};
            result.Validate();return result;
        }
        public RigidPlacement InParent(RigidPlacement parent)
        {
            Validate();parent.Validate();
            var z=parent.AxisX.Cross(parent.AxisY);
            Func<Vector3,Vector3> world=v=>new Vector3(
                parent.AxisX.X*v.X+parent.AxisY.X*v.Y+z.X*v.Z,
                parent.AxisX.Y*v.X+parent.AxisY.Y*v.Y+z.Y*v.Z,
                parent.AxisX.Z*v.X+parent.AxisY.Z*v.Y+z.Z*v.Z);
            var result=new RigidPlacement{Origin=parent.ToWorld(Origin),AxisX=world(AxisX),AxisY=world(AxisY)};
            result.Validate();return result;
        }
        public Vector3 ToWorld(Vector3 local)
        {
            Validate();
            Guard.Require(local != null && local.IsFinite(), "Неверная локальная точка.");
            var z = AxisX.Cross(AxisY);
            var p = new Vector3(Origin.X + AxisX.X * local.X + AxisY.X * local.Y + z.X * local.Z,
                Origin.Y + AxisX.Y * local.X + AxisY.Y * local.Y + z.Y * local.Z,
                Origin.Z + AxisX.Z * local.X + AxisY.Z * local.Y + z.Z * local.Z);
            Guard.Require(p.IsFinite(), "Переполнение координат.");
            return p;
        }
    }
    // Plane fitted from a polygon in world coordinates, independent of the editor UCS.
    public static class ContourPlane
    {
        public const double ToleranceMm=1e-6;
        static Vector3 Sub(Vector3 a,Vector3 b)=>new Vector3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        static Vector3 Scale(Vector3 a,double s)=>new Vector3(a.X*s,a.Y*s,a.Z*s);
        static Vector3 Unit(Vector3 a)
        {
            double n=Math.Sqrt(a.Dot(a));
            Guard.Require(Guard.Positive(n),"Не удалось определить плоскость контура.");
            return Scale(a,1/n);
        }
        public static RigidPlacement Fit(IList<Vector3> points,Vector3 preferredNormal)
        {
            Guard.Require(points!=null&&points.Count>=3&&points.All(p=>p!=null&&p.IsFinite()),"Неверные точки контура.");
            var origin=points[0];
            var vectors=points.Skip(1).Select(p=>Sub(p,origin)).ToList();
            var longest=vectors.OrderByDescending(v=>v.Dot(v)).First();
            Guard.Require(longest.Dot(longest)>ToleranceMm*ToleranceMm,"Контур вырожден.");
            var x=Unit(longest);
            var cross=vectors.Select(v=>x.Cross(v)).OrderByDescending(v=>v.Dot(v)).First();
            Guard.Require(cross.Dot(cross)>ToleranceMm*ToleranceMm,"Точки контура лежат на одной прямой.");
            var z=Unit(cross);
            var normal=preferredNormal!=null&&preferredNormal.IsFinite()&&preferredNormal.Dot(preferredNormal)>0?Unit(preferredNormal):new Vector3(0,0,1);
            double dot=z.Dot(normal);
            // For perpendicular normals use a deterministic world-axis sign.
            if(Math.Abs(dot)<1e-9)
                dot=Math.Abs(z.X)>=Math.Abs(z.Y)&&Math.Abs(z.X)>=Math.Abs(z.Z)?z.X:Math.Abs(z.Y)>=Math.Abs(z.Z)?z.Y:z.Z;
            if(dot<0)z=Scale(z,-1);
            var y=Unit(z.Cross(x));
            var frame=new RigidPlacement {Origin=new Vector3(origin.X,origin.Y,origin.Z),AxisX=x,AxisY=y};
            frame.Validate();
            foreach(var p in points)Project(frame,p);
            return frame;
        }
        public static Point2 Project(RigidPlacement frame,Vector3 point)
        {
            frame.Validate();
            Guard.Require(point!=null&&point.IsFinite(),"Неверная точка контура.");
            var v=Sub(point,frame.Origin);
            Guard.Require(Math.Abs(v.Dot(frame.AxisX.Cross(frame.AxisY)))<=ToleranceMm,"Контур и отверстия должны лежать в одной плоскости.");
            return new Point2(v.Dot(frame.AxisX),v.Dot(frame.AxisY));
        }
    }

    public sealed class AssemblySpecRow
    {
        public Guid PartId { get; set; }
        public string Position { get; set; }
        public string Name { get; set; }
        public PartKind Kind { get; set; }
        public string Material { get; set; }
        public int Count { get; set; }
        public double UnitMassKg { get; set; }
        public double TotalMassKg { get; set; }
    }
    internal static class Guard
    {
        internal static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        internal static bool Positive(double d) => Finite(d) && d > 0;
        internal static bool Text(string s) => !string.IsNullOrWhiteSpace(s);
        internal static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
