using System;
using AcApp=Autodesk.AutoCAD.ApplicationServices.Application;

namespace KotNode.AutoCAD
{
    internal static class PaletteCommands
    {
        internal static void Queue(string command)
        {
            var doc=AcApp.DocumentManager.MdiActiveDocument;
            if(doc==null)return;
            // Queue, rather than execute inside the modeless palette callback.
            // The drawing must own input before a command requests an entity or dialog.
            Diagnostics.Step("Палитра: передача фокуса чертежу");
            bool focused=doc.Window.Focus();
            Diagnostics.Step("Палитра: отправка "+command+"; фокус="+focused);
            doc.SendStringToExecute(command+"\n",true,false,false);
        }
    }
}
