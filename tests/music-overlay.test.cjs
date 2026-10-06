// Run from the repository root: node --test tests/music-overlay.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

function fixture() {
  const elements = new Map();
  function element(selector) {
    if (!elements.has(selector)) elements.set(selector, {
      textContent: '', attributes: {}, children: [], classes: new Set(),
      classList: { toggle(name, enabled) { elements.get(selector).classes[enabled ? 'add' : 'delete'](name); } },
      setAttribute(name, value) { this.attributes[name] = value; },
      replaceChildren() { this.children = []; }, append(...items) { this.children.push(...items); },
      addEventListener(event, callback) { this[event] = callback; }
    });
    return elements.get(selector);
  }
  const calls = [];
  const timers = new Map();
  let nextTimer = 0;
  const sandbox = {
    URLSearchParams, AbortSignal, console,
    window: { location: { search: '' }, setTimeout: (fn, delay) => { timers.set(++nextTimer, { fn, delay }); return nextTimer; }, clearTimeout: id => timers.delete(id) },
    document: { body: element('body'), querySelector: element, createElement: tag => element(`created-${elements.size}-${tag}`), createTextNode: value => value },
    fetch: async url => {
      calls.push(url);
      return { ok: true, json: async () => url === '/api/overlay/status'
        ? { musicOverlayVisible: false, tangiaBossFocusActive: false }
        : { connected: true, playback: null, queue: [] } };
    }
  };
  const context = vm.createContext(sandbox);
  return { context, sandbox, element, calls, timers };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test('Hidden overlay skips Spotify, preserves Boss Focus, and resumes one music loop', async () => {
  const f = fixture();
  vm.runInContext(readFileSync('src/StreamSidekick/wwwroot/music-overlay.js', 'utf8'), f.context);
  await settle();
  assert.deepEqual(f.calls, ['/api/overlay/status']);
  assert(f.element('body').classes.has('music-hidden'));
  vm.runInContext('renderBossFocus(true)', f.context);
  assert(f.element('#boss-focus-banner').classes.has('active'));
  vm.runInContext('renderMusicVisibility(true); renderMusicVisibility(true)', f.context);
  await settle();
  assert.equal(f.calls.filter(url => url === '/api/spotify/status').length, 1);
  assert(!f.element('body').classes.has('music-hidden'));
  assert.equal([...f.timers.values()].filter(t => t.delay === 5000).length, 1);
  vm.runInContext('renderMusicVisibility(false); refresh()', f.context);
  await settle();
  assert.equal(f.calls.filter(url => url === '/api/spotify/status').length, 1);
});

test('Dashboard only changes its displayed setting after a successful save', async () => {
  const f = fixture();
  const source = readFileSync('src/StreamSidekick/wwwroot/app.js', 'utf8');
  f.sandbox.state = { musicOverlayVisible: true, musicOverlayBusy: false };
  f.sandbox.$ = f.element;
  f.sandbox.toast = () => {};
  f.sandbox.api = async () => { throw new Error('Disk full'); };
  vm.runInContext(source.slice(source.indexOf('function renderMusicOverlayVisibility()'), source.indexOf('function renderSpotify()')), f.context);
  await f.element('#toggle-music-overlay').click();
  assert.equal(f.sandbox.state.musicOverlayVisible, true);
  assert.equal(f.element('#toggle-music-overlay').textContent, 'Hide overlay');
  f.sandbox.api = async () => ({ visible: false });
  await f.element('#toggle-music-overlay').click();
  assert.equal(f.sandbox.state.musicOverlayVisible, false);
  assert.equal(f.element('#toggle-music-overlay').textContent, 'Show overlay');
  assert.equal(f.element('#toggle-music-overlay').disabled, false);
});
