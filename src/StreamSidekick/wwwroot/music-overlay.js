const $ = selector => document.querySelector(selector);
const params = new URLSearchParams(window.location.search);
// OBS users can choose between one and five upcoming tracks with ?limit=3 in the source URL.
const queueLimit = Math.min(5, Math.max(1, Number.parseInt(params.get("limit") || "3", 10) || 3));
let bossFocusActive = false;
let lastMusicSignature = "";
let musicOverlayVisible = false;
let musicRefreshing = false;
let musicRefreshTimer;

function renderMusicVisibility(visible) {
  const wasVisible = musicOverlayVisible;
  musicOverlayVisible = visible === true;
  document.body.classList.toggle("music-hidden", !musicOverlayVisible);
  $("#music-content").setAttribute("aria-hidden", String(!musicOverlayVisible));
  if (musicOverlayVisible && !wasVisible) {
    // Show current metadata promptly without creating a second polling loop.
    window.clearTimeout(musicRefreshTimer);
    refresh();
  }
}

function setText(selector, value) {
  $(selector).textContent = value;
}

function renderBossFocus(active) {
  bossFocusActive = Boolean(active);
  const banner = $("#boss-focus-banner");
  banner.classList.toggle("active", bossFocusActive);
  banner.setAttribute("aria-hidden", String(!bossFocusActive));
}

function render(snapshot) {
  const playback = snapshot.playback;
  const track = playback?.track;
  const queue = (snapshot.queue || []).slice(0, queueLimit);
  // Playback progress changes constantly but is not displayed. Ignore it so OBS only rebuilds the
  // queue DOM when something a viewer can actually see has changed.
  const signature = JSON.stringify({
    connected: snapshot.connected,
    status: snapshot.status,
    playing: playback?.isPlaying,
    device: playback?.deviceName,
    track,
    requester: playback?.requester,
    queue
  });
  if (signature === lastMusicSignature) return;
  lastMusicSignature = signature;
  const state = $("#connection-state");
  state.classList.toggle("offline", !snapshot.connected || !playback);
  state.textContent = !snapshot.connected ? "Spotify offline" : playback?.isPlaying ? "Playing" : playback ? "Paused" : "No device";

  setText("#current-title", track?.title || (snapshot.connected ? "No active track" : "Spotify is not connected"));
  const currentCredit = playback?.requester ? ` · requested by ${playback.requester}` : "";
  setText("#current-detail", track ? `${track.artist}${currentCredit}` : (snapshot.status || "Open Spotify and start playback."));

  // Spotify may include autoplay tracks. Preserve them in queue order and credit known viewer requests.
  setText("#queue-summary", queue.length ? `${queue.length} upcoming` : "Viewer requests");
  const list = $("#queue-list");
  list.replaceChildren();
  if (!queue.length) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = snapshot.connected ? "No upcoming songs are queued." : "Connect Spotify in John's Twitch Handler.";
    list.append(empty);
    return;
  }

  queue.forEach((item, index) => {
    const row = document.createElement("div");
    row.className = "queue-row";
    const number = document.createElement("div");
    number.className = "queue-number";
    number.textContent = String(index + 1);
    const copy = document.createElement("div");
    copy.className = "queue-copy";
    const title = document.createElement("strong");
    title.textContent = `${item.track.title}${item.track.explicit ? " · E" : ""}`;
    const detail = document.createElement("small");
    detail.append(document.createTextNode(`${item.track.artist} · `));
    const credit = document.createElement("span");
    credit.className = item.requester ? "requester" : "";
    credit.textContent = item.requester ? `requested by ${item.requester}` : "Spotify queue";
    detail.append(credit);
    copy.append(title, detail);
    row.append(number, copy);
    list.append(row);
  });
}

async function refresh() {
  if (musicRefreshing) return;
  musicRefreshing = true;
  try {
    if (!musicOverlayVisible) return;
    const response = await fetch("/api/spotify/status", { cache: "no-store", signal: AbortSignal.timeout(8000) });
    if (!response.ok) throw new Error(`Status ${response.status}`);
    render(await response.json());
  } catch {
    render({ connected: false, status: "John's Twitch Handler is unavailable.", queue: [] });
  } finally {
    musicRefreshing = false;
    musicRefreshTimer = window.setTimeout(refresh, 5000);
  }
}

// Reuse the fast Boss Focus poll for visibility too. Both flags live in memory, so hiding or showing
// graphics needs no extra OBS or Spotify requests during a fight.
async function refreshBossFocus() {
  try {
    const response = await fetch("/api/overlay/status", { cache: "no-store", signal: AbortSignal.timeout(2500) });
    if (!response.ok) throw new Error(`Status ${response.status}`);
    const status = await response.json();
    renderBossFocus(status.tangiaBossFocusActive);
    renderMusicVisibility(status.musicOverlayVisible);
  } catch {
    // Preserve the last known state through a brief dashboard restart instead of flashing the
    // banner off in the middle of an encounter.
    renderBossFocus(bossFocusActive);
  } finally {
    window.setTimeout(refreshBossFocus, 1000);
  }
}

refreshBossFocus();
