from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "assets" / "branding" / "mynassistant-icon-source.png"
RESOURCE_DIR = ROOT / "src" / "MynAI.Windows" / "Resources"
TRANSPARENT_PNG_PATH = RESOURCE_DIR / "myn.png"
DARK_PNG_PATH = RESOURCE_DIR / "myn-dark.png"
ICO_PATH = RESOURCE_DIR / "myn.ico"
WINDOW_IMAGE_PATH = RESOURCE_DIR / "myn.jpg"
WIZARD_PATH = ROOT / "build" / "installer" / "MynAssistantWizardSmall.bmp"
DARK_BACKGROUND = (5, 10, 22, 255)


def remove_edge_connected_white_background(image: Image.Image) -> Image.Image:
    """Remove near-white pixels connected to image edges, preserving internal white artwork."""
    rgba = image.convert("RGBA")
    alpha_min, _ = rgba.getchannel("A").getextrema()
    if alpha_min < 255:
        return rgba

    pixels = rgba.load()
    width, height = rgba.size
    visited = bytearray(width * height)
    stack: list[int] = []

    def is_background(index: int) -> bool:
        x, y = index % width, index // width
        r, g, b, a = pixels[x, y]
        return a > 0 and min(r, g, b) >= 220 and max(r, g, b) - min(r, g, b) <= 35

    def add_if_background(x: int, y: int) -> None:
        index = y * width + x
        if not visited[index] and is_background(index):
            visited[index] = 1
            stack.append(index)

    for x in range(width):
        add_if_background(x, 0)
        add_if_background(x, height - 1)
    for y in range(height):
        add_if_background(0, y)
        add_if_background(width - 1, y)

    while stack:
        index = stack.pop()
        x, y = index % width, index // width
        if x > 0:
            add_if_background(x - 1, y)
        if x + 1 < width:
            add_if_background(x + 1, y)
        if y > 0:
            add_if_background(x, y - 1)
        if y + 1 < height:
            add_if_background(x, y + 1)

    for index, remove in enumerate(visited):
        if remove:
            x, y = index % width, index // width
            r, g, b, _ = pixels[x, y]
            pixels[x, y] = (r, g, b, 0)

    return rgba


def main() -> None:
    if not SOURCE.is_file():
        raise FileNotFoundError(f"New MynAssistant branding source is missing: {SOURCE}")

    RESOURCE_DIR.mkdir(parents=True, exist_ok=True)
    WIZARD_PATH.parent.mkdir(parents=True, exist_ok=True)

    with Image.open(SOURCE) as opened:
        cleaned = remove_edge_connected_white_background(opened)
        square = ImageOps.fit(
            cleaned,
            (512, 512),
            method=Image.Resampling.LANCZOS,
        )

        # Transparent image for the Windows app/taskbar/tray icon and the Settings preset.
        square.save(TRANSPARENT_PNG_PATH, format="PNG", optimize=True)

        # Separate dark-themed in-app image. Never use the JPEG for the actual Windows icon.
        dark = Image.new("RGBA", square.size, DARK_BACKGROUND)
        dark.alpha_composite(square)
        dark.save(DARK_PNG_PATH, format="PNG", optimize=True)

        # Keep the legacy JPEG as a compatibility fallback for older references.
        dark_rgb = dark.convert("RGB")
        dark_rgb.resize((256, 256), Image.Resampling.LANCZOS).save(
            WINDOW_IMAGE_PATH,
            format="JPEG",
            quality=94,
            optimize=True,
        )

        square.save(
            ICO_PATH,
            format="ICO",
            sizes=[
                (16, 16), (24, 24), (32, 32), (48, 48),
                (64, 64), (128, 128), (256, 256),
            ],
        )

        content = ImageOps.contain(
            dark_rgb,
            (48, 50),
            method=Image.Resampling.LANCZOS,
        )
        wizard = Image.new("RGB", (55, 58), "white")
        wizard.paste(
            content,
            ((55 - content.width) // 2, (58 - content.height) // 2),
        )
        wizard.save(WIZARD_PATH, format="BMP")

    for path in (
        TRANSPARENT_PNG_PATH,
        DARK_PNG_PATH,
        ICO_PATH,
        WINDOW_IMAGE_PATH,
        WIZARD_PATH,
    ):
        if not path.is_file() or path.stat().st_size < 100:
            raise RuntimeError(f"Generated branding asset is empty: {path}")

    with Image.open(TRANSPARENT_PNG_PATH) as transparent:
        if transparent.format != "PNG" or transparent.mode != "RGBA":
            raise RuntimeError(f"Transparent logo is invalid: {TRANSPARENT_PNG_PATH}")
        alpha_min, _ = transparent.getchannel("A").getextrema()
        if alpha_min != 0:
            raise RuntimeError(
                "The branding source did not produce transparent pixels. "
                "Inspect the approved source image before packaging."
            )

    with Image.open(ICO_PATH) as icon:
        if icon.format != "ICO" or icon.size[0] < 16:
            raise RuntimeError(f"Generated Windows icon is invalid: {ICO_PATH}")

    with Image.open(WIZARD_PATH) as wizard:
        if wizard.format != "BMP" or wizard.size != (55, 58):
            raise RuntimeError(f"Generated Inno wizard bitmap is invalid: {WIZARD_PATH}")

    print(f"Generated transparent app/tray logo: {TRANSPARENT_PNG_PATH} ({TRANSPARENT_PNG_PATH.stat().st_size} bytes)")
    print(f"Generated dark-themed in-app logo: {DARK_PNG_PATH} ({DARK_PNG_PATH.stat().st_size} bytes)")
    print(f"Generated Windows icon: {ICO_PATH} ({ICO_PATH.stat().st_size} bytes)")
    print(f"Generated backwards-compatible WPF image: {WINDOW_IMAGE_PATH} ({WINDOW_IMAGE_PATH.stat().st_size} bytes)")
    print(f"Generated Inno Setup wizard bitmap: {WIZARD_PATH} ({WIZARD_PATH.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
