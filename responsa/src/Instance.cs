// IT-780: finding Responsa, and the lifecycle of the tool's own instance on the private desktop.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace ResponsaMcp
{
    internal sealed class Install
    {
        public string Exe;       // ...\RESPONSA.exe
        public string Dir;       // program folder
        public string DataDir;   // folder holding Responsa.ini and DATA\
        public string Version;   // file version

        /// <summary>Finds the installation: RESPONSA_MCP_EXE, then the uninstall registry, then Program Files.</summary>
        public static Install Find()
        {
            var candidates = new List<string>();
            string env = Environment.GetEnvironmentVariable("RESPONSA_MCP_EXE");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (var path in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    try
                    {
                        using (var k = root.OpenSubKey(path))
                        {
                            if (k == null) continue;
                            foreach (var name in k.GetSubKeyNames())
                                using (var s = k.OpenSubKey(name))
                                {
                                    if (s == null) continue;
                                    string pub = (s.GetValue("Publisher") as string) ?? "";
                                    string disp = (s.GetValue("DisplayName") as string) ?? "";
                                    string loc = (s.GetValue("InstallLocation") as string) ?? "";
                                    if (loc.Length > 0 && (pub.IndexOf("Responsa", StringComparison.OrdinalIgnoreCase) >= 0 || disp.StartsWith("Responsa", StringComparison.OrdinalIgnoreCase)))
                                        candidates.Add(Path.Combine(loc, "RESPONSA.exe"));
                                }
                        }
                    }
                    catch { }
                }
            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
            {
                if (string.IsNullOrEmpty(pf) || !Directory.Exists(pf)) continue;
                try { foreach (var d in Directory.GetDirectories(pf, "Responsa*")) candidates.Add(Path.Combine(d, "RESPONSA.exe")); } catch { }
            }
            foreach (var c in candidates)
            {
                if (!File.Exists(c)) continue;
                var ins = new Install();
                ins.Exe = Path.GetFullPath(c);
                ins.Dir = Path.GetDirectoryName(ins.Exe);
                ins.DataDir = ReadDataLocation(ins.Dir);
                try { ins.Version = FileVersionInfo.GetVersionInfo(ins.Exe).FileVersion; } catch { ins.Version = "?"; }
                return ins;
            }
            throw new ToolError("Responsa (Bar-Ilan Responsa Project) was not found on this PC. Set RESPONSA_MCP_EXE to the full path of RESPONSA.exe.");
        }

        /// <summary>DataLocation from Responsa.env in the program folder; falls back to Public Documents.</summary>
        static string ReadDataLocation(string dir)
        {
            try
            {
                string envFile = Path.Combine(dir, "Responsa.env");
                if (File.Exists(envFile))
                    foreach (var line in File.ReadAllLines(envFile))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("DataLocation=", StringComparison.OrdinalIgnoreCase)) return t.Substring("DataLocation=".Length).Trim();
                    }
            }
            catch { }
            string pub = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
            return Path.Combine(pub, "Documents", Path.GetFileName(dir));
        }
    }

    /// <summary>
    /// The tool's own Responsa process. It is held by its process handle from CreateProcess (never by PID alone),
    /// recorded on disk until it has exited, watched by a WorkspaceGuard while alive, and ended without
    /// Responsa's exit save.
    /// </summary>
    internal sealed class Instance
    {
        public const string WorkspaceFile = @"DATA\responsaext1.029";
        readonly UiThread ui;
        readonly string stateDir, instancesDir, guardDir;
        readonly long serverStart = Proc.Self();
        readonly int serverPid = Process.GetCurrentProcess().Id;
        public Install Install;
        public uint Pid;
        IntPtr hProc = IntPtr.Zero;      // handle of our RESPONSA.exe, held until it has exited
        long created;                    // its creation time (FILETIME)
        int sequence;                    // instances started by this server
        WorkspaceGuard guard;
        GuardWorld world;
        Timer watch;
        public readonly List<string> StartupLog = new List<string>();

        public Instance(UiThread ui, string stateDir)
        {
            this.ui = ui; this.stateDir = stateDir;
            instancesDir = Path.Combine(stateDir, "instances");
            guardDir = Path.Combine(stateDir, "workspace");
        }

        public bool Alive { get { return Proc.Running(hProc); } }

        /// <summary>RESPONSA processes that are not instances of a live responsa-mcp server (i.e. the editor's).</summary>
        public int EditorInstances()
        {
            var tool = ToolInstances();
            return Proc.ResponsaProcesses().Count(p => !tool.Contains(p.Key + ":" + p.Value));
        }
        HashSet<string> ToolInstances()
        {
            var set = new HashSet<string>();
            if (hProc != IntPtr.Zero) set.Add(Pid + ":" + created);
            if (!Directory.Exists(instancesDir)) return set;
            foreach (var f in Directory.GetFiles(instancesDir, "*.txt"))
            {
                var r = Record.Read(f);
                if (r != null && Proc.IsSame(r.ServerPid, r.ServerStart)) set.Add(r.Pid + ":" + r.Created);
            }
            return set;
        }

        /// <summary>Guard ids of other live servers whose instance is alive.</summary>
        List<string> OtherGuardIds()
        {
            var ids = new List<string>();
            if (!Directory.Exists(instancesDir)) return ids;
            foreach (var f in Directory.GetFiles(instancesDir, "*.txt"))
            {
                var r = Record.Read(f);
                if (r == null || (r.ServerPid == serverPid && r.ServerStart == serverStart)) continue;
                if (Proc.IsSame(r.ServerPid, r.ServerStart) && Proc.IsSame(r.Pid, r.Created)) ids.Add(r.GuardId);
            }
            return ids;
        }

        /// <summary>Kept-aside copies of the editor's workspace file (see WorkspaceGuard), for `status`.</summary>
        public List<string> KeptBackups() { return WorkspaceGuard.KeptAside(guardDir); }

        /// <summary>
        /// The guard's view of the processes. Every editor's Responsa is opened (handle kept) when first seen, so its
        /// exit time can be read after it exits.
        /// </summary>
        sealed class GuardWorld : IGuardWorld
        {
            readonly Instance inst;
            readonly Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>();
            public GuardWorld(Instance inst) { this.inst = inst; }

            public ICollection<string> Editors()
            {
                var tool = inst.ToolInstances();
                var list = new List<string>();
                foreach (var p in Proc.ResponsaProcesses())
                {
                    string id = p.Key + ":" + p.Value;
                    if (tool.Contains(id)) continue;
                    list.Add(id);
                    if (!handles.ContainsKey(id))
                    {
                        IntPtr h = Proc.Open(p.Key);
                        if (h != IntPtr.Zero && Proc.CreationTime(h) != p.Value) { Native.CloseHandle(h); h = IntPtr.Zero; }
                        handles[id] = h;
                    }
                }
                return list;
            }

            public EditorExit ExitOf(string id)
            {
                IntPtr h;
                if (!handles.TryGetValue(id, out h)) return EditorExit.Unknown;
                if (h != IntPtr.Zero && Proc.Running(h)) { Native.CloseHandle(h); handles.Remove(id); return null; }   // another server's instance
                handles.Remove(id);
                if (h == IntPtr.Zero) return inst.ToolInstances().Contains(id) ? null : EditorExit.Unknown;
                long c, e, k, u; uint code;
                bool ok = Native.GetProcessTimes(h, out c, out e, out k, out u);
                bool okCode = Native.GetExitCodeProcess(h, out code);
                Native.CloseHandle(h);
                return new EditorExit { AtUtc = ok && e > 0 ? DateTime.FromFileTimeUtc(e) : DateTime.MaxValue, Normal = !okCode || code == 0 };
            }

            public ICollection<string> OtherGuardIds() { return inst.OtherGuardIds(); }

            public void Dispose()
            {
                foreach (var h in handles.Values) if (h != IntPtr.Zero) Native.CloseHandle(h);
                handles.Clear();
            }
        }

        public void EnsureRunning()
        {
            if (Alive) return;
            if (hProc != IntPtr.Zero && !Stop())
                throw new ToolError("the previous private Responsa has not exited yet; try again in a moment");
            if (Install == null) Install = Install.Find();
            if (!Directory.Exists(Install.DataDir)) throw new ToolError("Responsa data folder not found: " + Install.DataDir);

            sequence++;
            string id = serverPid + "-" + serverStart + "-" + sequence;
            world = new GuardWorld(this);
            guard = new WorkspaceGuard(Path.Combine(Install.DataDir, WorkspaceFile), guardDir, id, world);
            try { guard.Begin(); }
            catch { world.Dispose(); world = null; guard = null; throw; }
            StartupLog.Clear();
            int pid;
            hProc = ui.Launch(Install.Exe, Install.Dir, out pid);
            Pid = (uint)pid;
            created = Proc.CreationTime(hProc);
            new Record { Pid = pid, Created = created, ServerPid = serverPid, ServerStart = serverStart, GuardId = id }.Write(RecordFile());
            watch = new Timer(delegate { try { guard.Tick(); } catch { } }, null, 1500, 1500);
            string err = ui.Run(delegate () { return WaitReady(); }, 90000);
            if (err != null) { Stop(); throw new ToolError(err); }
        }

        /// <summary>Waits for the main window, answers the known start-up dialogs, closes windows restored from the workspace.</summary>
        string WaitReady()
        {
            var sw = Stopwatch.StartNew();
            IntPtr main = IntPtr.Zero;
            double mainReadyAt = -1;
            while (sw.Elapsed.TotalSeconds < 60)
            {
                if (!Alive) return "Responsa closed during start-up. Is the Responsa disk (Disk-on-Key) connected?";
                main = Ui.MainWindow(Pid);
                bool mainReady = main != IntPtr.Zero && Win.Text(main).Contains("שולחן");
                if (mainReady && mainReadyAt < 0) mainReadyAt = sw.Elapsed.TotalSeconds;
                foreach (var d in Win.Dialogs(Pid))
                {
                    string t = Win.Text(d);
                    if (t == "הדרכה") { Win.Press(d, 2); StartupLog.Add("closed the tutorial window"); continue; }
                    // "Loading. Please wait..." and other transient windows: wait.
                    if (!mainReady || sw.Elapsed.TotalSeconds - mainReadyAt < 5) continue;
                    // Still there 5 s after the main window is ready: a start-up message (e.g. the Responsa disk is missing).
                    return "Responsa shows a message at start-up: '" + t + "' " + Win.DialogMessage(d);
                }
                if (mainReady && sw.Elapsed.TotalSeconds > 3 && Win.Dialogs(Pid).Count == 0)
                {
                    Thread.Sleep(700);
                    if (Win.Dialogs(Pid).Count == 0) break;
                }
                Thread.Sleep(250);
            }
            if (main == IntPtr.Zero) return "Responsa did not open its main window within 60 seconds.";
            int closed = Ui.CloseAllMdi(Pid);
            if (closed > 0) StartupLog.Add("closed " + closed + " window(s) restored from the last session");
            StartupLog.Add(string.Format("ready after {0:N1} s", sw.Elapsed.TotalSeconds));
            return null;
        }

        /// <summary>
        /// Ends our instance (TerminateProcess on our own handle: no exit save). Only once it has exited are the
        /// workspace guard settled, the record deleted and the handle closed. Returns false if it is still running.
        /// </summary>
        public bool Stop()
        {
            if (hProc == IntPtr.Zero) return true;
            if (Proc.Running(hProc))
            {
                Native.TerminateProcess(hProc, 1);
                if (Native.WaitForSingleObject(hProc, 15000) != Native.WAIT_OBJECT_0) return false;  // keep everything
            }
            if (watch != null) { watch.Dispose(); watch = null; }
            if (guard != null) { guard.End(); guard = null; }      // a copy that could not be settled stays as *.kept.*
            if (world != null) { world.Dispose(); world = null; }
            try { File.Delete(RecordFile()); } catch { }
            Native.CloseHandle(hProc); hProc = IntPtr.Zero; Pid = 0; created = 0;
            return true;
        }

        string RecordFile() { return Path.Combine(instancesDir, serverPid + "-" + serverStart + ".txt"); }

        /// <summary>
        /// At server start: ends instances recorded by servers that are gone (same handle checked for name and creation
        /// time, then terminated), then settles their workspace guards. A record is removed only when its process is gone.
        /// </summary>
        public static List<string> CleanupOrphans(string stateDir)
        {
            var kept = new List<string>();
            string dir = Path.Combine(stateDir, "instances"), gdir = Path.Combine(stateDir, "workspace");
            if (!Directory.Exists(dir)) return kept;
            foreach (var f in Directory.GetFiles(dir, "*.txt"))
            {
                var r = Record.Read(f);
                if (r == null) { try { File.Delete(f); } catch { } continue; }
                if (Proc.IsSame(r.ServerPid, r.ServerStart)) continue;                // its server is alive
                if (!Proc.KillResponsa(r.Pid, r.Created, 15000)) continue;              // still running: keep the record
                string k = WorkspaceGuard.SettleOrphan(gdir, r.GuardId);
                if (k != null) kept.Add(k);
                try { File.Delete(f); } catch { }
            }
            return kept;
        }

        sealed class Record
        {
            public int Pid, ServerPid; public long Created, ServerStart; public string GuardId;
            public void Write(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, string.Join("|", new[] { Pid.ToString(), Created.ToString(), ServerPid.ToString(), ServerStart.ToString(), GuardId }));
            }
            public static Record Read(string path)
            {
                try
                {
                    var p = File.ReadAllText(path).Split('|');
                    return new Record { Pid = int.Parse(p[0]), Created = long.Parse(p[1]), ServerPid = int.Parse(p[2]), ServerStart = long.Parse(p[3]), GuardId = p[4] };
                }
                catch { return null; }
            }
        }
    }
}
