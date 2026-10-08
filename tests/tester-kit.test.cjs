const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const root = path.resolve(__dirname, '..');
const guidePath = 'src/StreamSidekick/wwwroot/tester-guide.html';
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const supportingFiles = ['README.md', 'CHECKLIST.md', 'FEEDBACK.md', 'KNOWN-LIMITATIONS.md'];
const markdownFiles = [
  'README.md', 'PROJECT_NOTES.md', 'RELEASE_WORKFLOW.md', 'docs/media/README.md',
  ...supportingFiles.map(name => `docs/tester-kit/${name}`),
  'docs/tester-kit/INVITATION.md',
];

test('tester-facing prose uses product branding and has no broken local document links', () => {
  const files = [...markdownFiles, guidePath, 'packaging/README FIRST.txt',
    '.github/ISSUE_TEMPLATE/problem.yml', '.github/ISSUE_TEMPLATE/feedback.yml'];
  const assistantBranding = /chat\s*gpt|codex/i;
  for (const name of files) {
    assert.doesNotMatch(read(name), assistantBranding, name);
  }
  for (const name of markdownFiles) {
    for (const match of read(name).matchAll(/\[[^\]]*\]\(([^)]+)\)/g)) {
      const target = match[1];
      if (/^(?:https?:|mailto:|#)/i.test(target)) continue;
      const localPath = target.split('#')[0];
      assert.ok(fs.existsSync(path.resolve(root, path.dirname(name), localPath)), `${name}: ${target}`);
    }
  }
});

test('offline guide is self-contained, navigable, printable, and does not submit data', () => {
  const html = read(guidePath);
  const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
  assert.equal(ids.length, new Set(ids).size, 'duplicate section ids');
  for (const match of html.matchAll(/href="#([^"]+)"/g)) {
    assert.ok(ids.includes(match[1]), `missing section ${match[1]}`);
  }
  assert.doesNotMatch(html, /<(?:script|iframe|form|link|img)\b/i, 'offline guide must not load resources or send form data');
  assert.match(html, /@media print/);
  assert.match(html, /<label><input type="checkbox">/);
  assert.match(html, /Only the guided-demo panel uses sample data/);
  assert.match(html, /Send that one downloaded file privately to John/);
  assert.match(read('src/StreamSidekick/wwwroot/index.html'), /href="\/tester-guide\.html"[^>]*>Tester guide<\/a>/);
});

test('published tour links point to the approved video and captions in the repository', () => {
  const videoPath = 'docs/media/Johns-Twitch-Handler-60-Second-Tour.mp4';
  const captionsPath = 'docs/media/Johns-Twitch-Handler-60-Second-Tour.srt';
  const downloadBase = 'https://github.com/ReallyJohnSmith/johns-twitch-handler/raw/refs/heads/main/';
  for (const name of ['README.md', 'docs/media/README.md', 'docs/tester-kit/README.md',
    'docs/tester-kit/KNOWN-LIMITATIONS.md', 'docs/tester-kit/INVITATION.md', guidePath]) {
    assert.ok(read(name).includes(downloadBase + videoPath), `${name}: missing video link`);
  }
  assert.ok(read('README.md').includes(downloadBase + captionsPath));
  const video = fs.readFileSync(path.join(root, videoPath));
  assert.equal(video.toString('ascii', 4, 8), 'ftyp', 'expected an MP4, not a pointer or HTML page');
  assert.ok(video.length > 1_000_000 && video.length < 10_000_000);
  const captions = read(captionsPath);
  assert.equal([...captions.matchAll(/ --> /g)].length, 8);
  assert.match(captions, /00:00:00,000 --> 00:00:06,500/);
  assert.match(captions, /00:00:52,250 --> 00:01:00,000/);
  assert.doesNotMatch(read(guidePath), /<video\b/i, 'the offline guide should link to, not preload, the tour');
});

test('feedback links name real forms and version notes match the approved version', () => {
  for (const match of read(guidePath).matchAll(/issues\/new\?template=([^"&]+)/g)) {
    assert.ok(fs.existsSync(path.join(root, '.github/ISSUE_TEMPLATE', match[1])));
  }
  const version = read('RELEASE_VERSION.txt').trim();
  assert.ok(read(guidePath).includes(`last approved release is ${version}`));
  assert.ok(read('docs/tester-kit/KNOWN-LIMITATIONS.md').includes(`**${version}**`));
  for (const name of ['problem.yml', 'feedback.yml']) {
    const form = read(`.github/ISSUE_TEMPLATE/${name}`);
    assert.match(form, /^name: /m);
    assert.match(form, /^description: /m);
    assert.match(form, /^body:/m);
    assert.match(form, /report as public/);
    const ids = [...form.matchAll(/^    id: ([\w-]+)$/gm)].map(match => match[1]);
    assert.equal(ids.length, new Set(ids).size, `${name}: duplicate field ids`);
  }
});

test('Windows packaging copies exactly the reviewed files, with the same offline guide',
  { skip: process.platform !== 'win32' }, () => {
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'handler-tester-kit-'));
    try {
      const destination = path.join(tempRoot, 'Tester kit');
      const powershell = path.join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
      // Allow this local helper only in the child process; do not change machine policy.
      const result = spawnSync(powershell, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'RemoteSigned', '-File',
        path.join(root, 'packaging/copy-tester-kit.ps1'), '-DestinationDirectory', destination],
      { encoding: 'utf8', windowsHide: true });
      assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
      assert.deepEqual(fs.readdirSync(destination).sort(), [...supportingFiles, 'START HERE.html'].sort());
      assert.deepEqual(fs.readFileSync(path.join(destination, 'START HERE.html')), fs.readFileSync(path.join(root, guidePath)));
      for (const name of supportingFiles) {
        assert.equal(fs.readFileSync(path.join(destination, name), 'utf8'), read(`docs/tester-kit/${name}`));
      }
      assert.match(read('build-tester-package.ps1'), /copy-tester-kit\.ps1.*-DestinationDirectory.*Tester kit/);
    } finally {
      // Only remove the unique scratch directory created by this test.
      const resolved = path.resolve(tempRoot);
      assert.equal(path.dirname(resolved).toLowerCase(), path.resolve(os.tmpdir()).toLowerCase());
      assert.match(path.basename(resolved), /^handler-tester-kit-/);
      fs.rmSync(resolved, { recursive: true, force: true });
    }
  });
