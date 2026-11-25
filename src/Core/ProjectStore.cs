using System;
using System.IO;
using System.Text;

namespace KotNode.Core
{
    public sealed class ProjectStore
    {
        public string Path { get; private set; }
        public Project Current { get; private set; }
        public ProjectStore(string path) { Path = System.IO.Path.GetFullPath(path); }
        public Project Load()
        {
            using (new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var p = Read(); p.Validate(); Current = p; return Json.Copy(p);
            }
        }
        private Project Read() { return Json.Read<Project>(File.ReadAllText(Path, Encoding.UTF8)); }
        public Project Save(Project candidate)
        {
            candidate.Validate();
            using (new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (File.Exists(Path))
                {
                    var disk = Read(); disk.Validate();
                    if (Current == null || disk.Id != Current.Id || disk.Version != Current.Version) throw new IOException("Проект изменен другим пользователем. Обновите проект и повторите изменение.");
                }
                else if (Current != null) throw new IOException("Файл проекта исчез. Сохранение остановлено.");
                if (Current != null && candidate.Id != Current.Id) throw new IOException("Нельзя заменить идентификатор проекта.");
                var next = Json.Copy(candidate); next.Version = checked((Current == null ? 0 : Current.Version) + 1);
                string temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(Json.Write(next));
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    if (File.Exists(Path)) File.Replace(temp, Path, Path + ".bak"); else File.Move(temp, Path);
                    Current = next; return Json.Copy(next);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
