from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "assets" / "branding" / "mynassistant-icon-source.png"
RESOURCE_DIR = ROOT / "src" / "MynAI.Windows" / "Resources"
ICO_PATH = RESOURCE_DIR / "myn.ico"
WINDOW_IMAGE_PATH = RESOURCE_DIR / "myn.jpg"
WIZARD_PATH = ROOT / "build" / "installer" / "MynAssistantWizardSmall.bmp"


def main() -> None:
    if not SOURCE.is_file():
        raise FileNotFoundError(f"New MynAssistant branding source is missing: {SOURCE}")

    RESOURCE_DIR.mkdir(parents=True, exist_ok=True)
    WIZARD_PATH.parent.mkdir(parents=True, exist_ok=True)

    with Image.open(SOURCE) as opened:
        source = opened.convert("RGBA")
        # Use one deterministic square artwork for the executable, tray, WPF,
        # settings page and installer. Padding/cropping is centralized here.
        square = ImageOps.fit(source, (512, 512), method=Image.Resampling.LANCZOS)

        # WPF window and settings icon are loaded as JPEG to avoid the historic
        # WPF ICO decoder startup crash. Composite transparency onto navy first.
        rgb = Image.new("RGB", square.size, (5, 10, 32))
        rgb.paste(square, mask=square.getchannel("A"))
        rgb.resize((256, 256), Image.Resampling.LANCZOS).save(
            WINDOW_IMAGE_PATH, format="JPEG", quality=94, optimize=True
        )

        # Generate a clean standards-compliant multi-size ICO for the app EXE,
        # system tray and Inno Setup. Never convert the legacy ICO via GDI+.
        square.save(
            ICO_PATH,
            format="ICO",
            sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
        )

        # Inno Setup's WizardSmallImageFile must be exactly 55x58 pixels.
        content = ImageOps.contain(rgb, (48, 50), method=Image.Resampling.LANCZOS)
        wizard = Image.new("RGB", (55, 58), "white")
        wizard.paste(content, ((55 - content.width) // 2, (58 - content.height) // 2))
        wizard.save(WIZARD_PATH, format="BMP")

    for path in (ICO_PATH, WINDOW_IMAGE_PATH, WIZARD_PATH):
        if not path.is_file() or path.stat().st_size < 100:
            raise RuntimeError(f"Generated branding asset is empty: {path}")

    with Image.open(ICO_PATH) as icon:
        if icon.format != "ICO" or icon.size[0] < 16:
            raise RuntimeError(f"Generated Windows icon is invalid: {ICO_PATH}")
    with Image.open(WIZARD_PATH) as wizard:
        if wizard.format != "BMP" or wizard.size != (55, 58):
            raise RuntimeError(f"Generated Inno wizard bitmap is invalid: {WIZARD_PATH}")

    print(f"Generated new application/tray icon: {ICO_PATH} ({ICO_PATH.stat().st_size} bytes)")
    print(f"Generated new WPF/settings image: {WINDOW_IMAGE_PATH} ({WINDOW_IMAGE_PATH.stat().st_size} bytes)")
    print(f"Generated Inno Setup wizard bitmap: {WIZARD_PATH} ({WIZARD_PATH.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
