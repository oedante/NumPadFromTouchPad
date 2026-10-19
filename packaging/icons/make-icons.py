#!/usr/bin/env python3
"""
Генерирует все значки программы из исходного рисунка packaging/icons/icon-source.png (Pillow, numpy):

  src/Ogneliss.NumTouch.App/Assets/app.ico         — значок программы (16…256; мелкие размеры — упрощённый рисунок)
  src/Ogneliss.NumTouch.App/Assets/logo.png        — логотип в боковой панели окна
  src/Ogneliss.NumTouch.App/Assets/tray_on|off.ico — значок в трее Windows (16…48)
  src/Ogneliss.NumTouch.App/Assets/tray_on|off.png — значок в трее Linux
  src/Ogneliss.NumTouch.App/Assets/tray_mac_on|off.png — значок в строке меню macOS (шаблон: чёрный силуэт,
                                                      система сама красит его под светлую/тёмную строку меню)
  packaging/windows/Assets/*.png                   — значки пакета MSIX
  packaging/linux/ogneliss-numtouch.png            — значок ярлыка Linux
  packaging/macos/AppIcon.icns                     — значок .app (плашка 824/1024 по сетке macOS)
  docs/icon.png                                    — для README

Мелкие размеры (≤ 32 px) рисуются отдельно: клавиши 3×3 без цифр и медальон «пламя с лисой» из основного
рисунка — полный рисунок на 16 px превращается в серое пятно.
Запуск из корня репозитория: python3 packaging/icons/make-icons.py
"""
import io, struct
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageOps

ROOT = Path(__file__).resolve().parents[2]
NAVY = (29, 41, 55)
WHITE = (238, 241, 244)

# ------------------------------------------------------------------ основной рисунок
src = np.asarray(Image.open(ROOT / "packaging/icons/icon-source.png").convert("RGB")).astype(float)
crop = src[197:1022, 184:1091]                      # рисунок без исходной плашки
mx = crop.max(axis=2)
t = np.clip((mx - 60) / 40, 0, 1)[..., None]       # тёмное → ровный цвет плашки (убрать шум и края старой плашки)
ART = Image.fromarray((np.array(NAVY) * (1 - t) + crop * t).clip(0, 255).astype("uint8"))


def rounded_mask(size, radius, ss=4):
    m = Image.new("L", (size * ss, size * ss), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size * ss - 1, size * ss - 1], radius * ss, fill=255)
    return m.resize((size, size), Image.LANCZOS)


def plate(S=1024, fill=0.80, radius=0.215):
    base = Image.new("RGB", (S, S), NAVY)
    w = int(S * fill); h = int(w * ART.height / ART.width)
    base.paste(ART.resize((w, h), Image.LANCZOS), ((S - w) // 2, (S - h) // 2 + int(S * 0.005)))
    out = base.convert("RGBA"); out.putalpha(rounded_mask(S, int(S * radius)))
    return out


MASTER = plate(1024)

# ------------------------------------------------------------------ упрощённый рисунок для мелких размеров
box = (598, 140, 930, 575)
med = MASTER.crop(box)
arr = np.asarray(med.convert("RGB")).astype(int)
yy, xx = np.mgrid[0:arr.shape[0], 0:arr.shape[1]]
inside = (xx - (767 - box[0])) ** 2 + (yy - (405 - box[1])) ** 2 < 147 ** 2
yel = (arr[..., 0] > 150) & (arr[..., 2] < 120) & (arr[..., 0] - arr[..., 2] > 60)
content = Image.fromarray(((yel | ((arr.max(axis=2) > 90) & inside)) * 255).astype("uint8")).filter(ImageFilter.MaxFilter(3))
ring = content.filter(ImageFilter.MaxFilter(25))
MED = Image.fromarray(np.where(np.asarray(content)[..., None] > 0, np.asarray(med), np.array([*NAVY, 255])).astype("uint8"))


def small(on=True, S=512):
    u = S / 16
    plate_c = (*NAVY, 255) if on else (52, 58, 66, 255)
    body = (*WHITE, 255) if on else (165, 171, 178, 255)
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, S - 1, S - 1], int(3.4 * u), fill=plate_c)
    d.rounded_rectangle([1.6 * u, 4.2 * u, 11.6 * u, 14.6 * u], int(1.4 * u), fill=body)
    for r in range(3):
        for c in range(3):
            x = 2.7 * u + c * 3.0 * u; y = 5.3 * u + r * 3.0 * u
            d.rounded_rectangle([x, y, x + 2.0 * u, y + 2.0 * u], int(0.5 * u), fill=plate_c)
    m = MED if on else ImageOps.grayscale(MED.convert("RGB")).point(lambda v: int(v * 0.62)).convert("RGBA")
    w = int(7.6 * u); h = int(w * MED.height / MED.width)
    mm = m.resize((w, h), Image.LANCZOS)
    if not on:
        a = np.asarray(mm).astype(int); a[a[..., :3].max(axis=2) < 60, :3] = plate_c[:3]; mm = Image.fromarray(a.astype("uint8"))
    im.paste(mm, (int(S - w - 0.5 * u), int(0.35 * u)), ring.resize((w, h), Image.LANCZOS))
    mask = rounded_mask(S, int(3.4 * u), ss=1)
    im.putalpha(Image.fromarray(np.minimum(np.asarray(im)[..., 3], np.asarray(mask))))
    return im


