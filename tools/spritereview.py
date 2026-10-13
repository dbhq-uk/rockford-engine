#!/usr/bin/env python3
"""An HTML page showing every sprite, original against redraw, for review.

    python3 tools/spritereview.py [outdir]

A contact sheet as a PNG is fine for a glance and useless for a decision: it
cannot be zoomed without going soft, it cannot be filtered, and there is nowhere
to write down which ones are wrong. This writes a page instead - the images stay
at full resolution, each sprite carries the name the prompt used, and clicking one
marks it as needing a redo. The marked list is printed as a command to re-run.

WHAT IT SHOWS, since 29 September 2026. The VIP column is cut from the atlas the
VIP build actually serves, not from the redraw it was assembled from - so a tile
flipped, moved along or cropped at assembly shows as the player sees it. The
sprites are grouped by OBJECT, with every frame of an animation in the order the
engine plays it, because a wrong foot or a wrong facing is only visible beside
the frame it alternates with; sheet order put them twenty tiles apart. And each
card carries what the 1988 tile shows and the verdict on the served one, from
sprite-readings.json. The verdict is on the tile served when the readings were
taken, before the redraw they drove, and the card says so.
"""
import json
import sys


def every_position():
    """(value, position) for every sprite the renderer can draw.

    This read `main` and nothing else. The page itself enumerates wanted() and
    always did, so the cards were there; what was wrong was the VALUE each one
    was labelled and prompted with. A worm position looked up `main[160]`, which
    does not exist, so it carried no name and no description - the tool for
    deciding which sprites need redoing showed them as anonymous tiles.

    The first version of this note claimed seventy-two positions were missing
    from the page entirely. They were not. Astra, 20 September 2026.

    Same fault as the atlas coverage check and spritedex's strip(), in a third
    tool: four or fewer of the six tables TileSet.cs declares.
    """
    tables = hires.sprites()
    out = list(enumerate(tables['main']))

    # A representative value for each, so the caption names something real. The
    # grouped tables hold one creature across three roles and four facings; the
    # first value of each group is what TileSet.Grouped keys on.
    for name, value in (('player', 0x10), ('enemyA', 0x8C), ('enemyB', 0x90),
                        ('groupA', 0xA0), ('groupB', 0xB0)):
        seen = set()
        for pos in tables[name]:
            if pos[0] < 0 or tuple(pos) in seen:
                continue
            seen.add(tuple(pos))
            out.append((value, pos))
    return out
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hires  # noqa: E402
import worlds  # noqa: E402

from usage import wants_help, help_and_exit  # noqa: E402

# Before OUT, deliberately. OUT reads sys.argv[1] at import time, so
# `spritereview.py --help` used to set the output directory to "--help" and
# main() would mkdir it and write a review page into it - a directory named
# --help, with an img/ full of sprites, appearing in the repository root.
#
# That is worse than the traceback the other six tools gave, because it exits 0.
# The CI step that was meant to catch all of this ran `|| true` and could not
# fail at all, so nothing said a word either way.
if wants_help():
    help_and_exit(sys.modules[__name__])

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / 'web/Rockford.Web/wwwroot/game'
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'build/sprite-review'



def value_index():
    """Sprite position -> runtime value, so a position can find its description."""
    index = {}
    for value, pos in every_position():
        if pos[0] >= 0:
            index.setdefault(tuple(pos), value)
    return index


# Sections follow spritedex.py's CATALOGUE. An entry is a label and the frames:
# ('main', value) for the one-picture table, or (table, index) for the grouped
# ones, listed in the order the engine plays them.
def _main(*values):
    return [(f'0x{v:02X}', 'main', v) for v in values]


def _table(name, count, label):
    return [(label(i), name, i) for i in range(count)]


