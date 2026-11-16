"""Render the README's looping pixel-art counter with Pillow."""

from pathlib import Path

from PIL import Image, ImageDraw


GLYPHS = {
    "0": ("111", "101", "101", "101", "111"),
    "1": ("010", "110", "010", "010", "111"),
    "2": ("111", "001", "111", "100", "111"),
    "3": ("111", "001", "111", "001", "111"),
    "4": ("101", "101", "111", "001", "001"),
    "5": ("111", "100", "111", "001", "111"),
    "6": ("111", "100", "111", "101", "111"),
    "7": ("111", "001", "010", "010", "010"),
    "8": ("111", "101", "111", "101", "111"),
    "9": ("111", "101", "111", "001", "111"),
    ":": ("0", "1", "0", "1", "0"),
}

NAVY = "#0b1428"
MIDNIGHT = "#101f34"
DEEP_TEAL = "#112f35"
TEAL = "#23604b"
MINT = "#93f5ad"
GOLD = "#ffc774"
OCHRE = "#b16b39"

WIDTH, HEIGHT = 328, 92
FRAME_COUNT = 80


def pixels(draw, rows, x, y, colors, scale=1):
    for row, pattern in enumerate(rows):
        for col, pixel in enumerate(pattern):
            if pixel in colors:
                left = x + col * scale
                top = y + row * scale
                draw.rectangle((left, top, left + scale - 1, top + scale - 1), fill=colors[pixel])


def clock(draw, text):
    scale = 4
    width = sum(len(GLYPHS[char][0]) + 2 for char in text) - 2
    x = (WIDTH - width * scale) // 2
    for char in text:
        glyph = GLYPHS[char]
        colors = {"1": GOLD if char == ":" else MINT}
        pixels(draw, glyph, x, 38, {"1": TEAL}, scale)
        pixels(draw, glyph, x, 35, colors, scale)
        x += (len(glyph[0]) + 2) * scale


def crop(draw, x, y, phase):
    leaves = (
        ("..M..", "M.M..", ".MMM.", "..M.M", "..MM."),
        ("..M..", "..M.M", ".MMM.", "M.M..", ".MM.."),
    )
    pixels(draw, leaves[phase], x, y, {"M": MINT}, 2)
    pixels(draw, (".OOO.", ".GGG.", "..G..", "..O.."), x, y + 10, {"G": GOLD, "O": OCHRE}, 2)


def frame(index):
    canvas = Image.new("RGB", (WIDTH, HEIGHT), NAVY)
    draw = ImageDraw.Draw(canvas)
    draw.rectangle((0, 71, WIDTH, HEIGHT), fill=DEEP_TEAL)
    for y in (75, 82, 90):
        draw.line((0, y, WIDTH, y), fill=TEAL)
    for x in range(-30, WIDTH, 14):
        draw.line((x, 91, x + 11, 71), fill=MIDNIGHT)

    draw.rounded_rectangle((52, 13, 275, 75), radius=4, fill=TEAL)
    draw.rectangle((55, 16, 272, 71), fill=MIDNIGHT)
    draw.rectangle((62, 23, 265, 64), fill=NAVY)
    draw.line((64, 67, 263, 67), fill=DEEP_TEAL)
    draw.point((69, 69), fill=MINT)
    draw.point((258, 69), fill=MINT)

    for x, y in ((11, 57), (35, 46), (293, 46), (305, 60)):
        crop(draw, x, y, (index // 10 + x) % 2)

    for n, x in enumerate((15, 29, 42, 285, 302, 315)):
        phase = (index * 2 + n * 9) % 48
        y = 61 - phase
        color = GOLD if 8 <= phase < 31 else TEAL
        draw.point((x + phase // 12, y), fill=color)
        if 15 <= phase < 27:
            draw.line((x + phase // 12 - 1, y, x + phase // 12 + 1, y), fill=color)
            draw.point((x + phase // 12, y - 1), fill=color)

    hours, remainder = divmod(index * 3727, 3600)
    minutes, seconds = divmod(remainder, 60)
    clock(draw, f"{hours:04}:{minutes:02}:{seconds:02}")
    return canvas.resize((WIDTH * 3, HEIGHT * 3), Image.Resampling.NEAREST).quantize(colors=16)


def main():
    target = Path(__file__).resolve().parents[1] / "assets" / "horas-subindo.gif"
    frames = [frame(index) for index in range(FRAME_COUNT)]
    frames[0].save(target, save_all=True, append_images=frames[1:], duration=110, loop=0, optimize=True)


if __name__ == "__main__":
    main()
