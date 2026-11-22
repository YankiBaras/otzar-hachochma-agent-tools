// IT-827: a scratch program with a Save As window, for test/SaveDialogTest.cs (build.ps1 -DialogTest). It is never
// shipped. Responsa 31 and 34 were not available, so the Save As handling is tested against this program's windows:
//   item[-ansi|-late|-stubborn|-elsewhere]  the common item dialog (GetSaveFileName without a hook upgrades to it)
//   classic[-ansi]                          the classic Explorer-style dialog (a hook keeps GetSaveFileName classic)
//   -ansi       the type list starts on the non-Unicode entry
//   -late       800 ms after the window is shown, the name is reset to "respsrch" once (as a dialog that sets its
//               default name after it is shown would)
//   -stubborn   the name is reset to "respsrch" whenever it changes, until the window closes
//   -elsewhere  the window takes the name, but the program saves to "respsrch" in its own folder (a pilot user's v34)
//   -exists     IT-871: the first chosen file is written with "LATE" text just after the window closes (as a slow
//               earlier save would), so the program shows Responsa's "file exists" prompt (its parts as measured on
//               Responsa 29). "ביטול" shows the Save As window again, "שכתב" overwrites, "הוסף" appends.
//   -exists2    as -exists, for every chosen file: the second prompt is for the tool's fresh name
//   -existsodd  as -exists2, but the second prompt is titled with a path that is not the tool's
// Args: <mode> <folder> <log file>. The folder is the initial folder and the only place it writes besides the
// chosen path; the driver only ever gives paths inside its own scratch folder.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class SaveAsHost
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OFN
    {
        public int lStructSize; public IntPtr hwndOwner, hInstance; public string lpstrFilter; public IntPtr lpstrCustomFilter; public int nMaxCustFilter, nFilterIndex;
        public IntPtr lpstrFile; public int nMaxFile; public IntPtr lpstrFileTitle; public int nMaxFileTitle; public string lpstrInitialDir, lpstrTitle;
        public int Flags; public short nFileOffset, nFileExtension; public string lpstrDefExt; public IntPtr lCustData; public HookProc lpfnHook; public string lpTemplateName;
        public IntPtr pvReserved; public int dwReserved, FlagsEx;
    }
    delegate IntPtr HookProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)] static extern bool GetSaveFileName(ref OFN o);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, StringBuilder l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    // IT-871: the "file exists" prompt
    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr h, int i, WndProc p);
    [DllImport("user32.dll")] static extern IntPtr CallWindowProc(IntPtr prev, IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    /// <summary>A window with the parts of Responsa 29's prompt; returns the id of the button pressed.</summary>
    static int ExistsPrompt(string path)
    {
        const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000, WS_CAPTION = 0xC00000, WS_CHILD = 0x40000000;
        IntPtr d = CreateWindowEx(0, "#32770", path, WS_POPUP | WS_VISIBLE | WS_CAPTION, 100, 100, 420, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Action<string, string, int, int> kid = (cls, text, id, y) =>
            CreateWindowEx(0, cls, text, WS_CHILD | WS_VISIBLE, 10, y, 380, 22, d, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
        kid("Button", "&הוסף", 1056, 100); kid("Button", "&שכתב", 1, 125); kid("Button", "ע&זרה", 57670, 150); kid("Button", "&ביטול", 2, 175);
        kid("Static", "קיים כבר קובץ בשם שבחרת.", 1057, 10); kid("Static", "", -1, 35);
        kid("Static", "בחר \"הוסף\" - להוספת החומר לסוף הקובץ הקיים, או בחר \"שכתב\" - לביטול הקובץ הקיים.", 1628, 60);
        int pressed = 0; IntPtr prev = IntPtr.Zero;
        WndProc proc = (h, m, w, l) =>
        {
            if (m == 0x111 /* WM_COMMAND */ && ((int)w & 0xFFFF) != 57670) { pressed = (int)w & 0xFFFF; return IntPtr.Zero; }
            return CallWindowProc(prev, h, m, w, l);
        };
        prev = SetWindowLong(d, -4 /* GWL_WNDPROC */, proc);
        MSG msg;
        while (pressed == 0 && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        DestroyWindow(d);
        GC.KeepAlive(proc);
        return pressed;
    }

    static string Class(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    static string Text(IntPtr h) { var sb = new StringBuilder(2048); SendMessage(h, 0x0D, (IntPtr)2048, sb); return sb.ToString(); }

    /// <summary>The file-name Edit of this process's visible Save As window (an Edit whose parent is a ComboBox).</summary>
    static IntPtr NameEdit(out IntPtr dialog)
    {
        IntPtr found = IntPtr.Zero, dlg = IntPtr.Zero; uint me = GetCurrentProcessId();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != me || !IsWindowVisible(h) || Class(h) != "#32770") return true;
            EnumChildWindows(h, delegate (IntPtr k, IntPtr l2)
            {
                if (Class(k) == "Edit" && Class(GetParent(k)).StartsWith("ComboBox") && IsWindowVisible(k)) { found = k; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) { dlg = h; return false; }
            return true;
        }, IntPtr.Zero);
        dialog = dlg;
        return found;
    }

    [STAThread]
    static int Main(string[] a)
    {
        string mode = a[0], dir = a[1], log = a[2];
        bool classic = mode.StartsWith("classic");
        var buf = Marshal.AllocHGlobal(2 * 2048);
        var init = Encoding.Unicode.GetBytes("respsrch\0"); Marshal.Copy(init, 0, buf, init.Length);
        var o = new OFN();
        o.lStructSize = Marshal.SizeOf(typeof(OFN)); o.lpstrFile = buf; o.nMaxFile = 2048;
        string filter = "מסמך טקסט\0*.txt\0מסמך טקסט - UNICODE\0*.txt\0";
        o.lpstrFilter = filter;
        o.nFilterIndex = mode.EndsWith("-ansi") ? 1 : 2;
        o.lpstrInitialDir = dir; o.lpstrTitle = "Save As";
        o.Flags = 0x80000 | 0x2 | 0x800;   // OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST
        HookProc hook = (h, m, w, l) => IntPtr.Zero;
        if (classic) { o.Flags |= 0x20; o.lpfnHook = hook; }   // OFN_ENABLEHOOK: stays the classic dialog

        bool done = false;
        if (mode.EndsWith("-late") || mode.EndsWith("-stubborn"))
            new Thread(delegate ()
            {
                IntPtr d, ed = IntPtr.Zero;
                for (int i = 0; i < 200 && ed == IntPtr.Zero; i++) { Thread.Sleep(50); ed = NameEdit(out d); }
                if (ed == IntPtr.Zero) return;
                Thread.Sleep(800);
                if (mode.EndsWith("-late")) { SendMessage(ed, 0x0C, IntPtr.Zero, "respsrch"); return; }
                while (!done && IsWindow(ed)) { if (Text(ed) != "respsrch") SendMessage(ed, 0x0C, IntPtr.Zero, "respsrch"); Thread.Sleep(100); }
            }) { IsBackground = true }.Start();

        bool exists1 = mode.EndsWith("-exists"), odd = mode.EndsWith("-existsodd"), exists2 = mode.EndsWith("-exists2") || odd, late = exists1 || exists2;
        int prompts = 0;
        bool ok; string path; bool append = false;
        while (true)
        {
            // The marshaller copies the strings back after a call, cut at their first NUL: set them again each time.
            o.lpstrFilter = filter; o.lpstrInitialDir = dir; o.lpstrTitle = "Save As";
            ok = GetSaveFileName(ref o);
            path = Marshal.PtrToStringUni(buf);
            if (ok && late) { File.WriteAllText(path, "LATE text of an earlier save\r\n", new UnicodeEncoding(false, true)); late = exists2; }
            if (ok && (exists1 || exists2) && File.Exists(path))
            {
                int b = ExistsPrompt(odd && ++prompts > 1 ? Path.Combine(dir, "not-the-tool's.txt") : path);
                File.AppendAllText(log, "EXISTS|" + b + "|" + path + "\r\n");
                if (b == 2) continue;          // ביטול: the Save As window again, as Responsa does
                append = b == 1056;            // הוסף
            }
            break;
        }
        done = true;
        if (ok && mode.EndsWith("-elsewhere")) path = Path.Combine(dir, "respsrch");
        File.AppendAllText(log, (ok ? "OK" : "CANCEL") + "|" + o.nFilterIndex + "|" + path + "\r\n");
        if (ok && append) File.AppendAllText(path, "החיפוש : test " + mode + "\r\n", new UnicodeEncoding(false, false));
        else if (ok) File.WriteAllText(path, "החיפוש : test " + mode + "\r\n", new UnicodeEncoding(false, true));
        GC.KeepAlive(hook);
        return ok ? 0 : 1;
    }
}
