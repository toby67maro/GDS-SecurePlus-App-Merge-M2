"""Builds the GDS branding assets of the desktop app, installer and MSIX package from the logos in the Documents repo.

Usage: python3 build/branding/make_branding.py <Documents/Logos folder>
Proof-of-concept artwork: the globe of the Global Data Systems logo on a navy tile, the full logo for the title bar
(navy and white versions) and the installer wizard images.
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

LOGOS = sys.argv[1] if len(sys.argv) > 1 else "/home/user/Documents/Logos"
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DESKTOP = os.path.join(ROOT, "src", "EngineeringWorkspace.Desktop", "Assets")
INSTALLER = os.path.join(ROOT, "packaging", "installer")
MSIX = os.path.join(ROOT, "packaging", "msix", "Assets")
NAVY = (11, 42, 91, 255)
os.makedirs(DESKTOP, exist_ok=True)

logo = Image.open(os.path.join(LOGOS, "Global-Data-Systems-logo.png.webp")).convert("RGBA")

# The globe: a circle centred at about (1178, 652), radius 290, in the 2880 x 1237 logo.
cx, cy, radius = 1178, 652, 289
globe = logo.crop((cx - radius, cy - radius, cx + radius, cy + radius))
mask = Image.new("L", globe.size, 0)
ImageDraw.Draw(mask).ellipse((2, 2, globe.size[0] - 3, globe.size[1] - 3), fill=255)
globe.putalpha(Image.composite(globe.getchannel("A"), Image.new("L", globe.size, 0), mask))


def tile(size, rounded=True, background=NAVY, ring=True):
    """The app icon: the globe on a navy rounded tile with a thin white ring (reads on light and dark taskbars)."""
    scale = 4
    big = size * scale
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    corner = int(big * 0.22) if rounded else 0
    draw.rounded_rectangle((0, 0, big - 1, big - 1), radius=corner, fill=background)
    inset = int(big * 0.10)
    if ring:
        draw.ellipse((inset, inset, big - inset, big - inset), fill=(255, 255, 255, 255))
    g = globe.resize((big - 2 * inset - int(big * 0.05), big - 2 * inset - int(big * 0.05)), Image.LANCZOS)
    offset = (big - g.size[0]) // 2
    canvas.alpha_composite(g, (offset, offset))
    return canvas.resize((size, size), Image.LANCZOS)


# Windows icon (title bar, taskbar, Alt+Tab, Programs and Features, shortcuts).
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
images = [tile(s) for s in sizes]
images[-1].save(os.path.join(DESKTOP, "gds.ico"), sizes=[(s, s) for s in sizes], append_images=images[:-1])
tile(256).save(os.path.join(DESKTOP, "gds-icon-256.png"))

# Full logo for the header: trimmed, navy (light headers) and white (dark headers).
bbox = logo.getbbox()
full = logo.crop(bbox)
full.thumbnail((720, 320), Image.LANCZOS)
full.save(os.path.join(DESKTOP, "gds-logo.png"))
white = full.copy()
pixels = white.load()
for y in range(white.size[1]):
    for x in range(white.size[0]):
        r, g, b, a = pixels[x, y]
        if a == 0:
            continue
        # Navy lettering and swooshes become white; the blue globe stays blue so the logo is still recognisable.
        if b < 140 and r < 60:
            pixels[x, y] = (255, 255, 255, a)
        elif r < 120 and b >= 140:
            pixels[x, y] = (114, 225, 245, a) if (x > white.size[0] * 0.485 and y > white.size[1] * 0.68) else (r, g, b, a)
white.save(os.path.join(DESKTOP, "gds-logo-white.png"))

# Installer: modern wizard images (left panel 164x314 at 100% plus 2x) and the small top-right image.
os.makedirs(INSTALLER, exist_ok=True)
for name, (w, h) in (("WizardImage.bmp", (164, 314)), ("WizardImage@2x.bmp", (328, 628))):
    panel = Image.new("RGBA", (w, h), NAVY)
    grad = Image.new("RGBA", (w, h))
    gd = ImageDraw.Draw(grad)
    for y in range(h):
        t = y / h
        gd.line((0, y, w, y), fill=(int(11 + 10 * t), int(42 + 54 * t), int(91 + 39 * t), 255))
    panel.alpha_composite(grad)
    icon = tile(int(w * 0.62), ring=True)
    panel.alpha_composite(icon, ((w - icon.size[0]) // 2, int(h * 0.16)))
    wl = white.copy()
    wl.thumbnail((int(w * 0.86), int(h * 0.3)), Image.LANCZOS)
    panel.alpha_composite(wl, ((w - wl.size[0]) // 2, int(h * 0.62)))
    panel.convert("RGB").save(os.path.join(INSTALLER, name))
for name, s in (("WizardSmallImage.bmp", 55), ("WizardSmallImage@2x.bmp", 110)):
    small = Image.new("RGBA", (s, s), (255, 255, 255, 255))
    small.alpha_composite(tile(s))
    small.convert("RGB").save(os.path.join(INSTALLER, name))
images[-1].save(os.path.join(INSTALLER, "gds.ico"), sizes=[(x, x) for x in sizes], append_images=images[:-1])

# MSIX tiles.
os.makedirs(MSIX, exist_ok=True)
for name, (w, h) in {"Square44x44Logo.png": (44, 44), "Square150x150Logo.png": (150, 150), "StoreLogo.png": (50, 50), "Wide310x150Logo.png": (310, 150)}.items():
    canvas = Image.new("RGBA", (w, h), NAVY)
    icon = tile(min(w, h) - (0 if w == h else 20))
    canvas.alpha_composite(icon, ((w - icon.size[0]) // 2, (h - icon.size[1]) // 2))
    canvas.save(os.path.join(MSIX, name))
print("Branding assets written.")
