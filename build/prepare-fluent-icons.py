# SPDX-License-Identifier: GPL-3.0-only
"""
Prepares Fluent UI System Icons' FluentSystemIcons-Resizable.ttf for src/EncoDeck.App/Assets/Fonts.

The upstream font draws every icon on a 500-unit square (the 20 px design grid) and adds a 45-unit line gap, so
a FontIcon's line box is taller than wide: in a square slot (NavigationViewItem's 16 px Viewbox) the icon shrinks and
sits high. This crops the em to the 16 px design area (the 400 units around the centre) and gives the font Segoe
Fluent Icons' metrics (ascent = em, no descent or gap), so a Fluent icon at a given FontSize fills and centres like a
Segoe one. Outlines are only moved, never changed; ink in the 20 px grid's outer 2 px stays drawn as overhang.

  python build/prepare-fluent-icons.py <upstream FluentSystemIcons-Resizable.ttf> <output .ttf>

Needs fontTools. Copy the upstream FluentSystemIcons-Resizable.json next to the output unchanged.
"""

import sys

from fontTools.pens.recordingPen import DecomposingRecordingPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont

SOURCE_EM = 500
EM = 400
SHIFT = (SOURCE_EM - EM) // 2


def main(source: str, target: str) -> None:
    font = TTFont(source)
    if font['head'].unitsPerEm != SOURCE_EM:
        raise SystemExit(f"Expected a {SOURCE_EM}-unit em, found {font['head'].unitsPerEm}.")
    glyph_set = font.getGlyphSet()
    glyf, hmtx = font['glyf'], font['hmtx']
    moved = {}
    for name in font.getGlyphOrder():
        recording = DecomposingRecordingPen(glyph_set)
        glyph_set[name].draw(recording)
        pen = TTGlyphPen(None)
        recording.replay(TransformPen(pen, (1, 0, 0, 1, -SHIFT, -SHIFT)))
        moved[name] = pen.glyph()
    for name, glyph in moved.items():
        glyf[name] = glyph
        glyph.recalcBounds(glyf)
        advance, _ = hmtx[name]
        hmtx[name] = (EM if advance == SOURCE_EM else max(advance - 2 * SHIFT, 0), getattr(glyph, 'xMin', 0))

    font['head'].unitsPerEm = EM
    hhea, os2 = font['hhea'], font['OS/2']
    hhea.ascent, hhea.descent, hhea.lineGap = EM, 0, 0
    os2.sTypoAscender, os2.sTypoDescender, os2.sTypoLineGap = EM, 0, 0
    os2.usWinAscent, os2.usWinDescent = EM, 0
    font['maxp'].recalc(font)
    font.save(target)


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
