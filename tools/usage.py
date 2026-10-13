#!/usr/bin/env python3
"""One `--help` that behaves, shared by the tools that take positional arguments.

Seven of the seventeen tools here answered `--help` with an IndexError traceback,
because they read `sys.argv[1]` straight into a filename. CI did not notice: the
step meant to catch it ran `python "$t" --help ... || true`, which cannot fail
whatever happens, so it reported green over seven broken entry points for as long
as it existed (found 18 September 2026).

Usage, from a tool's `__main__`:

    from usage import argv
    src, out = argv(2, 'ega.py <input.fil> <output.png>')

`argv(n, line)` prints the calling module's own docstring and exits 0 on `--help`
or `-h`, prints the usage line and exits 2 when there are fewer than n arguments,
and otherwise hands back the n it was asked for. The docstring is the help text
because it is already the explanation, and a second copy is a second thing to go
stale.
"""
import sys


def argv(count, line, module=None):
    """The first `count` positional arguments, or a clean exit explaining why not."""
    args = sys.argv[1:]

    if '--help' in args or '-h' in args:
        doc = (module or sys.modules['__main__']).__doc__
        print((doc or line).strip())
        raise SystemExit(0)

    if len(args) < count:
        # 2 rather than 1, which is what `raise SystemExit(str)` would give: the
        # convention is that 2 means "you called this wrong" and 1 means "it ran
        # and failed", and a caller scripting these deserves to tell them apart.
        print(f'usage: {line}', file=sys.stderr)
        raise SystemExit(2)

    return args[:count] if count != 1 else args[0]


def wants_help():
    """True if the caller was asked for help. For tools that parse argv themselves."""
    return '--help' in sys.argv[1:] or '-h' in sys.argv[1:]


def help_and_exit(module=None):
    """Print the calling module's docstring and exit cleanly."""
    doc = (module or sys.modules['__main__']).__doc__
    print((doc or '').strip())
    raise SystemExit(0)
