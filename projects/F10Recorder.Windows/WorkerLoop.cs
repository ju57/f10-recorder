using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace F10Recorder;

// A worker-owned STA message pump receives hotkeys and session/display/power events.
// It also resumes asynchronous settings/screenshot work on the same owner thread.
internal sealed class WorkerLoop : SynchronizationContext
{
    const uint Dispatch = 0x8002;
    readonly ConcurrentQueue<Action> actions = new();
    readonly uint thread;
    bool stopped;
    internal Action Tick;
    internal WorkerLoop()
    {
        thread = GetCurrentThreadId(); PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        SetSynchronizationContext(this);
    }
    public override void Post(SendOrPostCallback callback, object state) => Post(() => callback(state));
    internal void Post(Action action)
    {
        if (stopped) return;
        actions.Enqueue(action);
        if (!PostThreadMessage(thread, Dispatch, UIntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
    }
    internal Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () => { try { completion.TrySetResult(await action()); } catch (Exception ex) { completion.TrySetException(ex); } });
        return completion.Task;
    }
    internal void Stop() => Post(() => PostQuitMessage(0));
    internal void Run()
    {
        UIntPtr timer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 250, IntPtr.Zero);
        if (timer == UIntPtr.Zero) throw new Win32Exception();
        try
        {
            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) != 0)
            {
                if (result < 0) throw new Win32Exception();
                if (message.Window == IntPtr.Zero && message.Id == Dispatch) { while (actions.TryDequeue(out var action)) action(); }
                else if (message.Window == IntPtr.Zero && message.Id == 0x113) Tick?.Invoke();
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }
        finally { stopped = true; KillTimer(IntPtr.Zero, timer); SetSynchronizationContext(null); }
    }
    [StructLayout(LayoutKind.Sequential)] struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostThreadMessage(uint id, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr window, UIntPtr id);
}
