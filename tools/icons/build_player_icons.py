"""Player icons: one set of Fluent UI System Icons for the web, phone and TV players.

Fluent UI System Icons (MIT, Copyright (c) 2020 Microsoft Corporation,
github.com/microsoft/fluentui-system-icons) — the family the web UI's DIcon
already uses. From the package's 24 px SVGs this writes:

  src/Animarr.App/Resources/Fonts/fluent-icons.ttf
      the glyphs as a small TrueType font (alias "FluentIcons" in
      MauiProgram) — a MAUI Label draws an icon like text, so it takes
      TextColor / FontSize and a state change is a Text change;
  src/Animarr.App/FluentGlyphs.cs
      the glyphs' code points as C# constants;
  src/Animarr.UI/wwwroot/animarr-player.js
      the web player's path data, between the <fluent-icons> markers.

±10 come in two more pieces each (the arrow, the digits) so the players can
tint the digits with the accent.

Run (fontTools 4.60+):
    npm pack @fluentui/svg-icons@1.1.344 && tar -xzf fluentui-svg-icons-1.1.344.tgz
    python tools/icons/build_player_icons.py <that>/package/icons
New icons go at the END of ICONS so existing code points never move.
"""
import os
import re
import sys

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.cu2quPen import Cu2QuPen
from fontTools.pens.recordingPen import RecordingPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.svgLib.path import parse_path

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
FONT = os.path.join(REPO, 'src', 'Animarr.App', 'Resources', 'Fonts', 'fluent-icons.ttf')
GLYPHS_CS = os.path.join(REPO, 'src', 'Animarr.App', 'FluentGlyphs.cs')
PLAYER_JS = os.path.join(REPO, 'src', 'Animarr.UI', 'wwwroot', 'animarr-player.js')

# (C# name, web player key or None, Fluent icon, digits split: SVG y the
# digits start below, or None). Transport (play / pause / previous / next) is
# filled, the rest regular — as in the Windows Media Player.
ICONS = [
    ('Back',         'back',      'arrow_left_24_regular',            None),
    ('Previous',     'prev',      'previous_24_filled',               None),
    ('Next',         'next',      'next_24_filled',                   None),
    ('Play',         'play',      'play_24_filled',                   None),
    ('Pause',        'pause',     'pause_24_filled',                  None),
    ('SkipBack10',   'back10',    'skip_back_10_24_regular',          10.25),
    ('SkipForward10', 'fwd10',    'skip_forward_10_24_regular',       10.25),
    ('AudioTrack',   'audio',     'headphones_sound_wave_24_regular', None),
    ('Subtitles',    'cc',        'subtitles_24_regular',             None),
    ('Quality',      'quality',   'video_settings_24_regular',        None),
    ('Picture',      'aspect',    'scale_fit_24_regular',             None),
    ('Speed',        'speed',     'top_speed_24_regular',             None),
    ('Volume',       'volume',    'speaker_2_24_regular',             None),
    ('VolumeLow',    'volumeLow', 'speaker_1_24_regular',             None),
    ('Mute',         'mute',      'speaker_mute_24_regular',          None),
    ('Info',         'info',      'info_24_regular',                  None),
    ('Settings',     'settings',  'settings_24_regular',              None),
    ('FullScreen',   'fsEnter',   'full_screen_maximize_24_regular',  None),
    ('ExitFullScreen', 'fsExit',  'full_screen_minimize_24_regular',  None),
    ('Cast',         'cast',      'cast_24_regular',                  None),
    ('AudioSync',    'offset',    'timer_24_regular',                 None),
    ('PictureInPicture', None,    'picture_in_picture_24_regular',    None),
    ('Dismiss',      'dismiss',   'dismiss_24_regular',               None),
]

FIRST_CODEPOINT = 0xE000          # Private Use Area
UPM = 2400                        # 100 units per SVG px; the 24 px box = 1 em
SCALE = UPM / 24

LICENSE = (
    'MIT License\n\nCopyright (c) 2020 Microsoft Corporation\n\n'
    'Permission is hereby granted, free of charge, to any person obtaining a copy of this software '
    'and associated documentation files (the "Software"), to deal in the Software without restriction, '
    'including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, '
    'and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, '
    'subject to the following conditions:\n\nThe above copyright notice and this permission notice shall be '
    'included in all copies or substantial portions of the Software.\n\nTHE SOFTWARE IS PROVIDED "AS IS", '
    'WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF '
    'MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR '
    'COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, '
    'TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS '
    'IN THE SOFTWARE.')


def read_path(icons_dir, name):
    svg = open(os.path.join(icons_dir, name + '.svg'), encoding='utf-8').read()
    if 'viewBox="0 0 24 24"' not in svg:
        sys.exit(f'{name}: expected a 24x24 viewBox')
    paths = re.findall(r'<path[^>]*\sd="([^"]+)"', svg)
    if len(paths) != 1 or re.search(r'fill-rule|transform', svg):
        sys.exit(f'{name}: expected one plain <path>')
    return paths[0]


def contours(d):
    """The path as contours, each a list of RecordingPen operations (SVG space)."""
    rec = RecordingPen()
    parse_path(d, rec)
    out, cur = [], []
    for op in rec.value:
        cur.append(op)
        if op[0] in ('closePath', 'endPath'):
            out.append(cur)
            cur = []
    if cur:
        out.append(cur)
    return out


def min_y(contour):
    return min(pt[1] for _, args in contour for pt in args)


def replay(contour_list, pen):
    for c in contour_list:
        for op, args in c:
            getattr(pen, op)(*args)


