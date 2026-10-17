using Ogneliss.NumTouch.Core.Geometry;

namespace Ogneliss.NumTouch.Core.Engine;

/// <summary>
/// Физическое касание из одного кадра тачпада. Координаты нормализованы к 0..1 по логическому
/// диапазону устройства (ось Y вниз).
/// </summary>
/// <param name="Id">Contact ID устройства. Уникален среди одновременных касаний; может повторно использоваться после отпускания.</param>
/// <param name="Tip">Tip Switch: true — палец на поверхности; false — отчёт об отпускании.</param>
/// <param name="Confidence">false — устройство считает касание случайным (ладонь).</param>
public readonly record struct TouchContact(int Id, double X, double Y, bool Tip = true, bool Confidence = true)
{
    public PointD Position => new(X, Y);
}

/// <summary>Кадр: состояние всех касаний в момент времени.</summary>
/// <param name="TimestampMs">Монотонное время, мс.</param>
/// <param name="IsComplete">
/// true — кадр содержит все текущие касания; касание, которого нет в полном кадре, считается отпущенным
/// (некоторые устройства не присылают отдельный отчёт Tip=0). false — частичный кадр (гибридный режим
/// с потерей отчёта): отсутствие контакта ничего не значит.
/// </param>
public sealed record TouchFrame(long TimestampMs, IReadOnlyList<TouchContact> Contacts, bool IsComplete = true)
{
    public static TouchFrame Empty(long ts) => new(ts, Array.Empty<TouchContact>(), true);
}

public interface ITouchSource
{
    /// <summary>Вызывается в потоке, где работает движок (для Windows — UI-поток, из WM_INPUT).</summary>
    event Action<TouchFrame>? FrameReceived;
}
