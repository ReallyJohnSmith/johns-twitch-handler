// Isolated visual fixture: serves the real UI, with no accounts, OBS connections,
// system monitoring, or access to the installed Handler's data directory.
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const root = path.resolve(__dirname, '../src/StreamSidekick/wwwroot');
let setupAllowed = false, obsRequests = 0;
const json = (response, value, status = 200) => {
  response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
  response.end(JSON.stringify(value));
};
http.createServer(async (request, response) => {
  try {
    const pathname = decodeURIComponent(new URL(request.url, 'http://127.0.0.1').pathname);
    if (pathname === '/fixture/status') return json(response, { setupAllowed, obsRequests });
    if (pathname === '/fixture/allow-startup' && request.method === 'POST') { setupAllowed = true; return json(response, { ok: true }); }
    if (pathname === '/fixture/narrow') {
      response.writeHead(200, { 'Content-Type': 'text/html' });
      return response.end('<!doctype html><title>390px isolated layout</title><body style="margin:0;background:#ddd"><iframe title="Narrow dashboard fixture" src="/index.html" style="width:390px;height:950px;border:0"></iframe>');
    }
    if (pathname === '/api/dashboard-layout') return json(response, { panelOrder: [], panelPages: {} });
    if (pathname === '/api/setup') return setupAllowed ? json(response, {
      releaseChannel: 'Development', releaseLabel: 'ISOLATED UI FIXTURE', releaseVersion: 'preview',
      wowToolsEnabled: false, twitchApplicationConfigured: false, spotifyApplicationConfigured: false,
      spotifyEnabledByDefault: false, tangiaEnabledByDefault: false, streamElementsEnabledByDefault: false, tikFinityEnabledByDefault: false
    }) : json(response, { error: 'Preview helper is unavailable. This is an isolated startup-recovery test.' }, 503);
    if (pathname === '/api/tikfinity/enabled') return json(response, { enabled: false, connected: false, messages: [], status: 'Off in preview' });
    if (pathname === '/api/obs/status') return json(response, { connected: false, scenes: [], currentScene: null });
    if (pathname === '/api/obs/audio') return json(response, { connected: false, inputs: [] });
    if (pathname === '/api/preflight') return json(response, {
      obsConnected: false, microphoneReady: false, microphone: { connected: false, status: 'Connect OBS when ready' },
      nvidiaBroadcast: { installed: false, running: false }, twitchConfigured: false, twitchRewardsReady: false,
      sessionActive: false, streamElementsReady: true, streamElements: {}, tikFinityReady: true, tikFinity: {}
    });
    if (pathname === '/api/obs/connect') {
      obsRequests++;
      return setTimeout(() => json(response, { error: 'Preview only: this fixture never connects to OBS.' }, 400), 1000);
    }
    if (pathname.startsWith('/api/')) return json(response, { error: 'This tool is unavailable in the isolated UI fixture.' }, 503);
    const target = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
    if (!target.startsWith(root + path.sep)) return json(response, { error: 'Not found' }, 404);
    const content = await fs.readFile(target);
    const type = { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml' }[path.extname(target)] || 'text/plain';
    response.writeHead(200, { 'Content-Type': type + '; charset=utf-8', 'Cache-Control': 'no-store' }); response.end(content);
  } catch { json(response, { error: 'Not found' }, 404); }
}).listen(5286, '127.0.0.1', () => console.log('Isolated product-review fixture: http://127.0.0.1:5286 — no real integrations or monitoring.'));
