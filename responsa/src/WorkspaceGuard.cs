// IT-780 (reviews 1 and 2, finding 1): protects the editor's workspace autosave file (DATA\responsaext1.029).
//
// Responsa writes this file every few minutes (autosave) and on a normal exit, and reopens its windows from it on
// the next start. The tool's instance writes it too. Rules:
//   * The guard keeps a copy of the editor's version (the "kept" version) in its own files (server pid + start +
//     instance sequence). While the tool's instance is alive the file is checked every ~1.5 s (Tick).
//   * No editor's Responsa running: any difference from the kept version can only come from a tool instance, so the
//     kept version is put back (or the file removed, if it did not exist). This is done only while every other
//     live server's guard keeps the same version; if they disagree, nothing is written.
//   * Editor's Responsa running (the overlap): a change cannot be attributed and is left alone. The guard copies the
//     last bytes it sees in that time (the "candidate").
//   * The editor's Responsa exits normally (exit code 0): if the file was last written no later than the editor's
//     exit time, that is the editor's exit save and becomes the kept version. If it was written after the editor
//     exited, the tool wrote it; the candidate (the last bytes seen while the editor ran) becomes the kept version
//     and is put back.
//   * The editor's Responsa crashed or was ended (exit code not 0): there is no exit save, and the file may hold the
//     tool's overlap autosave. Nothing is written; if the file differs from the kept version, the kept version is
//     set aside (*.kept.*) before the file as it is becomes the version to keep.
//   * A second server never takes the file as it is on disk while another server's instance is alive: it takes that
//     guard's kept version, so two guards never put different bytes back.
//   * The kept bytes are deleted only when the workspace file equals them (End and SettleOrphan). Otherwise they are
//     kept as *.kept.bak / *.kept.meta and reported by `status`.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace ResponsaMcp
{
    /// <summary>How an editor's Responsa ended.</summary>
    internal sealed class EditorExit
    {
        public DateTime AtUtc;   // exit time; DateTime.MaxValue if unknown
        public bool Normal;      // exit code 0 (a normal close, with Responsa's exit save); true if unknown
        public static readonly EditorExit Unknown = new EditorExit { AtUtc = DateTime.MaxValue, Normal = true };
    }

    /// <summary>What the guard needs to know about the processes around it (real: Instance.GuardWorld; tests: a fake).</summary>
    internal interface IGuardWorld
    {
        /// <summary>Ids of the editor's Responsa processes running now (not this server's instance, not another live server's).</summary>
        ICollection<string> Editors();
        /// <summary>For an id returned earlier by Editors() and gone from it now: how it ended; EditorExit.Unknown if that
        /// cannot be read; null if it is still running (it was another server's instance whose record appeared).</summary>
        EditorExit ExitOf(string id);
        /// <summary>Guard ids of other servers whose instance is alive.</summary>
        ICollection<string> OtherGuardIds();
    }

    internal sealed class WorkspaceGuard
    {
        public const string Absent = "absent";

        readonly string file, dir, bak, meta, cand;
        readonly IGuardWorld world;
        readonly object gate = new object();
        bool existed;            // the kept (editor's) version exists as a file
        string keptHash;         // hash of the kept version, or Absent
        string lastHash;         // hash seen at the last check (for the meta file)
        bool overlap;            // an editor's Responsa was running at the last check
        bool candExisted; string candHash;
        HashSet<string> tracked = new HashSet<string>();
        bool active;

        public WorkspaceGuard(string workspaceFile, string guardDir, string id, IGuardWorld world)
        {
            file = workspaceFile;
            dir = guardDir;
            Directory.CreateDirectory(guardDir);
            bak = Path.Combine(guardDir, id + ".bak");
            meta = Path.Combine(guardDir, id + ".meta");
            cand = Path.Combine(guardDir, id + ".cand");
            this.world = world;
        }

        /// <summary>
        /// Called before the tool's instance starts. With no other live server, keeps the file as it is now. Otherwise
        /// takes the other guard's kept version (never the file, which that server's instance may have written).
        /// </summary>
        public void Begin()
        {
            lock (gate)
            {
                bool done = false;
                for (int attempt = 0; attempt < 30 && !done; attempt++)
                {
                    var others = ReadOthers(world.OtherGuardIds());
                    if (others == null) { Thread.Sleep(100); continue; }            // a meta file being written
                    if (others.Count == 0) { Keep(); done = true; break; }
                    if (others.Select(o => o.Kept).Distinct().Count() > 1)
                        throw new ToolError("two other responsa-mcp servers keep different versions of Responsa's workspace file; not starting a third instance. Try again after one of them has stopped.");
                    done = CopyFrom(others[0]);
                    if (!done) Thread.Sleep(100);                                    // its copy was being replaced
                }
                if (!done) throw new ToolError("could not take a copy of Responsa's workspace file; try again in a moment");
                tracked = new HashSet<string>(world.Editors());
                overlap = tracked.Count > 0;
                if (overlap) Cand();
                lastHash = HashOf(file);
                active = true;
                SaveMeta();
            }
        }

        /// <summary>Periodic check while the tool's instance may write the file.</summary>
        public void Tick()
        {
            lock (gate)
            {
                if (!active) return;
                var now = new HashSet<string>(world.Editors());
                bool exited = false, abnormal = false; DateTime lastExit = DateTime.MinValue;
                foreach (var id in tracked)
                {
                    if (now.Contains(id)) continue;
                    EditorExit x = world.ExitOf(id);
                    if (x == null) continue;                                         // not an editor after all
                    exited = true;
                    if (!x.Normal) abnormal = true;
                    if (x.AtUtc > lastExit) lastExit = x.AtUtc;
                }
                tracked = now;
                string cur = HashOf(file);

                if (now.Count > 0)
                {
                    // Overlap: ambiguous, left alone. Remember the last bytes seen while the editor runs.
                    if (!overlap || cur != candHash) Cand();
                    overlap = true;
                    lastHash = cur; SaveMeta();
                    return;
                }
                if (overlap)
                {
                    overlap = false;
                    if (exited && abnormal)
                    {
                        // Crashed or ended: no exit save. Who wrote the file is unknown; write nothing, and keep the
                        // older version aside before taking the file as it is.
                        if (HashOf(file) != keptHash) SetAside();
                        Keep();
                    }
                    else if (exited)
                    {
                        if (WrittenBy(lastExit)) Keep();     // the editor's exit save
                        else AdoptCand();                    // written after the editor had exited: the tool's
                    }
                    TryDelete(cand);
                }
                if (HashOf(file) != keptHash && OthersAgree()) PutBack();
                lastHash = HashOf(file);
                SaveMeta();
            }
        }

        /// <summary>
        /// Called after the tool's instance has exited (confirmed): a last check, then the guard's files go, but only if
        /// the workspace file equals the kept version. Otherwise the kept copy stays as *.kept.* and its path is returned.
        /// </summary>
        public string End()
        {
            lock (gate) { if (!active) return null; }
            Tick();
            lock (gate)
            {
                active = false;
                TryDelete(cand);
                if (HashOf(file) == keptHash) { TryDelete(bak); TryDelete(meta); return null; }
                return KeepAside(dir, Path.GetFileNameWithoutExtension(meta));
            }
        }

        // ---- internals ----
        void Keep()
        {
            existed = File.Exists(file);
            if (existed) { File.Copy(file, bak, true); keptHash = HashOf(bak); }
            else { TryDelete(bak); keptHash = Absent; }
        }
        void Cand()
        {
            candExisted = File.Exists(file);
            try
            {
                if (candExisted) { File.Copy(file, cand, true); candHash = HashOf(cand); }
                else { TryDelete(cand); candHash = Absent; }
            }
            catch { candHash = null; }        // busy: taken again at the next check (null never equals a hash)
        }
        void AdoptCand()
        {
            if (candHash == null || candHash == "unreadable") return;   // no good candidate: the older kept version stays
            if (candExisted) File.Copy(cand, bak, true); else TryDelete(bak);
            existed = candExisted; keptHash = candHash;
        }
        int asideCount;
        /// <summary>Copies the kept version aside as &lt;id&gt;-&lt;n&gt;.kept.bak / .kept.meta (reported by status, never deleted).</summary>
        void SetAside()
        {
            asideCount++;
            string baseName = Path.Combine(dir, Path.GetFileNameWithoutExtension(meta) + "-" + asideCount + ".kept");
            try
            {
                if (existed) File.Copy(bak, baseName + ".bak", true);
                File.WriteAllLines(baseName + ".meta", new[] { "existed=" + existed, "kept=" + keptHash, "file=" + file, "reason=the editor's Responsa did not exit normally" });
            }
            catch { }
        }
        bool WrittenBy(DateTime exitUtc)
        {
            if (exitUtc == DateTime.MaxValue) return true;               // exit time unknown: trust the exit save
            if (!File.Exists(file)) return true;
            // Measured on Responsa 29: the exit save is written within ~40 ms of the close request and the process
            // exits 2-6 s later, so the editor's exit save is always stamped before its exit time.
            return File.GetLastWriteTimeUtc(file) <= exitUtc;
        }
        bool PutBack()
        {
            try
            {
                if (existed) File.Copy(bak, file, true);
                else if (File.Exists(file)) File.Delete(file);
                return HashOf(file) == keptHash;
            }
            catch { return false; }   // file busy (being written): retried at the next check
        }
        bool OthersAgree()
        {
            var others = ReadOthers(world.OtherGuardIds());
            return others != null && others.All(o => o.Kept == keptHash);
        }
        bool CopyFrom(Other o)
        {
            try
            {
                if (o.Existed) File.Copy(Path.Combine(dir, o.Id + ".bak"), bak, true); else TryDelete(bak);
                string h = o.Existed ? HashOf(bak) : Absent;
                if (h != o.Kept) return false;
                existed = o.Existed; keptHash = o.Kept;
                return true;
            }
            catch { return false; }
        }
        void SaveMeta()
        {
            try
            {
                string tmp = meta + ".tmp";
                File.WriteAllLines(tmp, new[] { "existed=" + existed, "kept=" + keptHash, "last=" + lastHash, "file=" + file });
                if (File.Exists(meta)) File.Replace(tmp, meta, null); else File.Move(tmp, meta);
            }
            catch { }
        }

        sealed class Other { public string Id, Kept; public bool Existed; }
        /// <summary>Kept versions of other guards; null if one of them could not be read right now.</summary>
        List<Other> ReadOthers(ICollection<string> ids)
        {
            var list = new List<Other>();
            foreach (var id in ids)
            {
                var kv = ReadMeta(Path.Combine(dir, id + ".meta"));
                if (kv == null) return null;
                string k, e;
                if (!kv.TryGetValue("kept", out k) || !kv.TryGetValue("existed", out e)) return null;
                list.Add(new Other { Id = id, Kept = k, Existed = e == "True" });
            }
            return list;
        }
        static Dictionary<string, string> ReadMeta(string path)
        {
            try
            {
                var kv = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(path)) { int i = line.IndexOf('='); if (i > 0) kv[line.Substring(0, i)] = line.Substring(i + 1); }
                return kv;
            }
            catch { return null; }
        }
        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        /// <summary>Renames id.bak / id.meta to id.kept.bak / id.kept.meta. Returns the path to report.</summary>
        static string KeepAside(string guardDir, string id)
        {
            string bak = Path.Combine(guardDir, id + ".bak"), meta = Path.Combine(guardDir, id + ".meta");
            string keptBak = Path.Combine(guardDir, id + ".kept.bak"), keptMeta = Path.Combine(guardDir, id + ".kept.meta");
            try { if (File.Exists(bak)) File.Move(bak, keptBak); } catch { }
            try { if (File.Exists(meta)) File.Move(meta, keptMeta); } catch { }
            return File.Exists(keptBak) ? keptBak : keptMeta;
        }

        public static string HashOf(string f)
        {
            try
            {
                if (!File.Exists(f)) return Absent;
                using (var md5 = MD5.Create()) using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return Convert.ToBase64String(md5.ComputeHash(s));
            }
            catch { return "unreadable"; }
        }

        /// <summary>
        /// Settles the guard of a server that died (its instance already confirmed gone). The guard's files are removed
        /// only if the workspace file is the kept version. Otherwise the kept version stays on disk untouched (renamed
        /// *.kept.*) and its path is returned; the workspace file itself is never written here.
        /// </summary>
        public static string SettleOrphan(string guardDir, string id)
        {
            string meta = Path.Combine(guardDir, id + ".meta"), bak = Path.Combine(guardDir, id + ".bak");
            TryDelete(Path.Combine(guardDir, id + ".cand"));
            TryDelete(meta + ".tmp");
            if (!File.Exists(meta))
                // No meta: Begin never finished (crash between the copy and the meta). Nothing was protected yet;
                // a copy without its meta is still kept aside rather than deleted.
                return File.Exists(bak) ? KeepAside(guardDir, id) : null;
            var kv = ReadMeta(meta);
            string f, kept;
            if (kv == null || !kv.TryGetValue("file", out f) || !kv.TryGetValue("kept", out kept)) return KeepAside(guardDir, id);
            if (HashOf(f) == kept) { TryDelete(bak); TryDelete(meta); return null; }
            return KeepAside(guardDir, id);
        }

        /// <summary>Kept-aside copies in guardDir, as paths to report.</summary>
        public static List<string> KeptAside(string guardDir)
        {
            var r = new List<string>();
            if (!Directory.Exists(guardDir)) return r;
            foreach (var m in Directory.GetFiles(guardDir, "*.kept.meta"))
            {
                string b = m.Substring(0, m.Length - ".meta".Length) + ".bak";
                r.Add(File.Exists(b) ? b : m + " (the workspace file did not exist then)");
            }
            foreach (var b in Directory.GetFiles(guardDir, "*.kept.bak"))
                if (!File.Exists(b.Substring(0, b.Length - ".bak".Length) + ".meta")) r.Add(b);
            return r;
        }
    }
}
