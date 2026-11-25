using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace KotNode.Core
{
    public static class Json
    {
        public static string Write<T>(T value) { using (var s = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(s, value); return Encoding.UTF8.GetString(s.ToArray()); } }
        public static T Read<T>(string value) { using (var s = new MemoryStream(Encoding.UTF8.GetBytes(value))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(s); }
        public static T Copy<T>(T value) { return Read<T>(Write(value)); }
    }
    public sealed class Project
    {
        public int Schema { get; set; } = 1;
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public long Version { get; set; }
        public List<NodeMark> Marks { get; set; } = new List<NodeMark>();
        public List<Use> Uses { get; set; } = new List<Use>();
        public void Validate()
        {
            if (Schema != 1) throw new InvalidDataException("Неподдерживаемая версия проекта.");
            if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Code) || string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("Заполните шифр и название проекта.");
            if (Marks == null || Uses == null || Version < 0) throw new InvalidDataException("Поврежден состав проекта.");
            if (Marks.Any(m => m == null) || Uses.Any(u => u == null)) throw new InvalidDataException("Пустая запись проекта.");
            foreach (var m in Marks) m.Validate();
            if (Marks.Select(m => m.Id).Distinct().Count() != Marks.Count || Marks.Select(m => m.Code.Trim().ToUpperInvariant()).Distinct().Count() != Marks.Count) throw new InvalidDataException("Марки узлов должны быть уникальными.");
            if (Uses.Select(u => u.Id).Distinct().Count() != Uses.Count) throw new InvalidDataException("Дубли применений.");
            foreach (var u in Uses)
            {
                if (u.Id == Guid.Empty || !Marks.Any(m => m.Id == u.MarkId) || u.Count < 1 || u.Count > 1000000 || string.IsNullOrWhiteSpace(u.Location)) throw new InvalidDataException("Проверьте марку, место и количество применения.");
            }
        }
        public List<SteelRow> Steel(string scope = null)
        {
            Validate();
            var rows = new List<SteelRow>();
            foreach (var m in Marks)
            {
                var uses = Uses.Where(u => u.MarkId == m.Id && u.Included && (scope == null || u.Location == scope)).ToList();
                long count = uses.Sum(u => (long)u.Count);
                if (count == 0) continue;
                var g = Splice.Calculate(m.Parameters);
                rows.Add(new SteelRow { Mark = m.Code, Revision = m.Revision, Material = m.Parameters.Material, Thickness = m.Parameters.PlateThickness, NodeCount = count, DetailCount = count * m.Parameters.PlateCount, UnitMass = g.PlateMass, TotalMass = count * m.Parameters.PlateCount * g.PlateMass, Sources = string.Join("; ", uses.Select(u => u.Location + ": " + u.Count)) });
            }
            return rows;
        }
    }
    public sealed class NodeMark
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "У-01";
        public Guid DetailId { get; set; } = Guid.NewGuid();
        public string DetailCode { get; set; } = "Н-1";
        public int Revision { get; set; } = 1;
        public string SectionLabel { get; set; } = "1";
        public string Name { get; set; } = "Стык труб с накладками";
        public SpliceParameters Parameters { get; set; } = new SpliceParameters();
        public void Validate()
        {
            if (Id == Guid.Empty || DetailId == Guid.Empty || string.IsNullOrWhiteSpace(DetailCode) || string.IsNullOrWhiteSpace(Code) || Revision < 1 || Parameters == null) throw new InvalidDataException("Неверная марка узла.");
            Splice.Calculate(Parameters);
        }
    }
    public sealed class Use
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid MarkId { get; set; }
        public string Location { get; set; } = "Ярус 1";
        public int Count { get; set; } = 1;
        public bool Included { get; set; }
    }
    public sealed class SpliceParameters
    {
        public double PipeDiameter { get; set; } = 820;
        public double PipeThickness { get; set; } = 8;
        public double PlateLength { get; set; } = 950;
        public double PlateThickness { get; set; } = 8;
        public double DonorDiameter { get; set; } = 820;
        public int PlateCount { get; set; } = 4;
        public double ClearSpacing { get; set; } = 50;
        public double EndGap { get; set; } = 10;
        public bool ButtWeld { get; set; }
        public double WeldLeg { get; set; } = 9;
        public bool? FrontWelds { get; set; }
        public double? SinglePlateAngleDegrees { get; set; }
        public string Material { get; set; } = "С245";
    }
    public sealed class SpliceGeometry
    {
        public double Angle { get; set; }
        public double Shift { get; set; }
        public double InnerRadius { get; set; }
        public double OuterRadius { get; set; }
        public double PlateMass { get; set; }
        public double Gap { get; set; }
    }
    public static class Splice
    {
        private static bool Positive(double x) { return !double.IsNaN(x) && !double.IsInfinity(x) && x > 0 && x <= 100000; }
        public static double Spacing(SpliceParameters p, double angle)
        {
            double ri = p.DonorDiameter / 2 - p.PlateThickness, ro = p.DonorDiameter / 2, r = p.PipeDiameter / 2, half = angle / 2, q = Math.PI / p.PlateCount;
            double shift = -ri * Math.Cos(half) + Math.Sqrt(Math.Max(0, r * r - ri * ri * Math.Sin(half) * Math.Sin(half)));
            return 2 * (shift * Math.Sin(q) + (half <= q ? ri : ro) * Math.Sin(q - half));
        }
        public static SpliceGeometry Calculate(SpliceParameters p)
        {
            if (p == null || !new[] { p.PipeDiameter, p.PipeThickness, p.PlateLength, p.PlateThickness, p.DonorDiameter, p.WeldLeg }.All(Positive)) throw new InvalidDataException("Размеры должны быть положительными конечными числами.");
            if (p.PipeThickness * 2 >= p.PipeDiameter || p.PlateThickness * 2 >= p.DonorDiameter || p.DonorDiameter - 2 * p.PlateThickness > p.PipeDiameter) throw new InvalidDataException("Недопустимое соотношение диаметров и толщин.");
            if (p.PlateCount < 1 || p.PlateCount > 12) throw new InvalidDataException("Поддерживается от 1 до 12 накладок.");
            if ((p.PlateCount>1 && !Positive(p.ClearSpacing)) || double.IsNaN(p.EndGap) || double.IsInfinity(p.EndGap) || p.EndGap < 0 || p.EndGap > p.PlateLength / 2 || string.IsNullOrWhiteSpace(p.Material)) throw new InvalidDataException("Проверьте расстояние между накладками, зазор и материал.");
            double lo = 0, hi = Math.PI;
            if (p.PlateCount>1 && (p.ClearSpacing < Math.Max(0, Spacing(p, hi)) || p.ClearSpacing >= Spacing(p, lo))) throw new InvalidDataException("Накладки с таким расстоянием не размещаются на трубе.");
            for (int i = 0; i < 80; i++) { double mid = (lo + hi) / 2; if (Spacing(p, mid) > p.ClearSpacing) lo = mid; else hi = mid; }
            double a = p.PlateCount==1?(p.SinglePlateAngleDegrees??84)*Math.PI/180:(lo + hi) / 2;
            if (double.IsNaN(a)||double.IsInfinity(a)||a<=0||a>Math.PI)throw new InvalidDataException("Угол одиночной накладки должен быть от 0 до 180 градусов.");
            double ri = p.DonorDiameter / 2 - p.PlateThickness, ro = p.DonorDiameter / 2, r = p.PipeDiameter / 2;
            return new SpliceGeometry { Angle = a, InnerRadius = ri, OuterRadius = ro, Shift = -ri * Math.Cos(a / 2) + Math.Sqrt(Math.Max(0, r * r - ri * ri * Math.Sin(a / 2) * Math.Sin(a / 2))), PlateMass = 0.5 * a * (ro * ro - ri * ri) * p.PlateLength * 0.00000785, Gap = p.ButtWeld ? 0 : p.EndGap };
        }
    }
    public sealed class SteelRow
    {
        public string Mark { get; set; }
        public int Revision { get; set; }
        public string Material { get; set; }
        public double Thickness { get; set; }
        public long NodeCount { get; set; }
        public long DetailCount { get; set; }
        public double UnitMass { get; set; }
        public double TotalMass { get; set; }
        public string Sources { get; set; }
    }
    public static class Report
    {
        private static string Cell(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }
        public static string Csv(Project p, string scope = null)
        {
            var b = new StringBuilder("Марка;Редакция;Сталь;Толщина, мм;Узлов;Накладок;Масса накладки, кг;Масса, кг;Источник количества\r\n");
            foreach (var r in p.Steel(scope)) b.AppendLine(string.Join(";", Cell(r.Mark), r.Revision, Cell(r.Material), r.Thickness.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), r.NodeCount, r.DetailCount, r.UnitMass.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), r.TotalMass.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), Cell(r.Sources)));
            b.AppendLine("\r\nСталь;Толщина, мм;Масса, кг");
            foreach (var g in p.Steel(scope).GroupBy(r => new { r.Material, r.Thickness })) b.AppendLine(Cell(g.Key.Material) + ";" + g.Key.Thickness.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" + g.Sum(r => r.TotalMass).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            return b.ToString();
        }
    }
}
