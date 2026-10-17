using Ogneliss.NumTouch.Core.Engine;
using Ogneliss.NumTouch.Core.Geometry;
using static Ogneliss.NumTouch.Platform.Linux.LibC;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>
/// Пока тачпад захвачен (режим NumPad), система не видит касаний. Касания, начавшиеся в «зоне трекпада»
/// раскладки (NUM10), этот класс превращает в движение курсора, тап — в щелчок, а физическое нажатие
/// тачпада — в кнопку, через виртуальную мышь uinput. Касания зон клавиш никуда не передаются.
/// Ускорение простое и не повторяет libinput — достаточно для зоны трекпада, но не для основной работы.
/// </summary>
internal sealed class PointerForwarder : IDisposable
{
    private const double BasePxPerMm = 5.0;
    private const long TapMaxMs = 180;
    private const double TapMaxMoveMm = 1.5;

    private sealed class Track
    {
        public bool Pointer;
        public double X, Y, StartX, StartY;
        public long StartMs;
        public double MovedMm;
    }

    private readonly Func<UinputDevice?> _factory;
    private readonly Dictionary<int, Track> _tracks = new();
    private UinputDevice? _mouse;
    private bool _mouseFailed;
    private double _accX, _accY;
    private readonly object _sync = new();

    public PointerForwarder(Func<UinputDevice?> factory) => _factory = factory;

    public void Reset()
    {
        lock (_sync) { _tracks.Clear(); _accX = _accY = 0; }
    }

    /// <summary>Обработать кадр захваченного тачпада. Возвращает true, если в кадре было «курсорное» касание.</summary>
    public bool OnFrame(TouchFrame frame, MultitouchDecoder dec, Func<PointD, bool>? passThrough)
    {
        lock (_sync)
        {
            double wMm = dec.X.SizeMm ?? 100, hMm = dec.Y.SizeMm ?? 65;
            bool anyPointer = false;
            int? active = null;
            foreach (var c in frame.Contacts)
            {
                if (!_tracks.TryGetValue(c.Id, out var t))
                {
                    if (!c.Tip) continue;
                    bool pointer = passThrough is not null && SafeInvoke(passThrough, c.Position);
                    t = new Track { Pointer = pointer, X = c.X, Y = c.Y, StartX = c.X, StartY = c.Y, StartMs = frame.TimestampMs };
                    _tracks[c.Id] = t;
                }
                if (t.Pointer) anyPointer = true;

                if (!c.Tip)
                {
                    _tracks.Remove(c.Id);
                    if (t.Pointer && frame.TimestampMs - t.StartMs <= TapMaxMs && t.MovedMm <= TapMaxMoveMm)
                        Click(BTN_LEFT);
                    continue;
                }

                if (t.Pointer && (active is null || c.Id < active)) active = c.Id;
            }

            if (active is int id && _tracks.TryGetValue(id, out var a))
            {
                var c = frame.Contacts.First(x => x.Id == id);
                double dxMm = (c.X - a.X) * wMm, dyMm = (c.Y - a.Y) * hMm;
                a.X = c.X; a.Y = c.Y;
                double dist = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);
                a.MovedMm += dist;
                double gain = BasePxPerMm * (1 + Math.Clamp(dist / 1.5, 0, 2.5)); // простое ускорение
                _accX += dxMm * gain;
                _accY += dyMm * gain;
                int rx = (int)_accX, ry = (int)_accY;
                _accX -= rx; _accY -= ry;
                if (rx != 0 || ry != 0) Mouse()?.Emit(new[] { (EV_REL, REL_X, rx), (EV_REL, REL_Y, ry) });
            }
            // Все касания вне кадра (полный кадр) — забыть.
            var ids = frame.Contacts.Select(x => x.Id).ToHashSet();
            foreach (var gone in _tracks.Keys.Where(k => !ids.Contains(k)).ToList()) _tracks.Remove(gone);
            return anyPointer;
        }
    }

    /// <summary>Физическое нажатие тачпада: передаётся, только если на тачпаде лишь «курсорные» касания.</summary>
    public void OnButton(ushort code, int value)
    {
        lock (_sync)
        {
            bool onlyPointer = _tracks.Count > 0 && _tracks.Values.All(t => t.Pointer);
            if (onlyPointer || value == 0) Mouse()?.Emit(new[] { (EV_KEY, code, value) });
        }
    }

    private void Click(ushort button) =>
        Mouse()?.Emit(new[] { (EV_KEY, button, 1), (EV_KEY, button, 0) });

    private UinputDevice? Mouse()
    {
        if (_mouse is not null || _mouseFailed) return _mouse;
        _mouse = _factory();
        _mouseFailed = _mouse is null;
        return _mouse;
    }

    private static bool SafeInvoke(Func<PointD, bool> f, PointD p)
    {
        try { return f(p); } catch { return false; }
    }

    public void Dispose()
    {
        lock (_sync) { _mouse?.Dispose(); _mouse = null; }
    }
}
