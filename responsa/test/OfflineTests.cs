// IT-780: offline tests (no Responsa needed). Built and run by: .\build.ps1 -Test
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ResponsaMcp
{
    internal static class OfflineTests
    {
        static int failed, passed;
        static void Check(bool ok, string name)
        {
            if (ok) passed++; else { failed++; Console.WriteLine("FAIL: " + name); }
        }
        static void Throws(Action a, string contains, string name)
        {
            try { a(); Check(false, name + " (no error)"); }
            catch (ToolError e) { Check(e.Message.Contains(contains), name + " (message: " + e.Message + ")"); }
        }

        static int Main(string[] args)
        {
            string samples = args.Length > 0 ? args[0] : "samples";
            ValidationTests();
            ProtocolTests();
            ParseTests(samples);
            WorkspaceGuardTests();
            WorkspaceFileTests();
            InstallTests();
            BookTests();
            DpiTests();
            EnvTests();        // IT-868
            DistanceTests();   // IT-868
            SourceTests();     // IT-868
            SaveRaceTests();   // IT-871
            Check(Ui.FixBidi("(תלמוד ירושלמי (וילנא") == "תלמוד ירושלמי (וילנא)", "FixBidi visual parenthesis");
            Check(Ui.FixBidi("תלמוד בבלי") == "תלמוד בבלי", "FixBidi plain");
            Check(McpServer.Serialize(new Dictionary<string, object> { { "t", "גמ' <a> & b" } }) == "{\"t\":\"גמ' <a> & b\"}", "Serialize keeps ' < > & plain");
            Console.WriteLine(string.Format("{0} passed, {1} failed", passed, failed));
            return failed == 0 ? 0 : 1;
        }

        // ---- IT-871: a slow save is never taken for "saved elsewhere"; every round has its own path; the exists prompt
        static Ui.SaveState S(bool here, bool open, bool ourBox, bool otherBox, bool closed, bool idle, double sinceClosed, double sinceBox, double unchanged)
        {
            return new Ui.SaveState { Here = here, Open = open, OurBox = ourBox, OtherBox = otherBox, FileClosed = closed, Idle = idle, SinceClosed = sinceClosed, SinceBox = sinceBox, Unchanged = unchanged };
        }
        static void SaveRaceTests()
        {
            // Measured on Responsa 29: results 101-102 took over 6 s before the file appeared (0 B, open, Responsa busy).
            Check(Ui.SaveOutcome(S(false, false, false, false, false, false, 6.4, -1, 6.4)) == "wait", "slow save, Responsa busy, no file after 6.4 s: wait (was 'elsewhere')");
            Check(Ui.SaveOutcome(S(false, false, false, false, false, false, 40, -1, 40)) == "wait", "Responsa busy: never 'elsewhere', however long");
            Check(Ui.SaveOutcome(S(false, false, false, false, false, true, 6.4, -1, 6.4)) == "wait", "idle, no box, 6.4 s: still wait");
            Check(Ui.SaveOutcome(S(false, false, false, false, false, true, Ui.IdleElsewhere + 0.5, -1, 16)) == "elsewhere", "idle, no box, no file after 15 s: elsewhere");
            Check(Ui.SaveOutcome(S(false, true, false, false, false, true, -1, -1, 20)) == "wait", "Save As still open: wait");
            // The box Responsa shows after writing
            Check(Ui.SaveOutcome(S(true, false, true, false, false, false, 0.5, 0.1, 0)) == "saved", "file and our box: saved");
            Check(Ui.SaveOutcome(S(false, false, true, false, false, true, 1, 3.5, 3.5)) == "elsewhere", "our box but no file after 3 s: elsewhere");
            Check(Ui.SaveOutcome(S(false, false, false, true, false, true, 1, 3.5, 3.5)) == "elsewhere", "a box for another path, no file: elsewhere");
            Check(Ui.SaveOutcome(S(false, false, false, true, false, false, 1, 3.5, 3.5)) == "wait", "a box for another path while Responsa is busy: wait");
            Check(Ui.SaveOutcome(S(false, false, true, false, false, true, 1, 2, 2)) == "wait", "our box, no file yet, 2 s: wait");
            // No box (a version that shows none): only a closed, quiet file while Responsa is idle
            Check(Ui.SaveOutcome(S(true, false, false, false, true, true, 2.5, -1, 2.5)) == "saved", "no box: closed, quiet, idle file: saved");
            Check(Ui.SaveOutcome(S(true, false, false, false, true, true, 2.5, -1, 1)) == "wait", "no box: file still changing: wait");
            Check(Ui.SaveOutcome(S(true, false, false, false, true, false, 2.5, -1, 2.5)) == "wait", "no box: Responsa busy (between writes): wait");
            Check(Ui.SaveOutcome(S(true, false, false, false, false, true, 2.5, -1, 2.5)) == "wait", "no box: file still open: wait");
            Check(Ui.SaveOutcome(S(true, false, false, false, true, true, 1, -1, 2.5)) == "wait", "no box: window closed under 2 s ago: wait");

            // Fresh paths: never the first one, never one used or existing
            string dir = Path.Combine(Path.GetTempPath(), "responsa-it871-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string first = Path.Combine(dir, "abc.txt");
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first };
                string p2 = Ui.FreshPath(first, used);
                Check(p2 == Path.Combine(dir, "abc-2.txt"), "FreshPath: abc-2.txt beside the first (" + p2 + ")");
                used.Add(p2);
                string p3 = Ui.FreshPath(first, used);
                Check(p3 == Path.Combine(dir, "abc-3.txt"), "FreshPath: a used name (even with no file yet) is skipped (" + p3 + ")");
                File.WriteAllText(Path.Combine(dir, "abc-3.txt"), "x");
                File.WriteAllText(Path.Combine(dir, "abc-4.txt"), "x");
                string p5 = Ui.FreshPath(first, new HashSet<string> { first, p2 });
                Check(p5 == Path.Combine(dir, "abc-5.txt"), "FreshPath: an existing name is skipped (" + p5 + ")");
                File.WriteAllText(first, "x");
                File.WriteAllText(Path.Combine(dir, "abc-x.txt"), "x");
                File.WriteAllText(Path.Combine(dir, "abc-02.txt"), "x");
                File.WriteAllText(Path.Combine(dir, "abcd-2.txt"), "x");
                var all = Ui.SavePaths(first).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
                Check(string.Join(",", all.ToArray()) == "abc-3.txt,abc-4.txt,abc.txt", "SavePaths: the first and its -N names only (" + string.Join(",", all.ToArray()) + ")");
            }
            finally { Directory.Delete(dir, true); }

            // The prompt as measured on Responsa 29, and boxes that are not it
            var prompt = new List<KeyValuePair<int, string>> {
                new KeyValuePair<int, string>(1056, "&הוסף"), new KeyValuePair<int, string>(1, "&שכתב"),
                new KeyValuePair<int, string>(57670, "ע&זרה"), new KeyValuePair<int, string>(2, "&ביטול") };
            string promptText = "קיים כבר קובץ בשם שבחרת. | בחר \"הוסף\" - להוספת החומר לסוף הקובץ הקיים, או בחר \"שכתב\" - לביטול הקובץ הקיים.";
            Check(SaveDialog.IsExistsPrompt(promptText, prompt), "exists prompt recognized");
            Check(SaveDialog.IsExistsPrompt("", prompt), "exists prompt recognized by its buttons alone");
            Check(SaveDialog.IsExistsPrompt("קיים כבר קובץ בשם שבחרת.", new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(2, "&ביטול") }), "exists prompt recognized by its text alone");
            Check(SaveDialog.PromptButton(prompt, "ביטול") == 2 && SaveDialog.PromptButton(prompt, "שכתב") == 1 && SaveDialog.PromptButton(prompt, "הוסף") == 1056, "prompt buttons by label");
            Check(SaveDialog.PromptButton(prompt, "אישור") == -1, "no such button: -1");
            Check(!SaveDialog.IsExistsPrompt("הקובץ נשמר", new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(2, "OK") }), "the saved box is not the exists prompt");
            Check(!SaveDialog.IsExistsPrompt("לא נמצאה כל תוצאה!", new List<KeyValuePair<int, string>> { new KeyValuePair<int, string>(1, "אישור") }), "another message is not the exists prompt");

            // Box titles: Responsa titles them with the path
            Check(Ui.BoxPath(@"C:\Users\x\AppData\Local\ResponsaMcp\tmp\1-2\abc.txt") == @"C:\Users\x\AppData\Local\ResponsaMcp\tmp\1-2\abc.txt", "BoxPath: a path title");
            Check(Ui.BoxPath("RESPONSA") == null && Ui.BoxPath("") == null && Ui.BoxPath(null) == null, "BoxPath: other titles are none");
        }

        // ---- IT-868: the environment RESPONSA.exe is started with
        static Dictionary<string, string> D(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }
        static void EnvTests()
        {
            // What Claude Desktop passes (measured), and the user's default environment.
            var reduced = D("APPDATA", @"C:\U\a", "PATH", @"C:\client", "TEMP", @"C:\U\Temp", "SYSTEMROOT", @"C:\Windows", "RESPONSA_MCP_EXE", @"C:\R\RESPONSA.exe");
            var user = D("TMP", @"C:\U\Temp", "TEMP", @"C:\other", "windir", @"C:\Windows", "Path", @"C:\user;C:\sys", "ProgramData", @"C:\ProgramData", "=C:", @"C:\");
            var e = ResponsaEnv.Complete(reduced, user, @"C:\gtp\");
            Check(e.Vars["TMP"] == @"C:\U\Temp" && e.Added.Contains("TMP"), "env: TMP comes from the user's default environment");
            Check(e.Vars["PATH"] == @"C:\client" && e.Vars["TEMP"] == @"C:\U\Temp", "env: the client's PATH and TEMP are not changed (names compared without case)");
            Check(e.Vars["RESPONSA_MCP_EXE"] == @"C:\R\RESPONSA.exe", "env: a variable only the client set stays");
            Check(e.Vars.ContainsKey("windir") && e.Vars.ContainsKey("ProgramData") && !e.Vars.ContainsKey("=C:"), "env: missing variables added, '=' entries not");
            Check(string.Join(",", e.Added.ToArray()) == "ProgramData,TMP,windir", "env: added names listed, sorted: " + string.Join(",", e.Added.ToArray()));
            Check(e.Report().StartsWith("TMP was missing"), "env: report says TMP was missing: " + e.Report());
            var own = ResponsaEnv.Complete(D("PATH", "x", "TMP", "t"), D("MY_SECRET_PASS", "1", "ComSpec", "c"), null);
            Check(own.Report().Contains("2 variable(s)") && own.Report().Contains("ComSpec") && !own.Report().Contains("MY_SECRET"), "env: report names only well-known variables: " + own.Report());
            var noDef = ResponsaEnv.Complete(reduced, null, @"C:\gtp\");
            Check(noDef.Vars["TMP"] == @"C:\U\Temp" && noDef.Added.Count == 1, "env: no default environment: TMP from TEMP");
            var noTemp = ResponsaEnv.Complete(D("PATH", "x"), null, @"C:\gtp\");
            Check(noTemp.Vars["TMP"] == @"C:\gtp", "env: no TEMP either: the system temp path");
            var emptyTmp = ResponsaEnv.Complete(D("TMP", " ", "TEMP", @"C:\t"), null, null);
            Check(emptyTmp.Vars["TMP"] == @"C:\t" && emptyTmp.Added.Contains("TMP"), "env: an empty TMP is replaced");
            var full = ResponsaEnv.Complete(D("TMP", @"C:\mine", "TEMP", @"C:\t", "windir", "w"), D("TMP", @"C:\U\Temp", "windir", "x"), null);
            Check(full.Added.Count == 0 && full.Vars["TMP"] == @"C:\mine" && full.Report().StartsWith("complete"), "env: full environment: nothing added, nothing changed");
            string block = ResponsaEnv.Block(D("b", "2", "A", "1", "c", "x=y"));
            Check(block == "A=1\0b=2\0c=x=y\0\0", "env: block sorted without case, ends with two NULs");
            var parsed = ResponsaEnv.Parse("=C:=C:\\x\0A=1\0b=x=y\0\0");
            Check(parsed.Count == 3 && parsed["=C:"] == "C:\\x" && parsed["B"] == "x=y", "env: Parse keeps '=' names and values with '='");
            Check(ResponsaEnv.Parse("\0").Count == 0 && ResponsaEnv.Parse("=\0x\0").Count == 0, "env: Parse skips short entries");
            Check(ResponsaEnv.Get().Vars.ContainsKey("TMP") && ResponsaEnv.Get().DefaultError == null, "env: this process's completed environment has TMP, default environment read: " + ResponsaEnv.Get().Report());
            Check(ResponsaEnv.Hint("אזהרה: שגיאה מס' 15: לא ניתן לפתוח את הקובץ.") != null && ResponsaEnv.Hint("תקלה: שגיאה מס' 9: בעיית קלט/פלט") == null
                && ResponsaEnv.Hint("שגיאה מס' 150") == null, "env: hint only for error 15");
        }

        // ---- IT-868: distance order differs between versions
        static void DistanceTests()
        {
            Check(Distance.Swap("נר [1:3] שבת") == "נר [3:1] שבת", "distance: [1:3] -> [3:1]");
            Check(Distance.Swap("#א [1:2] #ב [ 0 : 5 ] ג") == "#א [2:1] #ב [5:0] ג", "distance: every range, spaces allowed");
            Check(Distance.Swap("א [-5:5] ב") == "א [5:-5] ב", "distance: negative part kept with its number");
            Check(Distance.Swap("א [3] ב 5: %קדש") == "א [3] ב 5: %קדש", "distance: one-number range and other colons unchanged");
            Check(Distance.Swap(Distance.Swap("x [1:3] y [2:4]")) == "x [1:3] y [2:4]", "distance: swap twice is the query");
            Check(Distance.IsOrderError(new List<string> { "שגיאה בהגדרת השאילתה: המרחק המקסימלי קטן מהמרחק המינימלי." }), "distance: Responsa's message recognized");
            Check(!Distance.IsOrderError(new List<string> { "לא נמצאה כל תוצאה!" }) && !Distance.IsOrderError(null), "distance: other messages not");
        }

        // ---- IT-868: open_text's source is never empty
        static void SourceTests()
        {
            var names = new List<string> { "שולחן ערוך אורח חיים סימן רסג", "", "טור אורח חיים סימן רסג" };
            var a = Tools.SourceFields("", "או\"ח סי' רסג", "שולחן ערוך אורח חיים סימן רסג", 4, names);
            Check((string)a["source"] == "שולחן ערוך אורח חיים סימן רסג" && a.ContainsKey("source_from") && (string)a["source_short"] == "או\"ח סי' רסג", "source: empty title -> the item opened from the list");
            Check(((List<object>)a["candidates"]).Count == 2 && (string)a["opened"] == "שולחן ערוך אורח חיים סימן רסג" && ((string)a["note"]).Contains("first 2"), "source: candidates listed, opened named");
            var b = Tools.SourceFields(" ", "או\"ח סי' רסג", null, 4, new List<string>());
            Check((string)b["source"] == "או\"ח סי' רסג" && !b.ContainsKey("source_short") && !b.ContainsKey("candidates"), "source: no title, no list -> the saved title line");
            var c = Tools.SourceFields("בבלי ברכות ב א", "ברכות ב א", null, 0, null);
            Check((string)c["source"] == "בבלי ברכות ב א" && !c.ContainsKey("source_from") && !c.ContainsKey("note"), "source: window title used as before");
            var d = Tools.SourceFields(null, null, null, 0, null);
            Check(((string)d["source"]).Length > 0, "source: never empty");
        }

        // ---- IT-794: a click is posted in the target window's DPI awareness, and the thread's own is restored after
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        static void DpiTests()
        {
            IntPtr unaware = (IntPtr)(-1), system = (IntPtr)(-2);
            IntPtr own;
            try { own = Native.GetThreadDpiAwarenessContext(); }
            catch (EntryPointNotFoundException) { Console.WriteLine("DPI tests skipped: no DPI awareness API on this Windows"); return; }
            Check(Native.AreDpiAwarenessContextsEqual(own, unaware), "the tool's thread is DPI-unaware (as when the scaling was measured)");
            // A window of this thread created as system-DPI-aware, like Responsa's windows.
            IntPtr prev = Native.SetThreadDpiAwarenessContext(system);
            IntPtr w = CreateWindowEx(0, "STATIC", "dpi", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Native.SetThreadDpiAwarenessContext(prev);
            try
            {
                Check(w != IntPtr.Zero && Native.AreDpiAwarenessContextsEqual(Native.GetWindowDpiAwarenessContext(w), system), "test window is system-DPI-aware");
                bool inside = false;
                Win.InDpiOf(w, delegate { inside = Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(), system); });
                Check(inside, "InDpiOf runs in the window's DPI awareness");
                Check(Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(), unaware), "InDpiOf restores the thread's awareness");
                try { Win.InDpiOf(w, delegate { throw new InvalidOperationException("x"); }); } catch (InvalidOperationException) { }
                Check(Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(), unaware), "InDpiOf restores the awareness after an exception");
                bool ran = false;
                Win.InDpiOf(IntPtr.Zero, delegate { ran = Native.AreDpiAwarenessContextsEqual(Native.GetThreadDpiAwarenessContext(), unaware); });
                Check(ran, "InDpiOf on no window runs unchanged");
            }
            finally { if (w != IntPtr.Zero) DestroyWindow(w); }
        }

        static Dictionary<string, object> Schema()
        {
            return new Dictionary<string, object> {
                { "type", "object" },
                { "properties", new Dictionary<string, object> {
                    { "q", new Dictionary<string, object> { { "type", "string" }, { "maxLength", 10 } } },
                    { "n", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 1 }, { "maximum", 50 } } },
                    { "b", new Dictionary<string, object> { { "type", "boolean" } } } } },
                { "required", new List<object> { "q" } } };
        }
        static Dictionary<string, object> A(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static void ValidationTests()
        {
            var s = Schema();
            var ok = McpServer.Validate(s, A("q", " שבת ", "n", 5, "b", true));
            Check((string)ok["q"] == "שבת" && (int)ok["n"] == 5 && (bool)ok["b"], "valid args are trimmed and typed");
            Check((int)McpServer.Validate(s, A("q", "x", "n", 7m))["n"] == 7, "integral decimal accepted");
            Throws(() => McpServer.Validate(s, A("n", 5)), "missing argument: q", "required");
            Throws(() => McpServer.Validate(s, A("q", "x", "zz", 1)), "unknown argument", "unknown key");
            Throws(() => McpServer.Validate(s, A("q", "x", "n", 51)), "between 1 and 50", "max");
            Throws(() => McpServer.Validate(s, A("q", "x", "n", 0)), "between 1 and 50", "min");
            Throws(() => McpServer.Validate(s, A("q", "x", "n", 2.5m)), "must be an integer", "fraction");
            Throws(() => McpServer.Validate(s, A("q", "x", "n", "3")), "must be an integer", "string as integer");
            Throws(() => McpServer.Validate(s, A("q", "   ")), "must not be empty", "blank string");
            Throws(() => McpServer.Validate(s, A("q", "a\nb")), "single line", "multi-line string");
            Throws(() => McpServer.Validate(s, A("q", "01234567890")), "too long", "maxLength");
            Throws(() => McpServer.Validate(s, A("q", "x", "b", "true")), "true or false", "boolean type");
            Throws(() => McpServer.Validate(s, A("q", 5)), "must be a string", "string type");
        }

        static void ProtocolTests()
        {
            var json = new JavaScriptSerializer();
            int calls = 0;
            var tools = new List<ToolDef> {
                new ToolDef { Name = "echo", Description = "d", Schema = Schema(), Run = a => { calls++; return new Dictionary<string, object> { { "q", a["q"] } }; } },
                new ToolDef { Name = "boom", Description = "d", Schema = Schema(), Run = a => { throw new ToolError("bad thing"); } } };
            var srv = new McpServer(tools, TextWriter.Null);
            Func<string, Dictionary<string, object>> call = line => json.Deserialize<Dictionary<string, object>>(srv.Handle(line));

            var init = call("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}");
            var ir = (Dictionary<string, object>)init["result"];
            Check((string)ir["protocolVersion"] == "2025-03-26", "initialize echoes the client protocol version");
            Check(srv.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") == null, "notification gets no reply");
            var list = (Dictionary<string, object>)call("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}")["result"];
            Check(((System.Collections.ArrayList)list["tools"]).Count == 2, "tools/list");
            var ok = (Dictionary<string, object>)call("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"q\":\"מקוה\"}}}")["result"];
            Check(!(bool)ok["isError"] && calls == 1 && ((string)((Dictionary<string, object>)((System.Collections.ArrayList)ok["content"])[0])["text"]).Contains("מקוה"), "tools/call ok");
            var bad = (Dictionary<string, object>)call("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"q\":\"x\",\"evil\":1}}}")["result"];
            Check((bool)bad["isError"] && calls == 1, "invalid arguments never reach the tool");
            var boom = (Dictionary<string, object>)call("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"boom\",\"arguments\":{\"q\":\"x\"}}}")["result"];
            Check((bool)boom["isError"], "ToolError becomes isError");
            Check(call("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}").ContainsKey("error"), "unknown tool is a JSON-RPC error");
            Check(call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"what\"}").ContainsKey("error"), "unknown method");
            Check(call("not json").ContainsKey("error"), "parse error");
        }

        // ---- reviews 1 and 2, finding 1: workspace guard scenarios (a fake data file and a fake process world)
        static string Read(string f) { return File.Exists(f) ? File.ReadAllText(f) : "<absent>"; }
        sealed class FakeWorld : IGuardWorld
        {
            public readonly HashSet<string> Running = new HashSet<string>();          // editors' Responsa now
            public readonly Dictionary<string, EditorExit> Exits = new Dictionary<string, EditorExit>();
            public readonly List<string> Others = new List<string>();                  // other live guards
            public ICollection<string> Editors() { return Running.ToList(); }
            public EditorExit ExitOf(string id) { EditorExit x; return Exits.TryGetValue(id, out x) ? x : EditorExit.Unknown; }
            public ICollection<string> OtherGuardIds() { return Others.ToList(); }
            public void Exit(string id, DateTime at, bool normal = true) { Running.Remove(id); Exits[id] = new EditorExit { AtUtc = at, Normal = normal }; }
        }
        static void Write(string f, string text, DateTime utc) { File.WriteAllText(f, text); File.SetLastWriteTimeUtc(f, utc); }
        static void TryDel(string f) { try { File.Delete(f); } catch { } }
        static void WorkspaceGuardTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "responsa-guard-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string ws = Path.Combine(root, "responsaext1.029"), gdir = Path.Combine(root, "guards");
            Func<string, bool> gone = id => !Directory.GetFiles(gdir, id + ".*").Any();
            DateTime t0 = DateTime.UtcNow.AddMinutes(-10);
            try
            {
                // C1: only the tool's instance runs; its autosave is put back at the next check.
                var w = new FakeWorld();
                File.WriteAllText(ws, "E0");
                var g = new WorkspaceGuard(ws, gdir, "c1", w); g.Begin();
                File.WriteAllText(ws, "T1"); g.Tick();
                Check(Read(ws) == "E0", "tool-only autosave is undone at the next check");
                File.WriteAllText(ws, "T2");
                Check(g.End() == null && Read(ws) == "E0" && Directory.GetFiles(gdir).Length == 0, "end: editor's version back, guard files removed");

                // C2: the file did not exist at start: a file only the tool created is removed.
                File.Delete(ws);
                g = new WorkspaceGuard(ws, gdir, "c2", w); g.Begin();
                File.WriteAllText(ws, "T1"); g.Tick();
                Check(!File.Exists(ws), "file created only by the tool is removed");
                Check(g.End() == null && gone("c2"), "c2: guard files removed");

                // A: the editor opens Responsa after the tool, saves on exit, and exits before the tool stops.
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "a", w); g.Begin();
                w.Running.Add("ed1"); g.Tick();                                    // editor running
                Write(ws, "E1", t0.AddSeconds(10)); w.Exit("ed1", t0.AddSeconds(10.1));   // exit save, then exit
                g.Tick();                                                          // E1 is the editor's version now
                File.WriteAllText(ws, "T2"); g.Tick();                             // later tool autosave
                Check(Read(ws) == "E1", "A: tool autosave after the editor exits is replaced by the editor's exit save");
                Check(g.End() == null && Read(ws) == "E1" && gone("a"), "A: end keeps the editor's save, guard files removed");

                // B: the editor is open when the tool starts; an ambiguous change; it exits; then the tool autosaves.
                w = new FakeWorld(); Write(ws, "E0", t0); w.Running.Add("ed1");
                g = new WorkspaceGuard(ws, gdir, "b", w); g.Begin();
                Write(ws, "T1", t0.AddSeconds(5)); g.Tick();                        // ambiguous while the editor runs: left alone
                Check(Read(ws) == "T1", "B: change while the editor runs is not overwritten with an old copy");
                Write(ws, "E1", t0.AddSeconds(20)); w.Exit("ed1", t0.AddSeconds(20)); g.Tick();
                File.WriteAllText(ws, "T2");
                Check(g.End() == null && Read(ws) == "E1", "B: the tool's later autosave does not stay; the editor's exit save does");

                // Review 2 case 3: the tool writes after the editor exited, before the exit is seen (same 1.5 s).
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "x3", w); g.Begin();
                w.Running.Add("ed1"); Write(ws, "E05", t0.AddSeconds(5)); g.Tick();   // editor's autosave, seen while it runs
                Write(ws, "E1", t0.AddSeconds(30)); w.Exit("ed1", t0.AddSeconds(30.05));
                Write(ws, "T2", t0.AddSeconds(31));                                 // tool, 0.95 s after the editor exited
                g.Tick();
                Check(Read(ws) == "E05", "x3: a write after the editor's exit is not adopted; the last bytes seen while the editor ran are put back");
                File.WriteAllText(ws, "T3"); g.Tick();
                Check(Read(ws) == "E05", "x3: later tool writes are restored to that version, not to T2");
                Check(g.End() == null && gone("x3"), "x3: end cleans up");
                // ... an exit save not yet seen by a tick (written 2 s before the exit, as measured) is adopted.
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "x3b", w); g.Begin();
                w.Running.Add("ed1"); g.Tick();
                Write(ws, "E1", t0.AddSeconds(38)); w.Exit("ed1", t0.AddSeconds(40));
                g.Tick(); File.WriteAllText(ws, "T2"); g.Tick();
                Check(Read(ws) == "E1", "x3b: exit save written before the exit time is the editor's");
                g.End();
                // ... any write stamped after the exit time is not, even 1 ms after.
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "x3e", w); g.Begin();
                w.Running.Add("ed1"); g.Tick();
                Write(ws, "T1", t0.AddSeconds(40.001)); w.Exit("ed1", t0.AddSeconds(40));
                g.Tick();
                Check(Read(ws) == "E0", "x3e: write 1 ms after the exit time is the tool's: last bytes seen while the editor ran are put back");
                g.End();
                // ... exit time unknown: the file at the exit tick is trusted as the exit save.
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "x3c", w); g.Begin();
                w.Running.Add("ed1"); g.Tick(); Write(ws, "E1", t0.AddSeconds(50)); w.Running.Remove("ed1"); w.Exits["ed1"] = EditorExit.Unknown;
                g.Tick(); File.WriteAllText(ws, "T2"); g.Tick();
                Check(Read(ws) == "E1", "x3c: unknown exit time: exit save adopted");
                g.End();
                // ... an "editor" that turns out to be another server's instance (its record appeared) is no exit.
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "x3d", w); g.Begin();
                w.Running.Add("srvB"); g.Tick(); File.WriteAllText(ws, "TB");
                w.Running.Remove("srvB"); w.Exits["srvB"] = null; g.Tick();
                Check(Read(ws) == "E0", "x3d: re-classified instance: nothing adopted, tool write undone");
                g.End();

                // The editor's Responsa crashes (exit code not 0) after the tool autosaved in the overlap: nothing is
                // written, and the copy from before the overlap survives as a kept copy.
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "crash", w); g.Begin();
                w.Running.Add("ed1"); g.Tick();
                Write(ws, "T1", t0.AddSeconds(5)); g.Tick();                        // tool autosave in the overlap
                w.Exit("ed1", t0.AddSeconds(60), false); g.Tick();                 // editor crashed, no exit save
                Check(Read(ws) == "T1", "crash: nothing written at the editor's crash");
                var aside = WorkspaceGuard.KeptAside(gdir).Where(k => k.Contains("crash-1.kept")).ToList();
                Check(aside.Count == 1 && File.ReadAllText(aside[0]) == "E0", "crash: the copy from before the overlap is kept aside");
                File.WriteAllText(ws, "T2"); g.Tick();
                Check(Read(ws) == "T1", "crash: later tool writes go back to the file as it was at the crash");
                Check(g.End() == null && aside.Count == 1 && File.Exists(aside[0]), "crash: end leaves the kept-aside copy");
                if (aside.Count == 1) { TryDel(aside[0]); TryDel(aside[0].Replace(".bak", ".meta")); }
                // ... crash with no change in the overlap: nothing set aside.
                w = new FakeWorld(); Write(ws, "E0", t0);
                g = new WorkspaceGuard(ws, gdir, "crash2", w); g.Begin();
                w.Running.Add("ed1"); g.Tick(); w.Exit("ed1", t0.AddSeconds(60), false); g.Tick();
                Check(!WorkspaceGuard.KeptAside(gdir).Any(k => k.Contains("crash2")) && g.End() == null && gone("crash2"), "crash: unchanged file, nothing kept aside");

                // C: crash in place: the tool's instance dies, the server starts a new one.
                w = new FakeWorld(); File.WriteAllText(ws, "E0");
                var g1 = new WorkspaceGuard(ws, gdir, "c-1", w); g1.Begin();
                File.WriteAllText(ws, "T1");                   // crashed instance's last autosave, not yet checked
                g1.End();                                      // Stop() after the crash: settle first
                var g2 = new WorkspaceGuard(ws, gdir, "c-2", w); g2.Begin();
                File.WriteAllText(ws, "T3"); g2.Tick();
                Check(Read(ws) == "E0", "C: after a crash the new copy is the editor's version, not the crashed instance's file");
                g2.End();

                // Review 2 case 2: server B starts while A's instance has written the file and A has not checked yet.
                var wa = new FakeWorld(); var wb = new FakeWorld(); File.WriteAllText(ws, "E0");
                var ga = new WorkspaceGuard(ws, gdir, "d-a", wa); ga.Begin();
                File.WriteAllText(ws, "TA");                                       // A's autosave, A's tick not yet run
                wb.Others.Add("d-a");
                var gb = new WorkspaceGuard(ws, gdir, "d-b", wb); gb.Begin();
                wa.Others.Add("d-b");
                Check(File.ReadAllText(Path.Combine(gdir, "d-b.bak")) == "E0", "D: second server takes the first guard's kept version, not the dirty file");
                bool steady = true;
                for (int i = 0; i < 3; i++)
                {
                    gb.Tick(); steady &= Read(ws) == "E0";
                    File.WriteAllText(ws, "TB"); ga.Tick(); steady &= Read(ws) == "E0";
                    File.WriteAllText(ws, "TA"); gb.Tick(); ga.Tick(); steady &= Read(ws) == "E0";
                }
                Check(steady, "D: both guards always restore the same version (no swapping)");
                wb.Others.Clear(); Check(ga.End() == null, "D: A ends clean");
                File.WriteAllText(ws, "TB"); Check(gb.End() == null && Read(ws) == "E0" && gone("d-a") && gone("d-b"), "D: both end on the editor's version, files removed");
                // ... other guards that disagree: a third server does not start.
                File.WriteAllText(Path.Combine(gdir, "p.meta"), "existed=True\nkept=AAA\nlast=AAA\nfile=" + ws);
                File.WriteAllText(Path.Combine(gdir, "q.meta"), "existed=True\nkept=BBB\nlast=BBB\nfile=" + ws);
                var w3 = new FakeWorld(); w3.Others.Add("p"); w3.Others.Add("q");
                Throws(() => new WorkspaceGuard(ws, gdir, "third", w3).Begin(), "different versions", "D: two disagreeing guards: no third instance");
                // ... and a guard whose kept version differs from a live other guard's writes nothing.
                var wp = new FakeWorld(); File.WriteAllText(ws, "E0");
                var gp = new WorkspaceGuard(ws, gdir, "pass", wp); gp.Begin(); wp.Others.Add("p");
                File.WriteAllText(ws, "TX"); gp.Tick();
                Check(Read(ws) == "TX", "D: guards disagree: no put back");
                wp.Others.Clear(); gp.Tick(); Check(Read(ws) == "E0", "D: agreement again: put back");
                gp.End();
                TryDel(Path.Combine(gdir, "p.meta")); TryDel(Path.Combine(gdir, "q.meta"));

                // Review 2 case 1: ambiguous autosave in the overlap, then the server is killed (no End).
                w = new FakeWorld(); File.WriteAllText(ws, "E0");
                var ge = new WorkspaceGuard(ws, gdir, "e", w); ge.Begin();
                w.Running.Add("ed1"); ge.Tick();
                File.WriteAllText(ws, "T1"); ge.Tick();                            // last = T1, kept = E0
                string kept = WorkspaceGuard.SettleOrphan(gdir, "e");
                Check(kept != null && File.ReadAllText(kept) == "E0" && Read(ws) == "T1", "E: cur == last != kept: the good copy is kept aside, the file is not written");
                Check(WorkspaceGuard.KeptAside(gdir).Contains(kept), "E: the kept copy is listed for status");
                var gn = new WorkspaceGuard(ws, gdir, "e-next", new FakeWorld()); gn.Begin();
                Check(File.ReadAllText(kept) == "E0", "E: a new server's copy does not replace the kept one");
                gn.End(); TryDel(kept); TryDel(kept.Replace(".bak", ".meta"));
                // ... changed after the last check: kept aside too.
                File.WriteAllText(ws, "E0");
                ge = new WorkspaceGuard(ws, gdir, "e2", new FakeWorld()); ge.Begin();
                File.WriteAllText(ws, "T1");
                kept = WorkspaceGuard.SettleOrphan(gdir, "e2");
                Check(kept != null && File.ReadAllText(kept) == "E0", "E: changed after the dead server's last check: kept aside");
                TryDel(kept); TryDel(kept.Replace(".bak", ".meta"));
                // ... file equals the kept version: cleaned up.
                File.WriteAllText(ws, "E0");
                var gf = new WorkspaceGuard(ws, gdir, "f", new FakeWorld()); gf.Begin();
                Check(WorkspaceGuard.SettleOrphan(gdir, "f") == null && gone("f"), "E: file is the kept version: cleaned up");
                // ... a copy without its meta (crash inside Begin) is kept aside, not deleted.
                File.WriteAllText(Path.Combine(gdir, "nometa.bak"), "E0");
                kept = WorkspaceGuard.SettleOrphan(gdir, "nometa");
                Check(kept != null && File.ReadAllText(kept) == "E0", "E: copy without meta kept aside");
                TryDel(kept);

                // End while the editor's Responsa is still open and the file changed: the editor's later save decides;
                // nothing is forced, and the older copy is kept aside, not deleted.
                w = new FakeWorld(); File.WriteAllText(ws, "E0");
                g = new WorkspaceGuard(ws, gdir, "open", w); g.Begin();
                w.Running.Add("ed1"); g.Tick();
                File.WriteAllText(ws, "E1"); kept = g.End();
                Check(Read(ws) == "E1" && kept != null && File.ReadAllText(kept) == "E0", "end with the editor open: its save stays, the older copy is kept aside");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ---- IT-827: with several installs the newest is used; RESPONSA_MCP_EXE overrides; status lists them all
        static void InstallTests()
        {
            Func<string, string, Install> I = (dir, v) => new Install { Exe = dir + @"\RESPONSA.exe", Dir = dir, DataDir = dir + "-data", Version = v, Number = new System.Version(v), Source = "Program Files" };
            var r31 = I(@"C:\P\ResponsaCD31C", "31.0.0.0"); var r34 = I(@"C:\P\ResponsaCD34C", "34.0.0.0"); var r29 = I(@"C:\P\ResponsaCD29C", "29.0.0.0");
            var all = new List<Install> { r31, r34, r29 };
            Check(Install.Choose(all, null) == r34 && r34.All.Count == 3, "install: the newest of 31, 34, 29 is 34 (the first found was 31)");
            Check(Install.Choose(new List<Install> { r29, I(@"C:\P\ResponsaCD29D", "29.0.1.0") }, null).Dir.EndsWith("29D"), "install: a newer build of the same major wins");
            Check(Install.Choose(new List<Install> { r31, r34 }, r31) == r31, "install: RESPONSA_MCP_EXE overrides the newest");
            Check(Install.Choose(new List<Install>(), null) == null, "install: none found");
            var unread = I(@"C:\P\ResponsaX", "0.0.0.0"); unread.Version = "?";
            Check(Install.Choose(new List<Install> { unread, r29 }, null) == r29, "install: an unreadable version never wins over a readable one");
            var rep = Install.Choose(all, null).Report();
            Check(rep.Count == 3 && rep.Cast<Dictionary<string, object>>().Count(d => (bool)d["used"]) == 1
                && (string)((Dictionary<string, object>)rep.Cast<Dictionary<string, object>>().First(d => (bool)d["used"]))["version"] == "34.0.0.0"
                && (string)((Dictionary<string, object>)rep[0])["data_folder"] == r31.DataDir, "install: report lists every install, the used one marked, with its data folder");
            string old = Environment.GetEnvironmentVariable("RESPONSA_MCP_EXE");
            try
            {
                Environment.SetEnvironmentVariable("RESPONSA_MCP_EXE", @"C:
o\such\RESPONSA.exe");
                Throws(() => Install.Find(), "no such file", "install: RESPONSA_MCP_EXE naming no file is an error, not a silent fallback");
            }
            finally { Environment.SetEnvironmentVariable("RESPONSA_MCP_EXE", old); }
        }

        // ---- IT-785: the workspace file is found by name (Responsa versions other than 29)
        static void WorkspaceFileTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "responsa-wsfile-test-" + Guid.NewGuid().ToString("N"));
            string data = Path.Combine(root, "DATA"), gdir = Path.Combine(root, "guards");
            Directory.CreateDirectory(data);
            try
            {
                // Files that are not the workspace file never match.
                foreach (var n in new[] { "responsahst.030", "responsaext1.029_", "responsaext1.029__", "responsaext1.29", "responsaext1.0299", "responsaext2.030", "responsaext1.0a9", "xresponsaext1.030" })
                    File.WriteAllText(Path.Combine(data, n), "x");

                // No match: the name this version is expected to create, protected as a file that did not exist.
                var none = WorkspaceFile.Find(root, 30);
                Check(none.State == "none" && none.Matches.Count == 0 && Path.GetFileName(none.Path) == "responsaext1.030", "none: expected name from the version (30 -> .030)");
                Check(Path.GetFileName(WorkspaceFile.Find(root, 0).Path) == "responsaext1.029", "none, version unknown: .029");
                Check(Path.GetFileName(WorkspaceFile.Find(root, 29).Path) == "responsaext1.029", "none on 29: today's name");
                var gNone = new WorkspaceGuard(none.Path, gdir, "n1", new FakeWorld()); gNone.Begin();
                File.WriteAllText(none.Path, "T1"); gNone.Tick();
                Check(!File.Exists(none.Path) && gNone.End() == null, "none: a file the tool creates is removed");

                // One match with a suffix other than .029 (any case): that file is protected, exactly as before.
                string ws30 = Path.Combine(data, "RESPONSAEXT1.030");
                File.WriteAllText(ws30, "E0");
                var one = WorkspaceFile.Find(root, 30);
                Check(one.State == "found" && one.Matches.Count == 1 && string.Equals(one.Path, ws30, StringComparison.OrdinalIgnoreCase), "one match .030: found");
                Check(WorkspaceFile.Find(root, 29).Path == one.Path, "one match is used even when the version differs");
                var g = new WorkspaceGuard(one.Path, gdir, "o1", new FakeWorld()); g.Begin();
                File.WriteAllText(ws30, "T1"); g.Tick();
                Check(File.ReadAllText(ws30) == "E0" && g.End() == null && !Directory.GetFiles(gdir, "o1.*").Any(), "one match .030: the tool's write is put back, guard files removed");
                Check(WorkspaceGuard.FileOf(gdir, "o1") == null, "FileOf: no meta -> null");

                // Several matches: no path, and an error that names the files.
                File.WriteAllText(Path.Combine(data, "responsaext1.029"), "E9");
                var several = WorkspaceFile.Find(root, 29);
                Check(several.State == "several" && several.Path == null && several.Matches.Count == 2, "several: no path");
                string err = several.SeveralError();
                Check(err.Contains("responsaext1.029") && err.Contains("RESPONSAEXT1.030") && err.Contains("does not start"), "several: the error names the files");
                Check(File.ReadAllText(ws30) == "E0" && File.ReadAllText(Path.Combine(data, "responsaext1.029")) == "E9", "several: nothing written");

                // The version report.
                var ins29 = new Install { Version = "29.0.0.0", Major = 29 };
                var rep = Tools.VersionReport(ins29, several);
                Check((bool)rep["version_tested"] && !rep.ContainsKey("version_note") && (string)rep["workspace_file"] == "several" && ((List<object>)rep["workspace_files"]).Count == 2, "report: 29 tested, several files listed");
                rep = Tools.VersionReport(new Install { Version = "30.0.0.0", Major = 30 }, one);
                Check(!(bool)rep["version_tested"] && ((string)rep["version_note"]).Contains("expected to work") && (string)rep["workspace_file"] == one.Path, "report: 30 not tested (not refused), file shown");
                rep = Tools.VersionReport(ins29, none);
                Check(((string)rep["workspace_file"]).StartsWith("none"), "report: none");

                // A record written by IT-780 (fixed name, the meta lines of that version) still settles by its own path.
                string old029 = Path.Combine(data, "responsaext1.029");
                File.WriteAllText(Path.Combine(gdir, "780.bak"), "E9");
                File.WriteAllLines(Path.Combine(gdir, "780.meta"), new[] { "existed=True", "kept=" + WorkspaceGuard.HashOf(Path.Combine(gdir, "780.bak")), "last=x", "file=" + old029 });
                Check(WorkspaceGuard.FileOf(gdir, "780") == old029, "FileOf reads an IT-780 meta");
                Check(WorkspaceGuard.SettleOrphan(gdir, "780") == null && !Directory.GetFiles(gdir, "780.*").Any(), "IT-780 record, file unchanged: settled and removed");
                File.WriteAllText(Path.Combine(gdir, "781.bak"), "E8");
                File.WriteAllLines(Path.Combine(gdir, "781.meta"), new[] { "existed=True", "kept=" + WorkspaceGuard.HashOf(Path.Combine(gdir, "781.bak")), "last=x", "file=" + old029 });
                string kept = WorkspaceGuard.SettleOrphan(gdir, "781");
                Check(kept != null && File.ReadAllText(kept) == "E8" && File.ReadAllText(old029) == "E9" && File.ReadAllText(ws30) == "E0", "IT-780 record, file changed: kept aside, no workspace file written");
                Check(WorkspaceGuard.KeptAside(gdir).Contains(kept), "IT-780 record: kept copy listed for status");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
            StartGuardTests();
        }

        // ---- IT-785 review round 1: a live guard of another file, or one whose meta cannot be read, stops the start
        // before Begin (Begin would adopt its "did not exist" state, and PutBack would delete the file found here).
        static void StartGuardTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "responsa-startguard-test-" + Guid.NewGuid().ToString("N"));
            string data = Path.Combine(root, "DATA"), gdir = Path.Combine(root, "guards");
            Directory.CreateDirectory(data); Directory.CreateDirectory(gdir);
            string ws29 = Path.Combine(data, "responsaext1.029"), ws30 = Path.Combine(data, "responsaext1.030");
            try
            {
                File.WriteAllText(ws29, "E9");
                string found = WorkspaceFile.Find(root, 30).Path;
                Check(found == ws29, "startguard: this server found .029");
                // Server A started with no match and protects .030 as a file that did not exist.
                File.WriteAllLines(Path.Combine(gdir, "a.meta"), new[] { "existed=False", "kept=" + WorkspaceGuard.Absent, "last=" + WorkspaceGuard.Absent, "file=" + ws30 });
                var w = new FakeWorld(); w.Others.Add("a");
                Func<string, bool> untouched = id => File.ReadAllText(ws29) == "E9" && !Directory.GetFiles(gdir, id + ".*").Any();

                // A readable meta with a different file= refuses.
                Throws(() => Instance.StartGuard(found, gdir, "b1", w, null, 3, 10), "protects the workspace file", "startguard: different file= refuses");
                Check(untouched("b1"), "startguard: different file=: no Begin, .029 unchanged");

                // A meta that stays unreadable refuses after every attempt.
                int calls = 0;
                Throws(() => Instance.StartGuard(found, gdir, "b2", w, id => { calls++; return null; }, 3, 10), "could not read", "startguard: unreadable meta refuses");
                Check(calls == 3 && untouched("b2"), "startguard: unreadable: read 3 times, no Begin, .029 unchanged");

                // The real reader on a meta held open without sharing (as while it is being replaced) refuses too.
                using (new FileStream(Path.Combine(gdir, "a.meta"), FileMode.Open, FileAccess.Read, FileShare.None))
                    Throws(() => Instance.StartGuard(found, gdir, "b3", w, null, 3, 10), "could not read", "startguard: locked meta refuses");
                Check(untouched("b3"), "startguard: locked meta: no Begin, .029 unchanged");

                // First read fails, a later one succeeds and names another file: refuses.
                calls = 0;
                Throws(() => Instance.StartGuard(found, gdir, "b4", w, id => ++calls < 2 ? null : WorkspaceGuard.FileOf(gdir, id), 3, 10), "protects the workspace file", "startguard: fail then different file refuses");
                Check(calls == 2 && untouched("b4"), "startguard: fail then different: no Begin, .029 unchanged");

                // First read fails, a later one succeeds and names the same file: the start goes on and Begin shares its copy.
                File.WriteAllText(Path.Combine(gdir, "s.bak"), "E9");
                File.WriteAllLines(Path.Combine(gdir, "s.meta"), new[] { "existed=True", "kept=" + WorkspaceGuard.HashOf(ws29), "last=" + WorkspaceGuard.HashOf(ws29), "file=" + ws29 });
                var ws = new FakeWorld(); ws.Others.Add("s");
                calls = 0;
                var g = Instance.StartGuard(found, gdir, "b5", ws, id => ++calls < 2 ? null : WorkspaceGuard.FileOf(gdir, id), 3, 10);
                Check(calls == 2 && File.Exists(Path.Combine(gdir, "b5.meta")), "startguard: fail then same file: started");
                File.WriteAllText(ws29, "T1"); g.Tick();
                Check(File.ReadAllText(ws29) == "E9" && g.End() == null, "startguard: same file: tool write put back, ends clean");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ---- IT-789: books scope (name matching, read-back comparison, arguments, schema)
        sealed class FakeTree : IBookTree
        {
            readonly List<BookNode> all = new List<BookNode>();
            public int ChildCalls;
            public FakeTree(params string[] paths)
            {
                // paths of raw names separated by '>', parents listed before children
                foreach (var p in paths)
                {
                    var names = p.Split('>');
                    var parent = names.Length == 1 ? null : all.First(n => n.Names.SequenceEqual(names.Take(names.Length - 1)));
                    int index = all.Count(n => n.Names.Length == names.Length && (parent == null || parent.IsAncestorOf(n)));
                    var path = (parent == null ? new int[0] : parent.Path).Concat(new[] { index }).ToArray();
                    all.Add(new BookNode { Path = path, Names = names });
                }
            }
            public List<BookNode> Top() { return all.Where(n => n.Depth <= 2).ToList(); }
            public List<BookNode> Children(BookNode n) { ChildCalls++; return all.Where(c => c.Depth == n.Depth + 1 && n.IsAncestorOf(c)).ToList(); }
            public BookNode Get(string p) { return all.First(n => string.Join(">", n.Names) == p); }
        }
        static FakeTree SampleTree()
        {
            return new FakeTree(
                "תנ\"ך", "תנ\"ך>בראשית", "תנ\"ך>דניאל", "תנ\"ך>אסתר",
                "מפרשי תנ\"ך", "מפרשי תנ\"ך>רש\"י ומפרשיו", "מפרשי תנ\"ך>רש\"י ומפרשיו>רש\"י",
                "מפרשי תנ\"ך>רש\"י ומפרשיו>רש\"י>בראשית", "מפרשי תנ\"ך>רש\"י ומפרשיו>רש\"י>דניאל",
                "מפרשי תנ\"ך>מפרשי תנ\"ך", "מפרשי תנ\"ך>מפרשי תנ\"ך>מצודת דוד", "מפרשי תנ\"ך>מפרשי תנ\"ך>רבינו] בחיי]",
                "מפרשים ופוסקים על הבבלי והירושלמי", "מפרשים ופוסקים על הבבלי והירושלמי>מפרשי הבבלי", "מפרשים ופוסקים על הבבלי והירושלמי>מפרשי הבבלי>רש\"י",
                "ספרי חסידות", "ספרי חסידות>אור המאיר", "ספרי חסידות>אור המאיר>בראשית",
                "(ספרי שאלות ותשובות (שו\"ת", "(ספרי שאלות ותשובות (שו\"ת>ראשונים", "(ספרי שאלות ותשובות (שו\"ת>ראשונים>רשב\"א");
        }

        static void BookTests()
        {
            Check(BookMatch.Key("רש\"י") == BookMatch.Key("רשי") && BookMatch.Key("רש''י") == BookMatch.Key("רשי"), "key: quotes and gershayim ignored");
            Check(BookMatch.Key("(ספרי שאלות ותשובות (שו\"ת") == BookMatch.Key("ספרי שאלות ותשובות (שו\"ת)"), "key: visual order and brackets ignored");
            Check(BookMatch.Key("מצודת  דוד ") == BookMatch.Key("מצודת דוד") && BookMatch.Key("דניאל") != BookMatch.Key("דניאל א"), "key: spaces; words count");
            Check(BookMatch.Display("(ספרי שאלות ותשובות (שו\"ת") == "ספרי שאלות ותשובות (שו\"ת)", "display: parentheses");
            Check(BookMatch.Display("רבינו] בחיי]") == "[רבינו] בחיי", "display: square brackets");
            Check(BookMatch.Segments(" מפרשי תנ\"ך / רש\"י /דניאל ").SequenceEqual(new[] { "מפרשי תנ\"ך", "רש\"י", "דניאל" }), "segments split on /");

            var t = SampleTree();
            string note;
            Check(BookMatch.Resolve(t, "דניאל", out note).Display == "תנ\"ך / דניאל" && note == null, "resolve: unique book");
            Check(BookMatch.Resolve(t, "רשב\"א").Display == "ספרי שאלות ותשובות (שו\"ת) / ראשונים / רשב\"א", "resolve: third level, quotes");
            Check(BookMatch.Resolve(t, "רשבא").Name == "רשב\"א", "resolve: without gershayim");
            Check(BookMatch.Resolve(t, "תנ\"ך").Display == "תנ\"ך", "resolve: a category");
            Check(BookMatch.Resolve(t, "[רבינו] בחיי").Name == "רבינו] בחיי]", "resolve: a visually stored name, typed in reading order");
            Throws(() => BookMatch.Resolve(t, "רש\"י"), "matches 2 places", "resolve: same depth twice is ambiguous");
            Throws(() => BookMatch.Resolve(t, "רש\"י"), "מפרשי תנ\"ך / רש\"י ומפרשיו / רש\"י", "resolve: ambiguity lists the paths");
            Check(BookMatch.Resolve(t, "רש\"י ומפרשיו / רש\"י").Path.SequenceEqual(t.Get("מפרשי תנ\"ך>רש\"י ומפרשיו>רש\"י").Path), "resolve: a path tells them apart");
            Check(BookMatch.Resolve(t, "מפרשי הבבלי/רשי").Path.SequenceEqual(t.Get("מפרשים ופוסקים על הבבלי והירושלמי>מפרשי הבבלי>רש\"י").Path), "resolve: any ancestor in the path");
            var b = BookMatch.Resolve(t, "בראשית", out note);
            Check(b.Display == "תנ\"ך / בראשית" && note != null && note.Contains("אור המאיר"), "resolve: the one highest match is taken, with a note");
            Throws(() => BookMatch.Resolve(t, "חסידות / בראשית"), "no book named", "resolve: a part of an ancestor name is not a match");
            Check(BookMatch.Resolve(t, "ספרי חסידות / בראשית").Path.SequenceEqual(t.Get("ספרי חסידות>אור המאיר>בראשית").Path), "resolve: path picks the deeper one");
            t.ChildCalls = 0;
            var deep = BookMatch.Resolve(t, "רש\"י ומפרשיו / רש\"י / דניאל");
            Check(deep.Path.SequenceEqual(t.Get("מפרשי תנ\"ך>רש\"י ומפרשיו>רש\"י>דניאל").Path) && t.ChildCalls == 1, "resolve: fourth level read only under the named parent");
            t.ChildCalls = 0;
            Throws(() => BookMatch.Resolve(t, "ספר שאינו קיים"), "no book named", "resolve: unknown name");
            Check(t.ChildCalls == 0, "resolve: one-part unknown name reads nothing deeper");
            Throws(() => BookMatch.Resolve(t, "מצודת"), "מצודת דוד", "resolve: unknown name lists close names");
            Throws(() => BookMatch.Resolve(t, "רש\"י ומפרשיו / רש\"י / ספר"), "no book named", "resolve: unknown fourth level");
            Throws(() => BookMatch.Resolve(t, " / "), "empty book name", "resolve: empty");
            Throws(() => BookMatch.Resolve(t, "\"\""), "no letters", "resolve: no letters");

            var all = BookMatch.ResolveAll(t, new List<string> { "דניאל", "תנ\"ך / דניאל", "אסתר" });
            Check(all.Count == 2, "resolve all: duplicates removed");
            all = BookMatch.ResolveAll(t, new List<string> { "דניאל", "תנ\"ך" });
            Check(all.Count == 1 && all[0].Display == "תנ\"ך", "resolve all: a book inside a chosen category is dropped");
            Throws(() => BookMatch.ResolveAll(t, new List<string>()), "at least one", "resolve all: empty list");
            Throws(() => BookMatch.ResolveAll(t, Enumerable.Repeat("דניאל", 21).ToList()), "at most 20", "resolve all: cap");
            List<string> notes;
            BookMatch.ResolveAll(t, new List<string> { "בראשית", "דניאל" }, out notes);
            Check(notes.Count == 1, "resolve all: notes collected");

            var dan = t.Get("תנ\"ך>דניאל"); var est = t.Get("תנ\"ך>אסתר"); var tanach = t.Get("תנ\"ך");
            var other = new BookNode { Path = new[] { -1 }, Names = new[] { "משהו" } };
            Check(BookMatch.Compare(new List<BookNode> { dan, est }, new List<BookNode> { est, dan }) == null, "compare: same set");
            Check(BookMatch.Compare(new List<BookNode> { dan }, new List<BookNode>()).Contains("not selected"), "compare: missing");
            Check(BookMatch.Compare(new List<BookNode> { dan }, new List<BookNode> { dan, other }).Contains("משהו"), "compare: extra");
            Check(BookMatch.Compare(new List<BookNode> { dan }, new List<BookNode> { tanach }) != null, "compare: a whole category instead of one book is a difference (never wider)");

            Throws(() => Tools.CheckScope(1, new List<string> { "דניאל" }), "cannot be used together", "scope: books with all_databases true");
            Tools.CheckScope(0, new List<string> { "דניאל" }); Tools.CheckScope(-1, new List<string> { "דניאל" }); Tools.CheckScope(1, null);
            Check(true, "scope: books with all_databases false or omitted, and no books, are fine");
            Throws(() => Tools.CheckScope(-1, new List<string>()), "at least one", "scope: empty books");
            Check(Tools.NeedsFreshScope(true, null) && !Tools.NeedsFreshScope(false, null) && !Tools.NeedsFreshScope(true, new List<string> { "x" }),
                "scope: a search without books after a scoped one restarts the instance");

            // the real schema of search
            var defs = ToolDefs.Build(null);
            var search = defs.First(d => d.Name == "search");
            var props = (Dictionary<string, object>)search.Schema["properties"];
            var bs = (Dictionary<string, object>)props["books"];
            Check((string)bs["type"] == "array" && Convert.ToInt32(bs["maxItems"]) == BookMatch.MaxBooks && Convert.ToInt32(bs["minItems"]) == 1, "schema: books is an array of at most 20");
            var v = McpServer.Validate(search.Schema, A("query", "x", "books", new System.Collections.ArrayList { " דניאל ", "אסתר" }));
            var vl = v["books"] as List<string>;
            Check(vl != null && vl.SequenceEqual(new[] { "דניאל", "אסתר" }), "validate: books trimmed into a list");
            Check(McpServer.Validate(search.Schema, A("query", "x", "books", new object[] { "דניאל" }))["books"] is List<string>, "validate: object[] accepted");
            Throws(() => McpServer.Validate(search.Schema, A("query", "x", "books", "דניאל")), "array of strings", "validate: a string is not an array");
            Throws(() => McpServer.Validate(search.Schema, A("query", "x", "books", new System.Collections.ArrayList { 5 })), "array of strings", "validate: non-string item");
            Throws(() => McpServer.Validate(search.Schema, A("query", "x", "books", new System.Collections.ArrayList())), "at least 1", "validate: empty array");
            Throws(() => McpServer.Validate(search.Schema, A("query", "x", "books", new System.Collections.ArrayList(Enumerable.Repeat("a", 21).ToList()))), "at most 20", "validate: too many");
            Throws(() => McpServer.Validate(search.Schema, A("query", "x", "books", new System.Collections.ArrayList { " " })), "empty string", "validate: blank item");
            Throws(() => McpServer.Validate(search.Schema, A("query", "x", "books", new System.Collections.ArrayList { new string('א', 121) })), "too long", "validate: long item");
            Throws(() => McpServer.Validate(search.Schema, A("query", "x", "books", new System.Collections.ArrayList { "a\nb" })), "single lines", "validate: multi-line item");
            var lim = (Dictionary<string, object>)props["limit"];
            Check(Convert.ToInt32(lim["maximum"]) == Tools.MaxResultsPerCall, "schema: per-call result cap unchanged");
        }

        static void ParseTests(string dir)
        {
            string q;
            var items = SaveParse.ParseResults(SaveParse.ReadSaved(Path.Combine(dir, "results.txt")), out q);
            Check(q == "#נר [1:3] #שבת", "results: query header");
            Check(items.Count == 3, "results: three items (got " + items.Count + ")");
            if (items.Count == 3)
            {
                Check(items[0].Number == 1 && items[0].Source == "ספר לדוגמה פרק א סימן ב", "results: first source");
                Check(items[1].Snippet == "שורה ראשונה של הקטע. שורה שנייה של הקטע.", "results: multi-line snippet joined");
                Check(items[2].Source.StartsWith("ספר שלישי") && items[2].Snippet.Contains("2. לא מספר תוצאה"), "results: a numbered line inside a snippet stays in the snippet");
            }
            // IT-789: a saved file still open by its writer is read only after it is closed
            string tmp = Path.Combine(Path.GetTempPath(), "rmcp-locked-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var w = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read);
                var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("שלום")).ToArray();
                w.Write(bytes, 0, bytes.Length); w.Flush();
                var closer = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(400); w.Dispose(); });
                closer.Start();
                Check(SaveParse.ReadSaved(tmp, 5000) == "שלום", "read saved: waits until the writer closes the file");
                closer.Join();
                var w2 = new FileStream(tmp, FileMode.Open, FileAccess.Write, FileShare.Read);
                bool threw = false;
                try { SaveParse.ReadSaved(tmp, 300); } catch (ToolError e) { threw = e.Message.Contains("kept the saved file open"); }
                w2.Dispose();
                Check(threw, "read saved: still open after the wait is an error, not a partial read");
                // IT-827: no file at the tool's path is a ToolError, never a raw FileNotFoundException
                Throws(() => SaveParse.ReadSaved(tmp + ".missing", 300), "did not save to the tool's path", "read saved: missing file");
            }
            finally { try { File.Delete(tmp); } catch { } }

            string title;
            string text = SaveParse.ParseText(SaveParse.ReadSaved(Path.Combine(dir, "text.txt")), out title);
            Check(title == "ספר לדוגמה דף ב עמוד א", "text: title");
            Check(text.StartsWith("פסקה ראשונה.") && text.EndsWith("פסקה שנייה."), "text: body without the closing stars");
            RangeTests();
        }

        /// <summary>A results save as Responsa writes it, with these result numbers (real numbers, as measured).</summary>
        static List<ResultItem> Saved(params int[] numbers)
        {
            var sb = new StringBuilder("החיפוש : #נר [1:3] #שבת\r\n===================\r\n\r\n");
            foreach (int n in numbers)
                sb.Append(string.Format(" {0}.  ספר {0} סימן א\r\nקטע של תוצאה {0}.\r\nשורה שנייה של הקטע.\r\n", n));
            string q;
            return SaveParse.ParseResults(sb.ToString(), out q);
        }

        /// <summary>IT-793: get_results returns a save only if it holds exactly results from..to.</summary>
        static void RangeTests()
        {
            var three = Saved(3);
            var five = Saved(1, 2, 3, 4, 5);
            Check(five.Count == 5 && five[4].Number == 5 && five[4].Snippet == "קטע של תוצאה 5. שורה שנייה של הקטע.", "range: synthetic save parses as five results");
            Check(three.Count == 1 && three[0].Number == 3 && three[0].Source == "ספר 3 סימן א","range: a save of result 3 parses with its real number");
            Check(SaveParse.CheckRange(three, 3, 3) == null, "range: from 3 count 1, exact save passes");
            Check(SaveParse.CheckRange(Saved(101, 102), 101, 102) == null, "range: 101-102 exact passes");
            Check(SaveParse.CheckRange(Saved(1, 2, 3, 4, 5), 1, 5) == null, "range: first page 1-5 passes");
            // the IT-789 finding: from 3 count 1 after a first page of 5 saved 1-5
            string leak = SaveParse.CheckRange(Saved(1, 2, 3, 4, 5), 3, 3);
            Check(leak != null && leak.Contains("3-3") && leak.Contains("1,2,3,4,5"), "range: marks left from an earlier save fail (" + leak + ")");
            Check(SaveParse.CheckRange(Saved(1, 2, 3, 5, 6), 5, 6) != null, "range: extra results before the range fail");
            Check(SaveParse.CheckRange(Saved(3, 4, 7), 3, 4) != null, "range: extra result after the range fails");
            Check(SaveParse.CheckRange(Saved(4, 5), 3, 4) != null, "range: shifted by one fails");
            Check(SaveParse.CheckRange(Saved(3), 3, 4) != null, "range: a missing result fails");
            Check(SaveParse.CheckRange(Saved(3, 5), 3, 4) != null, "range: a gap fails even with the right count");
            Check(SaveParse.CheckRange(Saved(), 1, 1) != null, "range: an empty save fails");
            var many = SaveParse.CheckRange(Saved(Enumerable.Range(1, 40).ToArray()), 2, 3);
            Check(many != null && many.Contains("saved 40") && many.EndsWith("..."), "range: long mismatch message is shortened (" + many + ")");
        }
    }
}
