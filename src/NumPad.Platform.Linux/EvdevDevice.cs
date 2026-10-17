using System.Text;
using static NumPad.Platform.Linux.LibC;

namespace NumPad.Platform.Linux;

/// <summary>Открытое устройство /dev/input/eventN: возможности, имя, диапазоны осей, захват, чтение событий.</summary>
internal sealed unsafe class EvdevDevice : IDisposable
{
    private int _fd;

    private EvdevDevice(string path, int fd)
    {
        Path = path;
        _fd = fd;
    }

    public string Path { get; }
    public int Fd => _fd;
    public string Name { get; private set; } = "";
    public InputId Id { get; private set; }

    /// <summary>Открыть устройство. errno: EACCES — нет прав (группа input).</summary>
    public static EvdevDevice? Open(string path, out int errno, bool nonBlocking = true)
    {
        int fd = open(path, O_RDONLY | O_CLOEXEC | (nonBlocking ? O_NONBLOCK : 0));
        errno = fd < 0 ? Errno : 0;
        if (fd < 0) return null;
        var d = new EvdevDevice(path, fd);
        d.Name = d.ReadName();
        d.Id = d.ReadId();
        return d;
    }

    private string ReadName()
    {
        var buf = stackalloc byte[256];
        int n = ioctlPtr(_fd, EVIOCGNAME(256), buf);
        if (n <= 0) return "";
        return Encoding.UTF8.GetString(buf, n).TrimEnd('\0');
    }

    private InputId ReadId()
    {
        InputId id;
        return ioctlPtr(_fd, EVIOCGID, &id) < 0 ? default : id;
    }

    public byte[] GetBits(int ev, int max)
    {
        var bits = new byte[max / 8 + 1];
        fixed (byte* p = bits) ioctlPtr(_fd, EVIOCGBIT(ev, bits.Length), p);
        return bits;
    }

    public byte[] GetProps()
    {
        var bits = new byte[8];
        fixed (byte* p = bits) ioctlPtr(_fd, EVIOCGPROP(bits.Length), p);
        return bits;
    }

    public bool TryGetAbs(int code, out InputAbsInfo info)
    {
        InputAbsInfo a;
        bool ok = ioctlPtr(_fd, EVIOCGABS(code), &a) >= 0;
        info = ok ? a : default;
        return ok;
    }

    public byte[] GetLeds()
    {
        var bits = new byte[(LED_MAX + 8) / 8];
        fixed (byte* p = bits) ioctlPtr(_fd, EVIOCGLED(bits.Length), p);
        return bits;
    }

    /// <summary>Эксклюзивный захват: события перестают доходить до libinput/X/Wayland (курсор, тапы, прокрутка).</summary>
    public bool Grab(bool grab) => ioctl(_fd, EVIOCGRAB, grab ? 1 : 0) == 0;

    public bool IsTouchpad
    {
        get
        {
            var ev = GetBits(0, 0x1f);
            if (!TestBit(ev, EV_ABS) || !TestBit(ev, EV_KEY)) return false;
            var abs = GetBits(EV_ABS, ABS_MAX);
            var keys = GetBits(EV_KEY, KEY_MAX);
            var props = GetProps();
            bool mt = TestBit(abs, ABS_MT_POSITION_X) && TestBit(abs, ABS_MT_POSITION_Y);
            bool finger = TestBit(keys, BTN_TOOL_FINGER);
            bool direct = TestBit(props, INPUT_PROP_DIRECT); // сенсорный экран, а не тачпад
            return mt && finger && !direct;
        }
    }

    public bool IsKeyboard
    {
        get
        {
            var ev = GetBits(0, 0x1f);
            if (!TestBit(ev, EV_KEY)) return false;
            var keys = GetBits(EV_KEY, KEY_MAX);
            // буквы A, Z и пробел — отличаем клавиатуру от кнопок питания и мультимедиа-пультов
            return TestBit(keys, 30) && TestBit(keys, 44) && TestBit(keys, 57);
        }
    }

    public bool HasMtSlots => TestBit(GetBits(EV_ABS, ABS_MAX), ABS_MT_SLOT);

    /// <summary>Ожидание данных (poll) с таймаутом. Возвращает false при таймауте; бросает IOException, если устройство пропало.</summary>
    public bool WaitReadable(int timeoutMs)
    {
        var pfd = new PollFd { fd = _fd, events = POLLIN };
        int r = poll(&pfd, (nuint)1, timeoutMs);
        if (r < 0)
        {
            int e = Errno;
            if (e == EINTR) return false;
            throw new IOException($"poll: errno {e}");
        }
        if (r == 0) return false;
        if ((pfd.revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) throw new IOException("Устройство отключено.");
        return true;
    }

    /// <summary>Прочитать доступные события в буфер. Возвращает число событий; 0 — нет данных.</summary>
    public int Read(Span<InputEvent> buffer)
    {
        fixed (InputEvent* p = buffer)
        {
            nint n = read(_fd, p, buffer.Length * sizeof(InputEvent));
            if (n < 0)
            {
                int e = Errno;
                if (e is EAGAIN or EINTR) return 0;
                throw new IOException(e == ENODEV ? "Устройство отключено." : $"read: errno {e}");
            }
            return (int)(n / sizeof(InputEvent));
        }
    }

    public static IEnumerable<string> EnumerateEventNodes() =>
        Directory.Exists("/dev/input")
            ? Directory.EnumerateFiles("/dev/input", "event*").OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

    public void Dispose()
    {
        if (_fd >= 0) { close(_fd); _fd = -1; }
    }
}
