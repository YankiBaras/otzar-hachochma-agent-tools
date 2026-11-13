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
                o.WriteLine("search: " + McpServer.Serialize(tools.Search(query, 1, null, 3, 180)));
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
}
