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
        public const int RangeFrom = 1618, RangeTo = 1619, RangeMark = 1620, RangeUnmark = 1061, RangeInvert = 1062, RangeOk = 1;
        // "עיון" → "כתיבת מקורות" page
        public const int RefEdit = 1021, RefFind = 1187, RefList = 1617, RefShow = 1;
        // IT-789: search dialog → databases (1210) → "ניהול המאגרים", page "עץ מקורות"
        public const int Databases = 1210, DbTree = 1002, DbSelectedView = 1199, DbReset = 1136, DbTitle = 1212, DbOk = 1649, DbCancel = 1651;
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
        /// <summary>
        /// IT-827: a control whose id was measured on Responsa 29. Missing on this version: a StuckError that names the
        /// version and the step (the instance is restarted), never an action on a window that is not there.
        /// </summary>
        static IntPtr Need(IntPtr dlg, int id, string step)
        {
            IntPtr h = Win.Kid(dlg, id);
            if (h == Z) throw new StuckError(Version + ": the " + step + " has no control " + id + " (the ids were measured on Responsa 29)");
            return h;
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
            if (Win.Text(s) != "חיפוש מתקדם") { Need(s, Ids.AdvancedType, "search window"); Win.Press(s, Ids.AdvancedType); }
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
            if (allDatabases >= 0) Win.Send(Need(a, Ids.AllDatabases, "advanced search window"), Native.BM_SETCHECK, allDatabases, 0);
            IntPtr q = Need(a, Ids.AdvQuery, "advanced search window");
            IntPtr run = Need(a, Ids.RunSearch, "advanced search window");
            Win.SetText(q, query);
            // IT-827: the query is read back before the search runs, so a field that did not take it never runs another query.
            if (Win.Text(q) != query) throw new StuckError(Version + ": the advanced search window did not take the query");
            var sw = Stopwatch.StartNew();
            Win.Click(run);
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

        // ------------------------------------------------------------------ book scope (IT-789, see Books.cs)
        static bool IsChooser(IntPtr d) { return Win.Text(d) == "ניהול המאגרים"; }
        static IntPtr TreePage(IntPtr sheet)
        {
            foreach (var k in Win.Kids(sheet)) if (Win.Class(k) == "#32770" && Win.Text(k) == "עץ מקורות" && Native.IsWindowVisible(k)) return k;
            return Z;
        }
        static bool SelectedView(IntPtr page) { return Win.Text(Win.Kid(page, Ids.DbTitle)).Contains("שנבחרו"); }

        /// <summary>A dialog of ours other than the search dialog and the chooser (a warning): fatal for this operation.</summary>
        static void CheckNoMessage(uint pid)
        {
            foreach (var d in Win.Dialogs(pid))
            {
                if (IsSearchDialog(d) || IsIyun(d) || IsChooser(d)) continue;
                // Measured: the "הקובץ נשמר" box of an earlier save can still appear after SaveVia returned. Harmless.
                if (Win.DialogMessage(d).Contains("הקובץ נשמר")) { Dismiss(d); continue; }
                string fatal = FatalText(d);
                if (fatal != null) throw new FatalDialogError(fatal);
                throw new StuckError("Responsa showed '" + Win.Text(d) + "': " + Win.DialogMessage(d) + " while the books were selected");
            }
        }

        /// <summary>
        /// IT-789: selects exactly the named books in the instance's database chooser and confirms it. `changing` is called
        /// just before the instance's selection is first changed (a name error leaves it untouched). Returns the selected
        /// nodes as Responsa's "selected" view shows them, and notes on names taken from several places. Throws ToolError
        /// for a name that matches nothing or several places, StuckError if the selection could not be made or read back
        /// as asked (the caller then restarts the instance, which drops whatever was selected).
        /// </summary>
        public static BookSelection SelectBooks(uint pid, IList<string> entries, BookIndexCache cache, Action changing)
        {
            // Measured: a message box of an earlier call can appear late ("לא נמצאה כל תוצאה!" after a failed open_text,
            // "הקובץ נשמר" after a save). Before anything is changed, such boxes are answered by their own button, as
            // Search does; an internal error or a box without a known button still throws (Dismiss).
            foreach (var d in Win.Dialogs(pid))
                if (!IsSearchDialog(d) && !IsIyun(d) && !IsChooser(d) && Win.Text(d).Length > 0) Dismiss(d);
            IntPtr adv = OpenAdvanced(pid);
            IntPtr sheet = Win.Dialogs(pid).FirstOrDefault(IsChooser);
            if (sheet == Z) { Need(adv, Ids.Databases, "advanced search window"); Win.Press(adv, Ids.Databases); sheet = WaitDialog(pid, IsChooser, 10000); }
            if (sheet == Z) throw new StuckError("the Responsa database chooser did not open");
            IntPtr page = TreePage(sheet);
            if (page == Z) { Win.Send(sheet, Native.PSM_SETCURSEL, 0, 0); Thread.Sleep(600); page = TreePage(sheet); }
            if (page == Z) throw new StuckError("the 'עץ מקורות' page of the database chooser did not open");
            foreach (int id in new[] { Ids.DbTree, Ids.DbSelectedView, Ids.DbReset, Ids.DbTitle, Ids.DbOk }) Need(page, id, "database chooser");
            bool done = false;
            try
            {
                if (SelectedView(page)) { Win.Press(page, Ids.DbSelectedView); WaitView(page, false); }
                List<BookNode> asked, shown; List<string> notes;
                using (var tree = new RemoteTree(Win.Kid(page, Ids.DbTree)))
                {
                    var live = new LiveBookTree(tree, cache);
                    try { asked = BookMatch.ResolveAll(live, entries, out notes); }
                    catch (BookTreeChanged) { cache.Top = null; asked = BookMatch.ResolveAll(live, entries, out notes); }

                    changing();
                    Win.Press(page, Ids.DbReset);
                    WaitTree(tree, t => t.Count > 0 && t.Children(IntPtr.Zero).All(i => t.Image(i) == RemoteTree.NotSelected), "clear the selection");
                    CheckNoMessage(pid);
                    foreach (var n in asked) Toggle(tree, n);
                    CheckNoMessage(pid);
                }
                // Read back what is selected, from Responsa's own "selected" view.
                Win.Press(page, Ids.DbSelectedView);
                WaitView(page, true);
                CheckNoMessage(pid);
                using (var tree = new RemoteTree(Win.Kid(page, Ids.DbTree))) shown = ReadSelected(tree, asked);
                string diff = BookMatch.Compare(asked, shown);
                if (diff != null) throw new StuckError("Responsa did not select the books as asked (" + diff + ")");

                Win.Press(page, Ids.DbOk);
                var sw = Stopwatch.StartNew();
                while (Native.IsWindow(sheet) && Native.IsWindowVisible(sheet) && sw.ElapsedMilliseconds < 8000) { Thread.Sleep(150); CheckNoMessage(pid); }
                if (Native.IsWindow(sheet) && Native.IsWindowVisible(sheet)) throw new StuckError("the database chooser did not close");
                Thread.Sleep(200);
                CheckNoMessage(pid);
                done = true;
                return new BookSelection { Nodes = shown, Notes = notes };
            }
            catch (BookTreeChanged) { cache.Top = null; throw; }   // read the tree names again next time
            finally
            {
                // A name error (or anything before OK): close the chooser without applying it.
                if (!done && Native.IsWindow(sheet) && Native.IsWindowVisible(sheet) && Native.IsWindow(page))
                {
                    Win.Press(page, Ids.DbCancel);
                    var sw = Stopwatch.StartNew();
                    while (Native.IsWindow(sheet) && Native.IsWindowVisible(sheet) && sw.ElapsedMilliseconds < 5000) Thread.Sleep(150);
                }
            }
        }

        static void WaitView(IntPtr page, bool selected)
        {
            var sw = Stopwatch.StartNew();
            while (SelectedView(page) != selected && sw.ElapsedMilliseconds < 5000) Thread.Sleep(100);
            if (SelectedView(page) != selected) throw new StuckError("the database chooser did not switch its view");
            Thread.Sleep(300);
        }

        static void WaitTree(RemoteTree t, Func<RemoteTree, bool> ok, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000) { if (ok(t)) return; Thread.Sleep(150); }
            throw new StuckError("Responsa's database chooser did not " + what);
        }

        /// <summary>Selects one node: found by its position, checked by name, clicked unless already selected, verified.</summary>
        static void Toggle(RemoteTree t, BookNode n)
        {
            IntPtr item = t.At(n.Path);
            if (item == IntPtr.Zero || t.Text(item) != n.Name) throw new BookTreeChanged();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (t.Image(item) == RemoteTree.Selected) return;
                t.Click(item);
                var sw = Stopwatch.StartNew();
                while (t.Image(item) != RemoteTree.Selected && sw.ElapsedMilliseconds < 1500) Thread.Sleep(100);
            }
            if (t.Image(item) != RemoteTree.Selected) throw new StuckError("could not select '" + n.Display + "' in Responsa's database chooser");
        }

        /// <summary>
        /// Walks the "selected" view: a node with image Selected is taken whole; a partly selected one is opened. Its nodes
        /// are mapped back to positions in the full tree through the asked nodes' names (the view keeps only the selected
        /// branches, so positions differ); a node no asked path explains gets a negative position, so it never matches.
        /// </summary>
        static List<BookNode> ReadSelected(RemoteTree t, List<BookNode> asked)
        {
            var shown = new List<BookNode>();
            int unknown = 0;
            Action<IntPtr, string[], int> walk = null;
            walk = delegate (IntPtr parent, string[] names, int depth)
            {
                if (parent != IntPtr.Zero) t.Expand(parent);
                foreach (var k in t.Children(parent))
                {
                    int img; string text = t.Text(k, out img);
                    var path = names.Concat(new[] { text }).ToArray();
                    if (img == RemoteTree.Selected || depth >= 6)
                    {
                        // same names as an asked node, or as an ancestor of one: that node's position
                        var m = asked.FirstOrDefault(a => a.Names.Length >= path.Length && a.Names.Take(path.Length).SequenceEqual(path));
                        int[] pos = m != null ? m.Path.Take(path.Length).ToArray() : new[] { -(++unknown) };
                        shown.Add(new BookNode { Path = pos, Names = path });
                    }
                    else if (img == RemoteTree.Partly) walk(k, path, depth + 1);
                }
            };
            walk(IntPtr.Zero, new string[0], 0);
            return shown;
        }

        // ------------------------------------------------------------------ saving
        /// <summary>
        /// Marks results [from..to] of a results window (clearing earlier marks) and saves the marked ones.
        /// IT-793 (measured on Responsa 29): in "סימון טווח תוצאות", "סמן" (1620), "בטל סימון" (1061) and "הפוך סימון"
        /// (1062) are auto radio buttons; the chosen action is applied to From..To only by "אישור" (1). A WM_COMMAND to a
        /// radio does not check it, so the old "unmark" press left "סמן" chosen and marks of earlier saves stayed. Unmark
        /// acts only on its range and marks persist in the window between saves. So: unmark 1..total, then mark from..to,
        /// each as its own dialog closed by "אישור". Tools.Page then checks the saved file (SaveParse.CheckRange).
        /// </summary>
        public static string SaveResultRange(uint pid, IntPtr window, int total, int from, int to, string path)
        {
            if (!Owned(pid, window)) throw new WindowGone();
            Activate(pid, window);
            // The dialog and the save act on the active MDI child; never mark or save another search's window.
            if (Win.Send(MdiClient(pid), Native.WM_MDIGETACTIVE, 0, 0) != window) throw new StuckError("the Responsa results window could not be made active");
            ApplyRange(pid, 1, total, Ids.RangeUnmark);
            ApplyRange(pid, from, to, Ids.RangeMark);
            if (Win.Send(MdiClient(pid), Native.WM_MDIGETACTIVE, 0, 0) != window) throw new StuckError("the Responsa results window is no longer the active one");
            return SaveVia(pid, Ids.SaveAllMarked, path);
        }

        /// <summary>
        /// IT-793: one use of "סימון טווח תוצאות": opens it, fills From/To, chooses the action radio by BM_CLICK, reads
        /// all of it back, and applies it with "אישור". Anything not as set, or a dialog that does not close, is a
        /// StuckError (the instance is restarted, which drops every mark).
        /// </summary>
        static void ApplyRange(uint pid, int from, int to, int action)
        {
            IntPtr r = Win.Dialogs(pid).FirstOrDefault(d => Win.Text(d).Contains("סימון טווח"));
            if (r == Z) { Command(pid, Ids.MarkRange); r = WaitTitle(pid, "סימון טווח", 10000); }
            if (r == Z) throw new StuckError("the Responsa range-marking window did not open");
            foreach (int id in new[] { Ids.RangeFrom, Ids.RangeTo, Ids.RangeMark, Ids.RangeUnmark, Ids.RangeInvert }) Need(r, id, "range-marking window");
            Win.SetText(Win.Kid(r, Ids.RangeFrom), from.ToString());
            Win.SetText(Win.Kid(r, Ids.RangeTo), to.ToString());
            Win.Click(Win.Kid(r, action));
            int[] radios = { Ids.RangeMark, Ids.RangeUnmark, Ids.RangeInvert };
            Func<bool> set = () =>
                Win.Text(Win.Kid(r, Ids.RangeFrom)).Trim() == from.ToString() && Win.Text(Win.Kid(r, Ids.RangeTo)).Trim() == to.ToString()
                && radios.All(id => (long)Win.Send(Win.Kid(r, id), Native.BM_GETCHECK, 0, 0) == (id == action ? 1 : 0));
            var sw = Stopwatch.StartNew();
            while (!set() && sw.ElapsedMilliseconds < 3000) Thread.Sleep(100);
            if (!set()) throw new StuckError("the Responsa range-marking window did not take the range " + from + "-" + to);
            Win.Press(r, Ids.RangeOk);
            sw.Restart();
            while (Native.IsWindow(r) && Native.IsWindowVisible(r) && sw.ElapsedMilliseconds < 15000) Thread.Sleep(100);
            if (Native.IsWindow(r) && Native.IsWindowVisible(r)) throw new StuckError("the Responsa range-marking window did not close");
            foreach (var d in Win.Dialogs(pid))
            {
                if (IsSearchDialog(d) || IsIyun(d)) continue;
                // As in CheckNoMessage: the "הקובץ נשמר" box of an earlier save can come late. Harmless.
                if (Win.DialogMessage(d).Contains("הקובץ נשמר")) { Dismiss(d); continue; }
                // Any other message after "אישור" (none was seen when measuring): the marks are unknown.
                string fatal = FatalText(d);
                if (fatal != null) throw new FatalDialogError(fatal);
                throw new StuckError("Responsa showed '" + Win.Text(d) + "': " + Win.DialogMessage(d) + " while marking results");
            }
        }

        /// <summary>True when no process has the file open (it can be opened with no sharing).</summary>
        static bool Closed(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
            catch (IOException) { return false; }
        }

        /// <summary>IT-827: "Responsa 34.0.0.0", set when the instance starts; named in errors of steps measured on 29.</summary>
        public static string Version = "Responsa (version unknown)";
        /// <summary>IT-827: how the last save went (dialog kind, type, method), for `doctor`.</summary>
        public static string LastSave;

        /// <summary>
        /// A window that looks like a Save As window that is still being built: an item-dialog view, or an Edit in a
        /// combo box. It is waited for, never answered like a message box.
        /// </summary>
        static bool LooksLikeFileDialog(IntPtr d)
        {
            return Win.Kids(d).Any(k => Win.Class(k) == "DUIViewWndClassName" || (Win.Class(k) == "Edit" && Win.Class(Native.GetParent(k)).StartsWith("ComboBox")));
        }

        /// <summary>
        /// Sends a Save command to the active window, fills the Save As window with path, waits for the file there.
        /// IT-827: a pilot user's Responsa 34 saved to its default name ("respsrch" in its TEMP folder) instead of the
        /// tool's path. Reproduced with a scratch program's item dialog: after its type changes, the dialog resets the
        /// name to its default and then saves with that internal name even while the field shows a path put in with
        /// WM_SETTEXT; a path typed in character by character is taken. So the path is typed first, must stay in the
        /// field before Save is pressed, and the save counts only if the file appears at the tool's own path; a file
        /// elsewhere is never read. If Responsa saved elsewhere, the save is tried once more with WM_SETTEXT (the
        /// method measured on Responsa 29); then it is an error naming the version.
        /// </summary>
        public static string SaveVia(uint pid, int command, string path)
        {
            var rounds = new List<string>();
            for (int round = 0; round < 2; round++)
            {
                if (File.Exists(path)) File.Delete(path);
                Command(pid, command);
                string r;
                try { r = FillAndSave(pid, path, round == 0); }
                finally { rounds.Add(LastSave); LastSave = string.Join("; then ", rounds.ToArray()); }
                if (r != null) return path;
            }
            throw new ToolError(Version + " did not save to the tool's path (it saved under another name, or not at all). " +
                "Nothing was read. Save As window: " + LastSave + ". Please report this with the output of responsa-mcp.exe doctor.");
        }

        /// <summary>
        /// IT-827: the Save As part of SaveVia, for any process `pid` (the offline dialog test drives a scratch program
        /// with it). Waits for the window, lets it settle, chooses the Unicode type, puts `path` in (typed or not) and
        /// checks it held, presses Save, and waits. Returns path when the file is at path; null when the window closed
        /// and no file came to path (Responsa saved elsewhere). Throws ToolError if Responsa refused with a message,
        /// StuckError if the window was not recognized, did not keep the path, or did not close.
        /// </summary>
        public static string FillAndSave(uint pid, string path, bool typed)
        {
            LastSave = "no Save As window recognized";
            IntPtr sv = Z, seen = Z; SaveDialog.Parts parts = null; var sw = Stopwatch.StartNew();
            while (sv == Z && sw.ElapsedMilliseconds < 15000)
            {
                foreach (var d in Win.Dialogs(pid))
                {
                    if (IsSearchDialog(d) || IsIyun(d)) continue;
                    parts = SaveDialog.Find(d);
                    if (parts != null) { sv = d; break; }
                    if (LooksLikeFileDialog(d)) { seen = d; continue; }   // still being built: wait
                    string msg = Win.DialogMessage(d);
                    if (msg.Length > 0) { Dismiss(d); throw new ToolError("Responsa refused to save: " + msg); }
                }
                if (sv == Z) Thread.Sleep(150);
            }
            if (sv == Z && seen != Z && Native.IsWindow(seen))
                throw new StuckError("the Save As window of " + Version + " was not recognized (" + SaveDialog.Describe(seen) + ")");
            if (sv == Z) throw new StuckError("the Save As window did not open (" + Version + ")");

            SaveDialog.Settle(parts, 500, 4000);
            string defaultName = Win.Text(parts.Name), typeBefore = parts.Type != Z ? Win.Text(parts.Type) : null;
            string type = SaveDialog.EnsureUnicode(parts, Version);
            // Measured: a type change makes the item dialog reset the file name to its default, a moment later.
            if (type != typeBefore) SaveDialog.Settle(parts, 500, 4000);
            LastSave = parts.Kind + ", type '" + (type ?? "none") + "', default name '" + defaultName + "', path " + (typed ? "typed" : "set");
            bool held = SaveDialog.PutName(parts, path, typed, 700);
            // Typed but not kept (a field that changes typed input): WM_SETTEXT in the same window before giving up.
            if (!held && typed) { LastSave += " (did not hold), then set"; held = SaveDialog.PutName(parts, path, false, 700); }
            if (!held)
                throw new StuckError("the Save As window of " + Version + " did not keep the tool's file name (it shows '" + Win.Text(parts.Name) + "'; " + LastSave + ")");
            SaveDialog.PressSave(sv, parts);

            sw.Restart();
            double closedAt = -1, boxAt = -1;
            while (sw.ElapsedMilliseconds < 30000)
            {
                Thread.Sleep(200);
                foreach (var d in Win.Dialogs(pid))
                {
                    if (IsSearchDialog(d) || IsIyun(d) || d == sv) continue;
                    if (SaveDialog.Find(d) != null || LooksLikeFileDialog(d)) continue;
                    string msg = Win.DialogMessage(d);
                    if (msg.Contains("נשמר") || Win.Text(d) == path) { Dismiss(d); if (boxAt < 0) boxAt = sw.Elapsed.TotalSeconds; continue; }
                    if (msg.Length > 0) { Dismiss(d); throw new ToolError("Responsa did not save: " + msg); }
                }
                bool open = Native.IsWindow(sv) && Native.IsWindowVisible(sv);
                if (!open && closedAt < 0) closedAt = sw.Elapsed.TotalSeconds;
                bool here = File.Exists(path) && new FileInfo(path).Length > 0;
                // Responsa's "הקובץ נשמר" box: the file is written (it is read once Responsa has closed it, SaveParse).
                if (here && boxAt >= 0) { LastSave += ": saved"; return path; }
                // IT-789 (found by the live test): Responsa keeps the file open until its "הקובץ נשמר" box is answered,
                // and that box can come late. Without the box, return only once Responsa has closed the file.
                if (here && !open && sw.ElapsedMilliseconds > 2000 && Closed(path)) { LastSave += ": saved"; return path; }
                // The window closed (and Responsa said it saved, or said nothing for a while) with no file here: it saved elsewhere.
                if (!here && !open && ((boxAt >= 0 && sw.Elapsed.TotalSeconds - boxAt > 3) || sw.Elapsed.TotalSeconds - closedAt > 6))
                {
                    LastSave += ": no file at the tool's path (" + (boxAt >= 0 ? "Responsa said it saved" : "no message") + ", " + (File.Exists(path) ? "an empty file" : "no file") + ")";
                    return null;
                }
            }
            if (Native.IsWindow(sv) && Native.IsWindowVisible(sv)) throw new StuckError("the Save As window of " + Version + " did not close after Save");
            throw new StuckError("saving did not finish");
        }

        // ------------------------------------------------------------------ open text by reference
        public sealed class TextOutcome
        {
            public IntPtr Window; public string Title; public int Candidates;
            public List<string> Names = new List<string>();   // IT-868: the choice list's items (at most MaxNames), when several matched
            public string Chosen;                              // IT-868: the item that was opened, as the list shows it
        }
        const int MaxNames = 20;

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
            IntPtr list = Need(page, Ids.RefList, "'כתיבת מקורות' page");
            IntPtr refEdit = Need(page, Ids.RefEdit, "'כתיבת מקורות' page");
            Need(page, Ids.RefFind, "'כתיבת מקורות' page");
            Win.SetText(refEdit, reference);
            // IT-827: read back, so a field that did not take the reference never opens another text.
            if (Win.Text(refEdit) != reference) throw new StuckError(Version + ": the 'כתיבת מקורות' page did not take the reference");
            Win.Press(page, Ids.RefFind);
            var res = new TextOutcome();
            var sw = Stopwatch.StartNew(); bool shown = false; int lastCount = -1; double stableSince = 0;
            while (sw.Elapsed.TotalSeconds < timeoutSec)
            {
                Thread.Sleep(250);
                var nw = MdiChildren(pid).Where(h => !before.Contains(h.ToInt64())).ToList();
                if (nw.Count > 0)
                {
                    // IT-868: a Responsa 34 pilot got an empty title after a choice from the list (on 29 it was filled at
                    // once). The window's caption can come late, so it is read again for up to 4 s before giving up.
                    Thread.Sleep(800);
                    res.Window = nw[0]; res.Title = Win.Text(nw[0]).Trim();
                    var tw = Stopwatch.StartNew();
                    while (res.Title.Length == 0 && tw.ElapsedMilliseconds < 4000 && Native.IsWindow(nw[0])) { Thread.Sleep(200); res.Title = Win.Text(nw[0]).Trim(); }
                    return res;
                }
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
                    // IT-868: what was found and what is opened, so a missing title never leaves the agent guessing
                    for (int i = 0; i < Math.Min(count, MaxNames); i++) res.Names.Add(Win.ListBoxItem(list, i));
                    int sel = (int)Win.Send(list, Native.LB_GETCURSEL, 0, 0);
                    res.Chosen = Win.ListBoxItem(list, sel < 0 ? 0 : sel);
                    Win.Press(page, Ids.RefShow);
                    shown = true;
                }
            }
            if (lastCount == 0) throw new ToolError("Responsa found no source for this reference");
            throw new StuckError("the text did not open in time");
        }
    }
}
