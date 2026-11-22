// IT-827: the Save As window, on every Responsa version. Two kinds exist:
//   - the common item dialog (Vista and later; measured on Responsa 29 on Windows 11): DUIViewWndClassName ->
//     DirectUIHWND -> FloatNotifySink -> ComboBox -> Edit 1001 (file name); a second FloatNotifySink -> ComboBox (type,
//     no Edit); the address bar under WorkerW / ReBarWindow32; Save = Button 1 of the dialog itself;
//   - the classic Explorer-style dialog (a hooked or templated GetSaveFileName): ComboBoxEx32 1148 -> ComboBox -> Edit
//     (file name), ComboBox 1136 (type), ComboBox 1137 ("save in"), Save = Button 1. Older ones: Edit 1152 (edt1).
// Measured with a scratch program on this PC (both kinds) and on Responsa 29 (the item dialog). Responsa 31 and 34
// were not available; a pilot user saw v34 save to its default name "respsrch" in its own TEMP folder.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace ResponsaMcp
{
    internal static class SaveDialog
    {
        static readonly IntPtr Z = IntPtr.Zero;
        public const uint CB_GETCOUNT = 0x146, CB_GETCURSEL = 0x147, CB_GETLBTEXT = 0x148, CB_GETLBTEXTLEN = 0x149, CB_SETCURSEL = 0x14E;
        public const int CBN_SELCHANGE = 1, CBN_CLOSEUP = 8, CBN_SELENDOK = 9;
        const uint EM_SETSEL = 0xB1, WM_CHAR = 0x102;
        // WM_CHAR must go through the Unicode entry point: Win.Send uses SendMessageTimeoutA, which would turn any
        // character past ANSI (a Hebrew Windows user name in the temp path) into something else.
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        static extern IntPtr SendW(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);

        public sealed class Parts
        {
            public string Kind;      // "item dialog" or "classic"
            public IntPtr Name;      // the file-name Edit
            public IntPtr Type;      // the save-type ComboBox; Zero if it could not be told apart
            public IntPtr Save;      // the Save button (id 1)
        }

        static bool Under(IntPtr h, IntPtr top, Func<string, bool> cls)
        {
            for (IntPtr p = Native.GetParent(h); p != Z && p != top; p = Native.GetParent(p)) if (cls(Win.Class(p))) return true;
            return false;
        }
        static bool InAddressBar(IntPtr h, IntPtr d)
        {
            return Under(h, d, c => c == "WorkerW" || c == "ReBarWindow32" || c == "Address Band Root" || c == "Search Box");
        }

        /// <summary>
        /// The parts of a Save As window, or null if `d` is not one (or not built yet). The file-name field is the one
        /// visible Edit inside a combo box outside the address bar; several such Edits (a layout never measured) are
        /// narrowed by the measured ids, and are otherwise not guessed: null.
        /// </summary>
        public static Parts Find(IntPtr d)
        {
            var kids = Win.Kids(d);
            IntPtr save = kids.FirstOrDefault(k => Native.GetDlgCtrlID(k) == 1 && Win.Class(k) == "Button" && Native.GetParent(k) == d && Native.IsWindowVisible(k));
            if (save == Z) return null;
            bool item = kids.Any(k => Win.Class(k) == "DUIViewWndClassName");
            var names = kids.Where(k => Win.Class(k) == "Edit" && Native.IsWindowVisible(k) && Win.Class(Native.GetParent(k)).StartsWith("ComboBox") && !InAddressBar(k, d)).ToList();
            if (names.Count > 1) names = names.Where(k => Native.GetDlgCtrlID(k) == 1001 || Native.GetDlgCtrlID(k) == 1148).ToList();
            if (names.Count == 0) names = kids.Where(k => Win.Class(k) == "Edit" && Native.GetDlgCtrlID(k) == 1152 && Native.IsWindowVisible(k)).ToList();
            if (names.Count != 1) return null;
            var p = new Parts { Kind = item ? "item dialog" : "classic", Name = names[0], Save = save };
            List<IntPtr> types;
            if (item)
                types = kids.Where(k => Win.Class(k) == "ComboBox" && Native.IsWindowVisible(k) && !InAddressBar(k, d) && Under(k, d, c => c == "DUIViewWndClassName")
                                        && !Win.Kids(k).Any(e => Win.Class(e) == "Edit")).ToList();
            else
                types = kids.Where(k => Win.Class(k) == "ComboBox" && Native.GetDlgCtrlID(k) == 1136 && Native.IsWindowVisible(k)).ToList();
            if (types.Count == 1) p.Type = types[0];
            return p;
        }

        /// <summary>Classes and ids of a window's descendants (at most 60), for an error a user can send back.</summary>
        public static string Describe(IntPtr d)
        {
            var sb = new StringBuilder("'" + Win.Text(d) + "':");
            int n = 0;
            foreach (var k in Win.Kids(d))
            {
                if (++n > 60) { sb.Append(" ..."); break; }
                sb.Append(' ').Append(Win.Class(k)).Append('#').Append(Native.GetDlgCtrlID(k));
                if (!Native.IsWindowVisible(k)) sb.Append("(h)");
            }
            return sb.ToString();
        }

        /// <summary>The items of a combo box (another process; CB_GETLBTEXT is marshalled by Windows).</summary>
        public static List<string> Items(IntPtr cb)
        {
            var r = new List<string>();
            int count = (int)Win.Send(cb, CB_GETCOUNT, 0, 0);
            for (int i = 0; i < count && i < 50; i++)
            {
                int len = (int)Win.Send(cb, CB_GETLBTEXTLEN, i, 0);
                if (len < 0 || len > 1000) { r.Add(""); continue; }
                var sb = new StringBuilder(len + 2); IntPtr res;
                Native.SendMessageTimeout(cb, CB_GETLBTEXT, (IntPtr)i, sb, 0x0002, 2000, out res);
                r.Add(sb.ToString());
            }
            return r;
        }
        static bool IsUnicode(string s) { return s.IndexOf("UNICODE", StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>
        /// SaveParse expects Unicode text (it reads cp1255 too). If the type list has a Unicode entry and another is
        /// chosen, the Unicode one is chosen as a user's pick from the list would be: CB_SETCURSEL, then CBN_SELENDOK,
        /// CBN_SELCHANGE and CBN_CLOSEUP to its parent. Measured with the scratch program: CBN_SELCHANGE alone changed
        /// what the box shows but not the type either dialog kind saved with; this sequence changed both. The item
        /// dialog then resets the file name to its default, so the caller settles the name again after a change.
        /// Returns the type shown, or null if there is no type box. Throws StuckError if the box did not take it.
        /// </summary>
        public static string EnsureUnicode(Parts p, string version)
        {
            if (p.Type == Z) return null;
            string shown = Win.Text(p.Type);
            if (IsUnicode(shown)) return shown;
            var items = Items(p.Type);
            int u = items.FindIndex(IsUnicode);
            if (u < 0) return shown;   // no Unicode type on this version: the save is read as cp1255
            Win.Send(p.Type, CB_SETCURSEL, u, 0);
            IntPtr parent = Native.GetParent(p.Type); long id = Native.GetDlgCtrlID(p.Type) & 0xFFFF;
            foreach (int code in new[] { CBN_SELENDOK, CBN_SELCHANGE, CBN_CLOSEUP })
                Win.Send(parent, Native.WM_COMMAND, id | ((long)code << 16), p.Type.ToInt64());
            var sw = Stopwatch.StartNew();
            while (!IsUnicode(Win.Text(p.Type)) && sw.ElapsedMilliseconds < 2000) Thread.Sleep(100);
            shown = Win.Text(p.Type);
            if (!IsUnicode(shown)) throw new StuckError("the Save As window of " + version + " did not take the type '" + items[u] + "' (it shows '" + shown + "')");
            return shown;
        }

        /// <summary>Waits until the file-name text has not changed for `quietMs` (the dialog has finished setting its default).</summary>
        public static void Settle(Parts p, int quietMs, int maxMs)
        {
            string last = Win.Text(p.Name);
            var sw = Stopwatch.StartNew(); var quiet = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < maxMs && quiet.ElapsedMilliseconds < quietMs)
            {
                Thread.Sleep(100);
                string now = Win.Text(p.Name);
                if (now != last) { last = now; quiet.Restart(); }
            }
        }

        /// <summary>
        /// Puts `path` into the file-name field and checks that it stays there for `holdMs` (a dialog that resets its
        /// default name after it is shown would otherwise save there). typed = false: WM_SETTEXT (what Responsa 29 was
        /// measured with); typed = true: select all and type each character, as a user's input arrives. Up to 3 tries.
        /// Returns true once the field holds exactly `path`.
        /// </summary>
        public static bool PutName(Parts p, string path, bool typed, int holdMs)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (typed)
                {
                    Win.Send(p.Name, EM_SETSEL, 0, -1);
                    IntPtr r;
                    foreach (char c in path) SendW(p.Name, WM_CHAR, (IntPtr)c, (IntPtr)1, 0x0002, 5000, out r);
                }
                else Win.SetText(p.Name, path);
                var sw = Stopwatch.StartNew(); bool held = true;
                while (sw.ElapsedMilliseconds < holdMs)
                {
                    Thread.Sleep(100);
                    if (Win.Text(p.Name) != path) { held = false; break; }
                }
                if (held && Win.Text(p.Name) == path) return true;
            }
            return false;
        }

        static string Label(string s) { return (s ?? "").Replace("&", "").Trim(); }

        /// <summary>
        /// IT-871: Responsa's "a file with this name exists" prompt, by its parts. Measured on Responsa 29: title = the
        /// path; Static 1057 "קיים כבר קובץ בשם שבחרת."; Static 1628 'בחר "הוסף" ... או בחר "שכתב" ...'; buttons
        /// "&amp;הוסף" 1056, "&amp;שכתב" 1, "ע&amp;זרה" 57670, "&amp;ביטול" 2. It is the prompt when it has both an append and an
        /// overwrite button, or its text says the file exists; the ids are not relied on.
        /// </summary>
        public static bool IsExistsPrompt(string message, IList<KeyValuePair<int, string>> buttons)
        {
            bool append = buttons.Any(b => Label(b.Value) == "הוסף"), overwrite = buttons.Any(b => Label(b.Value) == "שכתב");
            return (append && overwrite) || (message ?? "").Contains("קיים כבר קובץ");
        }

        /// <summary>IT-871: the id of the button with this label (ampersands ignored), or -1.</summary>
        public static int PromptButton(IList<KeyValuePair<int, string>> buttons, string label)
        {
            foreach (var b in buttons) if (Label(b.Value) == label) return b.Key;
            return -1;
        }

        /// <summary>Presses Save the way the dialog's own button would (WM_COMMAND to the dialog, from the button).</summary>
        public static void PressSave(IntPtr d, Parts p)
        {
            Native.PostMessage(d, Native.WM_COMMAND, (IntPtr)1, p.Save);
        }
    }
}
