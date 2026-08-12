"""生成 RuleProxy 应用图标 icon.ico（Pillow）。

优先使用项目根目录 source_icon.png（桌面图片优化而来）：
  - 白色背景 → 透明（从边缘连通填充，保留内部白色元素）
  - 去色边（透明区域 RGB 归零，避免缩放产生杂色边缘）
  - 按内容裁剪居中、圆角化、输出多尺寸 ICO
source_icon.png 缺失时回退到程序化绘制的三叉分流图标。
"""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")
SRC = os.path.join(ROOT, "source_icon.png")
OUT_ICO = os.path.join(ROOT, "icon.ico")
OUT_PNG = os.path.join(ROOT, "icon.png")

CANVAS = 512            # 输出画布边长
CORNER_RATIO = 0.10     # 圆角半径占画布比例
BG_TOLERANCE = 48       # 背景连通阈值（近白即背景）
MARK = (255, 0, 255)    # 背景标记色（magenta）
# 与应用主题（主色 #2f6feb）匹配：把源图 teal 色相偏移到主题蓝并提升亮度
HUE_SHIFT = 32          # 色相偏移（度）：teal → 主题蓝
V_SCALE = 1.33          # 亮度缩放，使主色亮度接近主题蓝

ICO_SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]


# ---------------------------------------------------------------- 图片源处理
def _hue_shift(img: Image.Image, degrees: float, v_scale: float = 1.0) -> Image.Image:
    """整体色相偏移 + 亮度缩放：把图标调成与应用主题一致的蓝色。"""
    if degrees == 0 and abs(v_scale - 1.0) < 1e-6:
        return img
    hsv = img.convert("HSV")
    h, s, v = hsv.split()
    if degrees:
        shift = round(degrees / 360.0 * 255) % 256
        h = h.point(lambda x: (x + shift) % 256)
    if abs(v_scale - 1.0) >= 1e-6:
        v = v.point(lambda x: min(255, int(x * v_scale)))
    return Image.merge("HSV", (h, s, v)).convert("RGB")


def _bg_to_alpha(img: Image.Image) -> Image.Image:
    """把与边缘连通的白色背景转透明（保留内部白色元素），返回 RGBA。"""
    work = img.convert("RGB").copy()
    w, h = work.size
    for xy in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]:
        r, g, b = work.getpixel(xy)
        if max(abs(r - 255), abs(g - 255), abs(b - 255)) <= BG_TOLERANCE:
            ImageDraw.floodfill(work, xy, MARK, thresh=BG_TOLERANCE)
    solid = Image.new("RGB", work.size, MARK)
    diff = ImageChops.difference(work, solid).convert("L")
    alpha = diff.point(lambda v: 0 if v < 12 else 255)
    rgba = work.convert("RGBA")
    rgba.putalpha(alpha)
    # 透明区域 RGB 归零，避免缩放时产生杂色边缘
    black = Image.new("RGB", rgba.size, (0, 0, 0))
    rgb = Image.composite(rgba.convert("RGB"), black, alpha)
    rgba = Image.merge("RGBA", (*rgb.split(), alpha))
    return rgba


def _trim_square(rgba: Image.Image, pad: int = 24) -> Image.Image:
    """按非透明内容包围盒裁剪并居中方正化。"""
    bbox = rgba.split()[3].getbbox()
    if bbox is None:
        return rgba
    x0, y0, x1, y1 = bbox
    w = max(x1 - x0, y1 - y0) + 2 * pad
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    half = w // 2
    left, top = max(0, cx - half), max(0, cy - half)
    return rgba.crop((left, top, left + w, top + w))


def _icon_from_source() -> Image.Image:
    img = Image.open(SRC).convert("RGB")
    img = _hue_shift(img, HUE_SHIFT, V_SCALE)   # 匹配应用主题蓝色
    rgba = _bg_to_alpha(img)
    rgba = _trim_square(rgba)
    rgba = rgba.resize((CANVAS, CANVAS), Image.LANCZOS)
    # 轻微收缩 alpha 一圈，去除背景残留白边
    a = rgba.split()[3].filter(ImageFilter.MinFilter(3))
    rgba.putalpha(a)
    return rgba


# ---------------------------------------------------------------- 程序化回退
def _fallback_icon() -> Image.Image:
    size = 256
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def vgrad(im, top, bottom):
        w, h = im.size
        px = im.load()
        for y in range(h):
            t = y / (h - 1)
            r = int(top[0] + (bottom[0] - top[0]) * t)
            g = int(top[1] + (bottom[1] - top[1]) * t)
            b = int(top[2] + (bottom[2] - top[2]) * t)
            for x in range(w):
                px[x, y] = (r, g, b, px[x, y][3])
        return im

    radius = 56
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=(47, 111, 235, 255))
    img = vgrad(img, (47, 111, 235), (28, 45, 96))
    d = ImageDraw.Draw(img)
    cx, cy = size // 2, size // 2
    d.ellipse([cx - 34, cy - 34, cx + 34, cy + 34], fill=(255, 255, 255, 255))
    d.polygon([(cx - 22, cy + 34), (cx + 22, cy + 34), (cx, cy + 74)], fill=(255, 255, 255, 255))
    d.line([(cx, cy), (cx - 70, cy - 70)], fill=(255, 255, 255, 255), width=26)
    d.polygon([(cx - 70, cy - 96), (cx - 104, cy - 62), (cx - 36, cy - 62)], fill=(255, 255, 255, 255))
    d.line([(cx, cy), (cx + 70, cy - 70)], fill=(255, 255, 255, 255), width=26)
    d.polygon([(cx + 70, cy - 96), (cx + 36, cy - 62), (cx + 104, cy - 62)], fill=(255, 255, 255, 255))
    d.ellipse([cx - 16, cy - 16, cx + 16, cy + 16], fill=(47, 111, 235, 255))
    return img


# ---------------------------------------------------------------- 统一收尾
def _finalize(icon: Image.Image) -> Image.Image:
    """统一到 512 画布并加圆角。"""
    if icon.size != (CANVAS, CANVAS):
        icon = icon.resize((CANVAS, CANVAS), Image.LANCZOS)
    radius = int(CANVAS * CORNER_RATIO)
    mask = Image.new("L", icon.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, CANVAS - 1, CANVAS - 1], radius=radius, fill=255)
    icon.putalpha(ImageChops.multiply(icon.split()[3], mask))
    return icon


def main() -> None:
    if os.path.exists(SRC):
        print(f"使用图片源: {SRC}")
        icon = _icon_from_source()
    else:
        print("未找到 source_icon.png，回退到程序化图标")
        icon = _fallback_icon()
    icon = _finalize(icon)
    icon.save(OUT_ICO, format="ICO", sizes=ICO_SIZES)
    icon.save(OUT_PNG, format="PNG")
    print(f"icon generated: {os.path.abspath(OUT_ICO)}")


if __name__ == "__main__":
    main()
