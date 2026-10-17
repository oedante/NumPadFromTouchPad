using Ogneliss.NumTouch.Core.Calibration;
using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Keys;
using Ogneliss.NumTouch.Core.Layouts;

namespace Ogneliss.NumTouch.Core.Engine;

public enum EngineEventKind { KeyPressed, KeyReleased, TextTyped, OutputError, ContactIgnored, StaleContactRecovered, AllReleased }

/// <summary>Событие движка для UI (подсветка, диагностика). В журнал пишутся только ошибки и счётчики, не клавиши.</summary>
public sealed record EngineEvent(EngineEventKind Kind, int ContactId, string? ZoneId, string Message);

/// <summary>Снимок активного касания (для визуализации в редакторе/калибровке/диагностике).</summary>
public sealed record ActiveContactInfo(int Id, PointD AreaPosition, string? ZoneId, bool Pressed, bool Ignored);

/// <summary>
/// Конечный автомат «физическое касание → логическое нажатие зоны → системные события».
///
/// Инварианты:
///  • логическое нажатие привязано к Contact ID и живёт ровно от касания до отрыва этого контакта;
///  • зона фиксируется в момент касания и не зависит от последующих координат (кроме ReleaseOnExit);
///  • системная клавиша нажимается при переходе счётчика ссылок 0→1 и отпускается при 1→0;
///  • после <see cref="ReleaseAll"/> счётчики равны нулю, а все отправленные KeyDown имеют парный KeyUp
///    (если KeyUp не удалось отправить, он ставится в очередь повторов).
///
/// Класс не потокобезопасен: все вызовы — из одного потока.
/// </summary>
public sealed class KeyPressEngine
{
    private sealed class ContactState
    {
        public int Id;
        public KeyZone? Zone;
        public PointD LastArea;
        public long StartMs;
        public long LastSeenMs;
        public bool Ignored;
        public bool SuppressesPointer;
        /// <summary>Клавиши, удерживаемые этим контактом (в порядке нажатия).</summary>
        public readonly List<KeyDefinition> HeldKeys = new();
        public KeyDefinition? RepeatKey;
        public string? RepeatText;
        public long NextRepeatMs;
    }

    private readonly IKeyboardOutput _output;
    private readonly Dictionary<int, ContactState> _contacts = new();
    private readonly Dictionary<string, int> _refCount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeyDefinition> _keyDefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _repeatOwner = new(StringComparer.Ordinal);
    private readonly List<KeyDefinition> _pendingUps = new();

    private Layout _layout;
    private HitTester _hit;
    private AffineTransform _transform;
    private RectD? _keyBounds;
    private EngineOptions _options;
    private long _lastSuppressActivityMs = long.MinValue / 2;

    public event Action<EngineEvent>? EventRaised;

    public KeyPressEngine(IKeyboardOutput output, Layout layout, AffineTransform? transform = null, EngineOptions? options = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _hit = new HitTester(layout);
        _transform = transform ?? AffineTransform.Identity;
        _keyBounds = layout.KeyBounds();
        _options = (options ?? new EngineOptions()).Sanitized();
    }

    public Layout Layout => _layout;
    public AffineTransform Transform => _transform;
    public EngineOptions Options => _options;

    /// <summary>Число системных клавиш, находящихся в нажатом состоянии (по данным движка).</summary>
    public int PressedKeyCount => _refCount.Count(kv => kv.Value > 0);
    public IReadOnlyCollection<string> PressedKeys => _refCount.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
    public int ActiveContactCount => _contacts.Count;
    public int PendingReleaseCount => _pendingUps.Count;
    public bool HasActiveContacts => _contacts.Count > 0;
    /// <summary>Нужен ли периодический вызов <see cref="Tick"/> (автоповтор, сторож, повтор KeyUp).</summary>
    public bool NeedsTick => _contacts.Count > 0 || _pendingUps.Count > 0;

    public IReadOnlyList<ActiveContactInfo> ActiveContacts =>
        _contacts.Values.Select(c => new ActiveContactInfo(c.Id, c.LastArea, c.Zone?.Id, c.HeldKeys.Count > 0 || c.RepeatText != null, c.Ignored)).ToList();

    /// <summary>Смена раскладки/калибровки/параметров. Удерживаемые клавиши отпускаются; пальцы, лежащие на тачпаде, игнорируются до отрыва.</summary>
    public void Configure(Layout layout, AffineTransform transform, EngineOptions options)
    {
        ReleaseAll("configuration changed", keepIgnoringActiveContacts: true);
        _layout = layout;
        _hit = new HitTester(layout);
        _transform = transform.IsValid ? transform : AffineTransform.Identity;
        _keyBounds = layout.KeyBounds();
        _options = options.Sanitized();
    }

