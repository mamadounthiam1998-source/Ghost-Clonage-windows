"""Génère l'icône GHOST CLONAGE WINDOWS (src/Ghost.App/Assets/ghost.ico). Usage : python tools/make-icon.py"""
from PIL import Image, ImageDraw, ImageFilter
import os

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Fond : carré arrondi noir/anthracite
d.rounded_rectangle((32, 32, S - 32, S - 32), radius=220, fill=(10, 14, 22, 255), outline=(30, 58, 82, 255), width=10)

# Halo cyan derrière le fantôme
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
g = ImageDraw.Draw(glow)
g.ellipse((200, 180, 824, 800), fill=(0, 200, 255, 140))
glow = glow.filter(ImageFilter.GaussianBlur(70))
img = Image.alpha_composite(img, glow)
d = ImageDraw.Draw(img)

# Fantôme : dôme + corps + bas ondulé
cx = S // 2
top, bottom = 230, 780
r = 215
body = [(cx - r, top + r)]
d.pieslice((cx - r, top, cx + r, top + 2 * r), 180, 360, fill=(220, 248, 255, 255))
d.rectangle((cx - r, top + r, cx + r, bottom - 60), fill=(220, 248, 255, 255))
waves = 4
w = (2 * r) / waves
for i in range(waves):   # bas ondulé : demi-disques tournés vers le bas
    x0 = cx - r + i * w
    d.pieslice((x0, bottom - 60 - w / 2, x0 + w, bottom - 60 + w / 2), 0, 180, fill=(220, 248, 255, 255))

# Yeux
d.ellipse((cx - 110, 440, cx - 50, 520), fill=(10, 14, 22, 255))
d.ellipse((cx + 50, 440, cx + 110, 520), fill=(10, 14, 22, 255))
d.ellipse((cx - 92, 458, cx - 70, 482), fill=(0, 220, 255, 255))
d.ellipse((cx + 68, 458, cx + 90, 482), fill=(0, 220, 255, 255))

os.makedirs("src/Ghost.App/Assets", exist_ok=True)
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
img.save("src/Ghost.App/Assets/ghost.ico", sizes=sizes)
img.resize((512, 512), Image.LANCZOS).save("src/Ghost.App/Assets/ghost.png")
print("OK")
