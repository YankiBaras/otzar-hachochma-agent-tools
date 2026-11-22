// IT-827: drives the tool's real Save As code (Ui.FillAndSave, SaveDialog) against test/SaveAsHost.cs on a private
// desktop, as the tool drives Responsa. No Responsa needed; nothing is shown on the user's screen; everything is
// written inside one scratch folder under %TEMP%, removed at the end. Built and run by: .\build.ps1 -DialogTest
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ResponsaMcp
{
    internal static class SaveDialogTest
    {
        static int failed, passed;
        static void Check(bool ok, string name)
        {
            if (ok) { passed++; Console.WriteLine("PASS: " + name); } else { failed++; Console.WriteLine("FAIL: " + name); }
        }

        static int Main(string[] args)
        {
            string host = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "responsa-mcp-savehost.exe");
            string root = Path.Combine(Path.GetTempPath(), "responsa-dialog-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var ui = new UiThread("ResponsaMcpDialogTest-" + Process.GetCurrentProcess().Id);
            ui.Start();
            Ui.Version = "Responsa (dialog test)";
            try
            {
                // mode, typed, expected outcome: "saved" (file at path, Unicode type), "elsewhere" (null), "stuck" (StuckError)
                var cases = new[] {
                    new { Mode = "item", Typed = false, Want = "saved" },
                    new { Mode = "item", Typed = true, Want = "saved" },
                    new { Mode = "classic", Typed = false, Want = "saved" },
                    new { Mode = "classic", Typed = true, Want = "saved" },
                    new { Mode = "item-ansi", Typed = false, Want = "elsewhere" },
                    new { Mode = "item-ansi", Typed = true, Want = "saved" },
                    new { Mode = "classic-ansi", Typed = false, Want = "saved" },
                    new { Mode = "item-late", Typed = false, Want = "saved" },
                    new { Mode = "item-hebrew", Typed = true, Want = "saved" },
                    new { Mode = "classic-hebrew", Typed = true, Want = "saved" },
                    new { Mode = "item-elsewhere", Typed = false, Want = "elsewhere" },
                    new { Mode = "item-stubborn", Typed = false, Want = "stuck" },
                    // IT-871: Responsa's "file exists" prompt is cancelled and the save goes to a fresh name; a second
                    // prompt, for that fresh name, is answered "שכתב"; "הוסף" is never pressed
                    new { Mode = "item-exists", Typed = true, Want = "fresh" },
                    new { Mode = "classic-exists", Typed = true, Want = "fresh" },
                    new { Mode = "item-exists2", Typed = true, Want = "fresh" },
                    // a second prompt that does not name the tool's fresh file: both windows cancelled, an error, no restart
                    new { Mode = "item-existsodd", Typed = true, Want = "error" },
                };
                foreach (var c in cases) Run(ui, host, root, c.Mode, c.Typed, c.Want);
            }
            finally
            {
                ui.Shutdown();
                // The dialogs' folder views can hold a folder open for a moment after the host exits.
                for (int i = 0; i < 10 && Directory.Exists(root); i++)
                {
                    try { Directory.Delete(root, true); } catch { System.Threading.Thread.Sleep(500); }
                }
                if (Directory.Exists(root)) Console.WriteLine("note: could not remove " + root);
            }
            Console.WriteLine(string.Format("{0} passed, {1} failed", passed, failed));
            return failed == 0 ? 0 : 1;
        }

        static void Run(UiThread ui, string host, string root, string mode, bool typed, string want)
        {
            // "-hebrew": the path has a Hebrew folder name (as under a Hebrew Windows user name); the host runs as plain mode.
            bool hebrew = mode.EndsWith("-hebrew");
            string dir = Path.Combine(root, mode + (typed ? "-typed" : ""), hebrew ? "משתמש" : "");
            if (hebrew) mode = mode.Substring(0, mode.Length - "-hebrew".Length);
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "host.log"), path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".txt");
            string name = mode + (typed ? " (typed)" : "") + ": ";
            ui.Run(delegate ()
            {
                var si = new Native.STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.lpDesktop = "WinSta0\\" + ui.DesktopName;
                Native.PROCESS_INFORMATION pi;
                string cmd = "\"" + host + "\" " + mode + " \"" + dir + "\" \"" + log + "\"";
                if (!Native.CreateProcess(host, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out pi))
                { Check(false, name + "could not start the host"); return 0; }
                Native.CloseHandle(pi.hThread);
                string outcome; var sw = Stopwatch.StartNew();
                string fresh = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "-2.txt"), got = null;
                try { got = Ui.FillAndSave((uint)pi.dwProcessId, path, typed); outcome = got == path ? "saved" : got == fresh ? "fresh" : got == null ? "elsewhere" : "other: " + got; }
                catch (StuckError e) { outcome = "stuck"; Console.WriteLine("    " + e.Message); }
                catch (ToolError e) { outcome = "error"; Console.WriteLine("    " + e.Message); }
                Console.WriteLine("    " + Ui.LastSave + " (" + sw.ElapsedMilliseconds + " ms)");
                if (Native.WaitForSingleObject(pi.hProcess, 5000) != Native.WAIT_OBJECT_0) Native.TerminateProcess(pi.hProcess, 1);
                Native.CloseHandle(pi.hProcess);
                Check(outcome == want, name + "outcome " + outcome + " (expected " + want + ")");
                // Typed cases must be taken as typed, not only through the WM_SETTEXT fallback (a Hebrew path typed
                // through SendMessageTimeoutA did not hold, and the fallback hid it).
                if (typed && want == "saved") Check(Ui.LastSave.Contains("path typed") && !Ui.LastSave.Contains("did not hold"), name + "the typed path held");
                string[] lines = File.Exists(log) ? File.ReadAllLines(log) : new string[0];
                if (want == "saved")
                {
                    Check(lines.Length == 1 && lines[0] == "OK|2|" + path, name + "the host saved to the tool's path with the Unicode type (" + string.Join(" ", lines) + ")");
                    Check(File.Exists(path) && SaveParse.ReadSaved(path).StartsWith("החיפוש : test"), name + "the file at the tool's path reads back");
                }
                if (want == "fresh")
                {
                    bool twice = mode.EndsWith("-exists2");
                    string expect = "EXISTS|2|" + path + (twice ? " EXISTS|1|" + fresh : "") + " OK|2|" + fresh;
                    Check(string.Join(" ", lines) == expect, name + "the prompt was cancelled" + (twice ? ", then overwrote the fresh name" : "") + ", the save went to the fresh name (" + string.Join(" ", lines) + ")");
                    Check(SaveParse.ReadSaved(fresh) == "החיפוש : test " + mode + "\r\n", name + "the fresh file holds exactly the new text (nothing appended)");
                    Check(SaveParse.ReadSaved(path) == "LATE text of an earlier save\r\n", name + "the earlier file was left as it was (not appended to, not overwritten)");
                    Check(!lines.Any(l => l.StartsWith("EXISTS|1056")), name + "\"הוסף\" was never pressed");
                }
                if (want == "error")
                    Check(string.Join(" ", lines) == "EXISTS|2|" + path + " EXISTS|2|" + fresh + " CANCEL|2|" + fresh && Ui.LastSave.Length > 0,
                        name + "both prompts and the Save As window were cancelled, nothing written (" + string.Join(" ", lines) + ")");
                if (want == "elsewhere")
                    Check(!File.Exists(path) && File.Exists(Path.Combine(dir, "respsrch")), name + "nothing at the tool's path; the host's own file was not taken");
                if (want == "stuck")
                    Check(!File.Exists(path) && !File.Exists(Path.Combine(dir, "respsrch")), name + "Save was never pressed");
                return 0;
            }, 120000);
        }
    }
}
