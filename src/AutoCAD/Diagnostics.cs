using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace KotNode.AutoCAD
{
    internal static class Diagnostics
    {
        static readonly List<string> steps=new List<string>();
        static string liveTrace;
        internal static string Stage { get; private set; }
        internal static string LastLog { get; private set; }
        internal static string Version { get { return typeof(Diagnostics).Assembly.GetName().Version.ToString(); } }
        internal static void Begin(string command)
        {liveTrace=null;steps.Clear();Step("Команда: "+command);}
        internal static void Step(string stage)
        {
            Stage=stage;var line=DateTimeOffset.Now.ToString("O")+" "+stage;
            steps.Add(line);if(steps.Count>200)steps.RemoveAt(0);
            if(liveTrace!=null)try{File.AppendAllText(liveTrace,line+Environment.NewLine,new UTF8Encoding(false));}catch{ }
        }
        // Persist before entering host/UI calls, so a forced termination preserves
        // the last checkpoint even when no exception can be handled.
        internal static string EnableLiveTrace(string command,string directory=null)
        {
            try
            {
                directory=directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KotNode","Logs");
                Directory.CreateDirectory(directory);
                var path=Path.Combine(directory,"trace-"+command+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".log");
                File.WriteAllText(path,"KotNode "+Version+Environment.NewLine+string.Join(Environment.NewLine,steps)+Environment.NewLine,new UTF8Encoding(false));
                liveTrace=path;LastLog=path;return path;
            }
            catch{liveTrace=null;return null;}
        }
        internal static void EndLiveTrace()
        {Step("Команда завершена");liveTrace=null;}
        internal static string Write(Exception exception,string directory=null)
        {
            try
            {
                directory=directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KotNode","Logs");
                Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,"error-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".log");
                var body=new StringBuilder().AppendLine("KotNode "+Version).AppendLine("Assembly: "+typeof(Diagnostics).Assembly.Location).AppendLine("Runtime: "+Environment.Version).AppendLine("OS: "+Environment.OSVersion).AppendLine("Stage: "+Stage).AppendLine("Steps:").AppendLine(string.Join(Environment.NewLine,steps)).AppendLine("Exception:").AppendLine(exception.ToString());
                File.WriteAllText(path,body.ToString(),new UTF8Encoding(true));LastLog=path;return path;
            }
            catch(Exception logError)
            {return "Журнал не записан: "+logError.GetType().Name+": "+logError.Message;}
        }
    }
}
