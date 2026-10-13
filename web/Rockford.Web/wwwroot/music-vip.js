// Rockford VIP theme - "Dance Floor Game".
//
// The VIP presentation plays its own piece of music rather than an arrangement
// of the original. That is a deliberate reversal of what was here before, and it
// is the thing that makes VIP publishable.
//
// The previous version arranged the 1988 tune: 22 patterns and an order list
// lifted out of the executable, rendered as drum and bass in Web Audio. It was
// good work and it is still in the history at ee8caa0. But the composition it
// arranged is First Star Software's, so every version of it - however well
// synthesised, however much of the code was ours - was a derivative of somebody
// else's tune. DATA-NOTICE.md already says anything published needs its own
// assets, and a cover inherits exactly the rights problem it was meant to solve.
//
// This track carries none of that. It is not the Rockford melody, so nothing in
// it traces back to First Star, and the faithful presentation still plays the
// original tune for anyone who wants to hear it. The split between the two
// presentations gets sharper rather than muddier: one is theirs, one is ours.
//
// An <audio> element rather than a decoded AudioBuffer. Decoding two and a half
// minutes of 48 kHz stereo to float PCM costs around 60 MB of memory and cannot
// start until the whole file has arrived; the element streams, starts on the
// first few kilobytes, and still routes through the graph via
// MediaElementAudioSourceNode - so the gain stage, and anything added to it
// later, applies exactly as it did to the synthesised version.

// Opus first, AAC behind it. Both are encoded from the lossless master rather
// than transcoded from each other, so neither carries the other's artefacts.
//
// Opus is the right primary: at 96 kbps it is 2.1 MB against 3.7 MB for the
// 182 kbps MP3 it replaces, and it sounds better at that size than any MP3
// does. Chrome, Firefox and Edge have supported it for years, and Safari since
// 17.4 - but Safari's WebM support is the shakiest part of that sentence, and a
// theme that silently fails to load is worse than one that costs half a
// megabyte more. So AAC in an MP4 container is the fallback: universally
// supported, including on every iOS that can run this game at all.
//
// The choice is made by asking the browser rather than sniffing a user agent.
// canPlayType returns '', 'maybe' or 'probably', and anything that is not an
// empty string means the browser is willing to try.

// Two cuts of the same track: instrumental and with vocals. Both exist in both
// codecs, so the choice of cut and the choice of container are independent.
//
// The vocal cut is the default (Dan, 16 Sep 2026). The instrumental stays one
// click away for anyone who wants the words out of the way.
const CUTS = {
    instrumental: 'theme-vip',
    vocal: 'theme-vip-vocal',
};
const DEFAULT_CUT = 'vocal';

// Seconds the world track takes to fade when the player dismisses the screen.
// Short enough not to outstay the keypress, long enough not to read as a cut.
const FADE = 0.35;

// Ducking. The sound effects are bare square waves, chosen in 1988 to be heard
// against silence; under a full mix they disappear, and the boulder and gem cues
// are how a player knows what is happening off-screen. So every effect pushes
// the music down briefly and lets it climb back.
//
// DUCK is a level rather than a subtraction so repeated effects cannot stack
// into silence - ten gems in a row duck to the same place as one.
const DUCK = 0.45;
const DUCK_DOWN = 0.03;      // seconds to drop: fast, or the effect starts first
const DUCK_UP = 0.28;        // seconds to recover: slow, or it pumps audibly
const MUSIC_LEVEL = 0.85;

/**
 * What the player asked for, 0 to 1, multiplying MUSIC_LEVEL.
 *
 * MUSIC_LEVEL is the theme's balance against the effects; this is the slider.
 * Ducking multiplies both, so a quiet theme still ducks by the same proportion
 * rather than disappearing.
 */
let level = 1;

/** The theme's gain right now, before any duck. */
const musicGain = () => MUSIC_LEVEL * level;

export function setLevel(value) {
    level = Math.min(1, Math.max(0, Number(value) || 0));
    if (out && ctx) {
        out.gain.cancelScheduledValues(ctx.currentTime);
        out.gain.setValueAtTime(musicGain(), ctx.currentTime);
    }
    return level;
}

