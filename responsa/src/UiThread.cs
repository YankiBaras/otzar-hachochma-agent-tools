// IT-780: one worker thread attached to the tool's private desktop. Every UI action runs on it, in order.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ResponsaMcp
{
    internal sealed class UiThread
    {
        readonly string desktopName;
        readonly Queue<Action> queue = new Queue<Action>();
        readonly object gate = new object();
        Thread thread;
        IntPtr desktop = IntPtr.Zero;
        Exception startError;

        public UiThread(string desktopName) { this.desktopName = desktopName; }
        public string DesktopName { get { return desktopName; } }

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate ()
            {
                try
                {
                    desktop = Native.OpenDesktop(desktopName, 0, false, Native.DESKTOP_ALL);
                    if (desktop == IntPtr.Zero)
                        desktop = Native.CreateDesktop(desktopName, IntPtr.Zero, IntPtr.Zero, 0, Native.DESKTOP_ALL, IntPtr.Zero);
                    if (desktop == IntPtr.Zero) throw new Exception("cannot create the private desktop (error " + Marshal.GetLastWin32Error() + ")");
                    if (!Native.SetThreadDesktop(desktop)) throw new Exception("cannot attach to the private desktop (error " + Marshal.GetLastWin32Error() + ")");
                }
                catch (Exception e) { startError = e; ready.Set(); return; }
                ready.Set();
                Loop();
            });
            thread.IsBackground = true;
            // MTA: an STA thread gets a hidden COM window, and SetThreadDesktop refuses threads that own windows (error 170).
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Name = "responsa-ui";
            thread.Start();
            ready.WaitOne();
            if (startError != null) throw startError;
        }

        void Loop()
        {
            while (true)
            {
                Action a;
                lock (gate)
                {
                    while (queue.Count == 0) Monitor.Wait(gate);
                    a = queue.Dequeue();
                }
                if (a == null) return;
                a();
            }
        }

        /// <summary>Runs fn on the UI thread and waits for it (with a timeout).</summary>
        public T Run<T>(Func<T> fn, int timeoutMs)
        {
            T result = default(T); Exception err = null;
            var done = new ManualResetEvent(false);
            lock (gate)
            {
                queue.Enqueue(delegate ()
                {
                    try { result = fn(); } catch (Exception e) { err = e; }
                    done.Set();
                });
                Monitor.Pulse(gate);
            }
            if (!done.WaitOne(timeoutMs)) throw new StuckError("Responsa did not finish the operation in time (" + timeoutMs / 1000 + " s)");
            if (err != null)
            {
                if (err is ToolError) throw err;
                throw new ToolError("internal error: " + err.Message);
            }
            return result;
        }

        /// <summary>Starts a process whose windows live on the private desktop. Returns its process handle (kept by the
        /// caller for the life of the process, so its PID can never be confused with a reused one) and its PID.</summary>
        public IntPtr Launch(string exe, string workDir, out int pid)
        {
            var si = new Native.STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "WinSta0\\" + desktopName;
            Native.PROCESS_INFORMATION pi;
            if (!Native.CreateProcess(exe, "\"" + exe + "\"", IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workDir, ref si, out pi))
                throw new ToolError("cannot start Responsa (error " + Marshal.GetLastWin32Error() + ")");
            Native.CloseHandle(pi.hThread);
            pid = pi.dwProcessId;
            return pi.hProcess;
        }

        /// <summary>Ends the worker thread and closes the private desktop (it is destroyed once no process uses it).</summary>
        public void Shutdown()
        {
            lock (gate) { queue.Enqueue(null); Monitor.Pulse(gate); }
            if (thread != null) thread.Join(5000);
            if (desktop != IntPtr.Zero) { Native.CloseDesktop(desktop); desktop = IntPtr.Zero; }
        }
    }

    /// <summary>An error whose message is meant for the agent.</summary>
    internal class ToolError : Exception
    {
        public ToolError(string message) : base(message) { }
    }
    /// <summary>Responsa is in a state the tool cannot trust (operation stuck, timed out): restart the instance.</summary>
    internal class StuckError : ToolError
    {
        public StuckError(string message) : base(message) { }
    }
    /// <summary>Responsa showed an internal error or an unknown window: do not click it; restart the instance.</summary>
    internal sealed class FatalDialogError : StuckError
    {
        public FatalDialogError(string message) : base("Responsa reported: " + message) { }
    }
    /// <summary>A stored results window is no longer a window of our instance.</summary>
    internal sealed class WindowGone : ToolError
    {
        public WindowGone() : base("the results window of this search is gone; run the search again") { }
    }
}
