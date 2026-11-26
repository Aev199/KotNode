using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace AutoCadMcp
{
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("ole32.dll")]
        public static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);
        [DllImport("oleaut32.dll")]
        public static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const int SW_RESTORE = 9;
        public const int SW_MAXIMIZE = 3;
        public const uint PW_RENDERFULLCONTENT = 2;
        public const uint BM_CLICK = 0x00F5;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_CLOSE = 0x0010;
        public const uint TCM_SETCURSEL = 0x130C;
        public const uint TCM_SETCURFOCUS = 0x1330;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public static object TryGetActiveObject(string progId)
        {
            if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
            if (GetActiveObject(ref clsid, IntPtr.Zero, out var obj) != 0) return null;
            return obj;
        }

        public static void ClickAt(int x, int y, bool right = false, bool doubleClick = false)
        {
            SetCursorPos(x, y);
            Thread.Sleep(50);
            uint down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
            uint up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            if (doubleClick)
            {
                Thread.Sleep(55);
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(35);
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            }
        }

        public static string GetTitle(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(128);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public sealed class WinInfo
        {
            public IntPtr Hwnd;
            public string Title;
            public string ClassName;
            public Rectangle Bounds;
        }

        // Returns visible top-level windows for pid in Z-order (topmost first).
        public static List<WinInfo> GetProcessWindows(int pid)
        {
            var list = new List<WinInfo>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid == (uint)pid && IsWindowVisible(h) && !IsIconic(h))
                {
                    if (GetWindowRect(h, out var r) && r.Right - r.Left >= 30 && r.Bottom - r.Top >= 30)
                    {
                        string cls = GetClass(h);
                        // Skip invisible tooltip/shadow helper windows
                        if (cls.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            cls == "tooltips_class32" || cls == "MSCTFIME UI")
                            return true;
                        list.Add(new WinInfo
                        {
                            Hwnd = h,
                            Title = GetTitle(h),
                            ClassName = cls,
                            Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
                        });
                    }
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    internal static class AutoCadBridge
    {
        static dynamic GetApp()
        {
            foreach (var id in new[] { "AutoCAD.Application.25.1", "AutoCAD.Application.25.0", "AutoCAD.Application.25", "AutoCAD.Application.24", "AutoCAD.Application" })
            {
                var obj = Native.TryGetActiveObject(id);
                if (obj != null) return obj;
            }
            return null;
        }

        static Process FindAcadProcess()
            => Process.GetProcessesByName("acad").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
               ?? Process.GetProcessesByName("acad").FirstOrDefault();

        static T WithTimeout<T>(Func<T> fn, int ms, T fallback)
        {
            try
            {
                var task = Task.Run(fn);
                return task.Wait(ms) ? task.Result : fallback;
            }
            catch { return fallback; }
        }

        public static string Launch(string acadPath, string dwgPath, int timeoutSec)
        {
            var app = GetApp();
            if (app == null)
            {
                if (string.IsNullOrWhiteSpace(acadPath))
                {
                    acadPath = @"C:\Program Files\Autodesk\AutoCAD 2026\acad.exe";
                    if (!File.Exists(acadPath))
                    {
                        var dir = Directory.GetDirectories(@"C:\Program Files\Autodesk", "AutoCAD *")
                            .OrderByDescending(d => d).FirstOrDefault(d => File.Exists(Path.Combine(d, "acad.exe")));
                        if (dir != null) acadPath = Path.Combine(dir, "acad.exe");
                    }
                }
                if (!File.Exists(acadPath)) throw new FileNotFoundException("AutoCAD executable not found: " + acadPath);
                var psi = new ProcessStartInfo(acadPath)
                {
                    Arguments = string.IsNullOrWhiteSpace(dwgPath) ? "/nologo" : "/nologo \"" + dwgPath + "\"",
                    UseShellExecute = true
                };
                Process.Start(psi);
                var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, timeoutSec));
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(1500);
                    app = GetApp();
                    if (app != null)
                    {
                        try
                        {
                            int cnt = (int)app.Documents.Count;
                            if (cnt == 0)
                            {
                                app.Documents.Add();
                                Thread.Sleep(1500);
                            }
                            var _ = (string)app.ActiveDocument.Name;
                            break;
                        }
                        catch { app = null; }
                    }
                }
                if (app == null) throw new TimeoutException("AutoCAD launched but COM ActiveDocument did not become ready within timeout.");
            }
            else if (!string.IsNullOrWhiteSpace(dwgPath) && File.Exists(dwgPath))
            {
                app.Documents.Open(dwgPath, false);
                Thread.Sleep(1000);
            }
            else
            {
                try
                {
                    if ((int)app.Documents.Count == 0)
                    {
                        app.Documents.Add();
                        Thread.Sleep(1200);
                    }
                }
                catch { }
            }

            var proc = FindAcadProcess();
            if (proc != null)
            {
                try { app.Visible = true; } catch { }
                var wins = Native.GetProcessWindows(proc.Id);
                var main = wins.OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height).FirstOrDefault();
                if (main != null)
                {
                    Native.ShowWindow(main.Hwnd, Native.SW_MAXIMIZE);
                    Native.SetForegroundWindow(main.Hwnd);
                }
            }
            string docName = WithTimeout(() => (string)app.ActiveDocument.Name, 2000, "");
            return JsonSerializer.Serialize(new
            {
                status = "ready",
                pid = proc?.Id ?? 0,
                windowTitle = proc?.MainWindowTitle ?? "",
                activeDocument = docName
            });
        }

        static void RetryCom(Action action, int attempts = 10, int delayMs = 600)
        {
            for (int i = 0; i < attempts; i++)
            {
                try { action(); return; }
                catch (COMException ex) when (i < attempts - 1 && (ex.HResult == unchecked((int)0x80010001) || ex.HResult == unchecked((int)0x8001010A)))
                {
                    Thread.Sleep(delayMs);
                }
            }
        }

        public static string Netload(string dllPath, int waitMs)
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
                throw new FileNotFoundException("Plugin DLL not found: " + dllPath);
            var app = GetApp() ?? throw new InvalidOperationException("AutoCAD is not running. Call autocad_launch first.");
            string escaped = Path.GetFullPath(dllPath).Replace("\\", "/");
            // Use AutoLISP (command "_.NETLOAD" ...) so AutoCAD NEVER opens the modal Open File dialog!
            string lisp = "(setvar \"SECURELOAD\" 0)(command \"_.NETLOAD\" \"" + escaped + "\") ";
            RetryCom(() => app.ActiveDocument.SendCommand(lisp));
            Thread.Sleep(Math.Max(1500, waitMs));
            string cmdNames = WithTimeout(() => Convert.ToString(app.ActiveDocument.GetVariable("CMDNAMES")) ?? "", 2500, "<busy/modal>");
            return JsonSerializer.Serialize(new { loaded = escaped, cmdNames });
        }

        public static string SendCommand(string command, int waitMs, bool expectModal)
        {
            var app = GetApp() ?? throw new InvalidOperationException("AutoCAD is not running. Call autocad_launch first.");
            string cmd = command ?? "";
            if (!cmd.EndsWith("\n") && !cmd.EndsWith(" ")) cmd += "\n";
            if (expectModal)
            {
                // AutoCAD COM SendCommand blocks until modal dialogs close; dispatch asynchronously when expecting a modal dialog.
                Task.Run(() => { try { RetryCom(() => app.ActiveDocument.SendCommand(cmd)); } catch { } });
                Thread.Sleep(Math.Max(1200, waitMs));
                return JsonSerializer.Serialize(new { sent = command, modal = true });
            }
            RetryCom(() => app.ActiveDocument.SendCommand(cmd));
            if (waitMs > 0) Thread.Sleep(waitMs);

            string cmdNames = WithTimeout(() => Convert.ToString(app.ActiveDocument.GetVariable("CMDNAMES")) ?? "", 2000, "<modal_or_busy>");
            int count = cmdNames == "<modal_or_busy>"
                ? -1
                : WithTimeout(() => (int)app.ActiveDocument.ModelSpace.Count, 2000, -1);
            return JsonSerializer.Serialize(new { sent = command, cmdNames, modelSpaceCount = count });
        }

        public static string GetState()
        {
            var app = GetApp() ?? throw new InvalidOperationException("AutoCAD is not running.");
            var proc = FindAcadProcess();
            var winTitles = proc != null
                ? Native.GetProcessWindows(proc.Id).Select(w => new { w.Title, w.ClassName, rect = $"{w.Bounds.Left},{w.Bounds.Top},{w.Bounds.Width}x{w.Bounds.Height}" }).ToList()
                : null;
            string name = WithTimeout(() => (string)app.ActiveDocument.Name, 2000, "<modal_or_busy>");
            if (name == "<modal_or_busy>")
                return JsonSerializer.Serialize(new { pid = proc?.Id ?? 0, modalOrBusy = true, windows = winTitles });

            var doc = app.ActiveDocument;
            string cmdNames = WithTimeout(() => Convert.ToString(doc.GetVariable("CMDNAMES")) ?? "", 1500, "");
            string insunits = WithTimeout(() => Convert.ToString(doc.GetVariable("INSUNITS")) ?? "", 1500, "");
            string viewSize = WithTimeout(() => Convert.ToString(doc.GetVariable("VIEWSIZE")) ?? "", 1500, "");
            int msCount = WithTimeout(() => (int)doc.ModelSpace.Count, 1500, -1);
            var layers = new List<string>();
            var blocks = new List<string>();
            WithTimeout(() => { foreach (dynamic l in doc.Layers) layers.Add((string)l.Name); return 0; }, 1500, 0);
            WithTimeout(() => { foreach (dynamic b in doc.Blocks) { string bn = (string)b.Name; if (!bn.StartsWith("*")) blocks.Add(bn); } return 0; }, 1500, 0);
            return JsonSerializer.Serialize(new
            {
                pid = proc?.Id ?? 0,
                document = name,
                cmdNames,
                insunits,
                viewSize,
                modelSpaceCount = msCount,
                layers,
                blocksCount = blocks.Count,
                windows = winTitles
            });
        }

        static Bitmap CaptureWindowPrintWindow(IntPtr hWnd, Rectangle bounds)
        {
            var bmp = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30, 30, 30));
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (!Native.PrintWindow(hWnd, hdc, Native.PW_RENDERFULLCONTENT))
                        Native.PrintWindow(hWnd, hdc, 0);
                }
                finally { g.ReleaseHdc(hdc); }
            }
            return bmp;
        }

        public static string Screenshot(string outputPath, bool fullScreen, string windowFilter)
        {
            Native.SetProcessDPIAware();
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = Path.Combine(Path.GetTempPath(), "autocad_mcp_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));

            var proc = FindAcadProcess();
            // 1. Specific window filter -> capture directly via PrintWindow (or crop docked palette from main window)
            if (!string.IsNullOrWhiteSpace(windowFilter) && proc != null)
            {
                var wins = Native.GetProcessWindows(proc.Id);
                var match = wins.FirstOrDefault(w => w.Title.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null)
                {
                    using (var bmp = CaptureWindowPrintWindow(match.Hwnd, match.Bounds))
                        bmp.Save(outputPath, ImageFormat.Png);
                    return JsonSerializer.Serialize(new { path = outputPath, method = "PrintWindow", window = match.Title, width = match.Bounds.Width, height = match.Bounds.Height });
                }
                // Try finding a docked palette/pane via UIA inside AutoCAD windows and cropping from main window
                foreach (var tw in AcadTopWindows())
                {
                    var pane = FindAnyByName(tw, windowFilter);
                    if (pane != null)
                    {
                        var pr = pane.Current.BoundingRectangle;
                        var main = wins.OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height).FirstOrDefault();
                        if (!pr.IsEmpty && pr.Width > 40 && pr.Height > 40 && main != null)
                        {
                            using (var full = CaptureWindowPrintWindow(main.Hwnd, main.Bounds))
                            {
                                int rx = Math.Max(0, (int)pr.Left - main.Bounds.Left);
                                int ry = Math.Max(0, (int)pr.Top - main.Bounds.Top);
                                int rw = Math.Min(full.Width - rx, (int)pr.Width);
                                int rh = Math.Min(full.Height - ry, (int)pr.Height);
                                if (rw > 20 && rh > 20)
                                {
                                    using (var crop = full.Clone(new Rectangle(rx, ry, rw, rh), full.PixelFormat))
                                        crop.Save(outputPath, ImageFormat.Png);
                                    return JsonSerializer.Serialize(new { path = outputPath, method = "DockedCrop", window = pane.Current.Name, width = rw, height = rh });
                                }
                            }
                        }
                    }
                }
                throw new InvalidOperationException("No visible AutoCAD window or docked pane matching: " + windowFilter);
            }

            // 2. Try direct desktop capture first (works on interactive desktop)
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                if (!fullScreen && proc != null)
                {
                    var main = Native.GetProcessWindows(proc.Id).OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height).FirstOrDefault();
                    if (main != null) bounds = main.Bounds;
                }
                using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    bmp.Save(outputPath, ImageFormat.Png);
                }
                return JsonSerializer.Serialize(new { path = outputPath, method = "CopyFromScreen", width = bounds.Width, height = bounds.Height });
            }
            catch
            {
                // 3. Composite all visible AutoCAD windows via PrintWindow (works even on non-input desktops!)
                if (proc == null) throw new InvalidOperationException("CopyFromScreen unavailable and AutoCAD is not running.");
                var wins = Native.GetProcessWindows(proc.Id);
                if (wins.Count == 0) throw new InvalidOperationException("No visible AutoCAD windows to capture.");

                int minX = wins.Min(w => w.Bounds.Left);
                int minY = wins.Min(w => w.Bounds.Top);
                int maxX = wins.Max(w => w.Bounds.Right);
                int maxY = wins.Max(w => w.Bounds.Bottom);
                int cw = Math.Min(3840, Math.Max(100, maxX - minX));
                int ch = Math.Min(2160, Math.Max(100, maxY - minY));

                // Draw bottom-to-top so floating palettes and modal dialogs overlay the main window
                wins.Reverse();
                using (var canvas = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.FromArgb(24, 24, 24));
                    foreach (var w in wins)
                    {
                        using (var wb = CaptureWindowPrintWindow(w.Hwnd, w.Bounds))
                            g.DrawImage(wb, w.Bounds.Left - minX, w.Bounds.Top - minY);
                    }
                    canvas.Save(outputPath, ImageFormat.Png);
                }
                return JsonSerializer.Serialize(new
                {
                    path = outputPath,
                    method = "CompositePrintWindow",
                    windowCount = wins.Count,
                    windows = wins.Select(w => w.Title).ToArray(),
                    width = cw,
                    height = ch
                });
            }
        }

        static List<AutomationElement> AcadTopWindows()
        {
            var proc = FindAcadProcess();
            if (proc == null) return new List<AutomationElement>();
            var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id);
            var col = AutomationElement.RootElement.FindAll(TreeScope.Children, cond);
            var res = new List<AutomationElement>();
            foreach (AutomationElement el in col) res.Add(el);
            return res;
        }

        public static string UiTree(string windowFilter, int maxDepth)
        {
            if (maxDepth <= 0) maxDepth = 5;
            var wins = AcadTopWindows();
            var list = new List<object>();
            foreach (var w in wins)
            {
                string wName = "";
                try { wName = w.Current.Name ?? ""; } catch { continue; }
                if (!string.IsNullOrWhiteSpace(windowFilter) && wName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var items = new List<object>();
                Walk(w, 0, maxDepth, items, 300);
                var rect = w.Current.BoundingRectangle;
                list.Add(new
                {
                    window = wName,
                    controlType = w.Current.ControlType.ProgrammaticName,
                    rect = FormatRect(rect),
                    controls = items
                });
            }
            return JsonSerializer.Serialize(list);
        }

        static string FormatRect(System.Windows.Rect r)
            => r.IsEmpty ? "" : $"{(int)r.Left},{(int)r.Top},{(int)r.Width}x{(int)r.Height}";

        static void Walk(AutomationElement el, int depth, int maxDepth, List<object> acc, int limit)
        {
            if (depth > maxDepth || acc.Count >= limit) return;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(el);
            while (child != null && acc.Count < limit)
            {
                try
                {
                    string name = child.Current.Name ?? "";
                    string type = child.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                    string autoId = child.Current.AutomationId ?? "";
                    var rect = child.Current.BoundingRectangle;
                    if (!string.IsNullOrWhiteSpace(name) || type == "Edit" || type == "ComboBox" || type == "DataGrid" || type == "TabItem" || type == "Button" || type == "TreeItem" || type == "ListItem" || type == "DataItem")
                    {
                        acc.Add(new { depth, type, name, autoId, rect = FormatRect(rect), enabled = child.Current.IsEnabled });
                    }
                    if (depth < maxDepth) Walk(child, depth + 1, maxDepth, acc, limit);
                }
                catch { }
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
            }
        }

        public static string UiClick(string name, string windowFilter, int? x, int? y, bool right, bool doubleClick, int? tabIndex = null)
        {
            Native.SetProcessDPIAware();
            if (x.HasValue && y.HasValue)
            {
                Native.ClickAt(x.Value, y.Value, right, doubleClick);
                Thread.Sleep(200);
                return JsonSerializer.Serialize(new { clicked = "coords", x = x.Value, y = y.Value });
            }
            if (string.IsNullOrWhiteSpace(name) && !tabIndex.HasValue)
                throw new ArgumentException("Provide 'name', 'tabIndex', or both 'x' and 'y'.");

            var topWins = AcadTopWindows();
            var seenWins = new List<string>();
            var searchRoots = new List<(AutomationElement Root, string Label)>();
            foreach (var w in topWins)
            {
                string wName = "";
                try { wName = w.Current.Name ?? ""; } catch { continue; }
                seenWins.Add(wName);
                if (string.IsNullOrWhiteSpace(windowFilter))
                {
                    searchRoots.Add((w, wName));
                }
                else if (wName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    searchRoots.Insert(0, (w, wName));
                }
                else
                {
                    var pane = FindAnyByName(w, windowFilter);
                    if (pane != null) searchRoots.Add((pane, pane.Current.Name ?? windowFilter));
                }
            }

            // Direct TabControl selection by 0-based tabIndex via Win32 TCM_SETCURFOCUS
            if (tabIndex.HasValue)
            {
                foreach (var (root, wName) in searchRoots)
                {
                    var tab = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                    if (tab == null) continue;
                    int tabHwnd = 0;
                    try { tabHwnd = tab.Current.NativeWindowHandle; } catch { }
                    if (tabHwnd != 0)
                    {
                        Native.SendMessage((IntPtr)tabHwnd, Native.TCM_SETCURFOCUS, (IntPtr)tabIndex.Value, IntPtr.Zero);
                        Thread.Sleep(300);
                        return JsonSerializer.Serialize(new { clicked = "tcm_setcurfocus_idx", tabIndex = tabIndex.Value, window = wName });
                    }
                }
                throw new InvalidOperationException("TabControl not found in matching windows: " + string.Join(" | ", seenWins));
            }

            foreach (var (root, wName) in searchRoots)
            {
                var found = FindByName(root, name);
                if (found == null) continue;

                string foundName = found.Current.Name ?? name;
                string ctype = found.Current.ControlType.ProgrammaticName;

                // 0. WinForms/Win32 TabItem: switch deterministically via TCM_SETCURFOCUS(tabIndex)
                if (!right && !doubleClick && ctype == ControlType.TabItem.ProgrammaticName)
                {
                    var parent = TreeWalker.ControlViewWalker.GetParent(found);
                    int tabHwnd = 0;
                    try { tabHwnd = parent != null ? parent.Current.NativeWindowHandle : 0; } catch { }
                    int tabIdx = -1;
                    if (parent != null)
                    {
                        // Count only header TabItems (NativeWindowHandle == 0); skip active TabPage pane (NativeWindowHandle != 0)
                        int i = 0;
                        var sib = TreeWalker.ControlViewWalker.GetFirstChild(parent);
                        while (sib != null)
                        {
                            if (sib.Current.ControlType == ControlType.TabItem && sib.Current.NativeWindowHandle == 0)
                            {
                                if (Automation.Compare(sib, found)) { tabIdx = i; break; }
                                i++;
                            }
                            sib = TreeWalker.ControlViewWalker.GetNextSibling(sib);
                        }
                    }
                    if (tabHwnd != 0 && tabIdx >= 0)
                    {
                        Native.SendMessage((IntPtr)tabHwnd, Native.TCM_SETCURFOCUS, (IntPtr)tabIdx, IntPtr.Zero);
                        Thread.Sleep(250);
                        return JsonSerializer.Serialize(new { clicked = "tcm_setcurfocus", name = foundName, tabIndex = tabIdx, window = wName });
                    }
                    if (found.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var tabSel))
                    {
                        ((SelectionItemPattern)tabSel).Select();
                        Thread.Sleep(250);
                        return JsonSerializer.Serialize(new { clicked = "select_tab", name = foundName, window = wName });
                    }
                }

                // 1. ListItem / TreeItem: SelectionItemPattern is non-blocking and works on any desktop
                if (!right && !doubleClick && found.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
                {
                    ((SelectionItemPattern)sel).Select();
                    Thread.Sleep(250);
                    return JsonSerializer.Serialize(new { clicked = "select", name = foundName, window = wName });
                }

                // 2. InvokePattern on background task with timeout: calls WinForms Button.PerformClick() directly
                // (works even when unfocused / non-input desktop AND never blocks MCP when opening ShowModalDialog!)
                if (!right && !doubleClick && found.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                {
                    var t = Task.Run(() => { try { ((InvokePattern)inv).Invoke(); } catch { } });
                    bool finished = t.Wait(700);
                    Thread.Sleep(250);
                    return JsonSerializer.Serialize(new { clicked = finished ? "invoke" : "invoke_async_modal", name = foundName, window = wName });
                }

                // 3. Fallback: PostMessage(BM_CLICK) or WM_LBUTTONDOWN/UP or mouse click
                int hwndInt = 0;
                try { hwndInt = found.Current.NativeWindowHandle; } catch { }
                if (!right && !doubleClick && hwndInt != 0 && ctype == ControlType.Button.ProgrammaticName)
                {
                    Native.PostMessage((IntPtr)hwndInt, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(350);
                    return JsonSerializer.Serialize(new { clicked = "post_bm_click", name = foundName, hwnd = hwndInt, window = wName });
                }

                var rect = found.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                {
                    int cx = (int)(rect.Left + rect.Width / 2);
                    int cy = (int)(rect.Top + rect.Height / 2);
                    Native.ClickAt(cx, cy, right, doubleClick);
                    Thread.Sleep(250);
                    return JsonSerializer.Serialize(new { clicked = "mouse", name = foundName, x = cx, y = cy, window = wName });
                }
            }
            throw new InvalidOperationException("UI control not found: '" + name + "'. Visible windows: " + string.Join(" | ", seenWins));
        }

        static bool IsClickableType(AutomationElement el)
        {
            string t = el.Current.ControlType.ProgrammaticName;
            return t == ControlType.Button.ProgrammaticName || t == ControlType.TabItem.ProgrammaticName ||
                   t == ControlType.MenuItem.ProgrammaticName || t == ControlType.ListItem.ProgrammaticName ||
                   t == ControlType.TreeItem.ProgrammaticName || t == ControlType.CheckBox.ProgrammaticName ||
                   t == ControlType.Edit.ProgrammaticName || t == ControlType.DataItem.ProgrammaticName;
        }

        static AutomationElement FindByName(AutomationElement root, string name)
        {
            // 1. Explicitly check all TabControl -> TabItem children first (SysTabControl32 TabItems can be missed by FindAll)
            try
            {
                var tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                foreach (AutomationElement tab in tabs)
                {
                    var child = TreeWalker.RawViewWalker.GetFirstChild(tab);
                    while (child != null)
                    {
                        try
                        {
                            if (child.Current.ControlType == ControlType.TabItem)
                            {
                                string tn = (child.Current.Name ?? "").Trim();
                                if (string.Equals(tn, name, StringComparison.OrdinalIgnoreCase)) return child;
                            }
                        }
                        catch { }
                        try { child = TreeWalker.RawViewWalker.GetNextSibling(child); } catch { break; }
                    }
                }
            }
            catch { }

            // 2. Fast native UIA exact name search across ALL depths (reaches docked palettes at depth 15+)
            try
            {
                var exactAll = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
                foreach (AutomationElement el in exactAll)
                {
                    try { if (IsClickableType(el)) return el; } catch { }
                }
                if (exactAll.Count > 0) return exactAll[0];
            }
            catch { }

            // 3. Fallback: case-insensitive exact then substring across clickable controls
            try
            {
                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
                var all = root.FindAll(TreeScope.Descendants, cond);
                AutomationElement partial = null;
                foreach (AutomationElement el in all)
                {
                    try
                    {
                        string cn = (el.Current.Name ?? "").Trim();
                        if (string.Equals(cn, name, StringComparison.OrdinalIgnoreCase)) return el;
                        if (partial == null && cn.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) partial = el;
                    }
                    catch { }
                }
                return partial;
            }
            catch { return null; }
        }

        static AutomationElement FindAnyByName(AutomationElement root, string nameSubstr)
        {
            try
            {
                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
                var all = root.FindAll(TreeScope.Descendants, cond);
                foreach (AutomationElement el in all)
                {
                    try
                    {
                        string cn = el.Current.Name ?? "";
                        if (cn.IndexOf(nameSubstr, StringComparison.OrdinalIgnoreCase) >= 0) return el;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public static string UiType(string text, string controlName, string windowFilter, int? gridRow = null, int? gridCol = null)
        {
            if (gridRow.HasValue && gridCol.HasValue)
            {
                foreach (var w in AcadTopWindows())
                {
                    string wName = "";
                    try { wName = w.Current.Name ?? ""; } catch { continue; }
                    if (!string.IsNullOrWhiteSpace(windowFilter) && wName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var grid = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Table))
                               ?? w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
                    if (grid == null) continue;
                    // Walk DataGridView rows (DataItem / Custom) and cells
                    var rows = grid.FindAll(TreeScope.Children, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
                    var dataRows = new List<AutomationElement>();
                    foreach (AutomationElement r in rows)
                    {
                        string rn = r.Current.Name ?? "";
                        if (rn.IndexOf("Top Row", StringComparison.OrdinalIgnoreCase) >= 0 || rn.IndexOf("Заголовок", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        dataRows.Add(r);
                    }
                    if (gridRow.Value >= 0 && gridRow.Value < dataRows.Count)
                    {
                        var cells = dataRows[gridRow.Value].FindAll(TreeScope.Children, Condition.TrueCondition);
                        if (gridCol.Value >= 0 && gridCol.Value < cells.Count)
                        {
                            var cell = cells[gridCol.Value];
                            if (cell.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                            {
                                ((ValuePattern)vp).SetValue(text ?? "");
                                Thread.Sleep(150);
                                return JsonSerializer.Serialize(new { typed = "gridCell", row = gridRow.Value, col = gridCol.Value, cellName = cell.Current.Name, value = text, window = wName });
                            }
                        }
                    }
                }
                throw new InvalidOperationException($"DataGridView cell ({gridRow},{gridCol}) not found or not editable in '{windowFilter}'.");
            }
            if (!string.IsNullOrWhiteSpace(controlName))
            {
                foreach (var w in AcadTopWindows())
                {
                    string wName = "";
                    try { wName = w.Current.Name ?? ""; } catch { continue; }
                    if (!string.IsNullOrWhiteSpace(windowFilter) && wName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var found = FindByName(w, controlName);
                    if (found != null && found.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                    {
                        ((ValuePattern)vp).SetValue(text ?? "");
                        return JsonSerializer.Serialize(new { typed = "valuePattern", control = found.Current.Name, value = text });
                    }
                }
                throw new InvalidOperationException("Editable control not found: " + controlName);
            }
            var proc = FindAcadProcess();
            if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
                Native.SetForegroundWindow(proc.MainWindowHandle);
            Thread.Sleep(100);
            SendKeys.SendWait(text ?? "");
            Thread.Sleep(150);
            return JsonSerializer.Serialize(new { typed = "sendKeys", value = text });
        }

        public static string Close(bool force)
        {
            var proc = FindAcadProcess();
            if (proc == null) return JsonSerializer.Serialize(new { status = "not_running" });
            if (force)
            {
                proc.Kill();
                proc.WaitForExit(5000);
                return JsonSerializer.Serialize(new { status = "killed" });
            }
            try
            {
                var app = GetApp();
                app?.Quit();
            }
            catch { }
            return JsonSerializer.Serialize(new { status = "quit_sent" });
        }
    }

    internal static class Program
    {
        static readonly JsonArray Tools = new JsonArray
        {
            Tool("autocad_launch", "Launch AutoCAD GUI (or attach to already running AutoCAD via COM) and optionally open a DWG file.",
                new JsonObject
                {
                    ["acadPath"] = Prop("string", "Optional path to acad.exe (auto-detects AutoCAD 2026 if omitted)"),
                    ["dwgPath"] = Prop("string", "Optional DWG file path to open"),
                    ["timeoutSec"] = Prop("integer", "Startup wait timeout in seconds (default 60)")
                }),
            Tool("autocad_netload", "Silently load a .NET plugin DLL into running AutoCAD via AutoLISP NETLOAD without opening any file dialog.",
                new JsonObject
                {
                    ["dllPath"] = Prop("string", "Full path to the .NET AutoCAD plugin DLL"),
                    ["waitMs"] = Prop("integer", "Milliseconds to wait after loading (default 1500)")
                }, "dllPath"),
            Tool("autocad_send_command", "Send a command string to AutoCAD ActiveDocument via COM SendCommand.",
                new JsonObject
                {
                    ["command"] = Prop("string", "Command string to execute in AutoCAD command line"),
                    ["waitMs"] = Prop("integer", "Milliseconds to wait after sending command (default 600)"),
                    ["expectModal"] = Prop("boolean", "Set true if the command opens a modal dialog so COM status polling is skipped")
                }, "command"),
            Tool("autocad_get_state", "Query running AutoCAD document state (document name, CMDNAMES, INSUNITS, VIEWSIZE, ModelSpace count, layers, blocks, open windows).",
                new JsonObject()),
            Tool("autocad_screenshot", "Take a PNG screenshot of the AutoCAD GUI window (or specific dialog/palette via windowFilter) and return its file path.",
                new JsonObject
                {
                    ["outputPath"] = Prop("string", "Optional output PNG path"),
                    ["fullScreen"] = Prop("boolean", "Capture full primary screen / all AutoCAD windows (default true)"),
                    ["windowFilter"] = Prop("string", "Optional substring filter to capture a specific palette or modal dialog cleanly")
                }),
            Tool("autocad_ui_tree", "Inspect visible AutoCAD windows, palettes, modal dialogs, tabs, buttons, and inputs via Windows UI Automation.",
                new JsonObject
                {
                    ["windowFilter"] = Prop("string", "Optional substring filter for window title"),
                    ["maxDepth"] = Prop("integer", "Max UI tree walk depth (default 5)")
                }),
            Tool("autocad_ui_click", "Click a named UI control (button, tab, list item, tree item) via UI Automation / Win32 without blocking on modal dialogs, switch a TabControl by 0-based tabIndex, or click at (x, y).",
                new JsonObject
                {
                    ["name"] = Prop("string", "Control name (button/tab/item text)"),
                    ["windowFilter"] = Prop("string", "Optional substring filter for window title or docked palette name"),
                    ["tabIndex"] = Prop("integer", "Optional 0-based TabControl tab index to select directly via TCM_SETCURFOCUS"),
                    ["x"] = Prop("integer", "Optional screen X coordinate"),
                    ["y"] = Prop("integer", "Optional screen Y coordinate"),
                    ["right"] = Prop("boolean", "Right-click instead of left-click"),
                    ["doubleClick"] = Prop("boolean", "Double-click")
                }),
            Tool("autocad_ui_type", "Type text or SendKeys sequences (e.g. {ENTER}, {ESC}) into AutoCAD, set value of a named UI control, or edit a DataGridView cell (gridRow, gridCol) via ValuePattern.",
                new JsonObject
                {
                    ["text"] = Prop("string", "Text or SendKeys string to type"),
                    ["controlName"] = Prop("string", "Optional control name to set via ValuePattern"),
                    ["windowFilter"] = Prop("string", "Optional substring filter for window title"),
                    ["gridRow"] = Prop("integer", "Optional 0-based DataGridView data row index"),
                    ["gridCol"] = Prop("integer", "Optional 0-based DataGridView column index")
                }, "text"),
            Tool("autocad_close", "Close running AutoCAD GUI.",
                new JsonObject
                {
                    ["force"] = Prop("boolean", "Force kill process if true (skips save prompts)")
                })
        };

        static JsonObject Prop(string type, string desc)
            => new JsonObject { ["type"] = type, ["description"] = desc };

        static JsonObject Tool(string name, string desc, JsonObject props, params string[] required)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required != null && required.Length > 0)
            {
                var req = new JsonArray();
                foreach (var r in required) req.Add(r);
                schema["required"] = req;
            }
            return new JsonObject { ["name"] = name, ["description"] = desc, ["inputSchema"] = schema };
        }

        [STAThread]
        static void Main()
        {
            Native.SetProcessDPIAware();
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode req;
                try { req = JsonNode.Parse(line); }
                catch (Exception ex) { Console.Error.WriteLine("PARSE_ERR: " + ex.Message + " | LINE=" + line); continue; }
                var id = req["id"]?.DeepClone();
                string method = req["method"]?.GetValue<string>() ?? "";
                if (id == null) continue;

                try
                {
                    JsonNode result;
                    if (method == "initialize")
                    {
                        result = new JsonObject
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "autocad-gui-mcp", ["version"] = "1.1.0" }
                        };
                    }
                    else if (method == "ping")
                    {
                        result = new JsonObject();
                    }
                    else if (method == "tools/list")
                    {
                        result = new JsonObject { ["tools"] = Tools.DeepClone() };
                    }
                    else if (method == "tools/call")
                    {
                        var p = req["params"]?.AsObject();
                        string name = p?["name"]?.GetValue<string>() ?? "";
                        var args = p?["arguments"]?.AsObject() ?? new JsonObject();
                        string text = Dispatch(name, args);
                        result = new JsonObject
                        {
                            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
                            ["isError"] = false
                        };
                    }
                    else
                    {
                        WriteError(id, -32601, "Method not found: " + method);
                        continue;
                    }
                    WriteResponse(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
                }
                catch (Exception ex)
                {
                    if (method == "tools/call")
                    {
                        WriteResponse(new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id,
                            ["result"] = new JsonObject
                            {
                                ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "ERROR: " + ex.Message } },
                                ["isError"] = true
                            }
                        });
                    }
                    else WriteError(id, -32603, ex.Message);
                }
            }
        }

        static string Str(JsonObject o, string k) => o.TryGetPropertyValue(k, out var n) && n != null ? n.GetValue<string>() : null;
        static int Int(JsonObject o, string k, int def) => o.TryGetPropertyValue(k, out var n) && n != null ? n.GetValue<int>() : def;
        static int? IntOpt(JsonObject o, string k) => o.TryGetPropertyValue(k, out var n) && n != null ? n.GetValue<int>() : (int?)null;
        static bool Bool(JsonObject o, string k, bool def = false) => o.TryGetPropertyValue(k, out var n) && n != null ? n.GetValue<bool>() : def;

        static string Dispatch(string name, JsonObject args)
        {
            switch (name)
            {
                case "autocad_launch": return AutoCadBridge.Launch(Str(args, "acadPath"), Str(args, "dwgPath"), Int(args, "timeoutSec", 60));
                case "autocad_netload": return AutoCadBridge.Netload(Str(args, "dllPath"), Int(args, "waitMs", 1500));
                case "autocad_send_command": return AutoCadBridge.SendCommand(Str(args, "command"), Int(args, "waitMs", 600), Bool(args, "expectModal"));
                case "autocad_get_state": return AutoCadBridge.GetState();
                case "autocad_screenshot": return AutoCadBridge.Screenshot(Str(args, "outputPath"), Bool(args, "fullScreen", true), Str(args, "windowFilter"));
                case "autocad_ui_tree": return AutoCadBridge.UiTree(Str(args, "windowFilter"), Int(args, "maxDepth", 5));
                case "autocad_ui_click": return AutoCadBridge.UiClick(Str(args, "name"), Str(args, "windowFilter"), IntOpt(args, "x"), IntOpt(args, "y"), Bool(args, "right"), Bool(args, "doubleClick"), IntOpt(args, "tabIndex"));
                case "autocad_ui_type": return AutoCadBridge.UiType(Str(args, "text"), Str(args, "controlName"), Str(args, "windowFilter"), IntOpt(args, "gridRow"), IntOpt(args, "gridCol"));
                case "autocad_close": return AutoCadBridge.Close(Bool(args, "force"));
                default: throw new InvalidOperationException("Unknown tool: " + name);
            }
        }

        static void WriteError(JsonNode id, int code, string msg)
            => WriteResponse(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = msg } });

        static void WriteResponse(JsonObject obj)
        {
            Console.Out.WriteLine(obj.ToJsonString());
            Console.Out.Flush();
        }
    }
}
