// IT-789: limiting a search to named books. The database chooser of the search dialog ("ניהול המאגרים", page
// "עץ מקורות") holds a tree of categories, books and parts. Measured on Responsa 29 (docs/IT-789-responsa-book-scope.md):
//   * The selection state of a node is its image index: 0 = selected (green), 1 = not selected (red), 2 = partly.
//   * "התחל שוב" (1136) clears the selection and rebuilds the tree collapsed.
//   * A left click on a node toggles it. The tree takes the click position from the message, so a click posted to the
//     tool's own hidden tree (no real input) toggles exactly the node under that position.
//   * " רשימת המאגרים שנבחרו" (1199) switches the tree to a view of the selected nodes only: a fully selected node
//     appears without children, a partly selected one with its selected children. This is how the tool reads back
//     what was selected.
//   * "אישור" (1649) applies the selection to the instance's memory. Nothing is written to the data folder then; the
//     selection is saved only by Responsa's normal exit, which the tool's instance never does.
//   * The "כתיבת מקורות" page of the chooser finds book names, but its OK did not change the selection when driven
//     by messages (row selected, LBN_SELCHANGE, LBN_DBLCLK, posted clicks, "הוסף לרשימה"): it is not used.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace ResponsaMcp
{
    /// <summary>A node of the database tree: its position (child index at each level) and its names from the root.</summary>
    internal sealed class BookNode
    {
        public int[] Path;        // child index at each level, from the root list
        public string[] Names;    // raw tree texts, root first; the last one is this node

        public string Name { get { return Names[Names.Length - 1]; } }
        public int Depth { get { return Path.Length - 1; } }
        /// <summary>The readable path, e.g. "תנ\"ך / דניאל".</summary>
        public string Display { get { return string.Join(" / ", Names.Select(BookMatch.Display).ToArray()); } }
        public string Id { get { return string.Join(".", Path.Select(i => i.ToString()).ToArray()); } }
        public bool IsAncestorOf(BookNode other)
        {
            if (other.Path.Length <= Path.Length) return false;
            for (int i = 0; i < Path.Length; i++) if (Path[i] != other.Path[i]) return false;
            return true;
        }
    }

    /// <summary>What name matching needs from the tree (live: LiveBookTree; tests: a fake).</summary>
    internal interface IBookTree
    {
        /// <summary>Every node of the top three levels (categories, sub-categories, books).</summary>
        List<BookNode> Top();
        /// <summary>The children of a node (deeper levels are read only along a path the agent gave).</summary>
        List<BookNode> Children(BookNode n);
    }

    /// <summary>Name matching. Pure, tested offline.</summary>
    internal static class BookMatch
    {
        public const int MaxBooks = 20;
        const int MaxListed = 15;
        const int MaxDeepParents = 10;

        /// <summary>
        /// The key two names are compared by. Letters and digits only: quotes, geresh and gershayim are dropped
        /// (רש"י = רשי); other marks (brackets, dashes, commas) separate words; the words are sorted. Responsa stores
        /// some names in visual order ("(ספרי שאלות ותשובות (שו\"ת", "רבינו] בחיי]"), so word order is not compared.
        /// </summary>
        public static string Key(string s)
        {
            const string quotes = "\"'׳״‘’“”";   // quotes, geresh, gershayim
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (quotes.IndexOf(c) >= 0) continue;
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }
            var words = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            words.Sort(StringComparer.Ordinal);
            return string.Join(" ", words.ToArray());
        }

        /// <summary>A tree text in reading order: "(ספרי שאלות ותשובות (שו\"ת" → "ספרי שאלות ותשובות (שו\"ת)",
        /// "רבינו] בחיי]" → "[רבינו] בחיי".</summary>
        public static string Display(string s)
        {
            s = Ui.FixBidi(s);
            int b = s.IndexOf("] ");
            if (b > 0 && s.EndsWith("]") && s.IndexOf('[') < 0) return "[" + s.Substring(0, b) + "] " + s.Substring(b + 2, s.Length - b - 3);
            return s;
        }

        /// <summary>"מפרשי תנ\"ך / רש\"י / דניאל" → its parts; '/' never occurs in a tree name (measured).</summary>
        public static string[] Segments(string entry)
        {
            return entry.Split('/').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }

        /// <summary>Every earlier part names an ancestor, in order (not necessarily the parent).</summary>
        static bool AncestorsMatch(BookNode n, string[] keys, int count)
        {
            int k = 0;
            for (int i = 0; i < n.Names.Length - 1 && k < count; i++) if (Key(n.Names[i]) == keys[k]) k++;
            return k == count;
        }

        /// <summary>
        /// The node one entry names. The last part is matched against the top three levels; if nothing matches and the
        /// entry has a path, the children of the matching third-level nodes are tried too (e.g. "רש\"י ומפרשיו / רש\"י /
        /// דניאל"). No match or more than one is a ToolError that lists candidates; the search never runs wider.
        /// </summary>
        public static BookNode Resolve(IBookTree tree, string entry) { string note; return Resolve(tree, entry, out note); }

        /// <summary>
        /// As above. When one match is higher in the tree than all the others (e.g. 'תנ"ך / בראשית' and the 'בראשית' parts
        /// of Hasidic books), that one is taken and `note` says where else the name is.
        /// </summary>
        public static BookNode Resolve(IBookTree tree, string entry, out string note)
        {
            note = null;
            var seg = Segments(entry);
            if (seg.Length == 0) throw new ToolError("books: an empty book name");
            var keys = seg.Select(Key).ToArray();
            if (keys.Any(k => k.Length == 0)) throw new ToolError("books: '" + entry + "' has no letters in one of its parts");
            string last = keys[keys.Length - 1];
            var top = tree.Top();
            var found = top.Where(n => Key(n.Name) == last && AncestorsMatch(n, keys, keys.Length - 1)).ToList();
            if (found.Count == 0 && keys.Length >= 2)
            {
                string parentKey = keys[keys.Length - 2];
                var parents = top.Where(n => n.Depth == 2 && Key(n.Name) == parentKey && AncestorsMatch(n, keys, keys.Length - 2)).ToList();
                if (parents.Count > MaxDeepParents)
                    throw new ToolError("books: '" + entry + "' is too general (" + parents.Count + " places match '" + seg[seg.Length - 2] + "'); give more of the path");
                foreach (var p in parents) found.AddRange(tree.Children(p).Where(c => Key(c.Name) == last));
            }
            if (found.Count == 1) return found[0];
            if (found.Count > 1)
            {
                int min = found.Min(n => n.Depth);
                var highest = found.Where(n => n.Depth == min).ToList();
                if (highest.Count == 1)
                {
                    var others = found.Where(n => n != highest[0]).ToList();
                    note = "'" + entry + "' was taken as '" + highest[0].Display + "'; it also names " + others.Count + " deeper place(s): " + List(others) + ". Give a path for one of those.";
                    return highest[0];
                }
            }
            if (found.Count > 1)
                throw new ToolError("books: '" + entry + "' matches " + found.Count + " places in Responsa's database tree: "
                    + List(found) + ". Give the one you mean as a path, e.g. '" + found[0].Display + "' (any part of the path is enough to tell them apart).");
            var near = top.Where(n => Key(n.Name).Contains(last) || Key(n.Name).Split(' ').Contains(last)).ToList();
            if (near.Count == 0 && keys.Length == 1)
                near = top.Where(n => keys[0].Split(' ').All(w => Key(n.Name).Contains(w))).ToList();
            string msg = "books: no book named '" + entry + "' in Responsa's database tree.";
            if (near.Count > 0) msg += " Close names: " + List(near) + ".";
            msg += " Use the names of the tree (categories, books, commentators); a commentary on one book is 'commentator / book', e.g. 'רש\"י ומפרשיו / רש\"י / דניאל'.";
            throw new ToolError(msg);
        }

        static string List(List<BookNode> nodes)
        {
            var shown = nodes.Take(MaxListed).Select(n => "'" + n.Display + "'").ToArray();
            return string.Join("; ", shown) + (nodes.Count > MaxListed ? " (and " + (nodes.Count - MaxListed) + " more)" : "");
        }

        /// <summary>All entries resolved; duplicates and nodes inside another chosen node removed.</summary>
        public static List<BookNode> ResolveAll(IBookTree tree, IList<string> entries) { List<string> notes; return ResolveAll(tree, entries, out notes); }

        public static List<BookNode> ResolveAll(IBookTree tree, IList<string> entries, out List<string> notes)
        {
            notes = new List<string>();
            if (entries.Count == 0) throw new ToolError("books: give at least one book name");
            if (entries.Count > MaxBooks) throw new ToolError("books: at most " + MaxBooks + " names per search");
            var nodes = new List<BookNode>();
            foreach (var e in entries)
            {
                string note;
                var n = Resolve(tree, e, out note);
                if (note != null) notes.Add(note);
                if (!nodes.Any(x => x.Id == n.Id)) nodes.Add(n);
            }
            return Cover(nodes);
        }

        /// <summary>The nodes that are not inside another node of the list.</summary>
        public static List<BookNode> Cover(List<BookNode> nodes)
        {
            return nodes.Where(n => !nodes.Any(o => o.IsAncestorOf(n))).ToList();
        }

        /// <summary>
        /// Checks what the "selected" view showed against what was asked: exactly the same nodes. A node shown whole
        /// while only parts of it were asked is a difference too (the search would run wider than asked); that happens
        /// only when every part of it was named, and then the agent can name the whole. Returns null if it matches.
        /// </summary>
        public static string Compare(List<BookNode> asked, List<BookNode> shown)
        {
            var missing = asked.Where(a => !shown.Any(s => s.Id == a.Id)).ToList();
            var extra = shown.Where(s => !asked.Any(a => a.Id == s.Id)).ToList();
            if (missing.Count == 0 && extra.Count == 0) return null;
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("not selected as asked: " + List(missing));
            if (extra.Count > 0) parts.Add("selected: " + List(extra));
            return string.Join("; ", parts.ToArray());
        }
    }

    /// <summary>
    /// Reads and clicks a SysTreeView32 of the tool's own Responsa (32-bit, like this exe) through one buffer in its
    /// memory. UI thread only.
    /// </summary>
    internal sealed class RemoteTree : IDisposable
    {
        const uint TVM_EXPAND = 0x1102, TVM_GETITEMRECT = 0x1104, TVM_GETCOUNT = 0x1105, TVM_GETNEXTITEM = 0x110A,
            TVM_ENSUREVISIBLE = 0x1114, TVM_GETITEMW = 0x113E, TVM_SELECTITEM = 0x110B, TVM_HITTEST = 0x1111;
        const int TVGN_ROOT = 0, TVGN_NEXT = 1, TVGN_CHILD = 4, TVGN_FIRSTVISIBLE = 5, TVGN_CARET = 9, TVE_EXPAND = 2;
        const int TVIF_TEXT = 0x1, TVIF_IMAGE = 0x2;
        public const int Selected = 0, NotSelected = 1, Partly = 2;

        readonly IntPtr tv, hp, mem;
        public RemoteTree(IntPtr tree)
        {
            tv = tree;
            hp = Native.OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, Win.Pid(tv));
            if (hp == IntPtr.Zero) throw new StuckError("cannot read Responsa's database tree");
            mem = Native.VirtualAllocEx(hp, IntPtr.Zero, 4096, 0x3000, 0x04);
            if (mem == IntPtr.Zero) { Native.CloseHandle(hp); throw new StuckError("cannot read Responsa's database tree"); }
        }
        public void Dispose() { Native.VirtualFreeEx(hp, mem, 0, 0x8000); Native.CloseHandle(hp); }

        public IntPtr Handle { get { return tv; } }
        public int Count { get { return (int)Win.Send(tv, TVM_GETCOUNT, 0, 0); } }
        public IntPtr Root() { return Win.Send(tv, TVM_GETNEXTITEM, TVGN_ROOT, 0); }
        public IntPtr Child(IntPtr item) { return Win.Send(tv, TVM_GETNEXTITEM, TVGN_CHILD, item.ToInt64()); }
        public IntPtr Next(IntPtr item) { return Win.Send(tv, TVM_GETNEXTITEM, TVGN_NEXT, item.ToInt64()); }
        public void Expand(IntPtr item) { Win.Send(tv, TVM_EXPAND, TVE_EXPAND, item.ToInt64()); }
        /// <summary>Collapses an item and drops its child items (Responsa inserts them again when it is expanded).</summary>
        public void CollapseReset(IntPtr item) { Win.Send(tv, TVM_EXPAND, 0x0001 | 0x8000, item.ToInt64()); }
        /// <summary>WM_SETREDRAW: off while many items are read or rebuilt (measured: much faster), on before clicks.</summary>
        public void Redraw(bool on) { Win.Send(tv, 0x000B, on ? 1 : 0, 0); }
        public List<IntPtr> Children(IntPtr parent)
        {
            var r = new List<IntPtr>();
            for (IntPtr c = parent == IntPtr.Zero ? Root() : Child(parent); c != IntPtr.Zero && r.Count < 5000; c = Next(c)) r.Add(c);
            return r;
        }

        /// <summary>Text and image index of an item (TVITEMW, 32-bit layout).</summary>
        public string Text(IntPtr item, out int image)
        {
            var b = new byte[40];
            BitConverter.GetBytes(TVIF_TEXT | TVIF_IMAGE).CopyTo(b, 0);
            BitConverter.GetBytes(item.ToInt32()).CopyTo(b, 4);
            BitConverter.GetBytes((uint)(mem.ToInt64() + 256)).CopyTo(b, 16);
            BitConverter.GetBytes(500).CopyTo(b, 20);
            IntPtr n; Native.WriteProcessMemory(hp, mem, b, 40, out n);
            Native.WriteProcessMemory(hp, (IntPtr)(mem.ToInt64() + 256), new byte[2], 2, out n);
            Win.Send(tv, TVM_GETITEMW, 0, mem.ToInt64());
            var back = new byte[40]; Native.ReadProcessMemory(hp, mem, back, 40, out n);
            image = BitConverter.ToInt32(back, 24);
            var buf = new byte[1000]; Native.ReadProcessMemory(hp, (IntPtr)(mem.ToInt64() + 256), buf, 1000, out n);
            string s = Encoding.Unicode.GetString(buf); int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }
        public string Text(IntPtr item) { int i; return Text(item, out i); }
        public int Image(IntPtr item) { int i; Text(item, out i); return i; }

        /// <summary>
        /// Toggles an item: scrolls it into view and posts a left click on its icon (just before its text) to the tree.
        /// A posted message to the tool's own hidden window; no real input.
        /// </summary>
        public void Click(IntPtr item)
        {
            // Measured: the view can still move just after an earlier click (the clicked node becomes the caret and is
            // scrolled to). So the target is made the caret first, and the click is posted only when its position and
            // the first visible row are the same twice in a row and a hit test at the click point returns the target.
            Win.Send(tv, TVM_SELECTITEM, TVGN_CARET, item.ToInt64());
            Win.Send(tv, TVM_ENSUREVISIBLE, 0, item.ToInt64());
            var sw = Stopwatch.StartNew();
            string last = null;
            while (sw.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(150);
                int x, y;
                if (!Point(item, out x, out y)) { Win.Send(tv, TVM_ENSUREVISIBLE, 0, item.ToInt64()); last = null; continue; }
                string now = x + "," + y + "," + Win.Send(tv, TVM_GETNEXTITEM, TVGN_FIRSTVISIBLE, 0);
                if (now != last) { last = now; continue; }
                if (HitTest(x, y) != item) { last = null; continue; }
                IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
                Native.PostMessage(tv, 0x0201 /*WM_LBUTTONDOWN*/, (IntPtr)1, lp);
                Native.PostMessage(tv, 0x0202 /*WM_LBUTTONUP*/, IntPtr.Zero, lp);
                return;
            }
            throw new StuckError("a node of Responsa's database tree did not stay in place to be selected");
        }

        /// <summary>The click point of an item: on its icon, just before its text.</summary>
        bool Point(IntPtr item, out int x, out int y)
        {
            x = y = 0;
            IntPtr n; Native.WriteProcessMemory(hp, mem, BitConverter.GetBytes(item.ToInt32()), 4, out n);
            if (Win.Send(tv, TVM_GETITEMRECT, 1, mem.ToInt64()) == IntPtr.Zero) return false;
            var r = new byte[16]; Native.ReadProcessMemory(hp, mem, r, 16, out n);
            int left = BitConverter.ToInt32(r, 0), top = BitConverter.ToInt32(r, 4), bottom = BitConverter.ToInt32(r, 12);
            x = Math.Max(1, left - 10); y = (top + bottom) / 2;
            return true;
        }

        /// <summary>TVM_HITTEST: the item at a client point (TVHITTESTINFO: pt, flags, hItem).</summary>
        IntPtr HitTest(int x, int y)
        {
            var b = new byte[16];
            BitConverter.GetBytes(x).CopyTo(b, 0); BitConverter.GetBytes(y).CopyTo(b, 4);
            IntPtr n; Native.WriteProcessMemory(hp, mem, b, 16, out n);
            Win.Send(tv, TVM_HITTEST, 0, mem.ToInt64());
            var back = new byte[16]; Native.ReadProcessMemory(hp, mem, back, 16, out n);
            return (IntPtr)BitConverter.ToInt32(back, 12);
        }

        /// <summary>The item at a path of child indexes, expanding on the way; Zero if the path is not in the tree.</summary>
        public IntPtr At(int[] path)
        {
            IntPtr parent = IntPtr.Zero, item = IntPtr.Zero;
            foreach (int i in path)
            {
                if (parent != IntPtr.Zero) Expand(parent);
                var kids = Children(parent);
                if (i >= kids.Count) return IntPtr.Zero;
                item = kids[i];
                parent = item;
            }
            return item;
        }
    }

    /// <summary>The live tree as an IBookTree. Top() is read once per server (the tree is the same for every instance).</summary>
    internal sealed class LiveBookTree : IBookTree
    {
        readonly RemoteTree t;
        readonly BookIndexCache cache;
        public LiveBookTree(RemoteTree t, BookIndexCache cache) { this.t = t; this.cache = cache; }

        public List<BookNode> Top()
        {
            if (cache.Top != null) return cache.Top;
            var list = new List<BookNode>();
            t.Redraw(false);
            try
            {
                Walk(IntPtr.Zero, new int[0], new string[0], 3, list);
                foreach (var root in t.Children(IntPtr.Zero)) t.CollapseReset(root);
            }
            finally { t.Redraw(true); }
            cache.Top = list;
            return list;
        }
        void Walk(IntPtr parent, int[] path, string[] names, int levels, List<BookNode> list)
        {
            if (parent != IntPtr.Zero) t.Expand(parent);
            var kids = t.Children(parent);
            for (int i = 0; i < kids.Count; i++)
            {
                var n = new BookNode { Path = path.Concat(new[] { i }).ToArray(), Names = names.Concat(new[] { t.Text(kids[i]) }).ToArray() };
                list.Add(n);
                if (levels > 1) Walk(kids[i], n.Path, n.Names, levels - 1, list);
            }
        }

        public List<BookNode> Children(BookNode n)
        {
            IntPtr item = t.At(n.Path);
            if (item == IntPtr.Zero || t.Text(item) != n.Name) throw new BookTreeChanged();
            t.Expand(item);
            var kids = t.Children(item);
            return kids.Select((k, i) => new BookNode { Path = n.Path.Concat(new[] { i }).ToArray(), Names = n.Names.Concat(new[] { t.Text(k) }).ToArray() }).ToList();
        }
    }

    internal sealed class BookIndexCache { public List<BookNode> Top; }

    /// <summary>What SelectBooks selected (as Responsa's "selected" view shows it) and notes on how names were taken.</summary>
    internal sealed class BookSelection { public List<BookNode> Nodes; public List<string> Notes; }

    /// <summary>The tree did not have a node where the index said: the index is read again.</summary>
    internal sealed class BookTreeChanged : ToolError
    {
        public BookTreeChanged() : base("Responsa's database tree is not as the tool read it before; try again") { }
    }
}
