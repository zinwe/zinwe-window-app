"""
Regenerate the installer's artwork (the MSI wizard screens) from the brand assets (needs pillow):

    python design/make_installer_art.py

Reads src/Zinwe.Agent.Tray/Assets (zinwe-mark.png, zinwe-logo.png; made by make_tray_brand.py) and writes:

    installer/art/dialog.bmp   493x312  welcome + finish screens: an orange brand panel with the white Z on the
                               left (WiX draws the title and text on the white area to its right)
    installer/art/banner.bmp   493x58   top strip of the other screens: white, the Zinwe wordmark on the right
                               (WiX draws the step title and description on the left)

Sizes are WixUI's (WixUIDialogBmp / WixUIBannerBmp in installer/Package.wxs). 24-bit BMP, no alpha.
"""
from __future__ import annotations

from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
ASSETS = HERE.parent / "src" / "Zinwe.Agent.Tray" / "Assets"
OUT = HERE.parent / "installer" / "art"

ORANGE = (253, 109, 2)   # #FD6D02, the logo orange
WHITE = (255, 255, 255)
LINE = (232, 232, 230)   # hairline under the banner, like the website's borders

DIALOG = (493, 312)
# WixUI's welcome/finish text starts at 135 dialog units = ~180 px (370 DU = 493 px); keep the panel clear of it.
PANEL_W = 164
BANNER = (493, 58)


def white_version(rgba: Image.Image) -> Image.Image:
    """The same shape in white (keeps the alpha), for the orange panel."""
    white = Image.new("RGBA", rgba.size, WHITE + (255,))
    white.putalpha(rgba.getchannel("A"))
    return white


def fit(img: Image.Image, max_w: int, max_h: int) -> Image.Image:
    scale = min(max_w / img.width, max_h / img.height)
    return img.resize((max(1, round(img.width * scale)), max(1, round(img.height * scale))), Image.LANCZOS)


def dialog() -> Image.Image:
    canvas = Image.new("RGB", DIALOG, WHITE)
    canvas.paste(Image.new("RGB", (PANEL_W, DIALOG[1]), ORANGE), (0, 0))
    mark = fit(white_version(Image.open(ASSETS / "zinwe-mark.png").convert("RGBA")), 84, 84)
    x = (PANEL_W - mark.width) // 2
    y = 46
    canvas.paste(mark, (x, y), mark)
    return canvas


def banner() -> Image.Image:
    canvas = Image.new("RGB", BANNER, WHITE)
    logo = fit(Image.open(ASSETS / "zinwe-logo.png").convert("RGBA"), 96, 24)
    canvas.paste(logo, (BANNER[0] - logo.width - 18, (BANNER[1] - logo.height) // 2), logo)
    for x in range(BANNER[0]):
        canvas.putpixel((x, BANNER[1] - 1), LINE)
    return canvas


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    dialog().save(OUT / "dialog.bmp")
    banner().save(OUT / "banner.bmp")
    print(f"wrote {OUT / 'dialog.bmp'} and {OUT / 'banner.bmp'}")


if __name__ == "__main__":
    main()