const CONTAINERS = [
    ['webm', 'audio/webm; codecs="opus"'],
    ['m4a', 'audio/mp4; codecs="mp4a.40.2"'],
];

let ctx = null;
let out = null;
let el = null;
let sting = null;
let world = null;
let worldGain = null;
let stingGain = null;
let onEnd = null;
let cut = DEFAULT_CUT;
let extension = null;        // settled once, at attach
let fadeTimer = null;
let themeWasPlaying = false;

function pickContainer(probe) {
    for (const [ext, type] of CONTAINERS) {
        if (probe.canPlayType(type) !== '') return ext;
    }
    // Nothing matched, which should not happen on anything with an AudioContext.
    // Hand back the fallback rather than nothing: an element that fails to load
    // reports an error, where an element with no source fails silently.
    return CONTAINERS[CONTAINERS.length - 1][0];
}

// The query string this module was fetched with, which carries the release's
// content identity. Reused on the audio URLs so the theme, the sting and the
// world track are pinned to the same release as the code that plays them.
//
// The same idiom as the sibling imports in game.js, and for the same reason: a
// four-hour Pages cache on a stable URL meant a new build could be handed the
// previous release's audio. import.meta.url carries it by construction, rather
// than by anyone remembering to thread a version through.
const V = new URL(import.meta.url).search;

// Where a URL actually loads from. game.js's load phase fetches every file
// below before the title appears and hands over a resolver that swaps each URL
// for its in-memory copy, so the theme starts on the first press and the world
// track is there the first time a world is finished. Identity until attached,
// which is what a build without the load phase gets.
let localOf = url => url;

function sourceFor(which) {
    return localOf(`${CUTS[which] ?? CUTS[DEFAULT_CUT]}.${extension}${V}`);
}

/**
 * Every file this module will ever play, in the container this browser takes,
 * so the load phase can fetch them all behind the progress bar.
 *
 * Both cuts of the theme, not only the selected one: switching cut is a click
 * in the audio panel, and it should not be the one thing in the game that
 * still waits on the network.
 */
export function sources() {
    extension ??= pickContainer(new Audio());
    return [
        ...Object.values(CUTS).map(name => `${name}.${extension}${V}`),
        `sting-vip.${extension}${V}`,
        `world-vip.${extension}${V}`,
    ];
}

/** How long the loading screen will wait for the theme before going on without it. */
const READY_CEILING_MS = 8000;

let ready = null;

/** Settles once the theme is buffered, or once waiting stops being worth it. */
export function whenReady() {
    return ready ?? Promise.resolve(false);
}

