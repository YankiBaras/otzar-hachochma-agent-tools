// IT-827: a scratch program with a Save As window, for test/SaveDialogTest.cs (build.ps1 -DialogTest). It is never
// shipped. Responsa 31 and 34 were not available, so the Save As handling is tested against this program's windows:
//   item[-ansi|-late|-stubborn|-elsewhere]  the common item dialog (GetSaveFileName without a hook upgrades to it)
//   classic[-ansi]                          the classic Explorer-style dialog (a hook keeps GetSaveFileName classic)
//   -ansi       the type list starts on the non-Unicode entry
//   -late       800 ms after the window is shown, the name is reset to "respsrch" once (as a dialog that sets its
//               default name after it is shown would)
//   -stubborn   the name is reset to "respsrch" whenever it changes, until the window closes
//   -elsewhere  the window takes the name, but the program saves to "respsrch" in its own folder (a pilot user's v34)
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
        o.lpstrFilter = "מסמך טקסט\0*.txt\0מסמך טקסט - UNICODE\0*.txt\0";
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

        bool ok = GetSaveFileName(ref o);
        done = true;
        string path = Marshal.PtrToStringUni(buf);
        if (ok && mode.EndsWith("-elsewhere")) path = Path.Combine(dir, "respsrch");
        File.AppendAllText(log, (ok ? "OK" : "CANCEL") + "|" + o.nFilterIndex + "|" + path + "\r\n");
        if (ok) File.WriteAllText(path, "החיפוש : test " + mode + "\r\n", new UnicodeEncoding(false, true));
        GC.KeepAlive(hook);
        return ok ? 0 : 1;
    }
}
