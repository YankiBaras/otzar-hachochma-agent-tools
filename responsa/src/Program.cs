// IT-780: responsa-mcp.exe entry point.
//   responsa-mcp.exe            MCP server on stdin/stdout (what MCP clients run)
//   responsa-mcp.exe doctor     start the private instance, run a control search, print a report, stop
//   responsa-mcp.exe version
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ResponsaMcp
{
    internal static class Program
    {
        static int Main(string[] argv)
        {
            string cmd = argv.Length > 0 ? argv[0].ToLowerInvariant() : "serve";
            if (cmd == "version" || cmd == "--version") { Console.WriteLine(McpServer.Name + "-mcp " + McpServer.Version); return 0; }

            string stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResponsaMcp");
            Directory.CreateDirectory(stateDir);
            Instance.CleanupOrphans(stateDir);   // copies it cannot settle stay as *.kept.* and are listed by `status`
            CleanupTemp(stateDir);

            // One private desktop and one temp folder per server process, so two MCP clients never share anything.
            string me = Process.GetCurrentProcess().Id + "-" + Proc.Self();
            string tmpDir = Path.Combine(stateDir, "tmp", me);
            var ui = new UiThread("ResponsaMcp-" + Process.GetCurrentProcess().Id);
            ui.Start();
            var inst = new Instance(ui, stateDir);
            var tools = new Tools(ui, inst, tmpDir);
            bool stopped = false;
            Action shutdown = delegate
            {
                if (stopped) return;
                stopped = true;
                try { inst.Stop(); } catch { }
                try { Directory.Delete(tmpDir, true); } catch { }
                try { ui.Shutdown(); } catch { }
            };
            AppDomain.CurrentDomain.ProcessExit += delegate { shutdown(); };

            if (cmd == "doctor") { try { return Doctor(tools, argv.Length > 1 ? argv[1] : "#שבת [1:2] #חנוכה"); } finally { shutdown(); } }
            if (cmd != "serve") { Console.Error.WriteLine("usage: responsa-mcp.exe [doctor [query] | version]"); return 2; }

            var server = new McpServer(ToolDefs.Build(tools), Console.Error);
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var stdout = Console.OpenStandardOutput();
            var utf8 = new UTF8Encoding(false);
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                string resp = server.Handle(line);
                if (resp == null) continue;
                byte[] b = utf8.GetBytes(resp + "\n");
                stdout.Write(b, 0, b.Length); stdout.Flush();
            }
            shutdown();
            return 0;
        }

        static int Doctor(Tools tools, string query)
        {
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            try
            {
                o.WriteLine("install: " + McpServer.Serialize(Tools.Installation()));   // IT-785: before the instance starts
                o.WriteLine("status: " + McpServer.Serialize(tools.Status()));
                o.WriteLine("search: " + McpServer.Serialize(tools.Search(query, 1, 3, 180)));
                o.WriteLine("open_text: " + Trim(McpServer.Serialize(tools.OpenText("בבלי ברכות ב א", 600, 30))));
                return 0;
            }
            catch (ToolError e) { o.WriteLine("ERROR: " + e.Message); return 1; }
        }

        /// <summary>Removes temp folders of servers that are gone (a crash may have left extracted text there).</summary>
        static void CleanupTemp(string stateDir)
        {
            string root = Path.Combine(stateDir, "tmp");
            if (!Directory.Exists(root)) return;
            foreach (var f in Directory.GetFiles(root)) { try { File.Delete(f); } catch { } }   // files of version 1.0
            foreach (var d in Directory.GetDirectories(root))
            {
                var parts = Path.GetFileName(d).Split('-');
                int pid; long start;
                bool alive = parts.Length == 2 && int.TryParse(parts[0], out pid) && long.TryParse(parts[1], out start) && Proc.IsSame(pid, start);
                if (!alive) { try { Directory.Delete(d, true); } catch { } }
            }
        }
        static string Trim(string s) { return s.Length > 1500 ? s.Substring(0, 1500) + "..." : s; }
    }

    internal static class ToolDefs
    {
        static Dictionary<string, object> Obj(Dictionary<string, object> props, params string[] required)
        {
            var d = new Dictionary<string, object> { { "type", "object" }, { "properties", props }, { "additionalProperties", false } };
            if (required.Length > 0) d["required"] = new List<object>(required);
            return d;
        }
        static Dictionary<string, object> Str(int maxLen, string desc) { return new Dictionary<string, object> { { "type", "string" }, { "maxLength", maxLen }, { "description", desc } }; }
        static Dictionary<string, object> Int(int min, int max, string desc) { return new Dictionary<string, object> { { "type", "integer" }, { "minimum", min }, { "maximum", max }, { "description", desc } }; }
        static Dictionary<string, object> Bool(string desc) { return new Dictionary<string, object> { { "type", "boolean" }, { "description", desc } }; }
        static int I(Dictionary<string, object> a, string k, int def) { return a.ContainsKey(k) ? (int)a[k] : def; }

        public static List<ToolDef> Build(Tools t)
        {
            return new List<ToolDef> {
                new ToolDef {
                    Name = "status",
                    Description = "Start (if needed) the tool's private Responsa instance and report the installation, version, instance and open searches. The instance runs on a hidden desktop; the user's own Responsa is never touched.",
                    Schema = Obj(new Dictionary<string, object>()),
                    Run = a => t.Status() },
                new ToolDef {
                    Name = "search",
                    Description = "Full-text search in Responsa with its ADVANCED query syntax (see the responsa skill), e.g. '#נר [1:3] #שבת', '5: %קדש #שם', 'משפט: $מנהג $שבת'. Returns the total, the number of results per database, and the first results (source line + snippet). Responsa refuses searches with more than 32,000 results: narrow the query.",
                    Schema = Obj(new Dictionary<string, object> {
                        { "query", Str(300, "Query in Responsa advanced syntax, typed in logical order.") },
                        { "all_databases", Bool("true = search all databases (recommended); false = only the databases selected in Responsa; omitted = leave Responsa's setting.") },
                        { "limit", Int(0, Tools.MaxResultsPerCall, "How many results to return now (default 20).") },
                        { "timeout_sec", Int(10, 600, "Default 180.") } }, "query"),
                    Run = a => t.Search((string)a["query"], a.ContainsKey("all_databases") ? ((bool)a["all_databases"] ? 1 : 0) : -1, I(a, "limit", 20), I(a, "timeout_sec", 180)) },
                new ToolDef {
                    Name = "get_results",
                    Description = "More results of an earlier search (source line + snippet), by position. Only the last 3 searches are kept.",
                    Schema = Obj(new Dictionary<string, object> {
                        { "search_id", Int(1, 1000000, "search_id returned by search.") },
                        { "from", Int(1, 32000, "First result number (1-based).") },
                        { "count", Int(1, Tools.MaxResultsPerCall, "How many (default 20).") } }, "search_id", "from"),
                    Run = a => t.GetResults((int)a["search_id"], (int)a["from"], I(a, "count", 20)) },
                new ToolDef {
                    Name = "open_text",
                    Description = "Text of one unit (amud, chapter, siman, teshuva...) by reference, as typed in Responsa's 'כתיבת מקורות', e.g. 'בבלי ברכות כז ב', 'רמבם תפילה ד א', 'שוע אוח רסג', 'משנה ברורה רסג'. Use a source line from search results to read a result in full. Returns Responsa's source title and the text.",
                    Schema = Obj(new Dictionary<string, object> {
                        { "reference", Str(120, "Reference in Hebrew, no commas; quotation marks optional (רמבם, שוע).") },
                        { "max_chars", Int(500, Tools.MaxTextChars, "Maximum characters of text to return (default 20000).") },
                        { "timeout_sec", Int(5, 120, "Default 30.") } }, "reference"),
                    Run = a => t.OpenText((string)a["reference"], I(a, "max_chars", 20000), I(a, "timeout_sec", 30)) },
                new ToolDef {
                    Name = "close_windows",
                    Description = "Close all result and text windows in the tool's private Responsa instance (housekeeping; search_ids become invalid).",
                    Schema = Obj(new Dictionary<string, object>()),
                    Run = a => t.CloseWindows() },
            };
        }
    }
}
