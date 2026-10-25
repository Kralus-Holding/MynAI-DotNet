from __future__ import annotations

from pathlib import Path
from PIL import Image, ImageOps

ROOT = Path(__file__).resolve().parents[1]
LOGO = ROOT / "src" / "MynAI.Windows" / "Resources" / "mynassistant-logo.png"
ICON = ROOT / "src" / "MynAI.Windows" / "Resources" / "myn.ico"
WIZARD = ROOT / "build" / "installer" / "MynAssistantWizardSmall.bmp"


def main() -> None:
    if not LOGO.is_file():
        raise FileNotFoundError(f"Shared MynAssistant logo is missing: {LOGO}")
    ICON.parent.mkdir(parents=True, exist_ok=True)
    WIZARD.parent.mkdir(parents=True, exist_ok=True)

    with Image.open(LOGO) as opened:
        image = ImageOps.exif_transpose(opened).convert("RGBA")
        if image.width < 32 or image.height < 32:
            raise ValueError("Shared MynAssistant logo must be at least 32x32 pixels.")
        image.save(
            ICON, format="ICO",
            sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
        )
        contained = ImageOps.contain(image, (48, 50), method=Image.Resampling.LANCZOS)
        canvas = Image.new("RGB", (55, 58), "white")
        canvas.paste(contained, ((55 - contained.width) // 2, (58 - contained.height) // 2), contained)
        canvas.save(WIZARD, format="BMP")

    for path in (ICON, WIZARD):
        if not path.is_file() or path.stat().st_size < 100:
            raise RuntimeError(f"Branding asset generation failed: {path}")
    print(f"Application icon generated: {ICON} ({ICON.stat().st_size} bytes)")
    print(f"Installer wizard image generated: {WIZARD} ({WIZARD.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
