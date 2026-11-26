using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KotNode.Core
{
    public sealed class TablePlan
    {
        public string Title { get; set; }
        public List<string[]> Rows { get; set; }=new List<string[]>();
        public double[] ColumnWidths { get; set; }
        public int[] MergedRows { get; set; }=new int[0];
        public double TotalMassKg { get; set; }
    }
    public sealed class VrsSummaryRow
    {
        public string Profile { get; set; }
        public string Material { get; set; }
        public long DetailCount { get; set; }
        public double MassKg { get; set; }
    }
    public static class AssemblyReporting
    {
        static string F(double v)=>v.ToString("0.#",CultureInfo.InvariantCulture);
        static string M(double v)=>v.ToString("0.00",CultureInfo.InvariantCulture);
        static string T(double v)=>(v/1000.0).ToString("0.000",CultureInfo.InvariantCulture);

        public static string Designation(PartDefinition p)
        {
            p.Validate();
            switch(p.Kind)
            {
                case PartKind.RectangularPlate:
                    return "Лист "+F(p.Plate.Thickness)+"×"+F(p.Plate.Width)+"×"+F(p.Plate.Length);
                case PartKind.PolygonPlate:
                {
                    double w=p.PolygonPlate.Outer.Max(q=>q.X)-p.PolygonPlate.Outer.Min(q=>q.X);
                    double h=p.PolygonPlate.Outer.Max(q=>q.Y)-p.PolygonPlate.Outer.Min(q=>q.Y);
                    return "Лист "+F(p.PolygonPlate.Thickness)+"×"+F(w)+"×"+F(h)+" (контур)";
                }
                case PartKind.CurvedPlate:
                {
                    double deg=p.CurvedPlate.AngleRadians*180.0/Math.PI;
                    return "Накладка R"+F(p.CurvedPlate.InnerRadius)+"×"+F(p.CurvedPlate.Thickness)+"×"+F(p.CurvedPlate.Length)+" ("+F(deg)+"°)";
                }
                case PartKind.Pipe:
                    return "Труба Ø"+F(p.Pipe.OuterDiameter)+"×"+F(p.Pipe.Thickness)+"×"+F(p.Pipe.Length);
                case PartKind.IBeam:
                    return "Двутавр "+(string.IsNullOrWhiteSpace(p.IBeam.Profile)?F(p.IBeam.Height):p.IBeam.Profile)+" L="+F(p.IBeam.Length)+(string.IsNullOrWhiteSpace(p.IBeam.CatalogStandard)?"":" "+p.IBeam.CatalogStandard);
                case PartKind.Rebar:
                    return "Ø"+F(p.Rebar.Diameter)+" "+p.Material+" L="+Math.Round(p.Rebar.CenterlineLengthMm()).ToString(CultureInfo.InvariantCulture);
                default:
                    return p.Name;
            }
        }
        public static string VrsProfile(PartDefinition p)
        {
            p.Validate();
            switch(p.Kind)
            {
                case PartKind.RectangularPlate: return "Лист t="+F(p.Plate.Thickness);
                case PartKind.PolygonPlate: return "Лист t="+F(p.PolygonPlate.Thickness);
                case PartKind.CurvedPlate: return "Накладка t="+F(p.CurvedPlate.Thickness);
                case PartKind.Pipe: return "Труба Ø"+F(p.Pipe.OuterDiameter)+"×"+F(p.Pipe.Thickness);
                case PartKind.IBeam: return "Двутавр "+(string.IsNullOrWhiteSpace(p.IBeam.Profile)?F(p.IBeam.Height):p.IBeam.Profile);
                case PartKind.Rebar: return "Арматура Ø"+F(p.Rebar.Diameter);
                default: return p.Kind.ToString();
            }
        }
        public static TablePlan BuildMarkSpec(AssemblyMark mark,long nodeCount=1)
        {
            mark.Validate();
            if(nodeCount<1)nodeCount=1;
            var spec=mark.Specification().OrderBy(r=>r.Position,StringComparer.OrdinalIgnoreCase).ToList();
            var rows=new List<string[]>
            {
                new[]{"Спецификация марки "+mark.Code+" — "+mark.Name},
                new[]{"Поз.","Обозначение / профиль","Наименование","Кол.","Масса ед., кг","Масса, кг","Материал"}
            };
            double perNode=0;
            foreach(var r in spec)
            {
                var part=mark.Parts.Single(p=>p.Id==r.PartId);
                perNode+=r.TotalMassKg;
                rows.Add(new[]{
                    r.Position,
                    Designation(part),
                    r.Name,
                    r.Count.ToString(CultureInfo.InvariantCulture),
                    M(r.UnitMassKg),
                    M(r.TotalMassKg),
                    r.Material
                });
            }
            double totalAll=perNode*nodeCount;
            rows.Add(new[]{"","","Итого на 1 марку:","","",M(perNode),""});
            if(nodeCount>1)
                rows.Add(new[]{"","","Итого на "+nodeCount+" марок:","","",M(totalAll),""});
            int excluded=mark.Parts.Count(p=>!p.IncludeInSpec&&mark.Instances.Any(i=>i.PartId==p.Id));
            string note="Масса нетто без припусков и металла швов."+(excluded>0?" Справочных позиций исключено: "+excluded+".":"");
            int noteRow=rows.Count;
            rows.Add(new[]{note});
            return new TablePlan
            {
                Title=rows[0][0],
                Rows=rows,
                ColumnWidths=new[]{5.0,23.0,19.0,5.5,11.5,9.0,9.5},
                MergedRows=new[]{0,noteRow},
                TotalMassKg=totalAll
            };
        }
        public static List<VrsSummaryRow> SummarizeMarkVrs(AssemblyMark mark,long nodeCount=1)
        {
            mark.Validate();
            if(nodeCount<1)nodeCount=1;
            var spec=mark.Specification();
            var groups=new Dictionary<string,VrsSummaryRow>(StringComparer.OrdinalIgnoreCase);
            foreach(var r in spec)
            {
                var part=mark.Parts.Single(p=>p.Id==r.PartId);
                string prof=VrsProfile(part);
                string mat=(r.Material??"").Trim();
                string key=prof+"|"+mat;
                if(!groups.TryGetValue(key,out var g))
                {
                    g=new VrsSummaryRow{Profile=prof,Material=mat};
                    groups[key]=g;
                }
                g.DetailCount+=r.Count*nodeCount;
                g.MassKg+=r.TotalMassKg*nodeCount;
            }
            return groups.Values.OrderBy(x=>x.Profile,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Material,StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static TablePlan BuildMarkVrs(AssemblyMark mark,long nodeCount=1)
        {
            var summary=SummarizeMarkVrs(mark,nodeCount);
            double total=summary.Sum(s=>s.MassKg);
            var rows=new List<string[]>
            {
                new[]{"Ведомость расхода стали — "+mark.Code+(nodeCount>1?" (×"+nodeCount+")":"")},
                new[]{"Профиль / толщина","Сталь","Кол. деталей","Масса, кг","Масса, т"}
            };
            foreach(var s in summary)
                rows.Add(new[]{s.Profile,s.Material,s.DetailCount.ToString(CultureInfo.InvariantCulture),M(s.MassKg),T(s.MassKg)});
            rows.Add(new[]{"Итого","",summary.Sum(s=>s.DetailCount).ToString(CultureInfo.InvariantCulture),M(total),T(total)});
            int noteRow=rows.Count;
            rows.Add(new[]{"Учтены только детали с флагом спецификации. Без припусков и наплавленного металла."});
            return new TablePlan
            {
                Title=rows[0][0],
                Rows=rows,
                ColumnWidths=new[]{22.0,10.0,10.0,11.0,11.0},
                MergedRows=new[]{0,noteRow},
                TotalMassKg=total
            };
        }
        public static List<VrsSummaryRow> SummarizeProjectVrs(AssemblyProject project,string scope=null)
        {
            project.Validate();
            var active=project.Uses.Where(u=>u.Included&&(scope==null||u.Location==scope))
                .GroupBy(u=>u.MarkId).ToDictionary(g=>g.Key,g=>g.Sum(u=>(long)u.Count));
            var groups=new Dictionary<string,VrsSummaryRow>(StringComparer.OrdinalIgnoreCase);
            Action<string,string,long,double> add=(prof,mat,cnt,mass)=>{
                string key=prof+"|"+mat;
                if(!groups.TryGetValue(key,out var g)){g=new VrsSummaryRow{Profile=prof,Material=mat};groups[key]=g;}
                g.DetailCount+=cnt;g.MassKg+=mass;
            };
            foreach(var m in project.Marks)
            {
                if(!active.TryGetValue(m.Id,out var nodeCount))continue;
                var g=Splice.Calculate(m.Parameters);
                add("Накладка t="+F(m.Parameters.PlateThickness)+" (Ø"+F(m.Parameters.DonorDiameter)+")",(m.Parameters.Material??"").Trim(),nodeCount*m.Parameters.PlateCount,nodeCount*m.Parameters.PlateCount*g.PlateMass);
            }
            foreach(var m in project.Assemblies)
            {
                if(!active.TryGetValue(m.Id,out var nodeCount))continue;
                foreach(var s in SummarizeMarkVrs(m,nodeCount))
                    add(s.Profile,s.Material,s.DetailCount,s.MassKg);
            }
            return groups.Values.OrderBy(x=>x.Profile,StringComparer.OrdinalIgnoreCase).ThenBy(x=>x.Material,StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static TablePlan BuildProjectVrs(AssemblyProject project,string scope=null)
        {
            project.Validate();
            var materialRows=project.Materials(scope);
            var summary=SummarizeProjectVrs(project,scope);
            double total=summary.Sum(s=>s.MassKg);
            string title="Сводная ВРС — "+project.Code+" / версия "+project.Version+(scope==null?"":" ("+scope+")");
            var rows=new List<string[]>
            {
                new[]{title},
                new[]{"Марка","Поз. / деталь","Сталь","Узлов","Деталей","Масса, кг"}
            };
            foreach(var r in materialRows)
                rows.Add(new[]{r.Mark,r.Position+" — "+r.Name,r.Material,r.NodeCount.ToString(CultureInfo.InvariantCulture),r.DetailCount.ToString(CultureInfo.InvariantCulture),M(r.TotalMassKg)});
            rows.Add(new[]{"Итого по маркам","","","",materialRows.Sum(r=>r.DetailCount).ToString(CultureInfo.InvariantCulture),M(total)});
            int summaryHeader=rows.Count;
            rows.Add(new[]{"Сводка по профилям и маркам стали"});
            rows.Add(new[]{"Профиль / толщина","Сталь","Деталей","Масса, кг","Масса, т",""});
            foreach(var s in summary)
                rows.Add(new[]{s.Profile,s.Material,s.DetailCount.ToString(CultureInfo.InvariantCulture),M(s.MassKg),T(s.MassKg),""});
            rows.Add(new[]{"Всего по проекту","",summary.Sum(s=>s.DetailCount).ToString(CultureInfo.InvariantCulture),M(total),T(total),""});
            return new TablePlan
            {
                Title=title,
                Rows=rows,
                ColumnWidths=new[]{14.0,22.0,10.0,8.0,9.0,11.0},
                MergedRows=new[]{0,summaryHeader},
                TotalMassKg=total
            };
        }
        public static string ToCsv(TablePlan plan)
        {
            var sb=new StringBuilder();
            foreach(var row in plan.Rows)
            {
                sb.AppendLine(string.Join(";",row.Select(cell=>{
                    string s=(cell??"").Replace("\"","\"\"");
                    return s.IndexOfAny(new[]{';','"','\r','\n'})>=0?"\""+s+"\"":s;
                })));
            }
            return sb.ToString();
        }
    }
}
