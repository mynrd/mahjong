"""
Draws the eight flower and season tiles.

A riichi tile set has no flowers or seasons, so the CC0 artwork this project uses for the other
34 faces stops short of them. These eight are drawn here instead of being pulled from a second,
differently-licensed set.

They are deliberately plain. In Filipino Mahjong a bonus tile is turned face up the moment it is
drawn and never takes part in a hand, so all a player has to do is recognise it and count it.
The motifs are vector shapes rather than the traditional Chinese characters on purpose: a glyph
would depend on a CJK font being installed, which is not true of every phone that will open this.

    python _generate-bonus-tiles.py
"""

import io
import os

W, H = 300, 400

CREAM = "#FBF7EC"
EDGE = "#CFC1A4"
INK = "#2E2A24"

# Flowers are numbered in green, seasons in red, which is how a physical set separates them.
FLOWER_NUMERAL = "#1F7A3D"
SEASON_NUMERAL = "#C0392B"


def tile(motif: str, numeral: str, numeral_colour: str, label: str) -> str:
    """
    Face art only, on a transparent background, in the same 300x400 box the CC0 tiles use.

    The tile body - the cream rounded rectangle - is drawn once by the tile component in the web
    app and sits behind whichever face is shown. Baking a body into these eight would make them
    the only tiles carrying their own, and they would not match the other 34.

    There is no written name on the face. A bonus tile is shown at about 20px across in the row in
    front of a player, where a word would render at roughly two pixels tall and read as smudge.
    The numeral and the motif carry it instead, with flowers numbered in green and seasons in red,
    which is how a physical set tells the two apart.
    """
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">
  <title>{label}</title>
  <text x="54" y="76" font-family="Segoe UI, Helvetica, Arial, sans-serif" font-size="66"
        font-weight="700" fill="{numeral_colour}" text-anchor="middle">{numeral}</text>
  <g transform="translate(150 226) scale(1.42)">
{motif}
  </g>
