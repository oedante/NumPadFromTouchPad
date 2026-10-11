using System.Text.Json.Serialization;
using NumPad.Core.Geometry;

namespace NumPad.Core.Layouts;

public enum KeyActionKind
{
    /// <summary>Одна клавиша (может быть модификатором). Удерживается, пока палец на зоне.</summary>
    Key,
    /// <summary>Сочетание: модификаторы + клавиша (например, Ctrl+C).</summary>
    Combo,
    /// <summary>Ввод текста через Unicode (не зависит от раскладки), например «=».</summary>
    Text,
    /// <summary>Зона ничего не делает (поглощает касание).</summary>
    None,
}

/// <summary>Назначение зоны. Хранится в JSON как данные — код обработчика ввода о конкретных клавишах не знает.</summary>
public sealed class KeyAction
{
    public KeyActionKind Kind { get; set; } = KeyActionKind.Key;
    /// <summary>Имя клавиши из <see cref="Keys.KeyCatalog"/> (для Key и Combo).</summary>
    public string? Key { get; set; }
    /// <summary>Модификаторы для Combo: Ctrl, Shift, Alt, Win.</summary>
    public List<string> Modifiers { get; set; } = new();
    /// <summary>Текст для Text.</summary>
    public string? Text { get; set; }

    public static KeyAction ForKey(string key) => new() { Kind = KeyActionKind.Key, Key = key };
    public static KeyAction ForText(string text) => new() { Kind = KeyActionKind.Text, Text = text };
    public static KeyAction ForCombo(string key, params string[] modifiers) =>
        new() { Kind = KeyActionKind.Combo, Key = key, Modifiers = modifiers.ToList() };

    public KeyAction Clone() => new() { Kind = Kind, Key = Key, Text = Text, Modifiers = Modifiers.ToList() };

    public string Describe() => Kind switch
    {
        KeyActionKind.Key => Key ?? "?",
        KeyActionKind.Combo => string.Join("+", Modifiers.Append(Key ?? "?")),
        KeyActionKind.Text => $"«{Text}»",
        _ => "—",
    };
}

/// <summary>Зона клавиши. Координаты нормализованы к области наклейки: 0..1 по ширине и высоте.</summary>
public sealed class KeyZone
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public KeyAction Action { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Автоповтор при удержании (для модификаторов и Text не применяется к модификаторам).</summary>
    public bool Repeat { get; set; } = true;

    [JsonIgnore]
    public RectD Bounds => new(X, Y, Width, Height);

    public KeyZone Clone() => new()
    {
        Id = Id, Label = Label, X = X, Y = Y, Width = Width, Height = Height,
        Action = Action.Clone(), Enabled = Enabled, Repeat = Repeat,
    };
}

public sealed class Layout
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Встроенная раскладка-образец (NUM10/20/30). Пользовательская копия с тем же Id переопределяет её.</summary>
    public bool IsBuiltIn { get; set; }
    /// <summary>Соотношение сторон области наклейки (ширина / высота) — для редактора и калибровки.</summary>
    public double AspectRatio { get; set; } = 1.6;
    /// <summary>Промежуток между клавишами (в долях ширины) — используется редактором при выравнивании.</summary>
    public double Gap { get; set; } = 0.01;
    /// <summary>
    /// Если true, касания вне прямоугольника клавиш в режиме NumPad не блокируются и двигают курсор
    /// (как «зона трекпада» в NUM10). Если false, весь тачпад работает только как клавиатура.
    /// </summary>
    public bool AllowPointerOutsideKeys { get; set; }
    public List<KeyZone> Keys { get; set; } = new();

    /// <summary>Описывающий прямоугольник всех включённых клавиш (с запасом в половину промежутка).</summary>
    public RectD? KeyBounds()
    {
        RectD? acc = null;
        foreach (var k in Keys.Where(k => k.Enabled))
            acc = acc is null ? k.Bounds : acc.Value.Union(k.Bounds);
        return acc?.Inflate(Gap / 2);
    }

    public Layout Clone() => new()
    {
        SchemaVersion = SchemaVersion, Id = Id, Name = Name, Description = Description, IsBuiltIn = IsBuiltIn,
        AspectRatio = AspectRatio, Gap = Gap, AllowPointerOutsideKeys = AllowPointerOutsideKeys,
        Keys = Keys.Select(k => k.Clone()).ToList(),
    };
}
