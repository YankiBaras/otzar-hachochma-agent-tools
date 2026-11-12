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
                Check(!(bool)rep["version_tested"] && ((string)rep["version_note"]).Contains("not tested yet") && (string)rep["workspace_file"] == one.Path, "report: 30 not tested (not refused), file shown");
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
