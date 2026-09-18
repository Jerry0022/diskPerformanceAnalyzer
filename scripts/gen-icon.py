"""Generates src/DiskPerformanceAnalyzer/Assets/app.ico (and a 256 px PNG preview).

Design: dark rounded tile, a disk platter drawn as a ring, and an activity trace crossing it —
read (green) as a filled area, requests (blue) as a line — echoing the in-app chart.
Run: python scripts/gen-icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parents[1] / "src" / "DiskPerformanceAnalyzer" / "Assets"
S = 1024  # render large, downsample for crisp edges

BG = (21, 25, 33, 255)
TILE = (29, 34, 44, 255)
RING = (59, 142, 234, 255)
RING_DIM = (59, 142, 234, 90)
GREEN = (76, 195, 138, 255)
GREEN_FILL = (76, 195, 138, 110)
BLUE = (140, 190, 255, 255)


def render() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # tile
    m = 40
    d.rounded_rectangle((m, m, S - m, S - m), radius=200, fill=TILE)

    # platter ring + hub
    c = S // 2
    r = 330
    d.ellipse((c - r, c - r, c + r, c + r), outline=RING, width=40)

    # activity trace: one calm green area (throughput) and one bold blue line (requests).
    # Few, wide bumps so the shape survives at 16 px.
    import math

    x0, x1 = 150, S - 150
    xs = list(range(x0, x1 + 1, 8))
    base = 760

    def bump(x, centers, width, amp):
        return sum(amp * math.exp(-((x - cx) / width) ** 2) for cx in centers)

    area = [(x, base - 60 - bump(x, [330, 700], 110, 230)) for x in xs]
    d.polygon([(x0, base)] + area + [(x1, base)], fill=GREEN_FILL)
    d.line(area, fill=GREEN, width=22, joint="curve")

    line = [(x, base - 120 - bump(x, [250, 520, 800], 80, 330)) for x in xs]
    d.line(line, fill=BLUE, width=34, joint="curve")

    return img


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    big = render()
    sizes = [256, 128, 64, 48, 32, 24, 16]
    frames = [big.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[0].save(OUT / "app.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[1:])
    frames[0].save(OUT / "app.png")
    print("wrote", OUT / "app.ico")


if __name__ == "__main__":
    main()
