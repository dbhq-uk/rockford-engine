#!/usr/bin/env python3
"""The one list of worlds the VIP tools work from.

    python3 tools/worldlist.py       prints the ten, and which have a VIP atlas

It is read from `web/Rockford.Web/wwwroot/game/worlds.json`, which
`tools/webassets.py` writes out of the executable's own menu table, in the menu's
order. Before this existed hires.py, spritedex.py, vipart.py, bonusring.py and
spritereview.py each typed their own list of five, and a hidden world could be
added to one and quietly missed by the rest (#107).

Two lists come out of it, and they are different questions:

  ALL         every world, ten of them, in menu order
  with_atlas  the worlds whose VIP atlas exists - `"vip": true` in the manifest

The VIP tools work on `working()`, which is `with_atlas()` unless the
environment says otherwise. To draw a world that has no atlas yet, name it:

    ROCKFORD_WORLDS=scuba python3 tools/hires.py extract

A name that is not one of the ten is an error, not an empty run.
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / 'web/Rockford.Web/wwwroot/game/worlds.json'

# Boxes on one page of the select screen. The same number as `WorldList.PageSize`
# in CaveTables.cs, and the first page is the five the 1988 menu picture shows.
PAGE_SIZE = 5


def entries():
    return json.loads(MANIFEST.read_text())


def all_worlds():
    """Every world's sheet name, in the executable's menu order."""
    return [w['sheet'] for w in entries()]


def with_atlas():
    """The worlds a VIP atlas exists for, in menu order."""
    return [w['sheet'] for w in entries() if w.get('vip')]


def first_page():
    """The five worlds the 1988 menu picture has portraits for."""
    return all_worlds()[:PAGE_SIZE]


def working():
    """The worlds a VIP tool should work on now."""
    asked = [n.strip() for n in os.environ.get('ROCKFORD_WORLDS', '').split(',') if n.strip()]
    if not asked:
        return with_atlas()
    unknown = [n for n in asked if n not in all_worlds()]
    if unknown:
        raise SystemExit(f'ROCKFORD_WORLDS names {unknown}, which is not one of {all_worlds()}')
    return [w for w in all_worlds() if w in asked]


if __name__ == '__main__':
    if '--help' in sys.argv or '-h' in sys.argv:
        print(__doc__.strip())
        sys.exit(0)
    atlas = set(with_atlas())
    for name in all_worlds():
        print(f'{name:8} {"VIP atlas" if name in atlas else "no VIP atlas yet"}')
