#!/usr/bin/env python3
"""Add a content hash to the assets Blazor does not fingerprint.

    python3 tools/cachebust.py <published-wwwroot>

Blazor fingerprints everything under `_framework`, so those files can be cached
forever and a deploy is picked up immediately. Nothing in `wwwroot` gets the same
treatment, and Cloudflare Pages serves it with a four-hour cache, which means a
deploy could land and a browser would keep running the previous stylesheet for
the rest of the afternoon. That happened twice before this script existed.

The obvious fix - setting Cache-Control from the Pages Function - does not work:
the Function wraps the response, but Pages sets its own header for static assets
afterwards and wins. The reliable fix is to change the URL when the content
changes, which is what this does.

`game.js` is not handled here. It is imported from C# rather than from the HTML,
so it carries the assembly version as its query string instead - see Home.razor.
"""
import hashlib
import re
import sys
from pathlib import Path

# Anything referenced from index.html that Blazor leaves alone.
TARGETS = [
    'css/app.css', 'Rockford.Web.styles.css',

    # The icons. They escaped everything: the release identity is compiled into
    # the application and stamped onto the URLs the APPLICATION builds, and
    # nobody builds these - the browser reads them straight out of the markup.
    # The VIP build regenerates them from site/icon.svg after the identity is
    # computed, so they can change without it moving at all. Astra, 19 September
    # 2026.
    'favicon.ico', 'icon-16.png', 'icon-32.png', 'icon-180.png',
]

# Referenced from inside a stylesheet rather than from the HTML, which is a
# different problem: a stylesheet's own query string does NOT propagate to the
# relative URLs inside it, so `app.css?v=abc` still asks for a bare `icon.svg`.
# Stamped before the stylesheet is hashed, so the hash covers the rewrite.
IN_CSS = {'css/app.css': ['../icon.svg']}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()[:10]


def main():
    if len(sys.argv) < 2:
        print(__doc__.strip())
        return 2

    root = Path(sys.argv[1])
    index = root / 'index.html'
    if not index.exists():
        print(f'no index.html in {root}')
        return 1

    html = index.read_text()
    changed = []

    # The stylesheets first, because stamping a URL inside one changes its bytes
    # and therefore its own digest.
    for sheet, urls in IN_CSS.items():
        asset = root / sheet
        if not asset.exists():
            continue

        css = asset.read_text()
        for url in urls:
            target = (asset.parent / url).resolve()
            if not target.exists():
                continue
            version = digest(target)
            pattern = re.compile(re.escape(url) + r'(\?v=[0-9a-f]+)?')
            css, n = pattern.subn(f'{url}?v={version}', css)
            if n:
                changed.append(f'{sheet} -> {url} -> {version}')
        asset.write_text(css)

    for target in TARGETS:
        asset = root / target
        if not asset.exists():
            continue

        version = digest(asset)
        # Replace the reference whether or not it already carries a query string,
        # so running this twice is harmless.
        pattern = re.compile(re.escape(target) + r'(\?v=[0-9a-f]+)?')
        html, n = pattern.subn(f'{target}?v={version}', html)
        if n:
            changed.append(f'{target} -> {version}')

    index.write_text(html)
    for line in changed:
        print(line)
    return 0


if __name__ == '__main__':
    sys.exit(main())
