// IT-780: Win32 layer. Every call that can block uses SendMessageTimeout.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ResponsaMcp
{
    internal static class Native
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumDesktopWindows(IntPtr desk, EnumProc f, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, StringBuilder l, uint f, uint t, out IntPtr r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, string l, uint f, uint t, out IntPtr r);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateDesktop(string name, IntPtr dev, IntPtr devmode, uint flags, uint access, IntPtr sa);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
        [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        public const uint PROCESS_TERMINATE = 0x0001, PROCESS_QUERY_LIMITED = 0x1000, SYNCHRONIZE = 0x00100000;
        public const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, uint size, uint type, uint protect);
        [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr p, IntPtr a, uint size, uint type);
        [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, uint size, out IntPtr written);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, uint size, out IntPtr read);

        public const uint WM_SETTEXT = 0x0C, WM_GETTEXT = 0x0D, WM_GETTEXTLENGTH = 0x0E, WM_CLOSE = 0x10, WM_COMMAND = 0x111;
        public const uint BM_GETCHECK = 0xF0, BM_SETCHECK = 0xF1, BM_CLICK = 0xF5;
        public const uint WM_MDIACTIVATE = 0x222, WM_MDIGETACTIVE = 0x229;
        public const uint LB_GETCOUNT = 0x18B, LB_GETCURSEL = 0x188, LB_SETCURSEL = 0x186;
        public const uint LVM_GETITEMCOUNT = 0x1004, LVM_GETITEMTEXTW = 0x1073;
        public const uint PSM_SETCURSEL = 0x465;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        public const uint DESKTOP_ALL = 0x01FF;
        const uint PROC_VM = 0x0008 | 0x0010 | 0x0020 | 0x0400;
    }

    /// <summary>Small helpers over Native, for windows of one Responsa process.</summary>
    internal static class Win
    {
        public static IntPtr Send(IntPtr h, uint msg, long w, long l, uint timeoutMs)
        {
            IntPtr r; Native.SendMessageTimeout(h, msg, (IntPtr)w, (IntPtr)l, 0x0002, timeoutMs, out r); return r;
        }
        public static IntPtr Send(IntPtr h, uint msg, long w, long l) { return Send(h, msg, w, l, 5000); }

        public static string Text(IntPtr h)
        {
            IntPtr n; Native.SendMessageTimeout(h, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, 0x0002, 2000, out n);
            int len = Math.Max(0, Math.Min((int)n, 100000));
            var sb = new StringBuilder(len + 2); IntPtr r;
            Native.SendMessageTimeout(h, Native.WM_GETTEXT, (IntPtr)(len + 1), sb, 0x0002, 2000, out r);
            return sb.ToString();
        }
        public static void SetText(IntPtr h, string s) { IntPtr r; Native.SendMessageTimeout(h, Native.WM_SETTEXT, IntPtr.Zero, s, 0x0002, 5000, out r); }
        public static string Class(IntPtr h) { var sb = new StringBuilder(256); Native.GetClassName(h, sb, 256); return sb.ToString(); }
        public static uint Pid(IntPtr h) { uint p; Native.GetWindowThreadProcessId(h, out p); return p; }

        /// <summary>Top-level windows of pid on the calling thread's desktop.</summary>
        public static List<IntPtr> Tops(uint pid)
        {
            var r = new List<IntPtr>();
            Native.EnumWindows(delegate (IntPtr h, IntPtr l) { if (Pid(h) == pid) r.Add(h); return true; }, IntPtr.Zero);
            return r;
        }
        public static List<IntPtr> Kids(IntPtr parent)
        {
            var r = new List<IntPtr>();
            if (parent == IntPtr.Zero) return r;
            Native.EnumChildWindows(parent, delegate (IntPtr h, IntPtr l) { r.Add(h); return true; }, IntPtr.Zero);
            return r;
        }
        public static IntPtr Kid(IntPtr parent, int id)
        {
            foreach (var k in Kids(parent)) if (Native.GetDlgCtrlID(k) == id) return k;
            return IntPtr.Zero;
        }
        public static IntPtr KidOfClass(IntPtr parent, string cls)
        {
            foreach (var k in Kids(parent)) if (Class(k) == cls) return k;
            return IntPtr.Zero;
        }
        /// <summary>Visible dialogs (#32770) of pid.</summary>
        public static List<IntPtr> Dialogs(uint pid)
        {
            var r = new List<IntPtr>();
            foreach (var h in Tops(pid)) if (Native.IsWindowVisible(h) && Class(h) == "#32770") r.Add(h);
            return r;
        }
        /// <summary>All visible static/edit texts of a dialog, joined (buttons excluded).</summary>
        public static string DialogMessage(IntPtr d)
        {
            var parts = new List<string>();
            foreach (var k in Kids(d))
            {
                string c = Class(k);
                if (c != "Static" && c != "Edit") continue;
                string t = Text(k).Trim();
                if (t.Length > 0) parts.Add(t);
            }
            return string.Join(" | ", parts.ToArray());
        }
        /// <summary>Presses a dialog button by id, through WM_COMMAND to its parent (works without focus).</summary>
        public static void Press(IntPtr dlg, int id)
        {
            IntPtr b = Kid(dlg, id);
            IntPtr parent = b != IntPtr.Zero ? Native.GetParent(b) : dlg;
            Native.PostMessage(parent, Native.WM_COMMAND, (IntPtr)(id & 0xFFFF), b);
        }
        public static void Click(IntPtr button) { Native.PostMessage(button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero); }

        // ---- cross-process reads (the target is 32-bit, and so is this exe) ----
        const uint PROC_VM = 0x0008 | 0x0010 | 0x0020 | 0x0400;

        /// <summary>Rows of a SysListView32 in another process.</summary>
        public static List<string[]> ListView(IntPtr lv, int cols)
        {
            var res = new List<string[]>();
            IntPtr hp = Native.OpenProcess(PROC_VM, false, Pid(lv));
            if (hp == IntPtr.Zero) return res;
            IntPtr mem = Native.VirtualAllocEx(hp, IntPtr.Zero, 4096, 0x3000, 0x04);
            try
            {
                int count = (int)Send(lv, Native.LVM_GETITEMCOUNT, 0, 0);
                for (int i = 0; i < count && i < 2000; i++)
                {
                    var row = new string[cols];
                    for (int c = 0; c < cols; c++)
                    {
                        var item = new byte[60]; // LVITEMW (32-bit layout)
                        BitConverter.GetBytes(0x1u).CopyTo(item, 0);
                        BitConverter.GetBytes(i).CopyTo(item, 4);
                        BitConverter.GetBytes(c).CopyTo(item, 8);
                        BitConverter.GetBytes((uint)(mem.ToInt64() + 256)).CopyTo(item, 20);
                        BitConverter.GetBytes(1000).CopyTo(item, 24);
                        IntPtr w; Native.WriteProcessMemory(hp, mem, item, (uint)item.Length, out w);
                        Send(lv, Native.LVM_GETITEMTEXTW, i, mem.ToInt64());
                        var buf = new byte[2000]; IntPtr rd;
                        Native.ReadProcessMemory(hp, (IntPtr)(mem.ToInt64() + 256), buf, 2000, out rd);
                        string s = Encoding.Unicode.GetString(buf); int z = s.IndexOf('\0');
                        row[c] = z >= 0 ? s.Substring(0, z) : s;
                    }
                    res.Add(row);
                }
            }
            finally { Native.VirtualFreeEx(hp, mem, 0, 0x8000); Native.CloseHandle(hp); }
            return res;
        }
    }
}
