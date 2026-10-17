using System.Runtime.InteropServices;
using System.Text.Json;
using NumPad.Core.Config;
using NumPad.Core.Diagnostics;
using static NumPad.Platform.Windows.NativeMethods;

namespace NumPad.Platform.Windows;

/// <summary>
/// Отключение тапов и жестов тачпада на время режима NumPad (SPI_SETTOUCHPADPARAMETERS, Windows 11 24H2+).
/// Исходные значения записываются в pending-restore.json ДО изменения и восстанавливаются при выходе из режима,
/// при завершении и при следующем запуске после сбоя. На более старых Windows — тихо недоступно.
/// </summary>
internal sealed class TouchpadGestureGuard : IGestureGuard
{
    // userFlags: bit2 tap, bit3 tapAndDrag, bit4 twoFingerTap, bit7 pan, bit8 zoom
    private const uint GestureMask = (1u << 2) | (1u << 3) | (1u << 4) | (1u << 7) | (1u << 8);

    private readonly SessionGuard _session;
    private readonly ILog _log;
    private bool _applied;

    public sealed record Saved(uint UserFlags);

    public TouchpadGestureGuard(SessionGuard session, ILog log)
    {
        _session = session;
        _log = log;
    }

    public bool IsApplied => _applied;

    internal static bool TryGet(out TOUCHPAD_PARAMETERS p)
    {
        p = new TOUCHPAD_PARAMETERS { versionNumber = TOUCHPAD_PARAMETERS_VERSION_1 };
        try
        {
            return SystemParametersInfo(SPI_GETTOUCHPADPARAMETERS, (uint)Marshal.SizeOf<TOUCHPAD_PARAMETERS>(), ref p, 0);
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>Доступно ли управление параметрами тачпада (Windows 11 24H2+ и PTP).</summary>
    public static bool SupportedOnThisSystem => TryGet(out var p) && (p.systemFlags & 1) != 0;
    public bool IsSupported => SupportedOnThisSystem;

    /// <summary>Есть ли тачпад, который Windows считает «устаревшим» (не PTP) — для понятного сообщения.</summary>
    public static bool LegacyTouchpadPresent => TryGet(out var p) && (p.systemFlags & 2) != 0;

    public void Apply()
    {
        if (_applied) return;
        if (!TryGet(out var p) || (p.systemFlags & 1) == 0) return;
        try
        {
            _session.SavePendingRestore(JsonSerializer.Serialize(new Saved(p.userFlags)));
            p.userFlags &= ~GestureMask;
            p.versionNumber = TOUCHPAD_PARAMETERS_VERSION_1;
            if (SystemParametersInfo(SPI_SETTOUCHPADPARAMETERS, (uint)Marshal.SizeOf<TOUCHPAD_PARAMETERS>(), ref p, SPIF_SENDCHANGE))
            {
                _applied = true;
                _log.Info("Тапы и жесты тачпада отключены на время режима NumPad.");
            }
            else
            {
                _session.ClearPendingRestore();
                _log.Warn($"SPI_SETTOUCHPADPARAMETERS: ошибка {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex) { _log.Warn("Не удалось отключить жесты тачпада", ex); }
    }

    /// <summary>Восстановить исходные значения (если были сохранены). Безопасно вызывать многократно.</summary>
    public void Restore()
    {
        var json = _session.LoadPendingRestore();
        if (json is null) { _applied = false; return; }
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(json);
            if (saved is not null && TryGet(out var p))
            {
                p.userFlags = (p.userFlags & ~GestureMask) | (saved.UserFlags & GestureMask);
                p.versionNumber = TOUCHPAD_PARAMETERS_VERSION_1;
                if (SystemParametersInfo(SPI_SETTOUCHPADPARAMETERS, (uint)Marshal.SizeOf<TOUCHPAD_PARAMETERS>(), ref p, SPIF_SENDCHANGE))
                {
                    _session.ClearPendingRestore();
                    _log.Info("Параметры жестов тачпада восстановлены.");
                }
                else _log.Warn($"Восстановление параметров тачпада: ошибка {Marshal.GetLastWin32Error()}");
            }
            else _session.ClearPendingRestore();
        }
        catch (Exception ex) { _log.Warn("Не удалось восстановить параметры тачпада", ex); }
        _applied = false;
    }
}
