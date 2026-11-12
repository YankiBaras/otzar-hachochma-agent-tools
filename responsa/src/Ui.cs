// IT-780: Responsa UI operations. Everything here runs on the UI thread (the private desktop).
// Command and control ids were measured on Responsa 29 (see docs/responsa-mcp-plan.md, section 13).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ResponsaMcp
{
    internal static class Ids
    {
        // main menu commands
        public const int Search = 32857, Iyun = 32781;
        // results window
        public const int MarkRange = 33438, SaveAllMarked = 32837;
        // text window
        public const int TextSaveAll = 32836;
        // search dialog
        public const int AdvancedType = 1209, AdvQuery = 1233, AllDatabases = 1024, RunSearch = 1;
        // "סימון טווח תוצאות"
        public const int RangeFrom = 1618, RangeTo = 1619, RangeMark = 1620, RangeUnmark = 1061, RangeOk = 1;
        // "עיון" → "כתיבת מקורות" page
        public const int RefEdit = 1021, RefFind = 1187, RefList = 1617, RefShow = 1;
    }

    internal static class Ui
    {
        static readonly IntPtr Z = IntPtr.Zero;

        public static IntPtr MainWindow(uint pid)
        {
            foreach (var h in Win.Tops(pid)) if (Win.Class(h) == "ResponsaProject") return h;
            return Z;
        }
        static IntPtr MdiClient(uint pid) { return Win.KidOfClass(MainWindow(pid), "MDIClient"); }
        public static List<IntPtr> MdiChildren(uint pid)
        {
            IntPtr mdi = MdiClient(pid);
            return Win.Kids(mdi).Where(k => Native.GetParent(k) == mdi).ToList();
        }
        /// <summary>
        /// Review 1, finding 3: a stored window handle is used only while it is still an MDI child of our own instance.
        /// Handles are recycled; a stale one could belong to the editor's Responsa or to another program.
        /// </summary>
        public static bool Owned(uint pid, IntPtr child)
        {
            return child != Z && Native.IsWindow(child) && Win.Pid(child) == pid && Native.GetParent(child) == MdiClient(pid);
        }
        public static void Activate(uint pid, IntPtr child)
        {
            if (!Owned(pid, child)) throw new WindowGone();
            Win.Send(MdiClient(pid), Native.WM_MDIACTIVATE, child.ToInt64(), 0); Thread.Sleep(150);
        }
        public static void Command(uint pid, int id) { Native.PostMessage(MainWindow(pid), Native.WM_COMMAND, (IntPtr)id, Z); }

        /// <summary>Closes one of our MDI windows; does nothing if the handle is no longer ours.</summary>
        public static void CloseWindow(uint pid, IntPtr child)
        {
            if (!Owned(pid, child)) return;
            Native.PostMessage(child, Native.WM_CLOSE, Z, Z);
            var sw = Stopwatch.StartNew();
            while (Native.IsWindow(child) && Win.Pid(child) == pid && sw.ElapsedMilliseconds < 5000) Thread.Sleep(100);
        }
        public static int CloseAllMdi(uint pid)
        {
            int n = 0;
            foreach (var c in MdiChildren(pid)) { CloseWindow(pid, c); n++; }
            return n;
        }

        public static IntPtr WaitDialog(uint pid, Func<IntPtr, bool> match, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                foreach (var d in Win.Dialogs(pid)) if (match(d)) return d;
                Thread.Sleep(150);
            }
            return Z;
        }
        static IntPtr WaitTitle(uint pid, string contains, int ms) { return WaitDialog(pid, d => Win.Text(d).Contains(contains), ms); }

        /// <summary>
        /// Answers a message box by the label of its own button: "No"/"לא" if it asks, otherwise OK/אישור.
        /// Review 1, finding 4: an internal-error box, or a box with no button we know, is never clicked; it throws
        /// FatalDialog so the instance is restarted instead (spike 11: a guessed button breaks the instance).
        /// </summary>
        public static void Dismiss(IntPtr d)
        {
            string fatal = FatalText(d);
            if (fatal != null) throw new FatalDialogError(fatal);
            int ok = -1, no = -1;
            foreach (var k in Win.Kids(d))
            {
                if (Win.Class(k) != "Button") continue;
                string t = Win.Text(k).Replace("&", "").Trim();
                if (t == "No" || t == "לא") no = Native.GetDlgCtrlID(k);
                else if (t == "OK" || t == "אישור") ok = Native.GetDlgCtrlID(k);
            }
            if (no >= 0) { Win.Press(d, no); return; }
            if (ok >= 0) { Win.Press(d, ok); return; }
            throw new FatalDialogError("unexpected Responsa window '" + Win.Text(d) + "': " + Win.DialogMessage(d));
        }
        static bool IsSearchDialog(IntPtr d) { return Win.Text(d).StartsWith("חיפוש"); }
        static bool IsIyun(IntPtr d) { return Win.Text(d).Contains("עיון"); }
        static bool IsBreakdown(IntPtr d) { return Regex.IsMatch(Win.Text(d), @"^\d+ תוצאות") && Win.KidOfClass(d, "SysListView32") != Z; }

        /// <summary>MFC error boxes (title "RESPONSA", e.g. "Encountered an improper argument.") mean the instance is no longer reliable.</summary>
        static string FatalText(IntPtr d)
        {
            string m = Win.DialogMessage(d);
            if (Win.Text(d) == "RESPONSA" || m.Contains("improper argument") || m.StartsWith("Encountered")) return m.Length > 0 ? m : Win.Text(d);
            return null;
        }
        public static string FatalDialog(uint pid)
        {
            foreach (var d in Win.Dialogs(pid)) { string f = FatalText(d); if (f != null) return f; }
            return null;
        }

        // ------------------------------------------------------------------ search
        static IntPtr OpenAdvanced(uint pid)
        {
            IntPtr a = Win.Dialogs(pid).FirstOrDefault(d => Win.Text(d) == "חיפוש מתקדם");
            if (a != Z) return a;
            IntPtr s = Win.Dialogs(pid).FirstOrDefault(IsSearchDialog);
            if (s == Z) { Command(pid, Ids.Search); s = WaitDialog(pid, IsSearchDialog, 10000); }
            if (s == Z) throw new StuckError("the Responsa search window did not open");
            if (Win.Text(s) != "חיפוש מתקדם") Win.Press(s, Ids.AdvancedType);
            a = WaitTitle(pid, "חיפוש מתקדם", 10000);
            if (a == Z) throw new StuckError("could not switch Responsa to advanced search");
            return a;
        }

        public sealed class SearchOutcome
        {
            public IntPtr Window; public int Total; public string Title;
            public List<KeyValuePair<string, int>> Breakdown = new List<KeyValuePair<string, int>>();
            public List<string> Messages = new List<string>();
            public double Seconds;
        }

        public static SearchOutcome Search(uint pid, string query, int allDatabases, int timeoutSec)
        {
            var res = new SearchOutcome();
            var before = new HashSet<long>(MdiChildren(pid).Select(h => h.ToInt64()));
            IntPtr a = OpenAdvanced(pid);
            if (allDatabases >= 0) Win.Send(Win.Kid(a, Ids.AllDatabases), Native.BM_SETCHECK, allDatabases, 0);
            Win.SetText(Win.Kid(a, Ids.AdvQuery), query);
            var sw = Stopwatch.StartNew();
            Win.Click(Win.Kid(a, Ids.RunSearch));
            double messageAt = -1;
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                Thread.Sleep(250);
                foreach (var d in Win.Dialogs(pid))
                {
                    if (IsSearchDialog(d) || IsIyun(d)) continue;
                    if (IsBreakdown(d)) { ReadBreakdown(d, res); Win.Press(d, 1); continue; }
                    string title = Win.Text(d), msg = Win.DialogMessage(d);
                    res.Messages.Add((title.Length > 0 ? title + ": " : "") + msg);
                    Dismiss(d);
                    if (messageAt < 0) messageAt = sw.Elapsed.TotalSeconds;
                }
                var nw = MdiChildren(pid).Where(h => !before.Contains(h.ToInt64())).ToList();
                if (nw.Count > 0)
                {
                    string t = Win.Text(nw[0]);
                    var m = Regex.Match(t, @"נמצאו\s+(\d+)\s+תוצאות");
                    if (m.Success)
                    {
                        res.Window = nw[0]; res.Title = t; res.Total = int.Parse(m.Groups[1].Value);
                        // the per-database breakdown sometimes appears just after the title
                        IntPtr late = WaitDialog(pid, IsBreakdown, 1200);
                        if (late != Z) { ReadBreakdown(late, res); Win.Press(late, 1); }
                        break;
                    }
                }
                // a message (error, too many results, nothing found) and no results window: the search is over
                if (messageAt >= 0 && sw.Elapsed.TotalSeconds - messageAt > 2.5 && nw.Count == 0) break;
            }
            res.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            if (res.Window == Z && res.Messages.Count == 0) throw new StuckError("the search did not finish within " + timeoutSec + " s");
            return res;
        }

        static void ReadBreakdown(IntPtr d, SearchOutcome res)
        {
            res.Breakdown.Clear();
            foreach (var row in Win.ListView(Win.KidOfClass(d, "SysListView32"), 2))
            {
                int n; if (!int.TryParse(row[0], out n) || n == 0) continue;
                res.Breakdown.Add(new KeyValuePair<string, int>(FixBidi(row[1]), n));
            }
        }

        /// <summary>Responsa stores some names visually: "(תלמוד ירושלמי (וילנא" → "תלמוד ירושלמי (וילנא)".</summary>
        public static string FixBidi(string s)
        {
            if (s.StartsWith("(") && !s.EndsWith(")") && s.IndexOf('(', 1) > 0) return s.Substring(1) + ")";
            return s;
        }

        // ------------------------------------------------------------------ saving
        /// <summary>Marks results [from..to] of a results window (clearing earlier marks) and saves the marked ones.</summary>
        public static string SaveResultRange(uint pid, IntPtr window, int total, int from, int to, string path)
        {
            if (!Owned(pid, window)) throw new WindowGone();
            Activate(pid, window);
            Command(pid, Ids.MarkRange);
            IntPtr r = WaitTitle(pid, "סימון טווח", 10000);
            if (r == Z) throw new StuckError("the Responsa range-marking window did not open");
            Win.SetText(Win.Kid(r, Ids.RangeFrom), "1"); Win.SetText(Win.Kid(r, Ids.RangeTo), total.ToString());
            Win.Press(r, Ids.RangeUnmark); Thread.Sleep(400);
            if (!Native.IsWindow(r)) { Command(pid, Ids.MarkRange); r = WaitTitle(pid, "סימון טווח", 10000); }
            Win.SetText(Win.Kid(r, Ids.RangeFrom), from.ToString()); Win.SetText(Win.Kid(r, Ids.RangeTo), to.ToString());
            Win.Press(r, Ids.RangeMark); Thread.Sleep(400);
            if (Native.IsWindow(r) && Native.IsWindowVisible(r)) { Win.Press(r, Ids.RangeOk); Thread.Sleep(300); }
            return SaveVia(pid, Ids.SaveAllMarked, path);
        }

        static bool IsFileDialog(IntPtr d)
        {
            foreach (var k in Win.Kids(d)) if (Native.GetDlgCtrlID(k) == 1001 && Win.Class(k) == "Edit" && Win.Class(Native.GetParent(k)).StartsWith("ComboBox")) return true;
            return false;
        }

        /// <summary>Sends a Save command to the active window, fills the Save As dialog with path, waits for the file.</summary>
        public static string SaveVia(uint pid, int command, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            Command(pid, command);
            IntPtr sv = Z; var sw = Stopwatch.StartNew();
            while (sv == Z && sw.ElapsedMilliseconds < 15000)
            {
                foreach (var d in Win.Dialogs(pid))
                {
                    if (IsSearchDialog(d) || IsIyun(d)) continue;
                    if (IsFileDialog(d)) { sv = d; break; }
                    string msg = Win.DialogMessage(d);
                    if (msg.Length > 0) { Dismiss(d); throw new ToolError("Responsa refused to save: " + msg); }
                }
                Thread.Sleep(150);
            }
            if (sv == Z) throw new StuckError("the Save As window did not open");
            IntPtr ed = Win.Kids(sv).First(k => Native.GetDlgCtrlID(k) == 1001 && Win.Class(k) == "Edit");
            Win.SetText(ed, path);
            Win.Press(sv, 1);
            sw.Restart();
            while (sw.ElapsedMilliseconds < 30000)
            {
                Thread.Sleep(200);
                foreach (var d in Win.Dialogs(pid))
                {
                    if (IsSearchDialog(d) || IsIyun(d) || d == sv) continue;
                    string msg = Win.DialogMessage(d);
                    if (msg.Contains("נשמר") || Win.Text(d) == path) { Dismiss(d); return path; }
                    if (msg.Length > 0) { Dismiss(d); throw new ToolError("Responsa did not save: " + msg); }
                }
                if (!Native.IsWindow(sv) && File.Exists(path) && new FileInfo(path).Length > 0 && sw.ElapsedMilliseconds > 2000) return path;
            }
            throw new StuckError("saving did not finish");
        }

        // ------------------------------------------------------------------ open text by reference
        public sealed class TextOutcome { public IntPtr Window; public string Title; public int Candidates; }

        static IntPtr RefPage(IntPtr iyun)
        {
            foreach (var k in Win.Kids(iyun)) if (Win.Class(k) == "#32770" && Win.Text(k) == "כתיבת מקורות") return k;
            return Z;
        }

        public static TextOutcome OpenReference(uint pid, string reference, int timeoutSec)
        {
            var before = new HashSet<long>(MdiChildren(pid).Select(h => h.ToInt64()));
            IntPtr iyun = Win.Dialogs(pid).FirstOrDefault(IsIyun);
            if (iyun == Z) { Command(pid, Ids.Iyun); iyun = WaitDialog(pid, IsIyun, 10000); }
            if (iyun == Z) throw new StuckError("the Responsa text (עיון) window did not open");
            IntPtr page = RefPage(iyun);
            if (page == Z) { Win.Send(iyun, Native.PSM_SETCURSEL, 1, 0); Thread.Sleep(600); page = RefPage(iyun); }
            if (page == Z) throw new StuckError("the 'כתיבת מקורות' page did not open");
            IntPtr list = Win.Kid(page, Ids.RefList);
            Win.SetText(Win.Kid(page, Ids.RefEdit), reference);
            Win.Press(page, Ids.RefFind);
            var res = new TextOutcome();
            var sw = Stopwatch.StartNew(); bool shown = false; int lastCount = -1; double stableSince = 0;
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                Thread.Sleep(250);
                var nw = MdiChildren(pid).Where(h => !before.Contains(h.ToInt64())).ToList();
                if (nw.Count > 0) { Thread.Sleep(800); res.Window = nw[0]; res.Title = Win.Text(nw[0]); return res; }
                foreach (var d in Win.Dialogs(pid))
                {
                    if (IsSearchDialog(d) || IsIyun(d)) continue;
                    string msg = Win.DialogMessage(d); Dismiss(d);
                    throw new ToolError("Responsa: " + (msg.Length > 0 ? msg : Win.Text(d)));
                }
                if (shown || !Native.IsWindow(list)) continue;
                int count = (int)Win.Send(list, Native.LB_GETCOUNT, 0, 0);
                if (count != lastCount) { lastCount = count; stableSince = sw.Elapsed.TotalSeconds; continue; }
                if (count > 0 && sw.Elapsed.TotalSeconds - stableSince > 0.8)
                {
                    res.Candidates = count;
                    if ((int)Win.Send(list, Native.LB_GETCURSEL, 0, 0) < 0) Win.Send(list, Native.LB_SETCURSEL, 0, 0);
                    Win.Press(page, Ids.RefShow);
                    shown = true;
                }
            }
            if (lastCount == 0) throw new ToolError("Responsa found no source for this reference");
            throw new StuckError("the text did not open in time");
        }
    }
}
