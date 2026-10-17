using System.Runtime.InteropServices;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>Минимальный набор вызовов libc и констант evdev/uinput (linux/input.h, linux/uinput.h). Только 64-битные системы.</summary>
internal static unsafe class LibC
{
    private const string Lib = "libc.so.6";

    public const int O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    public const int EACCES = 13, ENODEV = 19, EINTR = 4, EAGAIN = 11, ENOENT = 2;
    public const short POLLIN = 0x001, POLLERR = 0x008, POLLHUP = 0x010, POLLNVAL = 0x020;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd { public int fd; public short events; public short revents; }

    [DllImport(Lib, SetLastError = true)] public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(Lib, SetLastError = true)] public static extern int close(int fd);
    [DllImport(Lib, SetLastError = true)] public static extern nint read(int fd, void* buf, nint count);
    [DllImport(Lib, SetLastError = true)] public static extern nint write(int fd, void* buf, nint count);
    [DllImport(Lib, SetLastError = true)] public static extern int ioctl(int fd, nuint request, int arg);
    [DllImport(Lib, SetLastError = true, EntryPoint = "ioctl")] public static extern int ioctlPtr(int fd, nuint request, void* arg);
    [DllImport(Lib, SetLastError = true)] public static extern int poll(PollFd* fds, nuint nfds, int timeout);

    // ------------------------------------------------------------------ evdev

    public const ushort EV_SYN = 0x00, EV_KEY = 0x01, EV_REL = 0x02, EV_ABS = 0x03, EV_LED = 0x11;
    public const ushort SYN_REPORT = 0, SYN_DROPPED = 3;
    public const ushort ABS_X = 0x00, ABS_Y = 0x01, ABS_MT_SLOT = 0x2f, ABS_MT_POSITION_X = 0x35, ABS_MT_POSITION_Y = 0x36,
                        ABS_MT_TOOL_TYPE = 0x37, ABS_MT_TRACKING_ID = 0x39;
    public const ushort BTN_LEFT = 0x110, BTN_RIGHT = 0x111, BTN_MIDDLE = 0x112, BTN_TOOL_FINGER = 0x145, BTN_TOUCH = 0x14a;
    public const ushort REL_X = 0x00, REL_Y = 0x01, REL_HWHEEL = 0x06, REL_WHEEL = 0x08;
    public const int MT_TOOL_PALM = 2;
    public const int INPUT_PROP_POINTER = 0x00, INPUT_PROP_DIRECT = 0x01, INPUT_PROP_BUTTONPAD = 0x02;
    public const int LED_NUML = 0;
    public const int KEY_MAX = 0x2ff, ABS_MAX = 0x3f, LED_MAX = 0x0f, REL_MAX = 0x0f;

    /// <summary>struct input_event на 64-битной системе: timeval (16) + type (2) + code (2) + value (4) = 24 байта.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InputEvent
    {
        public long Sec;
        public long Usec;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputAbsInfo { public int Value, Minimum, Maximum, Fuzz, Flat, Resolution; }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputId { public ushort BusType, Vendor, Product, Version; }

    private const uint IOC_READ = 0x80000000, IOC_WRITE = 0x40000000;
    private static nuint Ioc(uint dir, uint type, uint nr, uint size) => (nuint)(dir | (size << 16) | (type << 8) | nr);

    public static nuint EVIOCGNAME(int len) => Ioc(IOC_READ, 'E', 0x06, (uint)len);
    public static nuint EVIOCGPROP(int len) => Ioc(IOC_READ, 'E', 0x09, (uint)len);
    public static nuint EVIOCGLED(int len) => Ioc(IOC_READ, 'E', 0x19, (uint)len);
    public static nuint EVIOCGBIT(int ev, int len) => Ioc(IOC_READ, 'E', (uint)(0x20 + ev), (uint)len);
    public static nuint EVIOCGABS(int abs) => Ioc(IOC_READ, 'E', (uint)(0x40 + abs), (uint)sizeof(InputAbsInfo));
    public static readonly nuint EVIOCGID = Ioc(IOC_READ, 'E', 0x02, (uint)sizeof(InputId));
    public static readonly nuint EVIOCGRAB = Ioc(IOC_WRITE, 'E', 0x90, sizeof(int));

    // ------------------------------------------------------------------ uinput

    public static readonly nuint UI_SET_EVBIT = Ioc(IOC_WRITE, 'U', 100, sizeof(int));
    public static readonly nuint UI_SET_KEYBIT = Ioc(IOC_WRITE, 'U', 101, sizeof(int));
    public static readonly nuint UI_SET_RELBIT = Ioc(IOC_WRITE, 'U', 102, sizeof(int));
    public static readonly nuint UI_DEV_CREATE = Ioc(0, 'U', 1, 0);
    public static readonly nuint UI_DEV_DESTROY = Ioc(0, 'U', 2, 0);

    public static int Errno => Marshal.GetLastPInvokeError();

    public static bool TestBit(ReadOnlySpan<byte> bits, int bit) => bit / 8 < bits.Length && (bits[bit / 8] & (1 << (bit % 8))) != 0;
}
