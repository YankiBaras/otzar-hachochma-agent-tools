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
            Check(Ui.FixBidi("(תלמוד ירושלמי (וילנא") == "תלמוד ירושלמי (וילנא)", "FixBidi visual parenthesis");
            Check(Ui.FixBidi("תלמוד בבלי") == "תלמוד בבלי", "FixBidi plain");
            Check(McpServer.Serialize(new Dictionary<string, object> { { "t", "גמ' <a> & b" } }) == "{\"t\":\"גמ' <a> & b\"}", "Serialize keeps ' < > & plain");
            Console.WriteLine(string.Format("{0} passed, {1} failed", passed, failed));
            return failed == 0 ? 0 : 1;
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
            string title;
            string text = SaveParse.ParseText(SaveParse.ReadSaved(Path.Combine(dir, "text.txt")), out title);
            Check(title == "ספר לדוגמה דף ב עמוד א", "text: title");
            Check(text.StartsWith("פסקה ראשונה.") && text.EndsWith("פסקה שנייה."), "text: body without the closing stars");
        }
    }
}
