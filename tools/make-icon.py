#!/usr/bin/env python3
"""Resources/app.ico を app-icon.svg から生成する。

16/20/24/32px は SVG の縮小だとカギ括弧の線が半端な位置に乗ってにじむため、
ピクセルグリッドに合わせて直接描く。48px 以上は SVG を高倍率で描いて縮小する。

依存: pip install cairosvg pillow
使い方: python make_icon.py app-icon.svg app.ico
"""
import io
import sys

import cairosvg
from PIL import Image, ImageDraw

# 墨色タイル。ダークテーマのタスクバー (#202020 前後) に溶けないよう、
# 小サイズはマスター SVG より一段明るくする。
TILE_TOP = (56, 65, 80, 255)
TILE_BOTTOM = (28, 34, 45, 255)
INK = (255, 255, 255, 255)

# 小サイズの設計値: {サイズ: (角丸, 線幅, 余白, 腕の長さ, 軸の長さ)}
# 「と」の間隔がタイル幅の 37% を下回ると、2 つの括弧が四角い枠に見えてしまう。
# 腕を伸ばすときは余白も一緒に増やして間隔を保つこと。
SMALL = {
    16: (3, 2, 3, 5, 5),
    20: (4, 2, 4, 7, 7),
    24: (5, 3, 4, 8, 8),
    32: (7, 4, 6, 11, 11),
}
LARGE = [48, 64, 128, 256]


def vertical_gradient(size: int) -> Image.Image:
    """タイル用の縦グラデーション。"""
    grad = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / max(size - 1, 1)
        grad.putpixel((0, y), tuple(
            round(a + (b - a) * t) for a, b in zip(TILE_TOP, TILE_BOTTOM)
        ))
    return grad.resize((size, size))


def draw_small(size: int) -> Image.Image:
    """ピクセルグリッドに合わせて描く（アンチエイリアスなし）。"""
    radius, w, pad, arm, stem = SMALL[size]

    # 全面タイル（小サイズはトレイで少しでも大きく見せたいので余白を取らない）
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    im.paste(vertical_gradient(size), (0, 0), mask)

    d = ImageDraw.Draw(im)

    def bar(x, y, bw, bh):
        d.rectangle([x, y, x + bw - 1, y + bh - 1], fill=INK)

    # 「: 上辺 + 左の軸
    bar(pad, pad, arm, w)
    bar(pad, pad, w, stem)
    # 」: 「を中心 (size/2) 対称に写した位置
    bar(size - pad - arm, size - pad - w, arm, w)
    bar(size - pad - w, size - pad - stem, w, stem)
    return im


def draw_large(svg_path: str, size: int) -> Image.Image:
    """SVG を 4 倍で描いてから縮小し、曲線を滑らかにする。"""
    png = cairosvg.svg2png(url=svg_path, output_width=size * 4, output_height=size * 4)
    with Image.open(io.BytesIO(png)) as big:
        return big.convert("RGBA").resize((size, size), Image.LANCZOS)


def main() -> None:
    svg_path = sys.argv[1] if len(sys.argv) > 1 else "app-icon.svg"
    out_path = sys.argv[2] if len(sys.argv) > 2 else "app.ico"

    frames = [draw_small(s) for s in sorted(SMALL)]
    frames += [draw_large(svg_path, s) for s in LARGE]
    frames.sort(key=lambda im: im.size[0])

    base = frames[-1]
    base.save(out_path, format="ICO", sizes=[im.size for im in frames], append_images=frames[:-1])
    print(f"{out_path}: " + ", ".join(f"{im.size[0]}px" for im in frames))


if __name__ == "__main__":
    main()