export function attach(audioContext, destination, resolveUrl) {
    ctx = audioContext;
    if (resolveUrl) localOf = resolveUrl;

    out = ctx.createGain();
    out.gain.value = musicGain();
    out.connect(destination ?? ctx.destination);

    el = new Audio();
    extension ??= pickContainer(el);
    el.src = sourceFor(cut);
    el.preload = 'auto';

    // The theme loops. It runs 2:39 and a cave can outlast that, so without this
    // the music simply stops partway through a cave, which reads as a fault
    // rather than an ending.
    el.loop = true;

    // Resolved when the browser reckons it can play the whole thing without
    // stopping to buffer. The loading screen waits on this, so the theme starts
    // on the first press rather than a second or two into the title screen - and
    // does not stutter partway through on a slow line.
    //
    // It resolves rather than rejects on error, and has a ceiling, because a
    // title screen that never appears because a 2 MB track is slow would be a
    // worse fault than a theme that starts late.
    ready = new Promise(resolve => {
        const done = () => resolve(true);
        el.addEventListener('canplaythrough', done, { once: true });
        el.addEventListener('error', () => resolve(false), { once: true });
        setTimeout(() => resolve(false), READY_CEILING_MS);
    });

    // createMediaElementSource throws if it is called twice for the same
    // element, and once an element is routed into a graph its audio only comes
    // out through that graph. So it happens exactly once, here, rather than
    // per play.
    ctx.createMediaElementSource(el).connect(out);

    el.addEventListener('ended', () => {
        const done = onEnd;
        onEnd = null;
        if (done) done();
    });

    // The cave-complete sting. Its own element, not a second use of the theme's:
    // the two can legitimately overlap, since the theme plays on the title screen
    // and the sting fires in play, and one element cannot hold two sources.
    sting = new Audio(localOf(`sting-vip.${extension}${V}`));
    sting.preload = 'auto';

    // ITS OWN GAIN, which is half of what #36 asked for. The sting used to run
    // straight into the theme's gain node, so the two were one control: the
    // music slider moved both together, an effect's duck pulled the sting down
    // with the theme, and there was no way to hand over from one to the other
    // because there was nothing to hand over between.
    stingGain = ctx.createGain();
    stingGain.gain.value = 1;
    stingGain.connect(out);
    ctx.createMediaElementSource(sting).connect(stingGain);

    // The world-complete track, through its own gain so it can be faded rather
    // than stopped. 'auto', no longer 'metadata': that was chosen so a megabyte
    // not needed until a world is finished would not compete with the theme for
    // bandwidth on the title screen. The load phase has fetched both before the
    // title appears, so there is no bandwidth left to compete for and the track
    // is ready the first time it is wanted.
    world = new Audio(localOf(`world-vip.${extension}${V}`));
    world.preload = 'auto';
    worldGain = ctx.createGain();
    worldGain.gain.value = 1;
    worldGain.connect(out);
    ctx.createMediaElementSource(world).connect(worldGain);
}

/**
 * Kept for the caller's sake, and does nothing.
 *
 * game.js hands the tracker data to whichever music module is loaded, because
 * the faithful player needs it. This one does not: the tune it plays is not the
 * one in that data. Accepting and ignoring it keeps the two modules
 * interchangeable, which is the whole point of the presentation switch.
 */
export function load() { }

export function play(onFinished, onRefused) {
    if (!ctx || !el) return false;
    onEnd = onFinished ?? null;
    el.currentTime = 0;

    // play() returns a promise that rejects if the context is still suspended or
    // the user gesture has been lost. Clearing the callback on that path matters:
    // game.js records what it believes is playing, and a silent failure would
    // leave the control saying the theme is on when nothing is coming out.
    //
    // And TELLING the caller matters as much. This returns true synchronously
    // whatever the promise goes on to do, so a refusal here was invisible: the
    // caller had already spent its one arming on a theme that never started,
    // and no later gesture tried again for the rest of the session.
    const started = el.play();
    if (started && typeof started.catch === 'function') {
        started.catch(() => {
            onEnd = null;
            if (onRefused) onRefused();
        });
    }
    return true;
}

export function stop() {
    onEnd = null;

    // Turning the theme off is a decision, and it outranks the world track's
    // note that the theme was playing when it took over. Without this, stopping
    // the theme during the world-complete screen has resumeTheme start it again
    // on the way out, while the control still says off.
    themeWasPlaying = false;

    if (!el) return;
    el.pause();
    el.currentTime = 0;
}

/**
 * Switches between the instrumental and the vocal cut.
 *
 * Returns the cut in force afterwards, so the caller can render the control
 * from the answer rather than from what it asked for. Switching while the theme
 * is playing restarts it on the other cut: the two are different lengths, so
 * carrying the playhead across would land somewhere arbitrary, and crossfading
 * one vocal take into another is worse than an honest restart.
 */
export function setCut(which) {
    if (!(which in CUTS) || which === cut) return cut;
    cut = which;

    if (!el) return cut;

    const wasPlaying = !el.paused;
    const finished = onEnd;

    // stop() clears themeWasPlaying, because turning the theme off outranks the
    // world track's note that it was on. Changing the cut is not turning it off,
    // so that note is carried across - without this, changing cut during the
    // world-complete screen meant the theme never came back afterwards while the
    // control still said it was on.
    const heldTheme = themeWasPlaying;
    stop();
    themeWasPlaying = heldTheme;

    el.src = sourceFor(cut);
    if (wasPlaying) play(finished);
    return cut;
}