</svg>
"""


def petals(count: int, rx: float, ry: float, distance: float, colour: str, stroke: str) -> str:
    out = []
    for i in range(count):
        angle = i * 360 / count
        out.append(
            f'    <ellipse cx="0" cy="{-distance}" rx="{rx}" ry="{ry}" fill="{colour}" '
            f'stroke="{stroke}" stroke-width="3" transform="rotate({angle})"/>'
        )
    return "\n".join(out)


PLUM = (
    petals(5, 30, 34, 40, "#E8748C", "#B23A55")
    + '\n    <circle cx="0" cy="0" r="20" fill="#F4C542" stroke="#B8860B" stroke-width="3"/>'
)

ORCHID = """    <path d="M -6 60 C -70 20 -84 -40 -54 -84" fill="none" stroke="#2E7D32" stroke-width="10" stroke-linecap="round"/>
    <path d="M 6 60 C 70 24 82 -30 58 -78" fill="none" stroke="#2E7D32" stroke-width="10" stroke-linecap="round"/>
    <ellipse cx="-26" cy="-24" rx="22" ry="30" fill="#9B59B6" stroke="#6C3483" stroke-width="3" transform="rotate(-28 -26 -24)"/>
    <ellipse cx="26" cy="-24" rx="22" ry="30" fill="#9B59B6" stroke="#6C3483" stroke-width="3" transform="rotate(28 26 -24)"/>
    <ellipse cx="0" cy="8" rx="20" ry="26" fill="#BB8FCE" stroke="#6C3483" stroke-width="3"/>
    <circle cx="0" cy="-14" r="12" fill="#F4C542" stroke="#B8860B" stroke-width="3"/>"""

CHRYSANTHEMUM = (
    petals(12, 15, 40, 42, "#F0A830", "#B9770E")
    + "\n"
    + petals(8, 12, 26, 22, "#F6C667", "#B9770E")
    + '\n    <circle cx="0" cy="0" r="16" fill="#8B4513" stroke="#5D2E0C" stroke-width="3"/>'
)

BAMBOO = """    <rect x="-44" y="-90" width="30" height="176" rx="10" fill="#2E7D32" stroke="#1B5E20" stroke-width="4"/>
    <rect x="16" y="-56" width="26" height="142" rx="9" fill="#43A047" stroke="#1B5E20" stroke-width="4"/>
    <line x1="-44" y1="-34" x2="-14" y2="-34" stroke="#1B5E20" stroke-width="5"/>
    <line x1="-44" y1="26" x2="-14" y2="26" stroke="#1B5E20" stroke-width="5"/>
    <line x1="16" y1="-6" x2="42" y2="-6" stroke="#1B5E20" stroke-width="5"/>
    <line x1="16" y1="46" x2="42" y2="46" stroke="#1B5E20" stroke-width="5"/>
    <path d="M -14 -60 C 30 -96 62 -92 84 -74 C 52 -54 16 -54 -14 -60 Z" fill="#66BB6A" stroke="#1B5E20" stroke-width="4"/>
    <path d="M -44 -18 C -88 -54 -104 -34 -102 -12 C -76 -6 -60 -8 -44 -18 Z" fill="#66BB6A" stroke="#1B5E20" stroke-width="4"/>"""

SPRING = """    <path d="M 0 90 L 0 -30" stroke="#2E7D32" stroke-width="12" stroke-linecap="round"/>
    <path d="M 0 22 C -70 12 -84 -40 -30 -50 C 6 -44 8 -6 0 22 Z" fill="#66BB6A" stroke="#1B5E20" stroke-width="4"/>
    <path d="M 0 -4 C 70 -14 84 -66 30 -76 C -6 -70 -8 -32 0 -4 Z" fill="#43A047" stroke="#1B5E20" stroke-width="4"/>
    <circle cx="0" cy="-56" r="16" fill="#F4C542" stroke="#B8860B" stroke-width="3"/>"""

SUMMER = (
    "\n".join(
        f'    <rect x="-7" y="-104" width="14" height="34" rx="7" fill="#E67E22" transform="rotate({i * 45})"/>'
        for i in range(8)
    )
    + '\n    <circle cx="0" cy="0" r="56" fill="#F5B041" stroke="#CA6F1E" stroke-width="6"/>'
)

AUTUMN = """    <path d="M 0 96 C -6 40 -6 10 0 -20" stroke="#7E5109" stroke-width="10" stroke-linecap="round" fill="none"/>
    <path d="M 0 -22 C -84 -22 -92 -96 -46 -110 C -14 -118 0 -80 0 -22 Z" fill="#D35400" stroke="#7E5109" stroke-width="4"/>
    <path d="M 0 -22 C 84 -22 92 -96 46 -110 C 14 -118 0 -80 0 -22 Z" fill="#E67E22" stroke="#7E5109" stroke-width="4"/>
    <path d="M 0 6 C -50 -6 -70 -46 -60 -72" stroke="#7E5109" stroke-width="4" fill="none" opacity="0.7"/>
    <path d="M 0 6 C 50 -6 70 -46 60 -72" stroke="#7E5109" stroke-width="4" fill="none" opacity="0.7"/>"""

WINTER = (
    "\n".join(
        f"""    <g transform="rotate({i * 60})">
      <line x1="0" y1="0" x2="0" y2="-92" stroke="#2980B9" stroke-width="10" stroke-linecap="round"/>
      <line x1="0" y1="-56" x2="-26" y2="-80" stroke="#2980B9" stroke-width="8" stroke-linecap="round"/>
      <line x1="0" y1="-56" x2="26" y2="-80" stroke="#2980B9" stroke-width="8" stroke-linecap="round"/>
    </g>"""
        for i in range(6)
    )
    + '\n    <circle cx="0" cy="0" r="16" fill="#AED6F1" stroke="#2980B9" stroke-width="5"/>'
)

TILES = [
    ("F1", PLUM, "1", FLOWER_NUMERAL, "Plum"),
    ("F2", ORCHID, "2", FLOWER_NUMERAL, "Orchid"),
    ("F3", CHRYSANTHEMUM, "3", FLOWER_NUMERAL, "Chrys"),
    ("F4", BAMBOO, "4", FLOWER_NUMERAL, "Bamboo"),
    ("S1", SPRING, "1", SEASON_NUMERAL, "Spring"),
    ("S2", SUMMER, "2", SEASON_NUMERAL, "Summer"),
    ("S3", AUTUMN, "3", SEASON_NUMERAL, "Autumn"),
    ("S4", WINTER, "4", SEASON_NUMERAL, "Winter"),
]


def main() -> None:
    here = os.path.dirname(os.path.abspath(__file__))

    for code, motif, numeral, colour, label in TILES:
        path = os.path.join(here, f"{code}.svg")
        with io.open(path, "w", encoding="utf-8") as handle:
            handle.write(tile(motif, numeral, colour, label))
        print(f"wrote {code}.svg  ({label})")


if __name__ == "__main__":
    main()