    public PointD ToArea(PointD device) => _transform.Apply(device);

    /// <summary>Есть ли сейчас касание, при котором указатель должен блокироваться.</summary>
    public bool HasSuppressingContact => _contacts.Values.Any(c => c.SuppressesPointer);
    /// <summary>Время последней «блокирующей» активности (касание, движение, отрыв) — для хвоста подавления.</summary>
    public long LastSuppressActivityMs => _lastSuppressActivityMs;

    /// <summary>Нужно ли блокировать указатель (курсор/тапы) в данный момент.</summary>
    public bool WantsPointerSuppression(long nowMs, int tailMs)
    {
        if (_contacts.Values.Any(c => c.SuppressesPointer)) return true;
        return nowMs - _lastSuppressActivityMs <= tailMs;
    }

    // ------------------------------------------------------------------ кадры

    public void ProcessFrame(TouchFrame frame)
    {
        long now = frame.TimestampMs;
        var seen = new HashSet<int>();

        foreach (var c in frame.Contacts)
        {
            if (!seen.Add(c.Id)) continue; // дубликат ID в кадре — берём первый
            if (_contacts.TryGetValue(c.Id, out var state))
            {
                state.LastSeenMs = now;
                state.LastArea = ToArea(c.Position);
                if (state.SuppressesPointer) _lastSuppressActivityMs = now;

                if (!c.Tip) { EndContact(state, now, cancelled: false); continue; }

                if (!state.Ignored && _options.IgnoreLowConfidence && !c.Confidence)
                {
                    ReleaseContactKeys(state);
                    state.Ignored = true;
                    Raise(EngineEventKind.ContactIgnored, state.Id, state.Zone?.Id, "low confidence (palm)");
                    continue;
                }

                if (!state.Ignored && _options.SlideBehavior == SlideBehavior.ReleaseOnExit && state.Zone is not null)
                {
                    var current = _hit.Hit(state.LastArea);
                    if (!ReferenceEquals(current, state.Zone))
                    {
                        ReleaseContactKeys(state);
                        state.Ignored = true; // до отрыва пальца новых нажатий нет
                    }
                }
            }
            else
            {
                if (!c.Tip) continue; // отрыв неизвестного контакта (касание началось до включения режима)
                BeginContact(c, now);
            }
        }

        if (frame.IsComplete)
        {
            foreach (var missing in _contacts.Values.Where(s => !seen.Contains(s.Id)).ToList())
                EndContact(missing, now, cancelled: false);
        }
    }

    private void BeginContact(TouchContact c, long now)
    {
        var area = ToArea(c.Position);
        var state = new ContactState { Id = c.Id, LastArea = area, StartMs = now, LastSeenMs = now };
        _contacts[c.Id] = state;

        state.SuppressesPointer = !_layout.AllowPointerOutsideKeys || (_keyBounds?.Contains(area) ?? false);
        if (state.SuppressesPointer) _lastSuppressActivityMs = now;

        if (_options.IgnoreLowConfidence && !c.Confidence)
        {
            state.Ignored = true;
            Raise(EngineEventKind.ContactIgnored, c.Id, null, "low confidence (palm)");
            return;
        }

        int pressedContacts = _contacts.Values.Count(s => s != state && !s.Ignored && s.Zone is not null);
        if (pressedContacts >= _options.MaxSimultaneousKeys)
        {
            state.Ignored = true;
            Raise(EngineEventKind.ContactIgnored, c.Id, null, "too many simultaneous keys");
            return;
        }

        state.Zone = _hit.Hit(area);
        if (state.Zone is null) return;

        var action = state.Zone.Action;
        bool holdSemantics = _options.PressTrigger == PressTrigger.OnTouchDown || IsModifierAction(action);
        if (holdSemantics) Press(state, now);
    }

    private void EndContact(ContactState state, long now, bool cancelled)
    {
        _contacts.Remove(state.Id);
        if (state.SuppressesPointer) _lastSuppressActivityMs = now;
        if (state.Ignored || state.Zone is null) { ReleaseContactKeys(state); return; }

        bool holdSemantics = _options.PressTrigger == PressTrigger.OnTouchDown || IsModifierAction(state.Zone.Action);
        if (holdSemantics)
        {
            ReleaseContactKeys(state);
            return;
        }

        // OnRelease: срабатывание только если палец остался в исходной зоне и отрыв достоверный.
        if (!cancelled && ReferenceEquals(_hit.Hit(state.LastArea), state.Zone))
        {
            Press(state, now);
            ReleaseContactKeys(state);
        }
    }

    // ------------------------------------------------------------------ действия

    private static bool IsModifierAction(KeyAction a) =>
        a.Kind == KeyActionKind.Key && KeyCatalog.TryGet(a.Key, out var k) && k.IsModifier;

