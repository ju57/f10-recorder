using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace F10Recorder
{
    internal sealed class WindowCaptureChoice
    {
        internal long Handle, ProcessStartedUtcTicks;
        internal int ProcessId;
        internal string Executable, ClassName, Title;
        public override string ToString() { return Path.GetFileNameWithoutExtension(Executable) + " · " + Title; }
    }

    // Created/disposed on the worker message thread. Destroy events invalidate a
    // lease even if Windows reuses its HWND within the same process.
    internal sealed class WindowCaptureTarget : IDisposable
    {
        readonly Settings settings;
        readonly WinEvent callback;
        readonly IntPtr hook;
        readonly object gate = new object();
        WindowCaptureChoice bound;
        long generation;
        bool destroyed, disposed, hadTarget;
        string screenshotProperty;
        long screenshotWindow, screenshotTargetGeneration, screenshotGeneration;
        internal WindowCaptureTarget(Settings config)
        {
            settings = config; callback = Destroyed;
            hook = SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, callback, 0, 0, 0);
            if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot track window lifetime.");
        }
        void Destroyed(IntPtr ignored, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time)
        {
            if (objectId != 0 || childId != 0) return;
            lock (gate) if (bound != null && bound.Handle == window.ToInt64()) destroyed = true;
        }
        internal static List<WindowCaptureChoice> List(string executable = null, string className = null)
        {
            var result = new List<WindowCaptureChoice>();
            using (var self = Process.GetCurrentProcess())
            {
                string ownImage = Native.ProcessImagePath((uint)self.Id);
                EnumWindows(delegate(IntPtr window, IntPtr unused)
                {
                    if (result.Count >= (executable == null ? 128 : 2)) return false;
                    if (!Native.IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
                    var choice = Inspect(window);
                    if (choice != null && choice.ProcessId != self.Id && !string.Equals(choice.Executable, ownImage, StringComparison.OrdinalIgnoreCase)
                        && (executable == null || string.Equals(choice.Executable, executable, StringComparison.OrdinalIgnoreCase) && choice.ClassName == className)) result.Add(choice);
                    return true;
                }, IntPtr.Zero);
            }
            return result.OrderBy(c => c.ToString(), StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        internal static WindowCaptureChoice Inspect(IntPtr window)
        {
            try
            {
                uint pid; if (!Native.IsWindow(window) || Native.GetWindowThreadProcessId(window, out pid) == 0) return null;
                string path = Native.ProcessImagePath(pid); if (string.IsNullOrEmpty(path)) return null;
                var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
                var name = new StringBuilder(256); Native.GetClassName(window, name, name.Capacity);
                if (title.Length == 0 || name.Length == 0) return null;
                using (var process = Process.GetProcessById((int)pid))
                    return new WindowCaptureChoice { Handle = window.ToInt64(), ProcessId = (int)pid, ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks, Executable = path, ClassName = name.ToString(), Title = title.ToString() };
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }
        internal WindowTargetSnapshot Read()
        {
            lock (gate)
            {
                if (disposed) return Missing(CapturePhase.TargetClosed, "Window tracking has stopped.");
                if (string.IsNullOrEmpty(settings.WindowExecutable) || string.IsNullOrEmpty(settings.WindowClass)) return Missing(CapturePhase.WaitingForTarget, "Choose an application window in Capture settings. The desktop will not be recorded.");
                var candidates = List(settings.WindowExecutable, settings.WindowClass).ToArray();
                if (candidates.Length != 1)
                {
                    ClearScreenshotIdentity();
                    bound = null;
                    return Missing(candidates.Length == 0 && hadTarget ? CapturePhase.TargetClosed : CapturePhase.WaitingForTarget,
                        candidates.Length > 1 ? "Multiple matching windows are open. Close the extra windows before recording can resume." : "Open the selected application. Recording resumes only when one matching window is available.");
                }
                var selected = candidates[0];
                if (bound == null || destroyed || bound.Handle != selected.Handle || bound.ProcessId != selected.ProcessId || bound.ProcessStartedUtcTicks != selected.ProcessStartedUtcTicks)
                { ClearScreenshotIdentity(); bound = selected; destroyed = false; generation++; hadTarget = true; }
                bool minimized = IsIconic(new IntPtr(bound.Handle));
                return new WindowTargetSnapshot { Handle = bound.Handle, ProcessId = bound.ProcessId, ProcessStartedUtcTicks = bound.ProcessStartedUtcTicks, Generation = generation,
                    ApplicationName = Path.GetFileNameWithoutExtension(bound.Executable), Phase = minimized ? CapturePhase.TargetMinimized : CapturePhase.Starting,
                    Reason = minimized ? "Restore the selected window to resume recording. The desktop will not be recorded." : "Preparing capture of " + bound.Title + "." };
            }
        }
        // Screenshot continuity must not depend on when the owner thread receives
        // an asynchronous destroy event. Properties belong to a window lifetime;
        // a recycled HWND does not retain this private marker. This is not an
        // authentication boundary against the trusted window owner itself.
        internal WindowTargetSnapshot ReadForScreenshot()
        {
            lock (gate)
            {
                var snapshot = Read();
                if (!snapshot.Available) { ClearScreenshotIdentity(); return snapshot; }
                if (screenshotProperty != null && (screenshotWindow != snapshot.Handle || screenshotTargetGeneration != snapshot.Generation
                    || GetProp(new IntPtr(snapshot.Handle), screenshotProperty) != new IntPtr(1))) ClearScreenshotIdentity();
                if (screenshotProperty == null)
                {
                    string property = "F10Recorder.Screenshot." + Guid.NewGuid().ToString("N");
                    IntPtr window = new IntPtr(snapshot.Handle);
                    if (!SetProp(window, property, new IntPtr(1)))
                        return Missing(CapturePhase.TargetClosed, "Cannot establish screenshot window identity (Windows error " + Marshal.GetLastWin32Error() + "). Windows may deny screenshots of an elevated application.");
                    bool retained = false;
                    try
                    {
                        // Establish the marker before exposing the screenshot
                        // epoch, then validate application intent and continuity.
                        var actual = Inspect(window);
                        if (actual == null || actual.ProcessId != snapshot.ProcessId || actual.ProcessStartedUtcTicks != snapshot.ProcessStartedUtcTicks
                            || !string.Equals(actual.Executable, settings.WindowExecutable, StringComparison.OrdinalIgnoreCase) || actual.ClassName != settings.WindowClass
                            || GetProp(window, property) != new IntPtr(1))
                            return Missing(CapturePhase.TargetClosed, "The selected window changed while establishing screenshot identity. No screenshot was saved.");
                        screenshotProperty = property; screenshotWindow = snapshot.Handle; screenshotTargetGeneration = snapshot.Generation;
                        screenshotGeneration++; retained = true;
                    }
                    finally { if (!retained && GetProp(window, property) == new IntPtr(1)) RemoveProp(window, property); }
                }
                // Read returns a fresh snapshot; recording keeps its own epoch.
                snapshot.Generation = screenshotGeneration;
                return snapshot;
            }
        }
        void ClearScreenshotIdentity()
        {
            if (screenshotProperty == null) return;
            IntPtr window = new IntPtr(screenshotWindow);
            if (GetProp(window, screenshotProperty) == new IntPtr(1)) RemoveProp(window, screenshotProperty);
            screenshotProperty = null; screenshotWindow = 0; screenshotGeneration++;
        }
        WindowTargetSnapshot Missing(CapturePhase phase, string reason) { return new WindowTargetSnapshot { Phase = phase, Reason = reason, Generation = generation }; }
        public void Dispose() { lock (gate) { disposed = true; ClearScreenshotIdentity(); bound = null; } if (hook != IntPtr.Zero) UnhookWinEvent(hook); }
        delegate bool EnumWindow(IntPtr window, IntPtr parameter);
        delegate void WinEvent(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr window, string name);
    }
}
