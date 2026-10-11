namespace NumPad.Core.Layouts;

/// <summary>
/// Встроенные раскладки — собственные редактируемые конфигурации, по назначению сопоставимые с
/// Magic Numpad NUM10 / NUM20 / NUM30 (не копия коммерческого продукта).
/// Клавиши задаются в «клавишных единицах» на сетке и затем нормализуются к 0..1.
/// </summary>
public static class BuiltInLayouts
{
    public const string Num10Id = "num10";
    public const string Num20Id = "num20";
    public const string Num30Id = "num30";

    public static IReadOnlyList<Layout> All => new[] { Num10(), Num20(), Num30() };

    public static Layout? Get(string id) => All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    // Геометрия сетки (в клавишных единицах).
    private const double U = 1.0;        // клавиша
    private const double G = 0.14;       // промежуток между клавишами
    private const double SmallH = 0.55;  // высота верхнего ряда малых клавиш
    private const double RowGapAfterSmall = 0.3;
    private const double Margin = 0.25;  // поля по краям наклейки

    private sealed class Builder
    {
        private readonly List<(string id, string label, double x, double y, double w, double h, KeyAction a, bool repeat)> _keys = new();
        public void Key(string id, string label, double x, double y, double w, double h, KeyAction action, bool repeat = true)
            => _keys.Add((id, label, x, y, w, h, action, repeat));

        public Layout Build(string id, string name, string description, double totalW, double totalH, bool pointerOutside)
        {
            var layout = new Layout
            {
                Id = id, Name = name, Description = description, IsBuiltIn = true,
                AspectRatio = Math.Round(totalW / totalH, 4),
                Gap = Math.Round(G / totalW, 4),
                AllowPointerOutsideKeys = pointerOutside,
            };
            foreach (var k in _keys)
            {
                layout.Keys.Add(new KeyZone
                {
                    Id = k.id, Label = k.label,
                    X = Math.Round(k.x / totalW, 5), Y = Math.Round(k.y / totalH, 5),
                    Width = Math.Round(k.w / totalW, 5), Height = Math.Round(k.h / totalH, 5),
                    Action = k.a, Repeat = k.repeat,
                });
            }
            return layout;
        }
    }

    /// <summary>X-координата колонки <paramref name="col"/> группы, начинающейся в <paramref name="x0"/>.</summary>
    private static double Col(double x0, int col) => x0 + col * (U + G);
    /// <summary>Y-координата основного ряда (0 = первый ряд под малыми клавишами).</summary>
    private static double Row(int row) => Margin + SmallH + RowGapAfterSmall + row * (U + G);

    /// <summary>Классический цифровой блок справа: ряд малых клавиш + 5 рядов. Возвращает ширину блока.</summary>
    private static void AddNumpadBlock(Builder b, double x0)
    {
        double sy = Margin;
        b.Key("tab", "Tab", Col(x0, 0), sy, U, SmallH, KeyAction.ForKey("Tab"));
        b.Key("bksp", "⌫", Col(x0, 1), sy, U, SmallH, KeyAction.ForKey("Backspace"));
        b.Key("calc", "Calc", Col(x0, 2), sy, U, SmallH, KeyAction.ForKey("LaunchCalculator"), repeat: false);
        b.Key("numlock", "Num", Col(x0, 3), sy, U, SmallH, KeyAction.ForKey("NumLock"), repeat: false);

        b.Key("esc", "Esc", Col(x0, 0), Row(0), U, U, KeyAction.ForKey("Escape"), repeat: false);
        b.Key("equals", "=", Col(x0, 1), Row(0), U, U, KeyAction.ForText("="));
        b.Key("divide", "/", Col(x0, 2), Row(0), U, U, KeyAction.ForKey("NumpadDivide"));
        b.Key("multiply", "*", Col(x0, 3), Row(0), U, U, KeyAction.ForKey("NumpadMultiply"));

        b.Key("n7", "7", Col(x0, 0), Row(1), U, U, KeyAction.ForKey("Numpad7"));
        b.Key("n8", "8", Col(x0, 1), Row(1), U, U, KeyAction.ForKey("Numpad8"));
        b.Key("n9", "9", Col(x0, 2), Row(1), U, U, KeyAction.ForKey("Numpad9"));
        b.Key("subtract", "−", Col(x0, 3), Row(1), U, U, KeyAction.ForKey("NumpadSubtract"));

        b.Key("n4", "4", Col(x0, 0), Row(2), U, U, KeyAction.ForKey("Numpad4"));
        b.Key("n5", "5", Col(x0, 1), Row(2), U, U, KeyAction.ForKey("Numpad5"));
        b.Key("n6", "6", Col(x0, 2), Row(2), U, U, KeyAction.ForKey("Numpad6"));
        b.Key("add", "+", Col(x0, 3), Row(2), U, U, KeyAction.ForKey("NumpadAdd"));

        b.Key("n1", "1", Col(x0, 0), Row(3), U, U, KeyAction.ForKey("Numpad1"));
        b.Key("n2", "2", Col(x0, 1), Row(3), U, U, KeyAction.ForKey("Numpad2"));
        b.Key("n3", "3", Col(x0, 2), Row(3), U, U, KeyAction.ForKey("Numpad3"));
        b.Key("enter", "Enter", Col(x0, 3), Row(3), U, 2 * U + G, KeyAction.ForKey("NumpadEnter"));

        b.Key("n0", "0", Col(x0, 0), Row(4), 2 * U + G, U, KeyAction.ForKey("Numpad0"));
        b.Key("decimal", ".", Col(x0, 2), Row(4), U, U, KeyAction.ForKey("NumpadDecimal"));
    }