    private KeyDefinition ResolveKey(KeyDefinition key)
    {
        if (_options.FollowNumLock && key.NumLockOffName is not null && !SafeNumLock()
            && KeyCatalog.TryGet(key.NumLockOffName, out var alt))
            return alt;
        return key;
    }

    private bool SafeNumLock()
    {
        try { return _output.IsNumLockOn; } catch { return true; }
    }

    private void Press(ContactState state, long now)
    {
        var zone = state.Zone!;
        var a = zone.Action;
        switch (a.Kind)
        {
            case KeyActionKind.Key:
            {
                if (!KeyCatalog.TryGet(a.Key, out var raw)) { Error(state, $"unknown key '{a.Key}'"); return; }
                var key = ResolveKey(raw);
                if (Acquire(key, state))
                {
                    Raise(EngineEventKind.KeyPressed, state.Id, zone.Id, key.Name);
                    if (CanRepeat(zone, key)) { state.RepeatKey = key; state.NextRepeatMs = now + _options.RepeatDelayMs; }
                }
                break;
            }
            case KeyActionKind.Combo:
            {
                var mods = new List<KeyDefinition>();
                foreach (var m in a.Modifiers)
                {
                    if (!KeyCatalog.TryGet(m, out var mk)) { Error(state, $"unknown modifier '{m}'"); return; }
                    mods.Add(mk);
                }
                if (!KeyCatalog.TryGet(a.Key, out var main)) { Error(state, $"unknown key '{a.Key}'"); return; }
                main = ResolveKey(main);
                foreach (var mk in mods.Append(main))
                {
                    if (!Acquire(mk, state))
                    {
                        ReleaseContactKeys(state); // откат уже нажатых модификаторов
                        return;
                    }
                }
                Raise(EngineEventKind.KeyPressed, state.Id, zone.Id, a.Describe());
                if (CanRepeat(zone, main)) { state.RepeatKey = main; state.NextRepeatMs = now + _options.RepeatDelayMs; }
                break;
            }
            case KeyActionKind.Text:
            {
                if (string.IsNullOrEmpty(a.Text)) return;
                if (SafeType(a.Text))
                {
                    Raise(EngineEventKind.TextTyped, state.Id, zone.Id, "text");
                    if (zone.Repeat && _options.AutoRepeat && _options.PressTrigger == PressTrigger.OnTouchDown)
                    { state.RepeatText = a.Text; state.NextRepeatMs = now + _options.RepeatDelayMs; }
                }
                else Error(state, "text output failed");
                break;
            }
            case KeyActionKind.None:
                break;
        }
    }

    private bool CanRepeat(KeyZone zone, KeyDefinition key) =>
        zone.Repeat && _options.AutoRepeat && _options.PressTrigger == PressTrigger.OnTouchDown && !key.IsModifier
        && key.Name is not ("NumLock" or "CapsLock" or "ScrollLock" or "PrintScreen" or "Pause");

    /// <summary>Нажать клавишу от имени контакта (счётчик ссылок).</summary>
    private bool Acquire(KeyDefinition key, ContactState owner)
    {
        _refCount.TryGetValue(key.Name, out int count);
        if (count == 0)
        {
            // Если для этой клавиши висит неотправленный KeyUp — он больше не нужен, клавиша снова нажата.
            _pendingUps.RemoveAll(k => k.Name == key.Name);
            if (!SafeDown(key))
            {
                Error(owner, $"KeyDown failed: {key.Name}");
                return false;
            }
            _repeatOwner[key.Name] = owner.Id;
        }
        _refCount[key.Name] = count + 1;
        _keyDefs[key.Name] = key;
        owner.HeldKeys.Add(key);
        return true;
    }

    private void ReleaseContactKeys(ContactState state)
    {
        for (int i = state.HeldKeys.Count - 1; i >= 0; i--)
            ReleaseKey(state.HeldKeys[i], state.Id);
        bool hadText = state.RepeatText != null;
        state.HeldKeys.Clear();
        state.RepeatKey = null;
        state.RepeatText = null;
        if (hadText && state.Zone is not null) Raise(EngineEventKind.KeyReleased, state.Id, state.Zone.Id, "text");
    }

    private void ReleaseKey(KeyDefinition key, int contactId)
    {
        if (!_refCount.TryGetValue(key.Name, out int count) || count <= 0) return;
        count--;
        if (count > 0) { _refCount[key.Name] = count; return; }
        _refCount.Remove(key.Name);
        _repeatOwner.Remove(key.Name);
        SendUpWithRetry(key, contactId);
        Raise(EngineEventKind.KeyReleased, contactId, null, key.Name);
    }

