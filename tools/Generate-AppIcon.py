#!/usr/bin/env python3
"""Render SonarMiniMixer's source SVG into application PNG and multi-frame ICO assets."""

from __future__ import annotations

import io
import struct
from pathlib import Path

import cairosvg
from PIL import Image

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
ROOT = Path(__file__).resolve().parents[1]
SVG = ROOT / "SonarMiniMixer.App" / "Assets" / "AppIcon.svg"
PNG = ROOT / "SonarMiniMixer.App" / "Assets" / "AppIcon.png"
ICO = ROOT / "SonarMiniMixer.App" / "Assets" / "AppIcon.ico"


def render_png(size: int) -> bytes:
    output = io.BytesIO()
    cairosvg.svg2png(
        url=str(SVG),
        write_to=output,
        output_width=size,
        output_height=size,
    )
    return output.getvalue()


def write_ico(images: list[tuple[int, bytes]]) -> None:
    header_size = 6 + 16 * len(images)
    offset = header_size
    entries: list[bytes] = []
    payloads: list[bytes] = []

    for size, payload in images:
        dimension = 0 if size == 256 else size
        entries.append(
            struct.pack(
                "<BBBBHHII",
                dimension,
                dimension,
                0,
                0,
                1,
                32,
                len(payload),
                offset,
            )
        )
        payloads.append(payload)
        offset += len(payload)

    ICO.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + b"".join(entries) + b"".join(payloads))


def main() -> None:
    images = [(size, render_png(size)) for size in SIZES]
    PNG.write_bytes(images[-1][1])
    write_ico(images)

    with Image.open(ICO) as icon:
        embedded_sizes = sorted(size[0] for size in icon.info.get("sizes", set()))
    if embedded_sizes != list(SIZES):
        raise RuntimeError(f"ICO sizes do not match: {embedded_sizes}")

    print(f"Wrote {PNG.relative_to(ROOT)} (256x256)")
    print(f"Wrote {ICO.relative_to(ROOT)} ({', '.join(map(str, embedded_sizes))} px)")


if __name__ == "__main__":
    main()