def _worm(i):
    return ('head', 'body', 'tail')[i // 8] + ' ' + ('left', 'up', 'right', 'down')[i % 8 // 2] \
        + ' ' + 'ab'[i % 2]


_WALK = ('left a', 'left b', 'up a', 'up b', 'right a', 'right b', 'down a', 'down b',
         'arm left a', 'arm left b', 'arm right a', 'arm right b',
         'idle A 1', 'idle A 2', 'idle A 3', 'idle A 4', 'idle B 1', 'idle B 2', 'idle B 3', 'idle B 4')

SECTIONS = [
    ('Rockford', [('Rockford', _table('player', 20, lambda i: f'{i} {_WALK[i]}'))]),
    ('The ground', [('Dirt', _main(0x01)), ('Steel', _main(0x02)),
                    ('Wall - also the expanding wall, and frame 0 of the timed wall', _main(0x03)),
                    ('Pass-through wall', _main(0x04))]),
    ('What you are there for', [('Treasure', _main(0x40)), ('Treasure bonus', _main(0x6C, 0x6D, 0x6E, 0x6F)),
                                ('Time bonus', _main(0x70, 0x71, 0x72, 0x73)),
                                ('Exit, closed then opening', _main(0x07, 0x31, 0x32, 0x33))]),
    ('Things that fall', [('Boulder', _main(0x44))]),
    ('Things that hunt', [('Enemy, clockwise', _table('enemyA', 8, lambda i: f'{i} {("left", "up", "right", "down")[i // 2]} {"ab"[i % 2]}')),
                          ('Enemy, anticlockwise', _table('enemyB', 4, lambda i: f'frame {i}')),
                          ('Amoeba', _main(0x48, 0x49, 0x4A, 0x4B))]),
    ('The worms', [('Evil worm, dormant', _main(0x78, 0x79, 0x7A, 0x7B)),
                   ('Evil worm', _table('groupA', 24, _worm)),
                   ('Good worm, dormant', _main(0x7C, 0x7D, 0x7E, 0x7F)),
                   ('Good worm', _table('groupB', 24, _worm))]),
    ('Walls that do something', [('Timed wall - frames 1 to 3 are frame 0 moved along', _main(0x74, 0x75, 0x76, 0x77))]),
    ('The taps and what they emit', [('Acid tap', _main(0x08, 0x09, 0x0A, 0x0B)),
                                     ('Acid, falling, breaking, settled', _main(0x20, 0x22, 0x24, 0x25, 0x28, 0x2A)),
                                     ('Fire tap', _main(0x0C, 0x0D, 0x0E, 0x0F)),
                                     ('Fire', _main(0x2C, 0x2D, 0x2E, 0x2F))]),
    ('Explosions', [('Blast to treasure - 0x11 is never drawn', _main(0x11, 0x12, 0x13, 0x14)),
                    ('Blast to space', _main(0x1B, 0x1C, 0x1D, 0x1E))]),
    ('Transformations', [('Boulder to anticlockwise enemy, and back', _main(0x34, 0x35, 0x36, 0x37)),
                         ('Boulder to treasure, and back', _main(0x17, 0x18, 0x19, 0x1A))]),
]


def layout():
    """SECTIONS with each frame's sheet position, repeats within an object dropped."""
    tables = hires.sprites()
    out = []
    for title, objects in SECTIONS:
        objs = []
        for name, frames in objects:
            seen, kept = set(), []
            for label, table, i in frames:
                pos = tuple(tables[table][i])
                if pos[0] < 0 or pos in seen:
                    continue
                seen.add(pos)
                kept.append({'label': label, 'pos': f'{pos[0]},{pos[1]}'})
            objs.append({'name': name, 'frames': kept})
        out.append({'title': title, 'objects': objs})
    return out


def main():
    (OUT / 'img').mkdir(parents=True, exist_ok=True)
    values = value_index()
    items = set(hires.wanted())
    sections = layout()

    # Every sprite the game can draw has to be in a group, or the page hides it.
    placed = {tuple(map(int, f['pos'].split(','))) for sec in sections
              for ob in sec['objects'] for f in ob['frames']}
    missing = sorted(items - placed)
    if missing:
        raise SystemExit(f'in no group on the page: {missing}')

    side = hires.TILE * hires.SCALE
    worlds_out = []
    for world in hires.WORLDS:
        sheet = Image.open(GAME / f'{world}.png').convert('RGBA')
        atlas = Image.open(GAME / f'{world}-x{hires.SCALE}.png').convert('RGBA')
        cards = {}
        for (x, y) in sorted(placed):
            o, n = f'img/{world}-{x}-{y}-o.png', f'img/{world}-{x}-{y}-v.png'
            sheet.crop((x, y, x + hires.TILE, y + hires.TILE)).resize(
                (side, side), Image.NEAREST).save(OUT / o)
            atlas.crop((x * hires.SCALE, y * hires.SCALE,
                        x * hires.SCALE + side, y * hires.SCALE + side)).save(OUT / n)
            v = values.get((x, y))
            read = worlds.READINGS.get(world, {}).get((x, y), {})
            cards[f'{x},{y}'] = {
                'o': o, 'n': n,
                'value': f'0x{v:02X}' if v is not None else '',
                'subject': (worlds.OBJECTS.get(world, {}).get(worlds.ROLES.get((x, y)))
                            or hires.subject_for(v, x, y) or 'unnamed'),
                'pose': read.get('pose', ''), 'vip': read.get('vip', ''),
            }
        worlds_out.append({'world': world, 'cards': cards, 'sections': sections})

    (OUT / 'index.html').write_text(PAGE.replace('__DATA__', json.dumps(worlds_out)))
    print(f'{OUT}/index.html  ({len(placed)} sprites x {len(hires.WORLDS)} worlds)')


PAGE = r'''<!doctype html>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>rockford - original against VIP, every sprite</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; background: #14141a; color: #e8e8ef; font: 15px/1.5 system-ui, sans-serif; }
  header { position: sticky; top: 0; z-index: 2; padding: 12px 20px; background: #1b1b24;
           border-bottom: 1px solid #2c2c3a; display: flex; gap: 14px; align-items: center; flex-wrap: wrap; }
  h1 { font-size: 16px; margin: 0; font-weight: 600; }
  button, select { background: #262633; color: inherit; border: 1px solid #3a3a4c;
    border-radius: 7px; padding: 6px 12px; font: inherit; cursor: pointer; }
  .muted { color: #9a9aad; font-size: 13px; }
  h2 { font-size: 15px; margin: 26px 20px 4px; }
  h3 { font-size: 13px; margin: 14px 20px 0; color: #b8b8cc; font-weight: 500; }
  .grid { display: grid; gap: 14px; padding: 8px 20px 4px;
          grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); }
  .card { background: #1d1d26; border: 2px solid transparent; border-radius: 10px; padding: 10px; cursor: pointer; }
  .card.bad { border-color: #e0504a; background: #2a1b1d; }
  .frame { font-size: 11px; color: #9a9aad; margin-bottom: 4px; }
  .pair { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
  .pair div { border-radius: 6px; aspect-ratio: 1; overflow: hidden; background: #34343f; }
  .check .pair div { background: repeating-conic-gradient(#555 0 25%, #333 0 50%) 0 0 / 16px 16px; }
  .pair img { width: 100%; height: 100%; display: block; }
  .pair img.o { image-rendering: pixelated; }
  .labels { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; font-size: 11px; color: #7e7e94;
            margin-bottom: 6px; text-align: center; }
  .pose { font-size: 12px; color: #c4c4d4; margin-top: 8px; }
  .vip { font-size: 12px; margin-top: 6px; }
  .vip.ok { color: #7fbf7f; } .vip.wrong { color: #f08a80; }
  .meta { font-size: 11px; color: #7e7e94; margin-top: 4px; }
  #out { white-space: pre-wrap; font: 12.5px ui-monospace, monospace; padding: 0 20px 30px; color: #9fd39f; }
</style>
<header>
  <h1>rockford - original against VIP</h1>
  <select id="world"></select>
  <select id="bg"><option value="">Grey backing</option><option value="check">Checkerboard</option></select>
  <span class="muted" id="count"></span>
  <button id="list">List the marked ones</button>
  <button id="clear">Clear marks</button>
  <span class="muted">Click a card to mark it wrong. Marks are kept in this browser.</span>
</header>
<div id="grid"></div>
<div id="out"></div>
<script>
const DATA = __DATA__;
const bad = new Set(JSON.parse(localStorage.getItem('rf-vip-bad') || '[]'));
const $ = id => document.getElementById(id);
const esc = t => t.replace(/[&<>]/g, c => ({'&': '&amp;', '<': '&lt;', '>': '&gt;'}[c]));
DATA.forEach((w, i) => $('world').add(new Option(w.world, i)));
const save = () => localStorage.setItem('rf-vip-bad', JSON.stringify([...bad]));

function card(w, pos, label) {
  const s = w.cards[pos], k = `${w.world} ${pos}`;
  const c = document.createElement('div');
  c.className = 'card' + (bad.has(k) ? ' bad' : '');
  c.dataset.key = k;
  const ok = s.vip.toLowerCase().startsWith('ok');
  c.innerHTML = `${label ? `<div class="frame">${esc(label)}</div>` : ''}
    <div class="labels"><span>1988</span><span>VIP (served)</span></div>
    <div class="pair"><div><img class="o" loading="lazy" src="${s.o}"></div>
    <div><img loading="lazy" src="${s.n}"></div></div>
    ${s.pose ? `<div class="pose">${esc(s.pose)}</div>` : ''}
    ${s.vip ? `<div class="vip ${ok ? 'ok' : 'wrong'}">Before the redraw: ${esc(s.vip)}</div>` : ''}
    <div class="meta">${s.value} &middot; ${pos}</div>`;
  c.onclick = () => {
    bad.has(k) ? bad.delete(k) : bad.add(k); save();
    document.querySelectorAll(`[data-key="${k}"]`).forEach(e => e.classList.toggle('bad', bad.has(k)));
    count();
  };
  return c;
}

function render() {
  const w = DATA[$('world').value], root = $('grid');
  root.className = $('bg').value; root.innerHTML = '';
  for (const sec of w.sections) {
    const h = document.createElement('h2'); h.textContent = sec.title; root.append(h);
    for (const ob of sec.objects) {
      const first = w.cards[ob.frames[0].pos];
      const h3 = document.createElement('h3');
      h3.innerHTML = `${esc(ob.name)} <span class="muted">&middot; ${esc(first.subject)}</span>`;
      root.append(h3);
      const g = document.createElement('div'); g.className = 'grid';
      ob.frames.forEach(f => g.append(card(w, f.pos, ob.frames.length > 1 ? f.label : '')));
      root.append(g);
    }
  }
  count();
}

function count() {
  const w = DATA[$('world').value], wrong = Object.values(w.cards)
    .filter(s => s.vip && !s.vip.toLowerCase().startsWith('ok')).length;
  const here = [...bad].filter(k => k.startsWith(w.world + ' ')).length;
  $('count').textContent = `${wrong} of ${Object.keys(w.cards).length} were wrong before the redraw - ${here} marked here, ${bad.size} in all`;
}

$('list').onclick = () => {
  const rows = [...bad].sort();
  const files = rows.map(k => { const [w, p] = k.split(' '); return `build/hires/${w}/out-${p.replace(',', '-')}.png`; });
  $('out').textContent = rows.length
    ? `${rows.length} marked:\n\n${rows.join('\n')}\n\nTo redraw them, remove their redraws and generate:\n\n` +
      `git rm ${files.join(' ')}\npython3 tools/hires.py generate && python3 tools/hires.py atlas`
    : 'Nothing marked.';
  $('out').scrollIntoView();
};
$('clear').onclick = () => { bad.clear(); save(); $('out').textContent = ''; render(); };
['world', 'bg'].forEach(id => $(id).onchange = render);
render();
</script>
'''


if __name__ == '__main__':
    sys.exit(main() or 0)