    private void SendUpWithRetry(KeyDefinition key, int contactId)
    {
        if (SafeUp(key)) return;
        if (SafeUp(key)) return; // одна немедленная повторная попытка
        if (!_pendingUps.Any(k => k.Name == key.Name)) _pendingUps.Add(key);
        Raise(EngineEventKind.OutputError, contactId, null, $"KeyUp failed: {key.Name}; queued for retry");
    }

    // ------------------------------------------------------------------ время

    /// <summary>Автоповтор, сторож зависших контактов, повтор неотправленных KeyUp. Вызывать каждые 10–20 мс, пока <see cref="NeedsTick"/>.</summary>
    public void Tick(long nowMs)
    {
        RetryPendingUps();

        foreach (var stale in _contacts.Values.Where(c => nowMs - c.LastSeenMs > _options.StaleContactTimeoutMs).ToList())
        {
            EndContact(stale, nowMs, cancelled: true);
            Raise(EngineEventKind.StaleContactRecovered, stale.Id, stale.Zone?.Id, "no updates from device; contact released");
        }

        foreach (var c in _contacts.Values)
        {
            if (c.Ignored) continue;
            if (c.RepeatKey is { } rk && nowMs >= c.NextRepeatMs)
            {
                // Typematic: повторный KeyDown без KeyUp — как у физической клавиатуры. Только «владелец» клавиши.
                if (_repeatOwner.TryGetValue(rk.Name, out int owner) && owner == c.Id && SafeDown(rk))
                    c.NextRepeatMs = AdvanceRepeat(c.NextRepeatMs, nowMs);
                else c.NextRepeatMs = nowMs + _options.RepeatIntervalMs;
            }
            else if (c.RepeatText is { } text && nowMs >= c.NextRepeatMs)
            {
                SafeType(text);
                c.NextRepeatMs = AdvanceRepeat(c.NextRepeatMs, nowMs);
            }
        }
    }

    private long AdvanceRepeat(long scheduled, long now)
    {
        long next = scheduled + _options.RepeatIntervalMs;
        // Не «догоняем» пропущенные интервалы пачкой (например, после зависания UI).
        return next <= now ? now + _options.RepeatIntervalMs : next;
    }

    private void RetryPendingUps()
    {
        if (_pendingUps.Count == 0) return;
        foreach (var k in _pendingUps.ToList())
            if (SafeUp(k)) _pendingUps.Remove(k);
    }

    // ------------------------------------------------------------------ аварийное отпускание

    /// <summary>
    /// Отпустить всё: при выключении режима, смене раскладки, блокировке, сне, потере устройства, выходе.
    /// </summary>
    /// <param name="keepIgnoringActiveContacts">true — пальцы, всё ещё лежащие на тачпаде, не вызовут нажатий до отрыва.</param>
    public void ReleaseAll(string reason, bool keepIgnoringActiveContacts = false)
    {
        foreach (var c in _contacts.Values.ToList())
        {
            ReleaseContactKeys(c);
            c.Ignored = true;
        }
        // Страховка: всё, что числится нажатым, но не принадлежит контактам.
        foreach (var name in _refCount.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList())
        {
            _refCount.Remove(name);
            if (_keyDefs.TryGetValue(name, out var def)) SendUpWithRetry(def, -1);
        }
        _refCount.Clear();
        _repeatOwner.Clear();
        if (!keepIgnoringActiveContacts) _contacts.Clear();
        RetryPendingUps();
        Raise(EngineEventKind.AllReleased, -1, null, reason);
    }

    /// <summary>Полный сброс перед включением режима. <paramref name="contactsAlreadyDown"/> — ID пальцев,
    /// уже лежащих на тачпаде: они будут проигнорированы до отрыва.</summary>
    public void Reset(IEnumerable<int>? contactsAlreadyDown = null, long nowMs = 0)
    {
        ReleaseAll("reset");
        if (contactsAlreadyDown is null) return;
        foreach (var id in contactsAlreadyDown)
            _contacts[id] = new ContactState { Id = id, Ignored = true, StartMs = nowMs, LastSeenMs = nowMs };
    }

    // ------------------------------------------------------------------ безопасный вывод

    private bool SafeDown(KeyDefinition k) { try { return _output.KeyDown(k); } catch { return false; } }
    private bool SafeUp(KeyDefinition k) { try { return _output.KeyUp(k); } catch { return false; } }
    private bool SafeType(string t) { try { return _output.TypeText(t); } catch { return false; } }

    private void Error(ContactState s, string message) => Raise(EngineEventKind.OutputError, s.Id, s.Zone?.Id, message);

    private void Raise(EngineEventKind kind, int contactId, string? zoneId, string message)
    {
        try { EventRaised?.Invoke(new EngineEvent(kind, contactId, zoneId, message)); }
        catch { /* подписчик UI не должен ломать автомат */ }
    }
}
