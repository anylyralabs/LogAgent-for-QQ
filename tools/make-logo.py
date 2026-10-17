# -*- coding: utf-8 -*-
"""把 logo jpg 处理成应用图标资源：
- logo.ico：多尺寸（16~256），白底原图（任务栏/深色底可见）
- logo.png：透明底 256px（浅色 UI 内嵌用）
"""
import os
from PIL import Image

SRC = r"C:\Users\Administrator\Pictures\260446645.jpg"
OUT_DIR = r"E:\logagent-for-qq\src\LogAgent.App\Assets"
os.makedirs(OUT_DIR, exist_ok=True)

img = Image.open(SRC).convert("RGBA")

# 1) 白底 ico：先缩放到 256 方形（等比缩放后居中到白底方画布）
side = max(img.size)
square = Image.new("RGBA", (side, side), (255, 255, 255, 255))
square.paste(img, ((side - img.width) // 2, (side - img.height) // 2))
base = square.resize((256, 256), Image.LANCZOS)
ico_path = os.path.join(OUT_DIR, "logo.ico")
base.save(ico_path, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ICO:", ico_path, os.path.getsize(ico_path), "bytes")

# 2) 透明底 png：亮度→alpha（黑图形 alpha=255，白底 alpha=0，边缘自然过渡）
rgba = base.convert("RGBA")
pixels = rgba.load()
for y in range(rgba.height):
    for x in range(rgba.width):
        r, g, b, a = pixels[x, y]
        lum = (r * 299 + g * 587 + b * 114) // 1000
        alpha = 255 - lum if lum < 250 else 0
        pixels[x, y] = (0, 0, 0, alpha)
png_path = os.path.join(OUT_DIR, "logo.png")
rgba.save(png_path, format="PNG")
print("PNG:", png_path, os.path.getsize(png_path), "bytes")

# 3) 透明底大图（向导页用 128px）
wiz = rgba.resize((128, 128), Image.LANCZOS)
wiz_path = os.path.join(OUT_DIR, "logo-128.png")
wiz.save(wiz_path, format="PNG")
print("PNG128:", wiz_path, os.path.getsize(wiz_path), "bytes")
