const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const source = readFileSync('src/StreamSidekick/wwwroot/website-connect.js', 'utf8');

async function fixture(savedToken = null) {
  const nodes = new Map();
  const appended = [];
  const calls = [];
  const events = new Map();
  const memory = new Map(savedToken ? [['http://127.0.0.1:5274', savedToken]] : []);
  const make = () => ({ hidden: false, disabled: false, value: '', textContent: '', listeners: {},
    classList: { add() {}, remove() {}, toggle() {} },
    addEventListener(name, handler) { this.listeners[name] = handler; },
    setAttribute() {}, append(...items) { appended.push(...items); },
    querySelector(selector) { if (!nodes.has(selector)) nodes.set(selector, make()); return nodes.get(selector); },
  });
  const document = { body: make(), createElement: make,
    querySelector: () => null, querySelectorAll: () => [],
    addEventListener: (name, handler) => events.set(name, handler),
  };
  const db = { transaction() {
    const tx = { objectStore: () => ({
      get: key => request(memory.get(key)),
      put: (value, key) => { memory.set(key, value); return request(); },
      delete: key => { memory.delete(key); return request(); },
    }) };
    function request(result) { const value = { result }; setImmediate(() => tx.oncomplete?.()); return value; }
    return tx;
  } };
  let fail = false;
  let revoked = false;
  const window = { fetch: async (url, options) => {
    calls.push({ url, options });
    if (fail) throw new TypeError('offline');
    const path = new URL(url).pathname;
    if (path === '/bridge/connect') return Response.json({ token: 'A'.repeat(64), protocol: 1 });
    if (path === '/bridge/status') return Response.json({ protocol: 1 });
    if (path === '/bridge/disconnect') { revoked = true; return new Response(null, { status: 204 }); }
    return Response.json({ ok: true });
  } };
  const location = { origin: 'https://dashboard.example', href: 'https://dashboard.example/', reloadCount: 0,
    reload() { this.reloadCount++; } };
  const context = vm.createContext({ document, window, location, navigator: { permissions: { query: async () => ({ state: 'prompt' }) } },
    indexedDB: { open() { const request = { result: db }; setImmediate(() => request.onsuccess()); return request; } },
    Headers, Response, URL, AbortSignal, setTimeout, clearTimeout, console,
  });
  await vm.runInContext(source, context);
  const click = async selector => nodes.get(selector).listeners.click();
  const pair = async () => {
    if (!nodes.has('#website-pair-code')) nodes.set('#website-pair-code', make());
    nodes.get('#website-pair-code').value = 'ABCD-1234-EF56';
    await nodes.get('#website-pair-form').listeners.submit({ preventDefault() {}, submitter: make() });
  };
  return { window, location, nodes, calls, memory, appended, click, pair, events,
    offline(value) { fail = value; }, isRevoked: () => revoked };
}

test('first visit does not contact localhost or load the dashboard before user action', async () => {
  const f = await fixture();
  assert.equal(f.calls.length, 0);
  assert.equal(f.appended.filter(item => item.src?.startsWith('app.js')).length, 0);
  await assert.rejects(f.window.HandlerConnection.fetch('/api/health'), /Connect/);
  assert.equal(f.calls.length, 0);
});
test('remembered pairing still waits for a user gesture if browser permission is not granted', async () => {
  const f = await fixture('B'.repeat(64));
  assert.equal(f.calls.length, 0);
  await f.click('#website-reconnect');
  assert.equal(f.appended.filter(item => item.src?.startsWith('app.js')).length, 1);
  await f.click('#website-reconnect');
  assert.equal(f.appended.filter(item => item.src?.startsWith('app.js')).length, 1);
});
test('pairing persists only the helper capability and routes calls directly to loopback', async () => {
  const f = await fixture(); await f.pair();
  assert.equal(f.memory.get('http://127.0.0.1:5274'), 'A'.repeat(64));
  assert.equal(f.nodes.get('#website-pair-code').value, '');
  assert.equal(f.appended.filter(item => item.src?.startsWith('app.js')).length, 1);
  await f.window.HandlerConnection.fetch('/api/obs/scene', { method: 'POST', body: '{"sceneName":"Test"}' });
  const last = f.calls.at(-1);
  assert.equal(last.url, 'http://127.0.0.1:5274/api/obs/scene');
  assert.equal(last.options.headers.get('X-Handler-Pairing'), 'A'.repeat(64));
  assert.equal(last.options.credentials, 'omit');
  assert.equal(last.options.redirect, 'error');
  assert.equal(last.options.targetAddressSpace, 'loopback');
  assert.ok(f.calls.every(call => !call.url.includes('A'.repeat(64))));
});
test('capability is never sent to a supplied external URL', async () => {
  const f = await fixture(); await f.pair();
  const count = f.calls.length;
  await assert.rejects(f.window.HandlerConnection.fetch('https://evil.example/api/health'), /Unsupported/);
  assert.equal(f.calls.length, count);
});
test('offline helper blocks additional polling and recovery reloads interrupted initialization', async () => {
  const f = await fixture(); await f.pair();
  f.offline(true);
  await assert.rejects(f.window.HandlerConnection.fetch('/api/health'));
  const count = f.calls.length;
  await assert.rejects(f.window.HandlerConnection.fetch('/api/health'), /Connect/);
  assert.equal(f.calls.length, count);
  f.offline(false); await f.click('#website-reconnect');
  assert.equal(f.location.reloadCount, 1);
});
test('forget revokes on helper before removing browser storage', async () => {
  const f = await fixture(); await f.pair();
  f.offline(true); await f.click('#website-forget');
  assert.equal(f.memory.size, 1);
  f.offline(false); await f.click('#website-forget');
  assert.equal(f.isRevoked(), true);
  assert.equal(f.memory.size, 0);
  assert.equal(f.location.reloadCount, 1);
});
