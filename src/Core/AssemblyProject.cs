using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KotNode.Core
{
    // Schema 2 is deliberately separate until every CAD consumer supports it.
    // Existing schema-1 applications must reject it rather than discard assemblies.
    public sealed class AssemblyProject
    {
        public int Schema { get; set; } = 2;
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public long Version { get; set; }
        public long? LegacySourceVersion { get; set; }
        public List<NodeMark> Marks { get; set; } = new List<NodeMark>();
        public List<AssemblyMark> Assemblies { get; set; } = new List<AssemblyMark>();
        public List<Use> Uses { get; set; } = new List<Use>();

        // Explicit migration to a new file; original schema-1 document is unchanged.
        public static AssemblyProject FromLegacy(Project source)
        {
            Guard.Require(source != null, "Не задан исходный проект.");
            source.Validate();
            var copy=Json.Copy(source);
            var migrated=new AssemblyProject { Id=copy.Id, Code=copy.Code, Name=copy.Name,
                LegacySourceVersion=copy.Version, Marks=copy.Marks, Uses=copy.Uses };
            migrated.Validate();
            return migrated;
        }

        public void Validate()
        {
            Guard.Require(Schema==2 && Id!=Guid.Empty && Guard.Text(Code) && Guard.Text(Name) && Version>=0 &&
                (!LegacySourceVersion.HasValue || LegacySourceVersion.Value>=0), "Неверная версия или свойства проекта.");
            Guard.Require(Marks!=null && Assemblies!=null && Uses!=null && Marks.All(m=>m!=null) &&
                Assemblies.All(m=>m!=null) && Uses.All(u=>u!=null), "Поврежден состав проекта.");
            foreach(var m in Marks) m.Validate();
            foreach(var m in Assemblies) m.Validate();
            var ids=new HashSet<Guid>();
            var codes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var m in Marks)
                Guard.Require(ids.Add(m.Id) && codes.Add(m.Code.Trim()), "Повторная марка узла.");
            foreach(var m in Assemblies)
                Guard.Require(ids.Add(m.Id) && codes.Add(m.Code.Trim()), "Повторная марка узла.");
            var useIds=new HashSet<Guid>();
            foreach(var u in Uses)
                Guard.Require(u.Id!=Guid.Empty && useIds.Add(u.Id) && ids.Contains(u.MarkId) &&
                    u.Count>=1 && u.Count<=1000000 && Guard.Text(u.Location), "Неверное применение марки.");
        }

        public List<ProjectMaterialRow> Materials(string scope=null)
        {
            Validate();
            var rows=new List<ProjectMaterialRow>();
            var active=Uses.Where(u=>u.Included && (scope==null || u.Location==scope))
                .GroupBy(u=>u.MarkId).ToDictionary(g=>g.Key,g=>g.Sum(u=>(long)u.Count));
            foreach(var m in Marks)
            {
                if(!active.TryGetValue(m.Id,out long count)) continue;
                var g=Splice.Calculate(m.Parameters);
                rows.Add(ProjectMaterialRow.Create(m.Id,m.Code,m.DetailId,m.DetailCode,"Накладка",
                    m.Parameters.Material,PartKind.CurvedPlate,count,m.Parameters.PlateCount,g.PlateMass));
            }
            foreach(var m in Assemblies)
            {
                if(!active.TryGetValue(m.Id,out long count)) continue;
                foreach(var r in m.Specification())
                    rows.Add(ProjectMaterialRow.Create(m.Id,m.Code,r.PartId,r.Position,r.Name,r.Material,r.Kind,count,r.Count,r.UnitMassKg));
            }
            return rows;
        }
    }

    public sealed class ProjectMaterialRow
    {
        public Guid MarkId { get; set; }
        public string Mark { get; set; }
        public Guid PartId { get; set; }
        public string Position { get; set; }
        public string Name { get; set; }
        public string Material { get; set; }
        public PartKind Kind { get; set; }
        public long NodeCount { get; set; }
        public int PartsPerNode { get; set; }
        public long DetailCount { get; set; }
        public double UnitMassKg { get; set; }
        public double TotalMassKg { get; set; }

        internal static ProjectMaterialRow Create(Guid markId,string mark,Guid partId,string position,
            string name,string material,PartKind kind,long nodeCount,int partsPerNode,double unitMass)
        {
            long quantity=checked(nodeCount*partsPerNode);
            double mass=quantity*unitMass;
            Guard.Require(Guard.Positive(mass), "Неверная суммарная масса.");
            return new ProjectMaterialRow { MarkId=markId,Mark=mark,PartId=partId,Position=position,Name=name,
                Material=material,Kind=kind,NodeCount=nodeCount,PartsPerNode=partsPerNode,DetailCount=quantity,
                UnitMassKg=unitMass,TotalMassKg=mass };
        }
    }

    // Same lock protocol as ProjectStore, but only reads schema 2.
    // Migration is explicit: Save to a new path, never replace a schema-1 file.
    public sealed class AssemblyProjectStore
    {
        public string Path { get; private set; }
        private AssemblyProject current;
        public AssemblyProjectStore(string path) { Path=System.IO.Path.GetFullPath(path); }

        private AssemblyProject Read()
        {
            var p=Json.Read<AssemblyProject>(File.ReadAllText(Path,Encoding.UTF8));
            Guard.Require(p!=null,"Пустой проект.");
            p.Validate();
            return p;
        }

        public AssemblyProject Load()
        {
            using(new FileStream(Path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                current=Read();
                return Json.Copy(current);
            }
        }

        public AssemblyProject Save(AssemblyProject candidate)
        {
            Guard.Require(candidate!=null,"Не задан проект.");
            var next=Json.Copy(candidate);
            next.Validate();
            using(new FileStream(Path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                if(File.Exists(Path))
                {
                    var disk=Read();
                    if(current==null || disk.Id!=current.Id || disk.Version!=current.Version ||
                        next.Id!=current.Id || next.Version!=current.Version)
                        throw new IOException("Проект изменен или сохранена устаревшая копия. Загрузите проект заново.");
                }
                else if(current!=null || next.Version!=0)
                    throw new IOException("Нельзя создать проект из устаревшей версии.");
                next.Version=checked((current==null?0:current.Version)+1);
                string temp=Path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    byte[] bytes=new UTF8Encoding(false).GetBytes(Json.Write(next));
                    using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
                    if(File.Exists(Path)) File.Replace(temp,Path,Path+".bak"); else File.Move(temp,Path);
                    current=next;
                    return Json.Copy(next);
                }
                finally { if(File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
