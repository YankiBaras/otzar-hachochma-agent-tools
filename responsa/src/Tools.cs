// IT-780: the MCP tools. Small, per-question extraction only (hard caps below).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ResponsaMcp
{
    /// <summary>
    /// IT-868: the order of a distance range differs between Responsa versions. Measured: Responsa 29 (here) and 34 (a
    /// pilot's doctor) take [min:max] and reject [3:1]; Responsa 31 (a pilot) rejects [1:3] with "המרחק המקסימלי קטן
    /// מהמרחק המינימלי" and takes [3:1]. So 31 reads the first number as the maximum. Swapping both numbers of every
    /// [a:b] gives the same range in that version's order, also with a negative part: [-5:5] (from 5 before to 5 after)
    /// becomes [5:-5], i.e. max 5, min -5 on 31. A range of one number ([3]) or with equal numbers is left as it is.
    /// </summary>
    internal static class Distance
    {
        public const string OrderError = "המרחק המקסימלי קטן מהמרחק המינימלי";
        static readonly System.Text.RegularExpressions.Regex Range = new System.Text.RegularExpressions.Regex(@"\[\s*(-?\d+)\s*:\s*(-?\d+)\s*\]");

        public static bool IsOrderError(IEnumerable<string> messages)
        {
            return messages != null && messages.Any(m => m != null && m.Replace("  ", " ").Contains(OrderError));
        }

        /// <summary>Every [a:b] as [b:a]; the rest of the query unchanged.</summary>
        public static string Swap(string query)
        {
            return Range.Replace(query, m => "[" + m.Groups[2].Value + ":" + m.Groups[1].Value + "]");
        }

        public static string Note(string ranAs, List<string> first, bool worked)
        {
            string said = first != null && first.Count > 0 ? " (Responsa: " + first[0] + ")" : "";
            return worked
                ? "this Responsa version wants the other order in [a:b]" + said + ": Responsa 31 reads [max:min], 29 and 34 [min:max]. "
                  + "The numbers of every [a:b] were swapped, and the search ran as: " + ranAs
                : "Responsa rejected the distance range in both orders" + said + "; check the numbers in [a:b] (ran last as: " + ranAs + ")";
        }
    }

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
        bool scoped;                                              // IT-789: the instance's selection is a books scope
        readonly BookIndexCache bookIndex = new BookIndexCache(); // IT-789: names of the database tree, read once

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
            if (!inst.Alive) { searches.Clear(); scoped = false; }
            inst.EnsureRunning();
        }
        void Restart()
        {
            searches.Clear();
            scoped = false;
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
        /// <summary>IT-871: removes the temp file of a save and the fresh names it may have used (Ui.SavePaths).</summary>
        static void DeleteSaves(string path) { foreach (var p in Ui.SavePaths(path)) Delete(p); }

        // ------------------------------------------------------------------ status
        public object Status()
        {
            return Op(delegate ()
            {
                var r = new Dictionary<string, object>();
                r["responsa_exe"] = inst.Install.Exe;
                foreach (var kv in VersionReport(inst.Install, inst.Workspace)) r[kv.Key] = kv.Value;
                r["data_folder"] = inst.Install.DataDir;
                r["responsa_installs"] = inst.Install.Report();   // IT-827: every install found, the one used marked
                r["instance_pid"] = (long)inst.Pid;
                r["private_desktop"] = ui.DesktopName;
                r["environment"] = ResponsaEnv.Get().Report();   // IT-868
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
            r["responsa_installs"] = ins.Report();   // IT-827
            r["environment"] = ResponsaEnv.Get().Report();   // IT-868: what RESPONSA.exe will be started with
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
                r["version_note"] = "responsa-mcp was tested live on Responsa " + WorkspaceFile.TestedMajor + " only. Other versions are expected to work; if something fails, report it with the output of responsa-mcp.exe doctor.";
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
        /// <summary>IT-789: books and all_databases: true contradict each other.</summary>
        public static void CheckScope(int allDatabases, IList<string> books)
        {
            if (books == null) return;
            if (allDatabases == 1) throw new ToolError("books and all_databases: true cannot be used together; leave all_databases out (or false) to search only the named books");
            if (books.Count == 0) throw new ToolError("books: give at least one book name, or leave books out");
            if (books.Count > BookMatch.MaxBooks) throw new ToolError("books: at most " + BookMatch.MaxBooks + " names per search");
        }

        /// <summary>
        /// IT-789: after a search with books, the instance's database selection is those books (in its memory only; never
        /// saved). Any later search without books would inherit it: measured, even "חיפוש בכל המאגרים" (all_databases:
        /// true) stays limited ("החיפוש מוגבל לספרים"). So the instance is restarted first: a new instance reads the
        /// selection saved in Responsa's data folder, i.e. the user's own, and all_databases works again.
        /// </summary>
        public static bool NeedsFreshScope(bool scoped, IList<string> books)
        {
            return books == null && scoped;
        }

        public object Search(string query, int allDatabases, List<string> books, int limit, int timeoutSec)
        {
            CheckScope(allDatabases, books);
            string note = null;
            if (NeedsFreshScope(scoped, books))
            {
                Restart();
                note = "the previous search was limited to books, so the private Responsa instance was restarted to lift that limit; earlier search_ids are gone";
            }
            return Op(delegate ()
            {
                int pid = (int)inst.Pid;
                BookSelection scope = null;
                if (books != null)
                {
                    scope = ui.Run(delegate () { return Ui.SelectBooks((uint)pid, books, bookIndex, delegate { scoped = true; }); }, 240000);
                    allDatabases = 0;
                }
                int all = allDatabases;
                Func<string, Ui.SearchOutcome> run = q => ui.Run(delegate () { return Ui.Search((uint)pid, q, all, timeoutSec); }, (timeoutSec + 30) * 1000);
                var outcome = run(query);
                // IT-868: Responsa 31 reads [a:b] as [max:min], 29 and 34 as [min:max]. Rejected for that: once more, swapped.
                string ranAs = null; List<string> firstMessages = null;
                if (outcome.Window == IntPtr.Zero && Distance.IsOrderError(outcome.Messages))
                {
                    string swapped = Distance.Swap(query);
                    if (swapped != query) { firstMessages = outcome.Messages; ranAs = swapped; outcome = run(swapped); }
                }
                var r = new Dictionary<string, object>();
                r["query"] = query;
                if (ranAs != null)
                {
                    r["ran_as"] = ranAs;
                    r["distance_note"] = Distance.Note(ranAs, firstMessages, outcome.Window != IntPtr.Zero || !Distance.IsOrderError(outcome.Messages));
                }
                if (scope != null)
                {
                    r["scope"] = scope.Nodes.Select(b => (object)b.Display).ToList();
                    if (scope.Notes.Count > 0) r["books_note"] = scope.Notes;
                }
                if (note != null) r["note"] = note;
                r["seconds"] = outcome.Seconds;
                if (outcome.Messages.Count > 0) r["responsa_messages"] = outcome.Messages;
                foreach (var m in outcome.Messages)
                {
                    string hint = ResponsaEnv.Hint(m);   // IT-868: error 15
                    if (hint != null) { r["hint"] = hint; break; }
                }
                if (outcome.Breakdown.Count > 0)
                    r["by_database"] = outcome.Breakdown.OrderByDescending(kv => kv.Value).Select(kv => (object)new Dictionary<string, object> { { "database", kv.Key }, { "results", kv.Value } }).ToList();
                if (outcome.Window == IntPtr.Zero) { r["total"] = 0; return (object)r; }

                var st = new SearchState { Id = nextSearchId++, Query = ranAs ?? query, Total = outcome.Total, Window = outcome.Window };
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
                // IT-871: the save may end at a fresh name beside `path` (Ui.SaveVia); every one of them is removed.
                string saved = ui.Run(delegate () { return Ui.SaveResultRange((uint)pid, st.Window, st.Total, from, to, path); }, 150000);
                string q;
                items = SaveParse.ParseResults(SaveParse.ReadSaved(saved), out q);
            }
            catch (WindowGone) { searches.Remove(st); throw; }
            finally { DeleteSaves(path); }
            // IT-793: the save carries each result's real number. Anything but exactly from..to (marks of an earlier
            // save left over, a missing result) is never returned; the instance is restarted, which drops every mark.
            string wrong = SaveParse.CheckRange(items, from, to);
            if (wrong != null) throw new StuckError("Responsa saved other results than asked (" + wrong + "); nothing was returned, and earlier search_ids are gone");
            return items.Select(it => (object)new Dictionary<string, object> { { "n", it.Number }, { "source", it.Source }, { "snippet", it.Snippet } }).ToList();
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
                    string saved = ui.Run(delegate () { Ui.Activate((uint)pid, t.Window); return Ui.SaveVia((uint)pid, Ids.TextSaveAll, path); }, 150000);
                    text = SaveParse.ParseText(SaveParse.ReadSaved(saved), out title);
                }
                finally
                {
                    DeleteSaves(path);
                    ui.Run(delegate () { Ui.CloseWindow((uint)pid, t.Window); return 0; }, 15000);
                }
                var r = new Dictionary<string, object>();
                r["reference"] = reference;
                foreach (var kv in SourceFields(t.Title, title, t.Chosen, t.Candidates, t.Names)) r[kv.Key] = kv.Value;
                if (text.Length > maxChars) { r["text"] = text.Substring(0, maxChars); r["truncated"] = true; r["chars_total"] = text.Length; }
                else r["text"] = text;
                return (object)r;
            });
        }

        /// <summary>
        /// IT-868: `source` is never empty. It is the text window's title; without one, the item opened from the choice
        /// list, else the title line of the saved text (`source_from` says which). With several matches, the list
        /// (`candidates`) and the item opened (`opened`) are given as well.
        /// </summary>
        internal static Dictionary<string, object> SourceFields(string windowTitle, string savedTitle, string chosen, int candidates, List<string> names)
        {
            var r = new Dictionary<string, object>();
            windowTitle = (windowTitle ?? "").Trim(); savedTitle = string.IsNullOrEmpty(savedTitle) ? null : savedTitle.Trim();
            chosen = string.IsNullOrEmpty(chosen) ? null : chosen.Trim();
            if (windowTitle.Length > 0) r["source"] = windowTitle;
            else if (chosen != null) { r["source"] = chosen; r["source_from"] = "the window had no title; this is the item opened from Responsa's choice list"; }
            else if (savedTitle != null) { r["source"] = savedTitle; r["source_from"] = "the window had no title; this is the title line of the saved text"; }
            else { r["source"] = "(Responsa gave no title for this text)"; }
            if (savedTitle != null && savedTitle != (string)r["source"]) r["source_short"] = savedTitle;
            if (candidates > 1)
            {
                var listed = (names ?? new List<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
                if (listed.Count > 0) r["candidates"] = listed.Cast<object>().ToList();
                if (chosen != null) r["opened"] = chosen;
                r["note"] = "Responsa matched " + candidates + " sources and opened its first choice" + (chosen != null ? " ('" + chosen + "')" : "")
                    + (listed.Count > 0 ? "; the others are in 'candidates'" + (candidates > listed.Count ? " (first " + listed.Count + ")" : "") : "")
                    + ". Give a more specific reference for another one.";
            }
            return r;
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
