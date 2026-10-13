// Checks the cartoon's own rules: which frame shows, when it ends, where it sits.
//
//   node tools/jscartoon.mjs
//
// The rules are the original's and are read from its code in docs/FORMATS.md,
// "When each cartoon plays". They live in wwwroot/cartoon.js with no DOM in them
// so that plain Node can hold them to it, the way tools/jslifecycle.mjs does for
// game.js.

import { strict as assert } from 'node:assert';
import { pathToFileURL } from 'node:url';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const mod = await import(pathToFileURL(join(here, '..', 'web', 'Rockford.Web', 'wwwroot', 'cartoon.js')).href);

let failed = 0;
function check(name, fn) {
    try { fn(); console.log('ok   ' + name); }
    catch (e) { failed++; console.log('FAIL ' + name + '\n     ' + e.message.split('\n')[0]); }
}

check('six frames of 150 ms, looped', () => {
    assert.equal(mod.frameAt(0), 0);
    assert.equal(mod.frameAt(149), 0);
    assert.equal(mod.frameAt(150), 1);
    assert.equal(mod.frameAt(899), 5);
    assert.equal(mod.frameAt(900), 0);
});

check('a key does nothing in the first second', () => {
    assert.equal(mod.finished(0, true), false);
    assert.equal(mod.finished(999, true), false);
});

check('after a second a key ends it, and nothing else does', () => {
    assert.equal(mod.finished(1000, true), true);
    assert.equal(mod.finished(1000, false), false);
    assert.equal(mod.finished(5999, false), false);
});

check('at six seconds it ends by itself', () => {
    assert.equal(mod.finished(6000, false), true);
});

check('a strip\'s frame size comes from its width: 48 faithful, 384 in VIP', () => {
    assert.equal(mod.frameSize(288), 48);
    assert.equal(mod.frameSize(2304), 384);
});

check('the window is centred on the player, 17 pixels left of and above the cell', () => {
    assert.deepEqual(mod.boxFor({ x: 10, y: 5 }, { x: 0, y: 0 }), { x: 143, y: 63 });
});

check('the window is placed relative to the view, not the cave', () => {
    assert.deepEqual(mod.boxFor({ x: 30, y: 12 }, { x: 20, y: 6 }), { x: 143, y: 79 });
});

check('the window is held 15 pixels inside the top and left', () => {
    assert.deepEqual(mod.boxFor({ x: 0, y: 0 }, { x: 0, y: 0 }), { x: 15, y: 15 });
});

check('the window is held 17 pixels inside the bottom and right', () => {
    // 320 - 48 - 17 and 176 - 48 - 17.
    assert.deepEqual(mod.boxFor({ x: 19, y: 10 }, { x: 0, y: 0 }), { x: 255, y: 111 });
});

if (failed) { console.log(`\n${failed} failed`); process.exit(1); }
console.log('\nall passed');
