// What the player chose, kept between visits.
//
// Everything in the Game menu was per-session until this: the two sound
// toggles, the vocal or instrumental cut, the presentation switch in a debug
// build. Flip one, reload, and it was back. That was tolerable while the only
// settings were two things you could flip again in a second, and it stops being
// tolerable the moment anything worth keeping exists - remappable keys, a
// motion switch, volume levels.
//
// So: one small store, read once at startup and written on change.
//
// NOTHING HERE REACHES THE SIMULATION. These are presentation choices and the
// engine cannot see them. A setting that changed what the game does would be an
// engine change wearing a different hat.

/** One key, so a clear-site-data leaves nothing of ours behind. */
const KEY = 'rockford.settings';

/**
 * Bumped when the SHAPE changes in a way an old value cannot be read as.
 *
 * A stored blob from an older version is discarded rather than guessed at.
 * Renaming a field or changing what a value means is a bump; adding a new field
 * with a default is not, because an old blob is still a correct answer for
 * everything it does contain.
 */
const VERSION = 1;

/**
 * Every setting, and what it is when nobody has chosen.
 *
 * The defaults are the behaviour the game had before any of this existed, which
 * is what makes an empty store and a cleared store the same thing as a first
 * visit.
 *
 * `vocal: true` must agree with DEFAULT_CUT in music-vip.js, or the control and
 * the audio disagree on the first play.
 */
const DEFAULTS = {
    music: true,
    effects: true,
    vocal: true,

    // High-contrast outlines around the things that move, #70.
    //
    // OFF by default, and that is a judgement rather than an oversight. It is a
    // legibility aid, not a correction: the picture without it is the picture
    // the artwork intends, and a permanent dark edge on every object is worse
    // than the problem it solves for some people. Anyone who needs it will go
    // and find it; nobody should have it imposed.
    outlines: false,

    // Contact shadows and emissive glow, #37. ON by default, unlike the
    // outlines: this adds light and never removes it, so the cave is exactly as
    // readable either way and it is a look rather than an aid.
    lighting: true,

    // The key bindings, or null for "whatever the game ships with". Null rather
    // than a copy of the defaults so that a later change to the defaults reaches
    // a player who never rebound anything - storing the shipped set would freeze
    // them at whatever they were on the day the player first visited.
    bindings: null,

    // How loud, 0 to 100, as separate channels.
    //
    // On and off is not a mix. The footstep fires up to ten times a second and
    // the theme runs underneath everything, so "turn the music down a bit" is
    // the thing most people actually want and the toggles could not express it.
    // The toggles remain as a quick mute, which is a different question from
    // how loud.
    musicLevel: 70,
    effectsLevel: 80,

    // Less motion than the game would otherwise use. Covers the renderer's own
    // blends, which a CSS prefers-reduced-motion rule cannot reach - a blend
    // computed in JavaScript is invisible to a media query.
    //
    // Null rather than false, because there are three answers and not two: yes,
    // no, and "whatever the system asks for". Null defers to the OS setting,
    // which is what a player who has already told their machine should not have
    // to say again.
    reducedMotion: null,
};

let current = { ...DEFAULTS };

/**
 * localStorage is not reliably there.
 *
 * Safari in private browsing has historically thrown on write; an iframe with
 * third-party storage blocked throws on read; a browser with storage disabled
 * has no localStorage at all. None of those should stop the game - it worked
 * without any of this yesterday - so every access is guarded and a failure
 * means "no stored settings", which is a state the code already handles.
 */
function storage() {
    try {
        return typeof localStorage === 'object' ? localStorage : null;
    } catch {
        return null;
    }
}

/**
 * Reads what was stored, falling back to the defaults for anything missing,
 * malformed or from an older shape.
 *
 * Deliberately forgiving. A corrupt value should cost the player their
 * preferences, which is annoying, rather than the game, which is not a trade
 * anybody would choose.
 */
export function load() {
    current = { ...DEFAULTS };

    const store = storage();
    if (!store) return { ...current };

    let raw;
    try { raw = store.getItem(KEY); } catch { return { ...current }; }
    if (!raw) return { ...current };

    let parsed;
    try { parsed = JSON.parse(raw); } catch { return { ...current }; }

    if (!parsed || typeof parsed !== 'object' || parsed.v !== VERSION)
        return { ...current };

    // Field by field, and only the ones we know about with the type we expect.
    // Taking the stored object wholesale would let anything that ever wrote to
    // this key decide what the settings are.
    for (const name of Object.keys(DEFAULTS)) {
        if (name === 'bindings' || name === 'reducedMotion') continue;

        // A level is a number and has to be in range: a stored 5000 would be a
        // channel nothing can turn down, and a NaN would silence it entirely.
        if (name.endsWith('Level')) {
            const level = parsed[name];
            if (typeof level === 'number' && Number.isFinite(level))
                current[name] = Math.min(100, Math.max(0, Math.round(level)));
            continue;
        }

        if (typeof parsed[name] === typeof DEFAULTS[name]) current[name] = parsed[name];
    }

    // Three answers, not two - see the default.
    if (parsed.reducedMotion === true || parsed.reducedMotion === false)
        current.reducedMotion = parsed.reducedMotion;

    // Bindings are an object of arrays rather than a boolean, so `typeof` says
    // nothing useful about them. game.js validates the contents - it is the one
    // that knows which actions exist - and this only has to establish that it is
    // an object worth passing on.
    if (parsed.bindings && typeof parsed.bindings === 'object'
        && !Array.isArray(parsed.bindings))
        current.bindings = parsed.bindings;

    return { ...current };
}

/** What is currently set, without re-reading. */
export function all() {
    return { ...current };
}

/**
 * Records one setting. Unknown names are ignored rather than stored, so a typo
 * is a no-op instead of a field nothing ever reads again.
 */
export function set(name, value) {
    if (!(name in DEFAULTS)) return false;

    if (name === 'bindings') {
        // null means "back to the shipped set", which is a real choice and not
        // a missing value.
        if (value !== null && (typeof value !== 'object' || Array.isArray(value)))
            return false;
    } else if (name === 'reducedMotion') {
        // null is "whatever the system asks for", which is a third answer.
        if (value !== null && typeof value !== 'boolean') return false;
    } else if (name.endsWith('Level')) {
        if (typeof value !== 'number' || !Number.isFinite(value)) return false;
        value = Math.min(100, Math.max(0, Math.round(value)));
    } else if (typeof value !== typeof DEFAULTS[name]) {
        return false;
    }

    current[name] = value;

    const store = storage();
    if (!store) return true;    // held for this session, which is what we had before

    try {
        store.setItem(KEY, JSON.stringify({ v: VERSION, ...current }));
    } catch {
        // Quota, private browsing, a disabled store. The setting still applies
        // for this session and the game carries on.
    }

    return true;
}

/** Forgets everything. Exported for the tests and for a future reset control. */
export function clear() {
    current = { ...DEFAULTS };
    const store = storage();
    if (!store) return;
    try { store.removeItem(KEY); } catch { /* nothing to do about it */ }
}
