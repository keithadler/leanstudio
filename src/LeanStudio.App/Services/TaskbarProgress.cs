using System.Runtime.InteropServices;

namespace LeanStudio.App.Services;

/// <summary>
/// A long task's progress on the app's icon, so it can be followed from another app: the percentage as a badge on
/// the Dock icon on macOS, and the progress bar in the taskbar button on Windows. When a long task ends while Lean
/// Studio is in the background, the Dock icon bounces once, or the taskbar button flashes. (Linux desktops have no
/// common way to do this; there the status bar and the notice have to do.)
/// </summary>
public sealed class TaskbarProgress
{
    private readonly Func<IntPtr> _windowHandle;
    private ITaskbarList3? _taskbar;

    /// <summary>Show progress for the window whose native handle <paramref name="windowHandle"/> gives (needed on Windows).</summary>
    /// <param name="windowHandle">The window's HWND on Windows; unused elsewhere.</param>
    public TaskbarProgress(Func<IntPtr> windowHandle) => _windowHandle = windowHandle;

    /// <summary>Whether to touch the real Dock or taskbar (off for the headless snapshot, which only checks <see cref="Shown"/>).</summary>
    public bool UseNative { get; set; } = true;

    /// <summary>What the icon shows now: <c>42%</c>, or empty when nothing is running.</summary>
    public string Shown { get; private set; } = "";

    /// <summary>Show <paramref name="fraction"/> (0 to 1) on the icon.</summary>
    public void Set(double fraction)
    {
        int percent = (int)Math.Floor(Math.Clamp(fraction, 0, 1) * 100);
        string label = percent + "%";
        if (label == Shown)
        {
            return;
        }
        Shown = label;
        Native(() =>
        {
            if (OperatingSystem.IsMacOS())
            {
                MacDock.SetBadge(label);
            }
            else if (OperatingSystem.IsWindows())
            {
                Taskbar()?.SetProgressState(_windowHandle(), TaskbarState.Normal);
                Taskbar()?.SetProgressValue(_windowHandle(), (ulong)percent, 100);
            }
        });
    }

    /// <summary>The task ended: take the progress off the icon.</summary>
    public void Clear()
    {
        if (Shown.Length == 0)
        {
            return;
        }
        Shown = "";
        Native(() =>
        {
            if (OperatingSystem.IsMacOS())
            {
                MacDock.SetBadge(null);
            }
            else if (OperatingSystem.IsWindows())
            {
                Taskbar()?.SetProgressState(_windowHandle(), TaskbarState.NoProgress);
            }
        });
    }

    /// <summary>A long task ended: bounce the Dock icon once, or flash the taskbar button, if Lean Studio is in the background.</summary>
    public void RequestAttention()
    {
        AttentionRequests++;
        Native(() =>
        {
            if (OperatingSystem.IsMacOS())
            {
                MacDock.RequestAttention();
            }
            else if (OperatingSystem.IsWindows())
            {
                // FLASHW_ALL | FLASHW_TIMERNOFG: flash until the window comes to the front (nothing if it is there).
                var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = _windowHandle(), Flags = 3 | 12, Count = 3 };
                FlashWindowEx(ref info);
            }
        });
    }

    /// <summary>How many times attention was asked for (for the snapshot).</summary>
    public int AttentionRequests { get; private set; }

    private void Native(Action action)
    {
        if (!UseNative)
        {
            return;
        }
        try
        {
            action();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidCastException)
        {
            UseNative = false; // not worth failing a build over
        }
    }

    private ITaskbarList3? Taskbar()
    {
        if (_taskbar is null && OperatingSystem.IsWindows())
        {
            _taskbar = (ITaskbarList3)new TaskbarInstance();
            _taskbar.HrInit();
        }
        return _taskbar;
    }

    /// <summary>The Dock through the Objective-C runtime: <c>NSApp.dockTile.badgeLabel</c> and <c>requestUserAttention:</c>.</summary>
    public static class MacDock
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern IntPtr Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendLong(IntPtr receiver, IntPtr selector, long arg);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendBool(IntPtr receiver, IntPtr selector);

        private static IntPtr App => Send(GetClass("NSApplication"), Selector("sharedApplication"));

        private static IntPtr DockTile => Send(App, Selector("dockTile"));

        /// <summary>Put <paramref name="label"/> on the Dock icon, or take the badge off with null.</summary>
        public static void SetBadge(string? label)
        {
            IntPtr text = label is null ? IntPtr.Zero : SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), label);
            Send(DockTile, Selector("setBadgeLabel:"), text);
            Send(DockTile, Selector("display"));
        }

        /// <summary>The Dock icon's badge now, or null.</summary>
        public static string? Badge()
        {
            IntPtr text = Send(DockTile, Selector("badgeLabel"));
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(text, Selector("UTF8String")));
        }

        /// <summary>Bounce the Dock icon once, if Lean Studio isn't the active app.</summary>
        public static void RequestAttention()
        {
            if (!SendBool(App, Selector("isActive")))
            {
                SendLong(App, Selector("requestUserAttention:"), 10); // NSInformationalRequest
            }
        }
    }

    private enum TaskbarState
    {
        NoProgress = 0,
        Normal = 2,
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3 (only what is used, in order)
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TaskbarState state);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}
