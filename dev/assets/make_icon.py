#!/usr/bin/env python3
"""Generate the application icon (PNG, ICO and ICNS) with Pillow."""

import pathlib
import sys

from PIL import Image, ImageDraw


def draw(size: int) -> Image.Image:
    """Draw the icon: a placeholder bracket around a masked line on a rounded square."""
    scale = 4
    s = size * scale
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=s // 5, fill=(37, 99, 235, 255))

    white = (255, 255, 255, 255)
    w = max(scale, s // 14)
    top, bottom = s * 0.24, s * 0.76
    left, right = s * 0.2, s * 0.8
    arm = s * 0.12
    # [ ... ]
    d.line(
        [(left + arm, top), (left, top), (left, bottom), (left + arm, bottom)],
        fill=white,
        width=w,
        joint="curve",
    )
    d.line(
        [(right - arm, top), (right, top), (right, bottom), (right - arm, bottom)],
        fill=white,
        width=w,
        joint="curve",
    )
    # Masked text: three bars.
    bar_h = s * 0.075
    for i, frac in enumerate((0.38, 0.5, 0.62)):
        y = s * frac - bar_h / 2
        x_end = s * (0.66 if i != 1 else 0.58)
        d.rounded_rectangle(
            (s * 0.34, y, x_end, y + bar_h), radius=bar_h / 2, fill=(255, 255, 255, 230)
        )

    return image.resize((size, size), Image.Resampling.LANCZOS)


def main() -> int:
    """Execute the main routine."""
    repo_root = pathlib.Path(__file__).resolve().parent.parent.parent
    out = repo_root / "src" / "HajdeAno.App" / "Assets"
    out.mkdir(parents=True, exist_ok=True)
    big = draw(512)
    draw(256).save(out / "hajde-ano.png")
    big.save(
        out / "hajde-ano.ico",
        sizes=[
            (16, 16),
            (24, 24),
            (32, 32),
            (48, 48),
            (64, 64),
            (128, 128),
            (256, 256),
        ],
    )
    big.save(repo_root / "dev" / "assets" / "hajde-ano.icns")
    print(f"Wrote icons to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
