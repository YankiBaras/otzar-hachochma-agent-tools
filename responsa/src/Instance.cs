// IT-780, IT-785: finding Responsa, and the lifecycle of the tool's own instance on the private desktop.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        public int Major;        // major part of the file version (29 for Responsa 29); 0 if it could not be read

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
                try { var fv = FileVersionInfo.GetVersionInfo(ins.Exe); ins.Version = fv.FileVersion; ins.Major = fv.FileMajorPart; } catch { ins.Version = "?"; }
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
    /// IT-785: which workspace autosave file Responsa uses. On Responsa 29 it is DATA\responsaext1.029, and every data
    /// file ends in .029, so the suffix is taken to be the version. The file is found by name instead of being fixed:
    /// DATA\responsaext1.0NN (three digits, the first 0; nothing longer, so a name like responsaext1.029_ never matches).
    /// </summary>
    internal sealed class WorkspaceFile
    {
        public const int TestedMajor = 29;
        static readonly Regex Name = new Regex(@"^responsaext1\.0[0-9]{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Path;            // the file to protect; null when several match
        public List<string> Matches;   // every matching file in DATA\
        public string State;           // "found", "none" or "several"

        /// <summary>
        /// Exactly one match: that file. None: the name Responsa of this version is expected to create
        /// (responsaext1.0NN for major version NN; .029 when the version is unknown), protected as a file that did not
        /// exist before. Several: no path; the caller refuses to start.
        /// </summary>
        public static WorkspaceFile Find(string dataDir, int major)
        {
            var r = new WorkspaceFile { Matches = new List<string>() };
            string data = System.IO.Path.Combine(dataDir, "DATA");
            // Every file is listed and matched here, not with a Directory.GetFiles pattern: Windows also matches
            // patterns against 8.3 short names, so "responsaext1.0??" could match a longer name.
            if (Directory.Exists(data))
                foreach (var f in Directory.GetFiles(data))
                    if (Name.IsMatch(System.IO.Path.GetFileName(f))) r.Matches.Add(f);
            r.Matches.Sort(StringComparer.OrdinalIgnoreCase);
            if (r.Matches.Count == 1) { r.State = "found"; r.Path = r.Matches[0]; }
            else if (r.Matches.Count == 0) { r.State = "none"; r.Path = System.IO.Path.Combine(data, ExpectedName(major)); }
            else r.State = "several";
            return r;
        }

        public static string ExpectedName(int major)
        {
            return "responsaext1." + (major > 0 && major < 100 ? major : TestedMajor).ToString("000");
        }

        /// <summary>The error when several files match: the tool does not guess which one Responsa uses.</summary>
        public string SeveralError()
        {
            return "Responsa's data folder has several workspace files (" + string.Join(", ", Matches.Select(m => System.IO.Path.GetFileName(m)).ToArray())
                + " in " + System.IO.Path.GetDirectoryName(Matches[0]) + "). responsa-mcp does not know which one this Responsa uses, so it does not start its instance. "
                + "The suffix is probably the Responsa version (.029 for Responsa 29). The user can move the file of a version that is no longer installed out of the DATA folder, with every Responsa closed.";
        }
    }

    /// <summary>
    /// The tool's own Responsa process. It is held by its process handle from CreateProcess (never by PID alone),
    /// recorded on disk until it has exited, watched by a WorkspaceGuard while alive, and ended without
    /// Responsa's exit save.
    /// </summary>
    internal sealed class Instance
    {
        public WorkspaceFile Workspace;  // IT-785: resolved when the current instance starts, kept for its lifetime
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

            // IT-785: the workspace file is found once per instance start, before anything is copied or launched.
            var ws = WorkspaceFile.Find(Install.DataDir, Install.Major);
            Workspace = ws;
            if (ws.Path == null) throw new ToolError(ws.SeveralError());

            sequence++;
            string id = serverPid + "-" + serverStart + "-" + sequence;
            world = new GuardWorld(this);
            try { guard = StartGuard(ws.Path, guardDir, id, world, null, 30, 100); }
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

        /// <summary>
        /// IT-785 (review round 1): checks that every other live server's guard protects the same workspace file, and
        /// only then creates this guard and calls Begin. Their kept copies are compared and shared, so a guard of another
        /// file must never be adopted. A meta that cannot be read (being rewritten) is read again, up to `attempts`
        /// times `sleepMs` apart, as Begin does; still unreadable, it is not taken as "the same file": nothing starts.
        /// `fileOf` is WorkspaceGuard.FileOf unless a test passes another reader.
        /// </summary>
        internal static WorkspaceGuard StartGuard(string path, string guardDir, string id, IGuardWorld world, Func<string, string> fileOf, int attempts, int sleepMs)
        {
            if (fileOf == null) fileOf = other => WorkspaceGuard.FileOf(guardDir, other);
            foreach (var other in world.OtherGuardIds())
            {
                string f = null;
                for (int i = 0; i < attempts && f == null; i++)
                {
                    if (i > 0) Thread.Sleep(sleepMs);
                    f = fileOf(other);
                }
                if (f == null)
                    throw new ToolError("could not read which workspace file another responsa-mcp server protects; not starting a second instance. Try again in a moment.");
                if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    throw new ToolError("another responsa-mcp server protects the workspace file " + f + ", but this one found " + path + "; not starting a second instance. Try again after the other server has stopped.");
            }
            var g = new WorkspaceGuard(path, guardDir, id, world);
            g.Begin();
            return g;
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