/** Which cut is selected. The caller owns the label, this owns the state. */
export function currentCut() { return cut; }

/** Whether the theme is sounding, so a caller can render a control from it. */
export function playing() { return !!el && !el.paused; }

/**
 * Stops every element this module owns.
 *
 * Pause rather than gain. The world-complete screen waits on worldStingPlaying,
 * which reads the element - a track silenced by gain would hold that screen for
 * its full 82 seconds with nothing coming out.
 */
export function stopAll() {
    stop();
    stopSting();
    stopWorldSting();
}

/** Cuts the world track off, without the fade. */
export function stopWorldSting() {
    if (fadeTimer !== null) { clearTimeout(fadeTimer); fadeTimer = null; }
    if (!world) return;
    world.pause();
    world.currentTime = 0;
    if (worldGain) worldGain.gain.setValueAtTime(1, ctx.currentTime);
}

/**
 * Pushes the music down for a moment, so a sound effect can be heard over it.
 *
 * Called per effect rather than held open for a run of them: the recovery ramp
 * is restarted by the next duck, so a burst of effects holds the music down for
 * as long as the burst lasts and then releases once.
 */
export function duck() {
    if (!out || !playing()) return;
    const now = ctx.currentTime;

    // cancelAndHoldAtTime rather than cancelScheduledValues, because cancelling
    // an in-progress ramp removes it and restores the value from BEFORE it - so
    // reading .value back afterwards and setting it is an approximation of the
    // envelope rather than the envelope. Held is exact, and ducks arrive every
    // 17 to 100 ms, which is to say almost always mid-ramp.
    if (out.gain.cancelAndHoldAtTime) out.gain.cancelAndHoldAtTime(now);
    else {
        const held = out.gain.value;
        out.gain.cancelScheduledValues(now);
        out.gain.setValueAtTime(held, now);
    }

    out.gain.linearRampToValueAtTime(musicGain() * DUCK, now + DUCK_DOWN);
    out.gain.linearRampToValueAtTime(musicGain(), now + DUCK_DOWN + DUCK_UP);
}

/**
 * Fires the cave-complete sting.
 *
 * It runs 5.15 seconds against a cave-complete pause of 1.2, so it deliberately
 * outlives the moment that triggered it and plays on over the next cave. The
 * alternative is holding the screen for five seconds every cave, which across a
 * twenty-cave run is more than a minute of waiting for the same five seconds of
 * music.
 *
 * Restarts rather than overlaps. Clearing two caves inside five seconds is rare
 * but possible, and one sting playing twice at an offset against itself is a
 * flam, not a fanfare.
 */
export function playSting() {
    if (!sting) return false;

    // The world track wins. Nothing should reach here while it is playing - the
    // interlude holds the game - but the precedence is worth enforcing rather
    // than relying on a screen state somewhere else to guarantee it.
    if (worldStingPlaying()) return false;

    sting.currentTime = 0;
    const started = sting.play();
    if (started && typeof started.catch === 'function') started.catch(() => { });

    handover();
    return true;
}

/** How far the theme steps back while the sting has the foreground. */
const HANDOVER = 0.35;

/** Down quickly, because the sting starts immediately. */
const HANDOVER_DOWN = 0.25;

/** Back slowly, and only once the sting is genuinely tailing off. */
const HANDOVER_HOLD = 3.2;
const HANDOVER_UP = 1.6;

/**
 * An explicit musical handover, which is the other half of #36.
 *
 * The theme did not previously know the sting existed. Both played at the same
 * level into the same node and the result was two pieces of music at once,
 * which is not a transition - it is an overlap that happens to end.
 *
 * NOTE THE SHAPE, and why it is not a duck. A duck is 310 ms and exists to let
 * one effect through; this steps the theme back for the length of a phrase and
 * brings it forward again underneath the sting's tail. The sting outliving the
 * 1.2 second hold is DELIBERATE and documented below - it plays on over the
 * next cave rather than the screen waiting for it - so the handover has to be
 * shaped around a sting that is still sounding while the player is already
 * playing.
 */
