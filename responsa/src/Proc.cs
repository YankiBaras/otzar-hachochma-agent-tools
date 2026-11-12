// IT-780 (review 1, finding 2): process identity by handle + creation time, never by PID alone.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ResponsaMcp
{
    internal static class Proc
    {
        /// <summary>Opens pid for query/terminate/wait. Caller closes the handle.</summary>
        public static IntPtr Open(int pid)
        {
            return Native.OpenProcess(Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED | Native.SYNCHRONIZE, false, (uint)pid);
        }
        public static long CreationTime(IntPtr h)
        {
            long c, e, k, u;
            return h != IntPtr.Zero && Native.GetProcessTimes(h, out c, out e, out k, out u) ? c : -1;
        }
        public static string ImageName(IntPtr h)
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return h != IntPtr.Zero && Native.QueryFullProcessImageName(h, 0, sb, ref n) ? Path.GetFileName(sb.ToString()) : "";
        }
        public static bool Running(IntPtr h) { return h != IntPtr.Zero && Native.WaitForSingleObject(h, 0) == Native.WAIT_TIMEOUT; }
        public static long Self() { return CreationTime(Native.GetCurrentProcess()); }

        /// <summary>True if pid is alive and is the process created at creationTime (same object checked and used).</summary>
        public static bool IsSame(int pid, long creationTime)
        {
            IntPtr h = Open(pid);
            try { return Running(h) && CreationTime(h) == creationTime; }
            finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
        }

        /// <summary>
        /// Terminates pid only if, on the same handle, it is RESPONSA.exe created at creationTime. True when the recorded
        /// process is gone: it exited, it was terminated, or pid now belongs to a process with another creation time or
        /// another (readable) image name. False, without killing, when it is still running and its creation time or name
        /// cannot be read: the caller keeps the record and tries again later.
        /// </summary>
        public static bool KillResponsa(int pid, long creationTime, uint waitMs)
        {
            IntPtr h = Open(pid);
            if (h == IntPtr.Zero)
            {
                // Gone, or not openable: only "gone" counts as gone (the record is kept otherwise).
                try { using (Process.GetProcessById(pid)) return false; } catch { return true; }
            }
            try
            {
                if (!Running(h)) return true;
                long ct = CreationTime(h);
                if (ct == -1) return false;                       // unreadable: keep the record
                if (ct != creationTime) return true;              // pid reused by another process: ours is gone
                string name = ImageName(h);
                if (name.Length == 0) return false;               // unreadable: keep the record
                if (!name.Equals("RESPONSA.exe", StringComparison.OrdinalIgnoreCase)) return true;  // not ours
                Native.TerminateProcess(h, 1);
                return Native.WaitForSingleObject(h, waitMs) == Native.WAIT_OBJECT_0;
            }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>(pid, creation time) of every running RESPONSA.exe.</summary>
        public static List<KeyValuePair<int, long>> ResponsaProcesses()
        {
            var r = new List<KeyValuePair<int, long>>();
            foreach (var p in Process.GetProcessesByName("RESPONSA"))
            {
                IntPtr h = Open(p.Id);
                try { if (Running(h)) r.Add(new KeyValuePair<int, long>(p.Id, CreationTime(h))); }
                finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
                p.Dispose();
            }
            return r;
        }
    }
}
