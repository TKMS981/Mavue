"""Generates the small committed Quick View test images that Windows cannot encode itself
(WIC has no WebP/AVIF encoder). Output is a synthetic gradient + noise pattern created by this
script; no third-party image content is involved. Requires Pillow (MIT-CMU license) at dev time only.

Usage: python tools/assets/generate_quickview_assets.py
"""
from pathlib import Path

import numpy as np
from PIL import Image, features

OUT = Path(__file__).resolve().parents[2] / "tests" / "assets" / "quickview"
W, H = 1600, 1200


def pattern() -> Image.Image:
    rng = np.random.default_rng(20261002)
    x = np.linspace(0, 255, W, dtype=np.float32)[None, :].repeat(H, 0)
    y = np.linspace(0, 255, H, dtype=np.float32)[:, None].repeat(W, 1)
    noise = rng.integers(0, 64, size=(H, W), dtype=np.uint8).astype(np.float32)
    rgb = np.stack([x, y, 96 + noise], axis=-1).clip(0, 255).astype(np.uint8)
    return Image.fromarray(rgb, "RGB")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    img = pattern()
    img.save(OUT / "sample.webp", "WEBP", quality=80, method=4)
    if features.check("avif"):
        img.save(OUT / "sample.avif", "AVIF", quality=70)
    for p in sorted(OUT.iterdir()):
        print(p.name, p.stat().st_size)


if __name__ == "__main__":
    main()
