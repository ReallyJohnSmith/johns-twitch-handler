const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

const source = readFileSync('src/StreamSidekick/wwwroot/app.js', 'utf8');
const chatCode = source.slice(source.indexOf('function renderCombinedChat()'), source.indexOf('async function openGameChat()'));
const escapeCode = source.slice(source.indexOf('function escapeHtml('), source.indexOf('function formatOffset('));

function fixture(overrides = {}) {
  const elements = new Map();
  const element = selector => {
    if (!elements.has(selector)) elements.set(selector, {
      innerHTML: '', textContent: '', className: '', dataset: {}, hidden: false,
      scrollTop: 0, scrollHeight: 600, clientHeight: 200,
      getClientRects: () => [{}], removeAttribute(name) { delete this[name]; },
    });
    return elements.get(selector);
  };
  ['#chat-frame-wrap', '#chat-heading', '#open-twitch-chat', '#twitch-chat-list', '#twitch-chat-status'].forEach(element);
  const calls = [];
  const state = { channelName: 'samplechannel', youtubeVideoId: '', tikFinityEnabled: false,
    twitchChatFeed: null, twitchChatBusy: false, ...overrides };
  const sandbox = {
    state, AbortSignal, window: { location: { hostname: '127.0.0.1' } },
    $: selector => elements.get(selector) || null,
    renderTikFinity: () => {},
    api: async (url, options) => { calls.push({ url, options }); return { connected: true, messages: [] }; },
  };
  const context = vm.createContext(sandbox);
  vm.runInContext(escapeCode + '\n' + chatCode, context);
  const run = code => vm.runInContext(code, context);
  return { state, sandbox, element, elements, calls, run };
}

test('Twitch dashboard uses local messages, not Twitch embed headers or promotions', () => {
  const f = fixture();
  f.run('renderCombinedChat()');
  const html = f.element('#chat-frame-wrap').innerHTML;
  assert.match(html, /id="twitch-chat-list"/);
  assert.doesNotMatch(html, /<iframe|twitch\.tv\/embed|darkpopout/);
  assert.equal(f.element('#chat-heading').textContent, 'samplechannel chat');
  assert.equal(f.element('#open-twitch-chat').hidden, false);
  assert.equal(f.element('#open-twitch-chat').href, 'https://www.twitch.tv/popout/samplechannel/chat?popout=');
  const css = readFileSync('src/StreamSidekick/wwwroot/styles.css', 'utf8');
  assert.match(css, /\.chat-frame-wrap:not\(\.multiplatform\) > \.chat-column > strong \{ display: none; \}/);
});

test('YouTube, TikFinity, chime, and overlay controls remain available', () => {
  const f = fixture({ youtubeVideoId: 'abcdefghijk', tikFinityEnabled: true });
  f.run('renderCombinedChat()');
  assert.match(f.element('#chat-frame-wrap').innerHTML, /youtube\.com\/live_chat/);
  assert.match(f.element('#chat-frame-wrap').innerHTML, /id="tikfinity-chat-list"/);
  assert.match(f.element('#chat-frame-wrap').className, /platform-count-3/);
  const html = readFileSync('src/StreamSidekick/wwwroot/index.html', 'utf8');
  for (const id of ['copy-chat-overlay', 'chat-popout', 'chat-chime-enabled', 'test-chat-chime']) {
    assert.ok(html.includes(`id="${id}"`));
  }
  f.state.channelName = '';
  f.run('renderCombinedChat()');
  assert.equal(f.element('#open-twitch-chat').hidden, true);
  assert.equal(f.element('#open-twitch-chat').href, undefined);
  assert.doesNotMatch(f.element('#chat-frame-wrap').innerHTML, /id="twitch-chat-list"/);
});

test('Messages are escaped and capped at the latest 80 in their original order', () => {
  const f = fixture();
  f.state.twitchChatFeed = { connected: true, messages: Array.from({ length: 85 }, (_, i) => ({
    id: `message-${i}`, userName: `<viewer-${i}>`, text: `message-${i} <img src=x onerror=bad()> & text`,
  })) };
  f.run('renderTwitchChat()');
  const html = f.element('#twitch-chat-list').innerHTML;
  assert.equal((html.match(/class="twitch-chat-message"/g) || []).length, 80);
  assert.doesNotMatch(html, /<img|<viewer-/);
  assert.match(html, /&lt;viewer-5&gt;/);
  assert.match(html, /&lt;img src=x onerror=bad\(\)&gt; &amp; text/);
  assert.ok(html.indexOf('message-5 ') < html.indexOf('message-84 '));
  assert.doesNotMatch(html, /message-4 /);
  assert.equal(f.element('#twitch-chat-status').hidden, true);
});

test('Unchanged chat is not repainted and manual scrollback is preserved', () => {
  const f = fixture({ twitchChatFeed: { connected: true, messages: [{ userName: 'Viewer', text: 'Hello' }] } });
  f.run('renderTwitchChat()');
  const list = f.element('#twitch-chat-list');
  assert.equal(list.scrollTop, 600);
  list.scrollTop = 80;
  f.run('renderTwitchChat()');
  assert.equal(list.scrollTop, 80);
  f.state.twitchChatFeed.messages.push({ userName: 'Viewer', text: 'New message' });
  f.run('renderTwitchChat()');
  assert.equal(list.scrollTop, 80);
  list.scrollTop = 400;
  f.state.twitchChatFeed.messages.push({ userName: 'Viewer', text: 'Newest message' });
  f.run('renderTwitchChat()');
  assert.equal(list.scrollTop, 600);
});

test('Empty, disconnected, and temporarily unavailable states are explained', async () => {
  const f = fixture();
  await f.run('refreshTwitchChat()');
  assert.match(f.element('#twitch-chat-list').textContent, /Waiting for new Twitch messages/);
  f.state.twitchChatFeed = { connected: false, messages: [] };
  f.run('renderTwitchChat()');
  assert.match(f.element('#twitch-chat-list').textContent, /Connect with Twitch in Settings/);
  assert.equal(f.element('#twitch-chat-status').hidden, false);
  f.state.twitchChatFeed.messages.push({ userName: 'Viewer', text: 'Keep this message' });
  f.sandbox.api = async () => { throw new Error('Offline'); };
  await f.run('refreshTwitchChat()');
  assert.match(f.element('#twitch-chat-list').innerHTML, /Keep this message/);
  assert.match(f.element('#twitch-chat-status').textContent, /Retrying/);
  assert.equal(f.state.twitchChatBusy, false);
});

test('Polling skips hidden/unconfigured chat, cannot overlap, and discards old-channel results', async () => {
  const f = fixture();
  f.element('#twitch-chat-list').getClientRects = () => [];
  await f.run('refreshTwitchChat()');
  assert.equal(f.calls.length, 0);
  f.element('#twitch-chat-list').getClientRects = () => [{}];
  f.state.channelName = '';
  await f.run('refreshTwitchChat()');
  assert.equal(f.calls.length, 0);
  f.state.channelName = 'samplechannel';
  let resolve;
  let count = 0;
  f.sandbox.api = () => { count++; return new Promise(done => { resolve = done; }); };
  const pending = f.run('refreshTwitchChat()');
  await f.run('refreshTwitchChat()');
  assert.equal(count, 1);
  f.state.channelName = 'differentchannel';
  resolve({ connected: true, messages: [{ userName: 'Old room', text: 'Ignore' }] });
  await pending;
  assert.equal(f.state.twitchChatFeed, null);
  assert.equal(f.state.twitchChatBusy, false);
  assert.match(source, /startPolling\(refreshTwitchChat, 1200, "chat"\)/);
});
