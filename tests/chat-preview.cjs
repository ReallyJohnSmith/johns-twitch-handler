// Isolated visual fixture: real chat markup, styles, and rendering; sample messages only.
// Does not start the Handler, read user preferences, or connect to OBS/Twitch.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../src/StreamSidekick/wwwroot');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
const start = html.indexOf('<section class="panel chat-panel"');
const panel = html.slice(start, html.indexOf('</section>', start) + '</section>'.length);
const code = source.slice(source.indexOf('function renderCombinedChat()'), source.indexOf('async function openGameChat()'));
const escapeCode = source.slice(source.indexOf('function escapeHtml('), source.indexOf('function formatOffset('));
if (start < 0 || !code.startsWith('function renderCombinedChat')) throw new Error('Chat fixture source markers changed.');
const page = `<!doctype html><html lang="en" data-theme="ocean"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Chat preview — sample messages only</title><link rel="stylesheet" href="/styles.css"><style>body{padding:18px;overflow:auto}main{max-width:850px;margin:auto}.chat-panel{width:100%}.preview-note{color:var(--muted);margin:0 0 14px;font-size:13px}</style></head><body data-visual-style="broadcast" data-user-mode="advanced"><main><p class="preview-note">Isolated chat preview · sample messages only</p>${panel}</main><script>
const $ = selector => document.querySelector(selector);
const state = {channelName:'samplechannel',youtubeVideoId:'',tikFinityEnabled:false,twitchChatFeed:null,twitchChatBusy:false};
const api = async url => (await fetch(url)).json();
const renderTikFinity = () => {};
${escapeCode}
${code}
document.querySelector('#chat-chime-status').textContent='Preview only — no OBS or account connections.';
renderCombinedChat(); refreshTwitchChat();
</script></body></html>`;
http.createServer((request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1:5288');
  if (url.pathname === '/styles.css') {
    response.writeHead(200, { 'Content-Type': 'text/css' });
    return response.end(fs.readFileSync(path.join(root, 'styles.css')));
  }
  if (url.pathname === '/api/chat/messages') {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    return response.end(JSON.stringify({ connected: true, messages: [
      { userName: 'SampleViewer', text: 'Hey! Ready for the stream?' },
      { userName: 'AnotherViewer', text: 'The message area is much cleaner now.' },
      { userName: 'SampleViewer', text: 'Longer messages wrap inside the panel instead of pushing the layout wider. No leaderboard or promotional banner above them.' },
      { userName: 'ChatTest', text: 'Emote names and ordinary text stay readable. <This is plain text, not HTML.>' },
    ] }));
  }
  response.writeHead(200, { 'Content-Type': 'text/html' });
  response.end(page);
}).listen(5288, '127.0.0.1', () => console.log('Chat preview: http://127.0.0.1:5288'));
