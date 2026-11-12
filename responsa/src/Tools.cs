// IT-780: the MCP tools. Small, per-question extraction only (hard caps below).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ResponsaMcp
{
    internal sealed class Tools
    {
        public const int MaxResultsPerCall = 50;
        public const int MaxTextChars = 60000;
        const int KeepSearchWindows = 3;

        readonly UiThread ui;
        readonly Instance inst;
        readonly string tmpDir;
        readonly List<SearchState> searches = new List<SearchState>();
        int nextSearchId = 1;
        bool restartNeeded;

        sealed class SearchState { public int Id; public string Query; public int Total; public IntPtr Window; }

        public Tools(UiThread ui, Instance inst, string tmpDir)
        {
            this.ui = ui; this.inst = inst; this.tmpDir = tmpDir;
        }

        // ------------------------------------------------------------------ common wrapper (review 1, findings 4 and 5)
        /// <summary>
        /// Runs one tool operation on a ready instance. A stuck operation (timeout, a window that did not open) or an
        /// internal Responsa error restarts the instance at once, so nothing late from that operation (a search still
        /// running, a broken dialog) can leak into the next call. After any other failure the instance is checked for an
        /// internal-error box too.
        /// </summary>
        T Op<T>(Func<T> f)
        {
            Ready();
            try
            {
                T r = f();
                CheckHealth();
                return r;
            }
            catch (StuckError e)
            {
                Restart();
                throw new ToolError(e.Message + ". The private Responsa instance was restarted; try again.");
            }
            catch (ToolError)
            {
                CheckHealth();
                throw;
            }
        }
        void Ready()
        {
            if (restartNeeded) Restart();
            if (restartNeeded) throw new ToolError("the previous private Responsa has not exited yet; try again in a moment");
            if (!inst.Alive) searches.Clear();
            inst.EnsureRunning();
        }
        void Restart()
        {
            searches.Clear();
            restartNeeded = !inst.Stop();
        }
        void CheckHealth()
        {
            try
            {
                if (!inst.Alive) return;
                int pid = (int)inst.Pid;
                if (ui.Run(delegate () { return Ui.FatalDialog((uint)pid); }, 10000) != null) restartNeeded = true;
            }
            catch { restartNeeded = true; }
        }

        // ------------------------------------------------------------------ temp files (review 1, finding 6)
        string TempFile()
        {
            Directory.CreateDirectory(tmpDir);
            return Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ".txt");
        }
        static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        // ------------------------------------------------------------------ status
        public object Status()
        {
            return Op(delegate ()
            {
                var r = new Dictionary<string, object>();
                r["responsa_exe"] = inst.Install.Exe;
                foreach (var kv in VersionReport(inst.Install, inst.Workspace)) r[kv.Key] = kv.Value;
                r["data_folder"] = inst.Install.DataDir;
                r["instance_pid"] = (long)inst.Pid;
                r["private_desktop"] = ui.DesktopName;
                r["editor_responsa_running"] = inst.EditorInstances();
                r["open_searches"] = searches.Select(s => (object)new Dictionary<string, object> { { "search_id", s.Id }, { "query", s.Query }, { "total", s.Total } }).ToList();
                r["startup"] = inst.StartupLog.ToList();
                var kept = inst.KeptBackups();
                if (kept.Count > 0) r["kept_workspace_backups"] = kept;
                return (object)r;
            });
        }

        /// <summary>
        /// IT-785: the Responsa version and the workspace file, without starting the instance (used by `doctor`, so it is
        /// shown even when the instance cannot start).
        /// </summary>
        public static Dictionary<string, object> Installation()
        {
            var ins = Install.Find();
            var r = new Dictionary<string, object>();
            r["responsa_exe"] = ins.Exe;
            foreach (var kv in VersionReport(ins, WorkspaceFile.Find(ins.DataDir, ins.Major))) r[kv.Key] = kv.Value;
            r["data_folder"] = ins.DataDir;
            return r;
        }

        /// <summary>IT-785: version, whether it was tested, and the workspace file (found, none or several).</summary>
        public static Dictionary<string, object> VersionReport(Install ins, WorkspaceFile ws)
        {
            var r = new Dictionary<string, object>();
            r["responsa_version"] = ins.Version;
            bool tested = ins.Major == WorkspaceFile.TestedMajor;
            r["version_tested"] = tested;
            if (!tested)
                r["version_note"] = "responsa-mcp was tested on Responsa " + WorkspaceFile.TestedMajor + " only. This version is not tested yet: it is not refused, but report any problem.";
            if (ws == null) return r;
            if (ws.State == "found") r["workspace_file"] = ws.Path;
            else if (ws.State == "none") r["workspace_file"] = "none yet (protected as a new file: " + ws.Path + ")";
            else
            {
                r["workspace_file"] = "several";
                r["workspace_files"] = ws.Matches.Cast<object>().ToList();
            }
            return r;
        }

        // ------------------------------------------------------------------ search
        public object Search(string query, int allDatabases, int limit, int timeoutSec)
        {
            return Op(delegate ()
            {
                int pid = (int)inst.Pid;
                var outcome = ui.Run(delegate () { return Ui.Search((uint)pid, query, allDatabases, timeoutSec); }, (timeoutSec + 30) * 1000);
                var r = new Dictionary<string, object>();
                r["query"] = query;
                r["seconds"] = outcome.Seconds;
                if (outcome.Messages.Count > 0) r["responsa_messages"] = outcome.Messages;
                if (outcome.Breakdown.Count > 0)
                    r["by_database"] = outcome.Breakdown.OrderByDescending(kv => kv.Value).Select(kv => (object)new Dictionary<string, object> { { "database", kv.Key }, { "results", kv.Value } }).ToList();
                if (outcome.Window == IntPtr.Zero) { r["total"] = 0; return (object)r; }

                var st = new SearchState { Id = nextSearchId++, Query = query, Total = outcome.Total, Window = outcome.Window };
                searches.Add(st);
                TrimSearchWindows();
                r["search_id"] = st.Id;
                r["total"] = st.Total;
                int n = Math.Min(limit, st.Total);
                if (n > 0) r["results"] = Page(st, 1, n);
                if (st.Total > n) r["more"] = string.Format("results {0}-{1} of {2}; use get_results with search_id {3} for more", 1, n, st.Total, st.Id);
                return (object)r;
            });
        }

        void TrimSearchWindows()
        {
            while (searches.Count > KeepSearchWindows)
            {
                var old = searches[0]; searches.RemoveAt(0);
                int pid = (int)inst.Pid;
                ui.Run(delegate () { Ui.CloseWindow((uint)pid, old.Window); return 0; }, 15000);   // no-op unless still ours
            }
        }

        List<object> Page(SearchState st, int from, int count)
        {
            int to = Math.Min(st.Total, from + count - 1);
            string path = TempFile();
            int pid = (int)inst.Pid;
            List<ResultItem> items;
            try
            {
                ui.Run(delegate () { return Ui.SaveResultRange((uint)pid, st.Window, st.Total, from, to, path); }, 90000);
                string q;
                items = SaveParse.ParseResults(SaveParse.ReadSaved(path), out q);
            }
            catch (WindowGone) { searches.Remove(st); throw; }
            finally { Delete(path); }
            // Responsa numbers the saved (marked) results 1..n; give them their real numbers in the search.
            int i = from;
            return items.Select(it => (object)new Dictionary<string, object> { { "n", i++ }, { "source", it.Source }, { "snippet", it.Snippet } }).ToList();
        }

        public object GetResults(int searchId, int from, int count)
        {
            return Op(delegate ()
            {
                var st = searches.FirstOrDefault(s => s.Id == searchId);
                if (st == null) throw new ToolError("unknown search_id " + searchId + " (only the last " + KeepSearchWindows + " searches are kept, and none survive a restart); run search again");
                if (from > st.Total) throw new ToolError("this search has only " + st.Total + " results");
                var r = new Dictionary<string, object>();
                r["search_id"] = st.Id; r["query"] = st.Query; r["total"] = st.Total;
                r["results"] = Page(st, from, count);
                return (object)r;
            });
        }

        // ------------------------------------------------------------------ open text
        public object OpenText(string reference, int maxChars, int timeoutSec)
        {
            return Op(delegate ()
            {
                int pid = (int)inst.Pid;
                var t = ui.Run(delegate () { return Ui.OpenReference((uint)pid, reference, timeoutSec); }, (timeoutSec + 30) * 1000);
                string path = TempFile();
                string text, title;
                try
                {
                    ui.Run(delegate () { Ui.Activate((uint)pid, t.Window); return Ui.SaveVia((uint)pid, Ids.TextSaveAll, path); }, 90000);
                    text = SaveParse.ParseText(SaveParse.ReadSaved(path), out title);
                }
                finally
                {
                    Delete(path);
                    ui.Run(delegate () { Ui.CloseWindow((uint)pid, t.Window); return 0; }, 15000);
                }
                var r = new Dictionary<string, object>();
                r["reference"] = reference;
                r["source"] = t.Title;
                if (title != null && title != t.Title) r["source_short"] = title;
                if (t.Candidates > 1) r["note"] = "Responsa matched " + t.Candidates + " sources and opened its first choice (see 'source'); give a more specific reference for another one.";
                if (text.Length > maxChars) { r["text"] = text.Substring(0, maxChars); r["truncated"] = true; r["chars_total"] = text.Length; }
                else r["text"] = text;
                return (object)r;
            });
        }

        // ------------------------------------------------------------------ housekeeping
        public object CloseWindows()
        {
            if (!inst.Alive) { searches.Clear(); return new Dictionary<string, object> { { "closed", 0 } }; }
            return Op(delegate ()
            {
                int pid = (int)inst.Pid;
                int n = ui.Run(delegate () { return Ui.CloseAllMdi((uint)pid); }, 60000);
                searches.Clear();
                return (object)new Dictionary<string, object> { { "closed", n } };
            });
        }
    }
}
