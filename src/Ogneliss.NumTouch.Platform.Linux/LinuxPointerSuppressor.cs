using Ogneliss.NumTouch.Core.Config;

namespace Ogneliss.NumTouch.Platform.Linux;

/// <summary>
/// Подавление указателя в Linux: эксклюзивный захват тачпада (EVIOCGRAB) на время режима NumPad.
/// В отличие от Windows блокируется только тачпад — внешняя мышь работает всегда.
/// Режимы WhileTouching и AllWhileNumPad здесь совпадают: захват действует весь режим, а касания «зоны трекпада»
/// передаются в курсор через виртуальную мышь.
/// </summary>
internal sealed class LinuxPointerSuppressor : IPointerSuppressor
{
    private readonly LinuxTouchpad _touchpad;
    private bool _active;

    public LinuxPointerSuppressor(LinuxTouchpad touchpad) => _touchpad = touchpad;

    public string Mechanism => L.T("Touchpad grab (EVIOCGRAB) + uinput virtual mouse for the trackpad zone");
    public bool IsActive => _active;
    public long BlockedEvents => _touchpad.BlockedFrames;

    public bool Activate(SuppressionConfig config, out string? error)
    {
        error = null;
        if (config.Mode == PointerSuppression.None) { Deactivate(); return true; }
        bool ok = _touchpad.SetGrab(true, config.PointerPassThrough, out error);
        _active = ok || _touchpad.IsGrabbed;
        return ok;
    }

    public void Deactivate()
    {
        _touchpad.SetGrab(false, null, out _);
        _active = false;
    }

    public void ReportTouchState(bool suppressingContactActive, long lastActivityMs) { /* захват не зависит от касаний */ }

    public void Dispose() => Deactivate();
}
