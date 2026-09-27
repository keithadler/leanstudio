using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace LeanStudio.App.Services;

/// <summary>
/// Answers the Apple Event that asks Lean Studio to quit (AppleScript's <c>quit</c>, the Dock's Quit, logging out)
/// on macOS. Avalonia accepts a quit by telling macOS "cancel" and then ending the app itself, so a script that quit
/// Lean Studio was told "User canceled" (-128) although the app quit. This handler quits the same way, through the
/// app's lifetime, and answers with success, or with "User canceled" when the quit waits on unsaved changes.
/// </summary>
internal static class MacQuitEvent
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const uint CoreEventClass = 0x61657674; // 'aevt'
    private const uint QuitApplication = 0x71756974; // 'quit'
    private const uint ErrorNumber = 0x6572726e; // 'errn'
    private const int UserCanceled = -128;

    private delegate void QuitHandler(IntPtr self, IntPtr command, IntPtr appleEvent, IntPtr reply);

    // The Objective-C runtime holds only the handler's address, so the delegate is kept alive here.
    private static QuitHandler? _handler;
    private static Func<bool>? _quit;

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr AllocateClass(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr extraBytes);

    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClass(IntPtr cls);

    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendInt(IntPtr receiver, IntPtr selector, int arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendDescriptor(IntPtr receiver, IntPtr selector, IntPtr descriptor, uint keyword);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendHandler(IntPtr receiver, IntPtr selector, IntPtr target, IntPtr action, uint eventClass, uint eventId);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(IntPtr receiver, IntPtr selector);

    /// <summary>
    /// Handle the quit event from now on. <paramref name="quit"/> runs on the UI thread and returns whether the app
    /// is quitting (false while it asks about unsaved changes). Does nothing off macOS, or if already installed.
    /// </summary>
    /// <remarks>
    /// AppKit installs its own handler as the app finishes launching, which can be after the window opens, and a
    /// handler installed before that is replaced. So this waits (checking every tenth of a second, for up to ten
    /// seconds) until macOS says the app has finished launching.
    /// </remarks>
    public static void Install(Func<bool> quit) => Install(quit, attempts: 100);

    private static void Install(Func<bool> quit, int attempts)
    {
        if (!OperatingSystem.IsMacOS() || _handler is not null)
        {
            return;
        }
        try
        {
            IntPtr current = Send(GetClass("NSRunningApplication"), Selector("currentApplication"));
            if (!SendBool(current, Selector("isFinishedLaunching")))
            {
                if (attempts > 0)
                {
                    DispatcherTimer.RunOnce(() => Install(quit, attempts - 1), TimeSpan.FromMilliseconds(100));
                }
                return;
            }
            _quit = quit;
            _handler = Handle;
            IntPtr action = Selector("handleQuit:withReplyEvent:");
            IntPtr cls = AllocateClass(GetClass("NSObject"), "LeanStudioQuitHandler", IntPtr.Zero);
            AddMethod(cls, action, Marshal.GetFunctionPointerForDelegate(_handler), "v@:@@");
            RegisterClass(cls);
            IntPtr target = Send(Send(cls, Selector("alloc")), Selector("init"));
            IntPtr manager = Send(GetClass("NSAppleEventManager"), Selector("sharedAppleEventManager"));
            SendHandler(manager, Selector("setEventHandler:andSelector:forEventClass:andEventID:"), target, action, CoreEventClass, QuitApplication);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Quitting still works without it; scripts are just told "User canceled".
        }
    }

    private static void Handle(IntPtr self, IntPtr command, IntPtr appleEvent, IntPtr reply)
    {
        // An exception must not leave this method: it would cross into Objective-C and end the app.
        bool quitting;
        try
        {
            quitting = _quit?.Invoke() ?? true;
        }
        catch (Exception e)
        {
            App.Record("quit event", e);
            quitting = false;
        }
        if (!quitting && reply != IntPtr.Zero)
        {
            IntPtr canceled = SendInt(GetClass("NSAppleEventDescriptor"), Selector("descriptorWithInt32:"), UserCanceled);
            SendDescriptor(reply, Selector("setParamDescriptor:forKeyword:"), canceled, ErrorNumber);
        }
    }
}
