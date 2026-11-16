"""Gera o ícone do Shift Control: quadrado grafite com três barras de medição (acento turquesa)."""
import sys
from pathlib import Path
from PIL import Image, ImageDraw

out = Path(sys.argv[1])
S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([40, 40, S - 40, S - 40], radius=200, fill=(23, 24, 26, 255), outline=(52, 55, 59, 255), width=16)
accent = (43, 179, 163, 255)
muted = (120, 126, 133, 255)
base = 760
w = 128
xs = [252, 448, 644]
heights = [250, 470, 340]
colors = [muted, accent, muted]
for x, h, c in zip(xs, heights, colors):
    d.rounded_rectangle([x, base - h, x + w, base], radius=36, fill=c)
d.rounded_rectangle([232, base + 52, S - 232, base + 84], radius=16, fill=(70, 74, 79, 255))

png32 = img.resize((32, 32), Image.LANCZOS)
png32.save(out / "AppIcon-32.png")
img.resize((256, 256), Image.LANCZOS).save(out / "AppIcon-256.png")
img.save(out / "ShiftControl.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok")
