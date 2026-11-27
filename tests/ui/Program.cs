using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using KotNode.AutoCAD;

static class Program
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS palette: "+message);}
    [STAThread] static int Main()
    {
        // A synchronous native UI stall must fail CI instead of hanging the job.
        Task.Delay(30000).ContinueWith(_=>{Console.Error.WriteLine("Palette smoke timeout");Environment.Exit(2);});
        int result=0;
        using(var form=new Form{ClientSize=new Size(400,200)})
        using(var button=new PaletteButton{Text="Ещё",Size=new Size(140,40),Location=new Point(10,10)})
        using(var menu=new ContextMenuStrip())
        {
            form.Controls.Add(button);menu.Items.Add("Параметры / формулы");
            int clicks=0;button.Click+=(s,e)=>clicks++;
            form.Shown+=(s,e)=>form.BeginInvoke(new Action(()=>{
                try
                {
                    Check(!typeof(Button).IsAssignableFrom(button.GetType()),"palette action bypasses WinForms Button native path");
                    Cursor.Position=button.PointToScreen(new Point(10,10));
                    for(int i=0;i<20;i++)
                    {
                        SendMessage(button.Handle,0x201,new IntPtr(1),new IntPtr(10|(10<<16)));
                        SendMessage(button.Handle,0x202,IntPtr.Zero,new IntPtr(10|(10<<16)));
                        Application.DoEvents();
                    }
                    Check(clicks==20&&!button.Checked,"repeated native mouse clicks dispatch once without toggling");
                    button.Focus();
                    SendMessage(button.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero);
                    SendMessage(button.Handle,0x101,new IntPtr((int)Keys.Enter),IntPtr.Zero);
                    Check(clicks==21&&!button.Checked,"Enter activates palette action once");
                    SendMessage(button.Handle,0x100,new IntPtr((int)Keys.Space),IntPtr.Zero);
                    SendMessage(button.Handle,0x101,new IntPtr((int)Keys.Space),IntPtr.Zero);
                    Check(clicks==22&&!button.Checked,"Space activates palette action once");
                    button.Click+=(a,b)=>menu.Show(button,new Point(0,button.Height));
                    SendMessage(button.Handle,0x201,new IntPtr(1),new IntPtr(10|(10<<16)));
                    SendMessage(button.Handle,0x202,IntPtr.Zero,new IntPtr(10|(10<<16)));
                    Application.DoEvents();
                    Check(menu.Visible&&clicks==23,"More popup opens from the palette control without a model");
                    menu.Close();
                }
                catch(Exception error){Console.Error.WriteLine(error);result=1;}
                finally{form.Close();}
            }));
            Application.Run(form);
        }
        return result;
    }
}