SMALL_ON, SMALL_OFF = small(True), small(False)


def icon(size, on=True, detailed_from=40):
    """Значок нужного размера: мелкие — упрощённые, крупные — основной рисунок."""
    if size < detailed_from:
        return (SMALL_ON if on else SMALL_OFF).resize((size, size), Image.LANCZOS)
    return MASTER.resize((size, size), Image.LANCZOS)


def write_ico(path, images):
    """ICO с PNG внутри (поддерживается Windows Vista+ и Avalonia на всех ОС)."""
    data = []
    for im in images:
        b = io.BytesIO(); im.save(b, "PNG"); data.append((im.width, b.getvalue()))
    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(data)))
    offset = 6 + 16 * len(data)
    for w, png in data:
        out.write(struct.pack("<BBBBHHII", w % 256, w % 256, 0, 0, 1, 32, len(png), offset))
        offset += len(png)
    for _, png in data:
        out.write(png)
    Path(path).write_bytes(out.getvalue())


assets = ROOT / "src/Ogneliss.NumTouch.App/Assets"
write_ico(assets / "app.ico", [icon(s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
write_ico(assets / "tray_on.ico", [icon(s, True, 99) for s in (16, 20, 24, 32, 40, 48)])
write_ico(assets / "tray_off.ico", [icon(s, False, 99) for s in (16, 20, 24, 32, 40, 48)])
icon(64, True, 99).save(assets / "tray_on.png")
icon(64, False, 99).save(assets / "tray_off.png")
MASTER.resize((128, 128), Image.LANCZOS).save(assets / "logo.png")


def mac_template(on=True, S=352):
    """Силуэт для строки меню macOS: цифровой блок 3×3 и пламя; выключено — полупрозрачный (как неактивный)."""
    u = S / 16
    im = Image.new("L", (S, S), 0); d = ImageDraw.Draw(im)
    d.rounded_rectangle([1.2 * u, 4.6 * u, 11.0 * u, 14.6 * u], int(1.6 * u), fill=255)
    for r in range(3):
        for c in range(3):
            x = 2.3 * u + c * 3.0 * u; y = 5.7 * u + r * 3.0 * u
            d.rounded_rectangle([x, y, x + 2.0 * u, y + 2.0 * u], int(0.5 * u), fill=0)
    cx, cy, rr = 12.4 * u, 5.4 * u, 2.6 * u
    d.ellipse([cx - rr - 1.0 * u, cy - rr - 1.0 * u, cx + rr + 1.0 * u, cy + rr + 1.0 * u], fill=0)  # отбивка
    d.polygon([(cx - rr * 1.25, cy - 0.2 * u), (cx + 0.4 * u, -0.2 * u), (cx + rr * 1.25, cy - 0.2 * u)], fill=0)
    d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=255)
    d.polygon([(cx - rr * 0.92, cy - 0.5 * u), (cx + 0.4 * u, 0.6 * u), (cx + rr * 0.92, cy - 0.5 * u)], fill=255)
    alpha = im.resize((44, 44), Image.LANCZOS)
    if not on:
        alpha = alpha.point(lambda v: int(v * 0.45))
    out = Image.new("RGBA", (44, 44), (0, 0, 0, 0)); out.putalpha(alpha)
    return out


mac_template(True).save(assets / "tray_mac_on.png")
mac_template(False).save(assets / "tray_mac_off.png")

msix = ROOT / "packaging/windows/Assets"
icon(44, True, 99).save(msix / "Square44x44Logo.png")
MASTER.resize((50, 50), Image.LANCZOS).save(msix / "StoreLogo.png")
MASTER.resize((150, 150), Image.LANCZOS).save(msix / "Square150x150Logo.png")

MASTER.resize((256, 256), Image.LANCZOS).save(ROOT / "packaging/linux/ogneliss-numtouch.png")
(ROOT / "docs").mkdir(exist_ok=True)
MASTER.resize((256, 256), Image.LANCZOS).save(ROOT / "docs/icon.png")

mac = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
p = plate(824, radius=0.225)
mac.paste(p, (100, 100), p)
mac.save(ROOT / "packaging/macos/AppIcon.icns", sizes=[(16, 16), (32, 32), (64, 64), (128, 128), (256, 256), (512, 512), (1024, 1024)])
print("icons written")
