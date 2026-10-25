#!/usr/bin/env python3
"""Prepare transparent MynAssistant branding for Windows packaging.

Source of truth: assets/branding/mynassistant-icon-source.png
Outputs:
  src/MynAI.Windows/Resources/mynassistant-logo.png
  src/MynAI.Windows/Resources/myn.ico
  build/installer/MynAssistantWizardSmall.bmp

This deliberately avoids System.Drawing.Icon.ToBitmap(), which fails on some
valid multi-resolution ICO files on Windows. Pillow renders the PNG source
directly for each output format.
"""
from __future__ import annotations

from collections import deque
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets" / "branding" / "mynassistant-icon-source.png"
RESOURCES = ROOT / "src" / "MynAI.Windows" / "Resources"
LOGO_OUT = RESOURCES / "mynassistant-logo.png"
ICON_OUT = RESOURCES / "myn.ico"
WIZARD_OUT = ROOT / "build" / "installer" / "MynAssistantWizardSmall.bmp"


def remove_connected_light_background(image: Image.Image) -> Image.Image:
    """Clear only near-white pixels connected to the image edge.

    If the source already has meaningful transparency, preserve it. Otherwise
    a four-neighbour flood fill removes a flat white/off-white canvas without
    erasing the white center emblem, which is separated from the edge by blue
    ribbon geometry.
    """
    image = image.convert("RGBA")
    alpha = image.getchannel("A")
    if alpha.getextrema()[0] < 255:
        return image

    width, height = image.size
    pixels = image.load()
    visited = bytearray(width * height)
    queue: deque[tuple[int, int]] = deque()

    def is_background(x: int, y: int) -> bool:
        r, g, b, a = pixels[x, y]
        return a > 0 and min(r, g, b) >= 218 and max(r, g, b) - min(r, g, b) <= 45

    # Start only at pale pixels touching the outer edge. This keeps internal
    # white details that are enclosed by the logo silhouette.
    for x in range(width):
        for y in (0, height - 1):
            if is_background(x, y):
                idx = y * width + x
                if not visited[idx]:
                    visited[idx] = 1
                    queue.append((x, y))
    for y in range(height):
        for x in (0, width - 1):
            if is_background(x, y):
                idx = y * width + x
                if not visited[idx]:
                    visited[idx] = 1
                    queue.append((x, y))

    while queue:
        x, y = queue.popleft()
        r, g, b, _ = pixels[x, y]
        pixels[x, y] = (r, g, b, 0)
        for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if nx < 0 or ny < 0 or nx >= width or ny >= height:
                continue
            idx = ny * width + nx
            if not visited[idx] and is_background(nx, ny):
                visited[idx] = 1
                queue.append((nx, ny))

    return image


def square_logo(image: Image.Image, size: int, margin_ratio: float = 0.045) -> Image.Image:
    image = image.convert("RGBA")
    alpha = image.getchannel("A")
    bbox = alpha.getbbox()
    if not bbox:
        raise RuntimeError("Brand source contains no visible pixels.")
    image = image.crop(bbox)

    width, height = image.size
    side = max(width, height)
    centered = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    centered.alpha_composite(image, ((side - width) // 2, (side - height) // 2))

    margin = max(2, round(size * margin_ratio))
    inner = size - margin * 2
    resized = centered.resize((inner, inner), Image.Resampling.LANCZOS)
    result = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    result.alpha_composite(resized, (margin, margin))
    return result


def main() -> int:
    if not SOURCE.is_file():
        raise FileNotFoundError(f"Brand source missing: {SOURCE}")

    RESOURCES.mkdir(parents=True, exist_ok=True)
    WIZARD_OUT.parent.mkdir(parents=True, exist_ok=True)

    with Image.open(SOURCE) as opened:
        base = opened.convert("RGBA")
    base = remove_connected_light_background(base)

    # A 512-pixel master is sharp for WPF, while ICO gets native multi-size
    # renditions for taskbar, shortcuts, File Explorer, and installer chrome.
    logo = square_logo(base, 512)
    logo.save(LOGO_OUT, format="PNG", optimize=True, compress_level=9)
    logo.save(
        ICON_OUT,
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    # Inno Setup's modern wizard requires a 55x58 bitmap. Render directly from
    # the transparent PNG onto white; never decode the ICO via GDI+.
    wizard = square_logo(base, 50, margin_ratio=0.02)
    canvas = Image.new("RGBA", (55, 58), (255, 255, 255, 255))
    wizard = wizard.resize((48, 50), Image.Resampling.LANCZOS)
    canvas.alpha_composite(wizard, ((55 - 48) // 2, (58 - 50) // 2))
    canvas.convert("RGB").save(WIZARD_OUT, format="BMP")

    # Basic guards against accidental empty/opaque outputs.
    with Image.open(LOGO_OUT) as check:
        if check.size != (512, 512) or check.mode != "RGBA":
            raise RuntimeError("Transparent logo output has unexpected format.")
        if check.getchannel("A").getextrema()[0] != 0:
            raise RuntimeError("Transparent logo output has no transparent pixels.")
    if not ICON_OUT.is_file() or ICON_OUT.stat().st_size < 1024:
        raise RuntimeError("Windows ICO output is missing or too small.")
    if not WIZARD_OUT.is_file() or WIZARD_OUT.stat().st_size < 100:
        raise RuntimeError("Installer wizard logo output is missing or too small.")

    print(f"Prepared transparent logo: {LOGO_OUT.relative_to(ROOT)} ({LOGO_OUT.stat().st_size} bytes)")
    print(f"Prepared Windows icon: {ICON_OUT.relative_to(ROOT)} ({ICON_OUT.stat().st_size} bytes)")
    print(f"Prepared installer wizard bitmap: {WIZARD_OUT.relative_to(ROOT)} ({WIZARD_OUT.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
