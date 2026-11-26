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
using System.Windows.Automation;
using System.Windows.Forms;

namespace AutoCadMcp
{
    internal static class Native
    {
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

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const int SW_RESTORE = 9;
        public const int SW_MAXIMIZE = 3;

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
            Thread.Sleep(60);
            uint down = right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
            uint up = right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            if (doubleClick)
            {
                Thread.Sleep(60);
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(35);
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            }
        }
    }

    internal static class AutoCadBridge
    {
        static dynamic GetApp()
        {
            foreach (var id in new[] { "AutoCAD.Application.25.1", "AutoCAD.Application.25", "AutoCAD.Application.24", "AutoCAD.Application" })
            {
                var obj = Native.TryGetActiveObject(id);
                if (obj != null) return obj;
            }
            return null;
        }

        static Process FindAcadProcess()
            => Process.GetProcessesByName("acad").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
               ?? Process.GetProcessesByName("acad").FirstOrDefault();

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
                        try { var _ = (string)app.ActiveDocument.Name; break; }
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
            var proc = FindAcadProcess();
            if (proc != null && proc.MainWindowHandle != IntPtr.Zero)
            {
                Native.ShowWindow(proc.MainWindowHandle, Native.SW_RESTORE);
                Native.SetForegroundWindow(proc.MainWindowHandle);
            }
            string docName = "";
            try { docName = (string)app.ActiveDocument.Name; } catch { }
            return JsonSerializer.Serialize(new
            {
                status = "ready",
                pid = proc?.Id ?? 0,
                windowTitle = proc?.MainWindowTitle ?? "",
                activeDocument = docName
            });
        }

        public static string SendCommand(string command, int waitMs)
        {
            var app = GetApp() ?? throw new InvalidOperationException("AutoCAD is not running. Call autocad_launch first.");
            string cmd = command ?? "";
            if (!cmd.EndsWith("\n") && !cmd.EndsWith(" ")) cmd += "\n";
            app.ActiveDocument.SendCommand(cmd);
            if (waitMs > 0) Thread.Sleep(waitMs);
            string cmdNames = "";
            int count = -1;
            try { cmdNames = Convert.ToString(app.ActiveDocument.GetVariable("CMDNAMES")) ?? ""; } catch { }
            try { count = (int)app.ActiveDocument.ModelSpace.Count; } catch { }
            return JsonSerializer.Serialize(new { sent = command, cmdNames, modelSpaceCount = count });
        }

        public static string GetState()
        {
            var app = GetApp() ?? throw new InvalidOperationException("AutoCAD is not running.");
            var doc = app.ActiveDocument;
            string name = ""; string cmdNames = ""; object insunits = null; object viewSize = null;
            int msCount = -1;
            var layers = new List<string>();
            var blocks = new List<string>();
            try { name = (string)doc.Name; } catch { }
            try { cmdNames = Convert.ToString(doc.GetVariable("CMDNAMES")) ?? ""; } catch { }
            try { insunits = doc.GetVariable("INSUNITS"); } catch { }
            try { viewSize = doc.GetVariable("VIEWSIZE"); } catch { }
            try { msCount = (int)doc.ModelSpace.Count; } catch { }
            try { foreach (dynamic l in doc.Layers) layers.Add((string)l.Name); } catch { }
            try { foreach (dynamic b in doc.Blocks) { string bn = (string)b.Name; if (!bn.StartsWith("*")) blocks.Add(bn); } } catch { }
            var proc = FindAcadProcess();
            return JsonSerializer.Serialize(new
            {
                pid = proc?.Id ?? 0,
                windowTitle = proc?.MainWindowTitle ?? "",
                document = name,
                cmdNames,
                insunits = insunits?.ToString(),
                viewSize = viewSize?.ToString(),
                modelSpaceCount = msCount,
                layers,
                blocksCount = blocks.Count
            });
        }

        public static string Screenshot(string outputPath, bool fullScreen)
        {
            Native.SetProcessDPIAware();
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = Path.Combine(Path.GetTempPath(), "autocad_mcp_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            Rectangle bounds;
            var proc = FindAcadProcess();
            if (!fullScreen && proc != null && proc.MainWindowHandle != IntPtr.Zero && Native.GetWindowRect(proc.MainWindowHandle, out var r) && r.Right > r.Left && r.Bottom > r.Top)
            {
                Native.SetForegroundWindow(proc.MainWindowHandle);
                Thread.Sleep(150);
                bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }
            else
            {
                bounds = Screen.PrimaryScreen.Bounds;
            }
            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                bmp.Save(outputPath, ImageFormat.Png);
            }
            return JsonSerializer.Serialize(new { path = outputPath, width = bounds.Width, height = bounds.Height, left = bounds.Left, top = bounds.Top });
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
            if (maxDepth <= 0) maxDepth = 4;
            var wins = AcadTopWindows();
            var list = new List<object>();
            foreach (var w in wins)
            {
                string wName = w.Current.Name ?? "";
                if (!string.IsNullOrWhiteSpace(windowFilter) && wName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var items = new List<object>();
                Walk(w, 0, maxDepth, items, 250);
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
                    if (!string.IsNullOrWhiteSpace(name) || type == "Edit" || type == "ComboBox" || type == "DataGrid" || type == "TabItem" || type == "Button" || type == "TreeItem")
                    {
                        acc.Add(new { depth, type, name, autoId, rect = FormatRect(rect), enabled = child.Current.IsEnabled });
                    }
                    if (depth < maxDepth) Walk(child, depth + 1, maxDepth, acc, limit);
                }
                catch { }
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
            }
        }

        public static string UiClick(string name, string windowFilter, int? x, int? y, bool right, bool doubleClick)
        {
            Native.SetProcessDPIAware();
            if (x.HasValue && y.HasValue)
            {
                Native.ClickAt(x.Value, y.Value, right, doubleClick);
                Thread.Sleep(200);
                return JsonSerializer.Serialize(new { clicked = "coords", x = x.Value, y = y.Value });
            }
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Provide either 'name' or both 'x' and 'y'.");

            foreach (var w in AcadTopWindows())
            {
                string wName = w.Current.Name ?? "";
                if (!string.IsNullOrWhiteSpace(windowFilter) && wName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var found = FindByName(w, name, 8);
                if (found == null) continue;
                try { w.SetFocus(); } catch { }
                if (!right && !doubleClick)
                {
                    if (found.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
                    {
                        ((InvokePattern)inv).Invoke();
                        Thread.Sleep(250);
                        return JsonSerializer.Serialize(new { clicked = "invoke", name = found.Current.Name, window = wName });
                    }
                    if (found.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
                    {
                        ((SelectionItemPattern)sel).Select();
                        Thread.Sleep(250);
                        return JsonSerializer.Serialize(new { clicked = "select", name = found.Current.Name, window = wName });
                    }
                }
                var rect = found.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                {
                    int cx = (int)(rect.Left + rect.Width / 2);
                    int cy = (int)(rect.Top + rect.Height / 2);
                    Native.ClickAt(cx, cy, right, doubleClick);
                    Thread.Sleep(250);
                    return JsonSerializer.Serialize(new { clicked = "mouse", name = found.Current.Name, x = cx, y = cy, window = wName });
                }
            }
            throw new InvalidOperationException("UI control not found: " + name);
        }

        static AutomationElement FindByName(AutomationElement root, string name, int maxDepth)
        {
            if (maxDepth < 0) return null;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(root);
            while (child != null)
            {
                try
                {
                    string cn = child.Current.Name ?? "";
                    if (string.Equals(cn, name, StringComparison.OrdinalIgnoreCase) ||
                        cn.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string t = child.Current.ControlType.ProgrammaticName;
                        if (t == ControlType.Button.ProgrammaticName || t == ControlType.TabItem.ProgrammaticName ||
                            t == ControlType.MenuItem.ProgrammaticName || t == ControlType.ListItem.ProgrammaticName ||
                            t == ControlType.TreeItem.ProgrammaticName || t == ControlType.CheckBox.ProgrammaticName ||
                            t == ControlType.Edit.ProgrammaticName)
                            return child;
                    }
                    var deeper = FindByName(child, name, maxDepth - 1);
                    if (deeper != null) return deeper;
                }
                catch { }
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
            }
            return null;
        }

        public static string UiType(string text, string controlName, string windowFilter)
        {
            if (!string.IsNullOrWhiteSpace(controlName))
            {
                foreach (var w in AcadTopWindows())
                {
                    string wName = w.Current.Name ?? "";
                    if (!string.IsNullOrWhiteSpace(windowFilter) && wName.IndexOf(windowFilter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var found = FindByName(w, controlName, 8);
                    if (found != null && found.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                    {
                        ((ValuePattern)vp).SetValue(text ?? "");
                        return JsonSerializer.Serialize(new { typed = "valuePattern", control = found.Current.Name, value = text });
                    }
                }
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
            Tool("autocad_send_command", "Send a command string to AutoCAD ActiveDocument via COM SendCommand.",
                new JsonObject
                {
                    ["command"] = Prop("string", "Command string to execute in AutoCAD command line"),
                    ["waitMs"] = Prop("integer", "Milliseconds to wait after sending command (default 600)")
                }, "command"),
            Tool("autocad_get_state", "Query running AutoCAD document state (document name, CMDNAMES, INSUNITS, VIEWSIZE, ModelSpace count, layers, blocks).",
                new JsonObject()),
            Tool("autocad_screenshot", "Take a PNG screenshot of the AutoCAD GUI window (or full screen) and return its file path.",
                new JsonObject
                {
                    ["outputPath"] = Prop("string", "Optional output PNG path"),
                    ["fullScreen"] = Prop("boolean", "Capture full primary screen instead of only main AutoCAD window (useful for modal dialogs/palettes)")
                }),
            Tool("autocad_ui_tree", "Inspect visible AutoCAD windows, palettes, modal dialogs, tabs, buttons, and inputs via Windows UI Automation.",
                new JsonObject
                {
                    ["windowFilter"] = Prop("string", "Optional substring filter for window title"),
                    ["maxDepth"] = Prop("integer", "Max UI tree walk depth (default 4)")
                }),
            Tool("autocad_ui_click", "Click a named UI control (button, tab, menu item, tree item) via UI Automation or click at exact screen coordinates (x, y).",
                new JsonObject
                {
                    ["name"] = Prop("string", "Control name (button/tab/item text)"),
                    ["windowFilter"] = Prop("string", "Optional substring filter for window title"),
                    ["x"] = Prop("integer", "Optional screen X coordinate"),
                    ["y"] = Prop("integer", "Optional screen Y coordinate"),
                    ["right"] = Prop("boolean", "Right-click instead of left-click"),
                    ["doubleClick"] = Prop("boolean", "Double-click")
                }),
            Tool("autocad_ui_type", "Type text or SendKeys sequences (e.g. {ENTER}, {ESC}) into AutoCAD or set value of a named UI input control.",
                new JsonObject
                {
                    ["text"] = Prop("string", "Text or SendKeys string to type"),
                    ["controlName"] = Prop("string", "Optional control name to set via ValuePattern"),
                    ["windowFilter"] = Prop("string", "Optional substring filter for window title")
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
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode req;
                try { req = JsonNode.Parse(line); }
                catch { continue; }
                var id = req["id"]?.DeepClone();
                string method = req["method"]?.GetValue<string>() ?? "";
                if (id == null) continue; // notification

                try
                {
                    JsonNode result;
                    if (method == "initialize")
                    {
                        result = new JsonObject
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "autocad-gui-mcp", ["version"] = "1.0.0" }
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
                case "autocad_send_command": return AutoCadBridge.SendCommand(Str(args, "command"), Int(args, "waitMs", 600));
                case "autocad_get_state": return AutoCadBridge.GetState();
                case "autocad_screenshot": return AutoCadBridge.Screenshot(Str(args, "outputPath"), Bool(args, "fullScreen"));
                case "autocad_ui_tree": return AutoCadBridge.UiTree(Str(args, "windowFilter"), Int(args, "maxDepth", 4));
                case "autocad_ui_click": return AutoCadBridge.UiClick(Str(args, "name"), Str(args, "windowFilter"), IntOpt(args, "x"), IntOpt(args, "y"), Bool(args, "right"), Bool(args, "doubleClick"));
                case "autocad_ui_type": return AutoCadBridge.UiType(Str(args, "text"), Str(args, "controlName"), Str(args, "windowFilter"));
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