def ntos(v):
    """2 decimals, svgo-style: 0.75 → .75, 2.50 → 2.5."""
    s = f'{v:.2f}'.rstrip('0').rstrip('.')
    return '0' if s in ('', '-0') else re.sub(r'^(-?)0\.', r'\1.', s)


def svg_d(contour_list):
    pen = SVGPathPen(None, ntos=ntos)
    replay(contour_list, pen)
    return pen.getCommands()


def tt_glyph(contour_list):
    tt = TTGlyphPen(None)
    flip = TransformPen(Cu2QuPen(tt, max_err=1, reverse_direction=True), (SCALE, 0, 0, -SCALE, 0, UPM))
    replay(contour_list, flip)
    return tt.glyph()


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    icons_dir = sys.argv[1]

    glyphs = []      # (C# name, glyph name, contours)
    web = []         # (key, d | [arrow d, digits d])
    for cs, key, name, split in ICONS:
        d = read_path(icons_dir, name)
        cs_contours = contours(d)
        glyphs.append((cs, name, cs_contours))
        if split is None:
            if key: web.append((key, d))
            continue
        arrow = [c for c in cs_contours if min_y(c) < split]
        digits = [c for c in cs_contours if min_y(c) >= split]
        if not arrow or not digits:
            sys.exit(f'{name}: the split at y={split} left a part empty')
        glyphs.append((cs + 'Arrow', name + '.arrow', arrow))
        glyphs.append((cs + 'Digits', name + '.digits', digits))
        if key: web.append((key, [svg_d(arrow), svg_d(digits)]))

    # ── the font ──────────────────────────────────────────────────────────
    names = [g for _, g, _ in glyphs]
    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(['.notdef', 'space'] + names)
    cmap = {0x20: 'space'}
    for i, g in enumerate(names):
        cmap[FIRST_CODEPOINT + i] = g
    fb.setupCharacterMap(cmap)
    empty = TTGlyphPen(None).glyph()
    fb.setupGlyf({'.notdef': empty, 'space': TTGlyphPen(None).glyph(),
                  **{g: tt_glyph(c) for _, g, c in glyphs}})
    glyf = fb.font['glyf']
    fb.setupHorizontalMetrics({g: (UPM, getattr(glyf[g], 'xMin', 0)) for g in ['.notdef', 'space'] + names})
    # The em box is the icon box: ascent = 1 em, no descent, so a Label of
    # FontSize N is an N×N icon centred like any other view.
    fb.setupHorizontalHeader(ascent=UPM, descent=0, lineGap=0)
    fb.setupNameTable({
        'familyName': 'Animarr Fluent Icons',
        'styleName': 'Regular',
        'uniqueFontIdentifier': 'AnimarrFluentIcons-Regular',
        'fullName': 'Animarr Fluent Icons Regular',
        'psName': 'AnimarrFluentIcons-Regular',
        'version': 'Version 1.000',
        'copyright': 'Copyright (c) 2020 Microsoft Corporation (Fluent UI System Icons)',
        'description': 'A subset of Fluent UI System Icons for the Animarr players.',
        'licenseDescription': LICENSE,
        'licenseInfoURL': 'https://github.com/microsoft/fluentui-system-icons/blob/main/LICENSE',
    })
    fb.setupOS2(sTypoAscender=UPM, sTypoDescender=0, sTypoLineGap=0,
                usWinAscent=UPM, usWinDescent=0, fsType=0, achVendID='NONE')
    fb.setupPost()
    fb.save(FONT)

    # ── C# code points ────────────────────────────────────────────────────
    lines = [
        '// <auto-generated>',
        '// Fluent UI System Icons (MIT, Copyright (c) 2020 Microsoft Corporation,',
        '// github.com/microsoft/fluentui-system-icons) in Resources/Fonts/fluent-icons.ttf.',
        '// Written by tools/icons/build_player_icons.py — regenerate rather than hand-edit.',
        '// </auto-generated>',
        'namespace Animarr.App;',
        '',
        '/// <summary>The icon font\'s glyphs (font alias <see cref="FontFamily"/>): a',
        '/// Label with this FontFamily and one of these as Text draws the icon.</summary>',
        'public static class FluentGlyphs',
        '{',
        '    public const string FontFamily = "FluentIcons";',
        '',
    ]
    width = max(len(cs) for cs, _, _ in glyphs)
    for i, (cs, g, _) in enumerate(glyphs):
        lines.append(f'    public const string {cs.ljust(width)} = "\\u{FIRST_CODEPOINT + i:04X}";   // {g}')
    lines.append('}')
    with open(GLYPHS_CS, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')

    # ── web player path data ──────────────────────────────────────────────
    js = open(PLAYER_JS, encoding='utf-8').read()
    start, end = '    // <fluent-icons>\n', '    // </fluent-icons>\n'
    a, b = js.find(start), js.find(end)
    if a < 0 or b < a:
        sys.exit('animarr-player.js: <fluent-icons> markers not found')
    kw = max(len(k) for k, _ in web)
    body = ['    const ICON_PATHS = {']
    for k, d in web:
        v = f"['{d[0]}',\n{' ' * (kw + 10)}'{d[1]}']" if isinstance(d, list) else f"'{d}'"
        body.append(f"        {(k + ':').ljust(kw + 1)} {v},")
    body.append('    };')
    js = js[:a + len(start)] + '\n'.join(body) + '\n' + js[b:]
    with open(PLAYER_JS, 'w', encoding='utf-8', newline='') as f:
        f.write(js)

    print(f'{len(glyphs)} glyphs → {os.path.relpath(FONT, REPO)} ({os.path.getsize(FONT)} bytes), '
          f'{os.path.relpath(GLYPHS_CS, REPO)}, {len(web)} web icons')


if __name__ == '__main__':
    main()
