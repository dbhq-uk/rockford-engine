#!/usr/bin/env python3
"""Fail if a tree that is about to be public holds anything that must stay private.

    python3 tools/publiccheck.py <dir>

The public repository (dbhq-uk/rockford-engine) is exported from the private one by
scripts/export-public.sh, and every export is checked by this before anything is
pushed. Its own CI runs it too, so a file added there by hand is held to the same
rules. Issue #166 records what goes public and why.

What it refuses:
- the game's files and anything made from them: disk images, executables, the
  original art and music in any format, and every picture or sound (this project's
  derived art and audio are kept private too, so a public repository can never hold
  something a takedown could not reach), apart from the icons rendered from the
  project's own gem;
- the private directories, by path;
- data copied out of the executable that the front end loads (only worlds.json,
  this project's own manifest, may sit in wwwroot/game);
- internal addresses, credential-shaped strings and pointers into the private notes;
- anything larger than 1 MB.
"""
import re
import sys
from pathlib import Path

if len(sys.argv) != 2 or sys.argv[1] in ('-h', '--help'):
    print(__doc__.strip())
    sys.exit(0 if len(sys.argv) == 2 else 2)

ROOT = Path(sys.argv[1]).resolve()

BANNED_SUFFIXES = {
    '.fil', '.car', '.scm', '.ccm', '.bin', '.exe', '.com', '.img', '.st', '.adf',
    '.d64', '.dsk', '.lha', '.zip', '.mid', '.midi', '.wav', '.mp3', '.ogg', '.m4a',
    '.webm', '.flac', '.png', '.jpg', '.jpeg', '.gif', '.webp', '.avif', '.bmp',
    '.ico', '.ega', '.whl',
}

# Rendered from wwwroot/icon.svg, the project's own gem, by the export.
ALLOWED_BINARIES = {
    'web/Rockford.Web/wwwroot/favicon.png', 'web/Rockford.Web/wwwroot/favicon.ico',
    *(f'web/Rockford.Web/wwwroot/icon-{s}.png' for s in (16, 32, 48, 180, 192, 512)),
}

PRIVATE_PREFIXES = (
    'original/', 'extracted/', 'unpacked/', 'render/', 'third_party/', 'correspondence/',
    'levels/', 'sprites/', 'music/', 'build/', 'site/', 'scripts/',
    'docs/LEGAL-POSITION', 'docs/astra-', 'docs/research/', 'docs/screenshots/',
    'docs/ROCKFORD-VIP', 'web/Rockford.Web/wwwroot/vip/', 'web/Rockford.Web/functions/',
    'DATA-NOTICE',
)
GAME_DIR = 'web/Rockford.Web/wwwroot/game/'
GAME_DIR_ALLOWED = {GAME_DIR + 'worlds.json'}

PATTERNS = [
    (re.compile(r'100\.\d{1,3}\.\d{1,3}\.\d{1,3}'), 'a Tailscale address'),
    (re.compile(r'\b[\w-]+\.ts\.net\b'), 'a Tailscale hostname'),
    (re.compile(r'-----BEGIN [A-Z ]*PRIVATE KEY-----'), 'a private key'),
    (re.compile(r'\bghp_[A-Za-z0-9]{20,}'), 'a GitHub token'),
    (re.compile(r'\bsk-[A-Za-z0-9_-]{20,}'), 'an API key'),
    (re.compile(r'(CLOUDFLARE_API_TOKEN|OPENAI_API_KEY)\s*=\s*\S'), 'a credential assignment'),
    (re.compile(r'\.dbhq/env'), 'the private environment file'),
    (re.compile(r'correspondence/'), 'a pointer to the private email record'),
]

MAX_BYTES = 1_000_000


def problems():
    for path in sorted(ROOT.rglob('*')):
        rel = path.relative_to(ROOT).as_posix()
        if rel.startswith('.git/') or rel == '.git' or path.is_dir():
            continue
        if rel.startswith(PRIVATE_PREFIXES):
            yield rel, 'a private path'
            continue
        if rel.startswith(GAME_DIR) and rel not in GAME_DIR_ALLOWED:
            yield rel, 'game data the front end loads; only worlds.json may be public'
            continue
        if path.suffix.lower() in BANNED_SUFFIXES and rel not in ALLOWED_BINARIES:
            yield rel, f'a {path.suffix} file'
            continue
        size = path.stat().st_size
        if size > MAX_BYTES:
            yield rel, f'{size:,} bytes, over the 1 MB limit'
            continue
        try:
            text = path.read_text(encoding='utf-8')
        except UnicodeDecodeError:
            if rel not in ALLOWED_BINARIES:
                yield rel, 'binary content'
            continue
        if rel == 'tools/publiccheck.py':
            continue
        for pattern, what in PATTERNS:
            m = pattern.search(text)
            if m:
                line = text.count('\n', 0, m.start()) + 1
                yield f'{rel}:{line}', what


found = list(problems())
for where, why in found:
    print(f'PRIVATE  {where}: {why}')
count = sum(1 for p in ROOT.rglob('*') if p.is_file() and '.git' not in p.relative_to(ROOT).parts)
if found:
    sys.exit(f'{len(found)} problem(s) in {count} files: not safe to publish')
print(f'publiccheck: {count} files, nothing private')
