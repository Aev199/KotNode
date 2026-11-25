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

        public PartInstance CopyInstance(Guid id)
        {
            Validate();
            var source = Instances.Single(i => i.Id == id);
            var copy = Json.Copy(source);
            copy.Id = Guid.NewGuid();
            Instances.Add(copy);
            return copy;
        }
    }

    public enum PartKind { Pipe = 1, RectangularPlate = 2, CurvedPlate = 3 }

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

        public void Validate()
        {
            Guard.Require(Id != Guid.Empty && Guard.Text(Name) && Guard.Text(Material), "Неверные свойства детали.");
            Guard.Require(!IncludeInSpec || Guard.Text(Position), "Учитываемая деталь должна иметь позицию.");
            Guard.Require(Guard.Positive(DensityKgM3), "Неверная плотность материала.");
            switch (Kind)
            {
                case PartKind.Pipe:
                    Guard.Require(Pipe != null && Plate == null && CurvedPlate == null, "Неверная геометрия трубы."); Pipe.Validate(); break;
                case PartKind.RectangularPlate:
                    Guard.Require(Plate != null && Pipe == null && CurvedPlate == null, "Неверная геометрия листа."); Plate.Validate(); break;
                case PartKind.CurvedPlate:
                    Guard.Require(CurvedPlate != null && Pipe == null && Plate == null, "Неверная геометрия криволинейного листа."); CurvedPlate.Validate(); break;
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
