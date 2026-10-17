using System.Runtime.InteropServices;

namespace Ogneliss.NumTouch.Platform.MacOS;

/// <summary>Вызовы CoreFoundation, CoreGraphics, ApplicationServices и Carbon (HIToolbox).</summary>
internal static unsafe class MacNative
{
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    public const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    public const string MultitouchSupport = "/System/Library/PrivateFrameworks/MultitouchSupport.framework/MultitouchSupport";

    // ------------------------------------------------------------------ CoreFoundation
    [DllImport(CoreFoundation)] public static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] public static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] public static extern void CFRelease(nint obj);
    [DllImport(CoreFoundation)] public static extern nint CFRunLoopGetMain();
    [DllImport(CoreFoundation)] public static extern void CFRunLoopAddSource(nint rl, nint source, nint mode);
    [DllImport(CoreFoundation)] public static extern void CFRunLoopRemoveSource(nint rl, nint source, nint mode);
    [DllImport(CoreFoundation)] public static extern nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);
    [DllImport(CoreFoundation)] public static extern void CFMachPortInvalidate(nint port);

    /// <summary>Глобальная константа kCFRunLoopCommonModes (CFStringRef).</summary>
    public static nint CommonModes
    {
        get
        {
            var lib = NativeLibrary.Load(CoreFoundation);
            return *(nint*)NativeLibrary.GetExport(lib, "kCFRunLoopCommonModes");
        }
    }

    // ------------------------------------------------------------------ CoreGraphics (события)
    public const int kCGHIDEventTap = 0, kCGSessionEventTap = 1, kCGHeadInsertEventTap = 0, kCGEventTapOptionDefault = 0;
    public const ulong FlagShift = 0x20000, FlagControl = 0x40000, FlagAlternate = 0x80000, FlagCommand = 0x100000, FlagNumericPad = 0x200000;
    public const uint kCGEventTapDisabledByTimeout = 0xFFFFFFFE, kCGEventTapDisabledByUserInput = 0xFFFFFFFF;
    public const int kCGEventSourceUserData = 42; // поле CGEventField, куда мы пишем подпись своих событий

    [DllImport(CoreGraphics)] public static extern nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.U1)] bool keyDown);
    [DllImport(CoreGraphics)] public static extern void CGEventSetFlags(nint ev, ulong flags);
    [DllImport(CoreGraphics)] public static extern void CGEventPost(int tap, nint ev);
    [DllImport(CoreGraphics)] public static extern void CGEventKeyboardSetUnicodeString(nint ev, nuint length, char* chars);
    [DllImport(CoreGraphics)] public static extern long CGEventGetIntegerValueField(nint ev, int field);
    [DllImport(CoreGraphics)] public static extern void CGEventSetIntegerValueField(nint ev, int field, long value);
    [DllImport(CoreGraphics)]
    public static extern nint CGEventTapCreate(int tap, int place, int options, ulong eventsOfInterest,
        delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint> callback, nint userInfo);
    [DllImport(CoreGraphics)] public static extern void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.U1)] bool enable);

    // ------------------------------------------------------------------ ApplicationServices
    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXIsProcessTrusted();

    // ------------------------------------------------------------------ Carbon (глобальные сочетания)
    public const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
    public const uint kEventHotKeyPressed = 5;
    public const uint cmdKey = 0x100, shiftKey = 0x200, optionKey = 0x800, controlKey = 0x1000;

    [StructLayout(LayoutKind.Sequential)] public struct EventTypeSpec { public uint EventClass; public uint EventKind; }
    [StructLayout(LayoutKind.Sequential)] public struct EventHotKeyID { public uint Signature; public uint Id; }

    [DllImport(Carbon)] public static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)]
    public static extern int InstallEventHandler(nint target, delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        nuint numTypes, EventTypeSpec* list, nint userData, nint* outRef);
    [DllImport(Carbon)] public static extern int RemoveEventHandler(nint handlerRef);
    [DllImport(Carbon)] public static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, nint target, uint options, nint* outRef);
    [DllImport(Carbon)] public static extern int UnregisterEventHotKey(nint hotKeyRef);
}