function handover() {
    if (!out || !playing()) return;

    const now = ctx.currentTime;

    if (out.gain.cancelAndHoldAtTime) out.gain.cancelAndHoldAtTime(now);
    else {
        const held = out.gain.value;
        out.gain.cancelScheduledValues(now);
        out.gain.setValueAtTime(held, now);
    }

    out.gain.linearRampToValueAtTime(musicGain() * HANDOVER, now + HANDOVER_DOWN);
    out.gain.setValueAtTime(musicGain() * HANDOVER, now + HANDOVER_HOLD);
    out.gain.linearRampToValueAtTime(musicGain(), now + HANDOVER_HOLD + HANDOVER_UP);
}

/**
 * The sting's own level, independent of the theme's.
 *
 * Separate from setLevel on purpose: the slider is the player saying how loud
 * they want the MUSIC, and a completion cue that the player has to hear is not
 * the same decision as a theme that sits under the game.
 */
export function setStingLevel(value) {
    if (!stingGain) return;
    stingGain.gain.setValueAtTime(Math.max(0, Math.min(1, value)), ctx.currentTime);
}

/** Cuts the sting off. Used when the run ends, not between caves. */
export function stopSting() {
    if (!sting) return;
    sting.pause();
    sting.currentTime = 0;
}

/**
 * The whole 82-second track, for finishing a world.
 *
 * Its own element again, and its own gain, because this one gets faded out
 * rather than stopped: the player dismisses the world-complete screen whenever
 * they like, which is almost always mid-phrase, and cutting the music dead at
 * that point sounds like a fault rather than a choice.
 *
 * Firing it silences the cave sting. A world is finished by finishing a cave, so
 * both fire on the same frame, and two pieces of the same track playing at
 * different offsets is the worst of both.
 */
export function playWorldSting() {
    if (!world) return false;
    stopSting();

    // A pending fade from a previous world would otherwise pause this one
    // mid-phrase when its timer came due.
    if (fadeTimer !== null) { clearTimeout(fadeTimer); fadeTimer = null; }

    // The theme steps aside rather than playing underneath. Both are full mixes
    // of the same track at different points, and two of those at once is noise.
    // Remembered so it can be put back exactly as it was.
    themeWasPlaying = playing();
    if (themeWasPlaying) el.pause();

    worldGain.gain.cancelScheduledValues(ctx.currentTime);
    worldGain.gain.setValueAtTime(1, ctx.currentTime);
    world.currentTime = 0;
    const started = world.play();
    if (started && typeof started.catch === 'function') started.catch(() => { });
    return true;
}

/**
 * Restarts the theme if the world track displaced it.
 *
 * Separate from fadeWorldSting because the screen can also end by the track
 * finishing on its own, and the theme has to come back either way.
 */
export function resumeTheme() {
    if (!themeWasPlaying || !el) return;
    themeWasPlaying = false;
    const started = el.play();
    if (started && typeof started.catch === 'function') started.catch(() => { });
}

/** Whether the world track is still running, so a caller can wait for it. */
export function worldStingPlaying() {
    return !!world && !world.paused && !world.ended;
}

/** Fades the world track out over FADE seconds, then stops it. */
export function fadeWorldSting() {
    if (!world || world.paused) return;
    const now = ctx.currentTime;
    worldGain.gain.cancelScheduledValues(now);
    worldGain.gain.setValueAtTime(worldGain.gain.value, now);
    worldGain.gain.linearRampToValueAtTime(0.0001, now + FADE);
    if (fadeTimer !== null) clearTimeout(fadeTimer);
    fadeTimer = setTimeout(() => {
        fadeTimer = null;
        world.pause();
        world.currentTime = 0;
        worldGain.gain.setValueAtTime(1, ctx.currentTime);
    }, FADE * 1000);
}