    private const double LeftBlockW = 3 * U + 2 * G;
    private const double GroupGap = 0.6;
    private static double NumpadW => 4 * U + 3 * G;
    private static double TotalH => Row(4) + U + Margin;

    /// <summary>Левый блок малых клавиш (Esc/PrtSc/Ins) и навигация Home/PgUp/Del/End/PgDn.</summary>
    private static void AddNavTop(Builder b, double x0)
    {
        double sy = Margin;
        b.Key("prtsc", "PrtSc", Col(x0, 0), sy, U, SmallH, KeyAction.ForKey("PrintScreen"), repeat: false);
        b.Key("ins", "Ins", Col(x0, 1), sy, U, SmallH, KeyAction.ForKey("Insert"), repeat: false);
        b.Key("shift", "Shift", Col(x0, 2), sy, U, SmallH, KeyAction.ForKey("LeftShift"), repeat: false);

        b.Key("ctrl", "Ctrl", Col(x0, 0), Row(0), U, U, KeyAction.ForKey("LeftCtrl"), repeat: false);
        b.Key("home", "Home", Col(x0, 1), Row(0), U, U, KeyAction.ForKey("Home"));
        b.Key("pgup", "Page Up", Col(x0, 2), Row(0), U, U, KeyAction.ForKey("PageUp"));

        b.Key("del", "Del", Col(x0, 0), Row(1), U, U, KeyAction.ForKey("Delete"));
        b.Key("end", "End", Col(x0, 1), Row(1), U, U, KeyAction.ForKey("End"));
        b.Key("pgdn", "Page Down", Col(x0, 2), Row(1), U, U, KeyAction.ForKey("PageDown"));
    }

    /// <summary>NUM10: цифровой блок справа, слева — свободная зона трекпада (курсор работает).</summary>
    public static Layout Num10()
    {
        var b = new Builder();
        double x0 = Margin + LeftBlockW + GroupGap;
        AddNumpadBlock(b, x0);
        double totalW = x0 + NumpadW + Margin;
        return b.Build(Num10Id, "NUM10 — цифровой блок + трекпад",
            "Классический цифровой блок справа; левая часть тачпада остаётся трекпадом.",
            totalW, TotalH, pointerOutside: true);
    }

    /// <summary>NUM20: полный блок — навигация, стрелки, цифровой блок.</summary>
    public static Layout Num20()
    {
        var b = new Builder();
        double xl = Margin;
        double x0 = Margin + LeftBlockW + GroupGap;
        AddNavTop(b, xl);
        b.Key("up", "↑", Col(xl, 1), Row(3), U, U, KeyAction.ForKey("Up"));
        b.Key("left", "←", Col(xl, 0), Row(4), U, U, KeyAction.ForKey("Left"));
        b.Key("down", "↓", Col(xl, 1), Row(4), U, U, KeyAction.ForKey("Down"));
        b.Key("right", "→", Col(xl, 2), Row(4), U, U, KeyAction.ForKey("Right"));
        AddNumpadBlock(b, x0);
        double totalW = x0 + NumpadW + Margin;
        return b.Build(Num20Id, "NUM20 — полный цифровой блок",
            "Навигационные клавиши, стрелки и цифровой блок. Весь тачпад работает как клавиатура.",
            totalW, TotalH, pointerOutside: false);
    }

    /// <summary>NUM30: как NUM20, но вместо стрелок — 6 настраиваемых клавиш.</summary>
    public static Layout Num30()
    {
        var b = new Builder();
        double xl = Margin;
        double x0 = Margin + LeftBlockW + GroupGap;
        AddNavTop(b, xl);
        b.Key("mail", "Почта", Col(xl, 0), Row(3), U, U, KeyAction.ForKey("LaunchMail"), repeat: false);
        b.Key("web", "Web", Col(xl, 1), Row(3), U, U, KeyAction.ForKey("BrowserHome"), repeat: false);
        b.Key("undo", "Отмена", Col(xl, 2), Row(3), U, U, KeyAction.ForCombo("Z", "Ctrl"));
        b.Key("cut", "Вырез.", Col(xl, 0), Row(4), U, U, KeyAction.ForCombo("X", "Ctrl"), repeat: false);
        b.Key("copy", "Копир.", Col(xl, 1), Row(4), U, U, KeyAction.ForCombo("C", "Ctrl"), repeat: false);
        b.Key("paste", "Вставка", Col(xl, 2), Row(4), U, U, KeyAction.ForCombo("V", "Ctrl"));
        AddNumpadBlock(b, x0);
        double totalW = x0 + NumpadW + Margin;
        return b.Build(Num30Id, "NUM30 — с настраиваемыми клавишами",
            "Как NUM20, но вместо стрелок — шесть клавиш действий (почта, браузер, буфер обмена).",
            totalW, TotalH, pointerOutside: false);
    }
}
