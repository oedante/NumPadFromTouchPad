using System.Text;
using static Ogneliss.NumTouch.Platform.Linux.LibC;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>
/// Виртуальное устройство ввода /dev/uinput (клавиатура или мышь). Работает и под X11, и под Wayland,
/// потому что события входят в систему на уровне ядра. Нужны права на /dev/uinput (правило udev, см. packaging/linux).
/// </summary>
internal sealed unsafe class UinputDevice : IDisposable
{
    public const string KeyboardName = "Ogneliss NumTouch virtual keyboard";
    public const string MouseName = "Ogneliss NumTouch virtual pointer";

    private int _fd = -1;
    private readonly object _sync = new();

    private UinputDevice() { }

    public bool IsOpen => _fd >= 0;

    public static UinputDevice? CreateKeyboard(IEnumerable<ushort> keys, out string? error) =>
        Create(KeyboardName, fd =>
        {
            ioctl(fd, UI_SET_EVBIT, EV_KEY);
            foreach (var k in keys.Distinct()) ioctl(fd, UI_SET_KEYBIT, k);
        }, out error);

    public static UinputDevice? CreateMouse(out string? error) =>
        Create(MouseName, fd =>
        {
            ioctl(fd, UI_SET_EVBIT, EV_KEY);
            ioctl(fd, UI_SET_KEYBIT, BTN_LEFT);
            ioctl(fd, UI_SET_KEYBIT, BTN_RIGHT);
            ioctl(fd, UI_SET_KEYBIT, BTN_MIDDLE);
            ioctl(fd, UI_SET_EVBIT, EV_REL);
            ioctl(fd, UI_SET_RELBIT, REL_X);
            ioctl(fd, UI_SET_RELBIT, REL_Y);
            ioctl(fd, UI_SET_RELBIT, REL_WHEEL);
            ioctl(fd, UI_SET_RELBIT, REL_HWHEEL);
        }, out error);

    private static UinputDevice? Create(string name, Action<int> configure, out string? error)
    {
        error = null;
        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0)
        {
            int e = Errno;
            error = e == EACCES
                ? L.T("No access to /dev/uinput. Install the udev rule from the Linux package (install.sh) and log in again.")
                : e == ENOENT ? L.T("No /dev/uinput: load the kernel module (sudo modprobe uinput).") : $"/dev/uinput: errno {e}";
            return null;
        }
        configure(fd);

        // struct uinput_user_dev: name[80], input_id (8), ff_effects_max (4), absmax/absmin/absfuzz/absflat [64] × 4 байта
        var dev = new byte[80 + 8 + 4 + 64 * 4 * 4];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Array.Copy(nameBytes, dev, Math.Min(nameBytes.Length, 79));
        BitConverter.TryWriteBytes(dev.AsSpan(80), (ushort)0x06);     // BUS_VIRTUAL
        BitConverter.TryWriteBytes(dev.AsSpan(82), (ushort)0x4E50);   // vendor
        BitConverter.TryWriteBytes(dev.AsSpan(84), (ushort)0x5450);   // product
        BitConverter.TryWriteBytes(dev.AsSpan(86), (ushort)1);        // version
        fixed (byte* p = dev)
        {
            if (write(fd, p, dev.Length) != dev.Length || ioctl(fd, UI_DEV_CREATE, 0) < 0)
            {
                error = L.F("Could not create the virtual device (errno {0}).", Errno);
                close(fd);
                return null;
            }
        }
        return new UinputDevice { _fd = fd };
    }

    /// <summary>Отправить события и SYN_REPORT одной записью.</summary>
    public bool Emit(ReadOnlySpan<(ushort type, ushort code, int value)> events)
    {
        if (_fd < 0) return false;
        Span<InputEvent> buf = stackalloc InputEvent[events.Length + 1];
        for (int i = 0; i < events.Length; i++)
            buf[i] = new InputEvent { Type = events[i].type, Code = events[i].code, Value = events[i].value };
        buf[events.Length] = new InputEvent { Type = EV_SYN, Code = SYN_REPORT, Value = 0 };
        lock (_sync)
        {
            fixed (InputEvent* p = buf)
            {
                nint size = buf.Length * sizeof(InputEvent);
                return write(_fd, p, size) == size;
            }
        }
    }

    public void Dispose()
    {
        if (_fd < 0) return;
        ioctl(_fd, UI_DEV_DESTROY, 0);
        close(_fd);
        _fd = -1;
    }
}
