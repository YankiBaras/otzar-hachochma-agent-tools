// IT-868: the environment the tool's RESPONSA.exe is started with.
// Measured (Responsa 29, this PC): with only the variables MCP clients pass to stdio servers by default (Claude Desktop
// omits TMP), Responsa shows "שגיאה מס' 15: לא ניתן לפתוח את הקובץ" and then "שגיאה מס' 9: בעיית קלט/פלט"; with TMP
// added it works. Responsa needs TMP and does not fall back to TEMP. A v31 pilot got error 15 already at start-up.
//
// So RESPONSA.exe is started with the server's own environment, completed with every variable of the user's default
// environment (CreateEnvironmentBlock: what a new logon session of this user gets) that the server lacks; TMP last, from
// TEMP or the system temp path, if still missing. A variable the client set is never removed or changed, and nothing of
// the user's own environment or registry is written.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ResponsaMcp
{
    internal sealed class ResponsaEnv
    {
        public Dictionary<string, string> Vars;   // the environment RESPONSA.exe gets
        public List<string> Added;                // names added to the server's environment, sorted
        public string DefaultError;               // why the user's default environment could not be read; null if it was

        /// <summary>For status and doctor: "complete" or which variables were missing and added.</summary>
        public string Report()
        {
            string s;
            if (Added.Count == 0) s = "complete (nothing was missing)";
            else
            {
                // Only well-known Windows names are listed: the user's own variables are counted, never named.
                bool tmp = Added.Contains("TMP");
                var known = Added.Where(a => Known.Contains(a, StringComparer.OrdinalIgnoreCase) && a != "TMP").ToArray();
                s = (tmp ? "TMP was missing (Responsa needs it); added. " : "")
                    + Added.Count + " variable(s) the MCP client did not pass were added from the user's default environment"
                    + (known.Length > 0 ? ", among them " + string.Join(", ", known) : "");
            }
            if (DefaultError != null) s += ". The user's default environment could not be read (" + DefaultError + "); only TMP is filled in";
            return s;
        }

        static readonly string[] Known = { "TMP", "TEMP", "windir", "ComSpec", "ProgramData", "ALLUSERSPROFILE", "PUBLIC", "ProgramFiles(x86)",
            "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "PATHEXT", "OS", "COMPUTERNAME", "USERDOMAIN", "SystemDrive", "SystemRoot" };

        static ResponsaEnv cached;
        /// <summary>Computed once per server process (the server's environment does not change).</summary>
        public static ResponsaEnv Get()
        {
            if (cached != null) return cached;
            var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) own[(string)e.Key] = (string)e.Value;
            string err;
            var def = UserDefault(out err);
            cached = Complete(own, def, System.IO.Path.GetTempPath());
            cached.DefaultError = err;
            return cached;
        }

        /// <summary>
        /// The server's variables, plus each variable of `userDefault` the server lacks (names compared without case);
        /// then TMP, if still missing, from TEMP or else `tempPath`. Never removes or changes a variable of `own`.
        /// An empty value counts as missing only for TMP (Responsa needs a usable one).
        /// </summary>
        internal static ResponsaEnv Complete(IDictionary<string, string> own, IDictionary<string, string> userDefault, string tempPath)
        {
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in own) if (!string.IsNullOrEmpty(kv.Key) && !kv.Key.StartsWith("=")) vars[kv.Key] = kv.Value ?? "";
            var added = new List<string>();
            if (userDefault != null)
                foreach (var kv in userDefault)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Key.StartsWith("=") || vars.ContainsKey(kv.Key)) continue;
                    vars[kv.Key] = kv.Value ?? ""; added.Add(kv.Key);
                }
            string tmp;
            if (!vars.TryGetValue("TMP", out tmp) || tmp.Trim().Length == 0)
            {
                string temp;
                string v = vars.TryGetValue("TEMP", out temp) && temp.Trim().Length > 0 ? temp : (tempPath ?? "").TrimEnd('\\');
                // An empty TMP of the client is replaced too: Responsa cannot use it.
                if (v.Length > 0) { vars["TMP"] = v; if (!added.Contains("TMP", StringComparer.OrdinalIgnoreCase)) added.Add("TMP"); }
            }
            added.Sort(StringComparer.OrdinalIgnoreCase);
            return new ResponsaEnv { Vars = vars, Added = added };
        }

        /// <summary>
        /// A Unicode environment block for CreateProcess (with CREATE_UNICODE_ENVIRONMENT): "name=value\0" for each
        /// variable, sorted by name without case (as CreateProcess documents), ending with an extra \0.
        /// </summary>
        internal static string Block(IDictionary<string, string> vars)
        {
            var sb = new StringBuilder();
            foreach (var k in vars.Keys.OrderBy(k => k.ToUpperInvariant(), StringComparer.Ordinal))
                sb.Append(k).Append('=').Append(vars[k]).Append('\0');
            sb.Append('\0');
            return sb.ToString();
        }

        /// <summary>Parses a block of "name=value\0...\0\0" (as CreateEnvironmentBlock returns it).</summary>
        internal static Dictionary<string, string> Parse(string block)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in block.Split('\0'))
            {
                if (entry.Length < 2) continue;
                int eq = entry.IndexOf('=', 1);   // a leading '=' belongs to the name (=C:=C:\...)
                if (eq <= 0) continue;
                string k = entry.Substring(0, eq);
                if (!d.ContainsKey(k)) d[k] = entry.Substring(eq + 1);
            }
            return d;
        }

        /// <summary>The user's default environment (CreateEnvironmentBlock, not inheriting this process's). Null on failure.</summary>
        static Dictionary<string, string> UserDefault(out string error)
        {
            error = null;
            IntPtr token = IntPtr.Zero, block = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(Native.GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_IMPERSONATE, out token))
                { error = "OpenProcessToken error " + Marshal.GetLastWin32Error(); return null; }
                if (!CreateEnvironmentBlock(out block, token, false))
                { error = "CreateEnvironmentBlock error " + Marshal.GetLastWin32Error(); return null; }
                // The block ends with two NULs; read characters up to there.
                var sb = new StringBuilder();
                for (int i = 0; i < 1 << 20; i++)
                {
                    char c = (char)Marshal.ReadInt16(block, i * 2);
                    if (c == '\0' && (sb.Length == 0 || sb[sb.Length - 1] == '\0')) break;
                    sb.Append(c);
                }
                return Parse(sb.ToString());
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
            finally
            {
                if (block != IntPtr.Zero) DestroyEnvironmentBlock(block);
                if (token != IntPtr.Zero) Native.CloseHandle(token);
            }
        }

        /// <summary>
        /// IT-868: Responsa's error 15 ("cannot open the file") is what a missing TMP looked like. The agent gets what to
        /// check, not only Responsa's text. Null when `message` is not error 15.
        /// </summary>
        public static string Hint(string message)
        {
            if (message == null || !System.Text.RegularExpressions.Regex.IsMatch(message, "שגיאה מס'?\\s*15\\b")) return null;
            string tmp;
            var env = Get();
            env.Vars.TryGetValue("TMP", out tmp);
            return "Responsa error 15 (cannot open a file). This was seen when Responsa had no TMP variable; responsa-mcp now starts it with TMP="
                + (tmp ?? "(none)") + " (environment: " + env.Report() + "). Check that this folder exists and can be written, "
                + "that the Responsa disk (Disk-on-Key / drive) is connected, and send the output of responsa-mcp.exe doctor if it persists.";
        }

        const uint TOKEN_QUERY = 0x0008, TOKEN_DUPLICATE = 0x0002, TOKEN_IMPERSONATE = 0x0004;
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr block);
    }
}
