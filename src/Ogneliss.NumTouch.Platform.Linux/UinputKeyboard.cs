using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Keys;
using static Ogneliss.NumTouch.Platform.Linux.LibC;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>
/// Keyboard Output Backend для Linux: виртуальная клавиатура uinput.
/// Ограничение: uinput передаёт коды клавиш, а не символы, поэтому <see cref="TypeText"/> поддерживает только
/// символы, которые есть на клавиатуре US-раскладки (их итоговое значение зависит от активной раскладки системы).
/// Для «=» на наклейке рекомендуется назначение «Клавиша → Equals» или «Num =».
/// </summary>
internal sealed class UinputKeyboard : IKeyboardOutput, IDisposable
{
    private const ushort KeyLeftShift = 42;
    private static readonly Dictionary<char, (ushort code, bool shift)> UsLayout = BuildUsLayout();

    private readonly UinputDevice? _device;
    private readonly Func<bool> _numLock;

    public UinputKeyboard(Func<bool> numLockReader)
    {
        _numLock = numLockReader;
        var codes = PlatformKeyCodes.Linux.Values.Concat(UsLayout.Values.Select(v => v.code)).Append(KeyLeftShift);
        _device = UinputDevice.CreateKeyboard(codes, out var err);
        Error = err;
    }

    public string? Error { get; }
    public bool IsAvailable => _device?.IsOpen == true;

    public bool KeyDown(KeyDefinition key) => Send(key, 1);
    public bool KeyUp(KeyDefinition key) => Send(key, 0);

    private bool Send(KeyDefinition key, int value)
    {
        if (_device is null || !PlatformKeyCodes.TryGetLinux(key, out var code)) return false;
        return _device.Emit(new[] { (EV_KEY, code, value) });
    }

    public bool TypeText(string text)
    {
        if (_device is null) return false;
        foreach (char ch in text)
        {
            if (!UsLayout.TryGetValue(ch, out var k)) return false;
            bool ok = k.shift
                ? _device.Emit(new[] { (EV_KEY, KeyLeftShift, 1), (EV_KEY, k.code, 1), (EV_KEY, k.code, 0), (EV_KEY, KeyLeftShift, 0) })
                : _device.Emit(new[] { (EV_KEY, k.code, 1), (EV_KEY, k.code, 0) });
            if (!ok) return false;
        }
        return true;
    }

    public bool IsNumLockOn
    {
        get { try { return _numLock(); } catch { return true; } }
    }

    private static Dictionary<char, (ushort, bool)> BuildUsLayout()
    {
        var d = new Dictionary<char, (ushort, bool)>();
        foreach (var k in KeyCatalog.All.Where(k => k.Group == KeyGroup.Letter))
        {
            d[char.ToLowerInvariant(k.Name[0])] = (k.WinScan, false);
            d[k.Name[0]] = (k.WinScan, true);
        }
        const string digits = "1234567890", shifted = "!@#$%^&*()";
        for (int i = 0; i < 10; i++) { d[digits[i]] = ((ushort)(2 + i), false); d[shifted[i]] = ((ushort)(2 + i), true); }
        void Add(char plain, char shift, ushort code) { d[plain] = (code, false); d[shift] = (code, true); }
        Add('-', '_', 12); Add('=', '+', 13); Add('[', '{', 26); Add(']', '}', 27); Add(';', ':', 39); Add('\'', '"', 40);
        Add('`', '~', 41); Add('\\', '|', 43); Add(',', '<', 51); Add('.', '>', 52); Add('/', '?', 53);
        d[' '] = (57, false);
        d['\n'] = (28, false);
        d['\t'] = (15, false);
        return d;
    }

    public void Dispose() => _device?.Dispose();
}
