/* Hosted entry point. Only this module knows the loopback address or pairing credential.
   The original dashboard is loaded after a successful handshake. */
"use strict";
(async () => {
  const helperOrigin = "http://127.0.0.1:5274";
  const nativeFetch = window.fetch.bind(window);
  let token = null;
  let launched = false;
  let connectionLost = false;
  let database;
  const gate = document.createElement("section");
  gate.id = "website-connection-screen";
  gate.className = "connection-page";
  gate.setAttribute("aria-label", "Connect your Windows helper");
  gate.innerHTML = `<div class="connection-card">
    <a href="index.html">← Overview</a> · <a href="demo.html">Explore the sample demo</a>
    <span class="connection-eyebrow">JOHN'S TWITCH HANDLER · CONNECT YOUR PC</span>
    <h1>Your stream.<br>Your dashboard.</h1>
    <p>Use your full dashboard here. The Windows helper runs OBS controls and internet monitoring on your computer—even after you close this tab.</p>
    <ol class="connection-steps">
      <li><strong>Install and start the Windows helper</strong><p class="connection-note" id="website-download-note">This is a development preview. The updated helper installer has not been released yet. An older installer cannot connect to this website.</p><a href="/tester-guide.html" target="_blank" rel="noopener">Read the setup guide ↗</a></li>
      <li><strong>Approve this website on your computer</strong><p class="connection-note">Open the helper's connection page, check the website address, and create a one-time code.</p><a id="website-open-pair" target="_blank" rel="noopener noreferrer">Open helper connection page ↗</a></li>
      <li><form id="website-pair-form"><label for="website-pair-code"><strong>Enter your pairing code</strong></label><input id="website-pair-code" placeholder="ABCD-1234-EF56" maxlength="14" autocomplete="off" spellcheck="false" required><button class="connection-primary" type="submit">Connect this browser</button></form></li>
    </ol><div class="connection-actions"><a class="connection-button" href="johnshandler://start">Start installed helper</a><button id="website-reconnect" type="button">Check connection</button><button id="website-forget" type="button" hidden>Forget this browser</button></div>
    <p id="website-connection-message" role="status" aria-live="polite">Connect once on each browser you use.</p>
    <p class="connection-note">OBS, Twitch, and Spotify credentials stay in your Windows helper. Your browser remembers its permission to control this PC. Pair only on a computer you trust.</p>
  </div>`;
  document.body.classList.add("website-awaiting");
  document.body.append(gate);
  gate.querySelector("#website-open-pair").href = `${helperOrigin}/pair.html`;
  const message = gate.querySelector("#website-connection-message");
  function say(text, error = false) { message.textContent = text; message.classList.toggle("connection-error", error); }
  function showGate(text) {
    document.querySelectorAll("dialog[open]").forEach(dialog => dialog.close());
    gate.hidden = false; document.body.classList.add("website-awaiting"); say(text, true);
  }

  // Store the helper capability separately from ordinary dashboard preferences. No
  // account passwords/tokens are copied from the helper, and no secret enters a URL.
  function openDatabase() {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open("johns-handler-pairing", 1);
      request.onupgradeneeded = () => request.result.createObjectStore("connection");
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(new Error("Allow this website to save browser data so it can remember your pairing."));
    });
  }
  function storage(mode, value) {
    return new Promise((resolve, reject) => {
      const tx = database.transaction("connection", mode === "read" ? "readonly" : "readwrite");
      const store = tx.objectStore("connection");
      const request = mode === "read" ? store.get(helperOrigin) : mode === "clear" ? store.delete(helperOrigin) : store.put(value, helperOrigin);
      tx.oncomplete = () => resolve(request.result);
      tx.onerror = tx.onabort = () => reject(new Error("Could not save this browser's pairing. Check website storage permissions."));
    });
  }
  async function helperRequest(path, options = {}) {
    if (!path.startsWith("/api/") && !path.startsWith("/bridge/")) throw new Error("Unsupported helper request.");
    const headers = new Headers(options.headers);
    if (token) headers.set("X-Handler-Pairing", token);
    return nativeFetch(`${helperOrigin}${path}`, {
      ...options, headers, mode: "cors", credentials: "omit", cache: "no-store", redirect: "error",
      targetAddressSpace: "loopback", signal: options.signal || AbortSignal.timeout(12000)
    });
  }
  window.HandlerConnection = Object.freeze({
    helperOrigin,
    async fetch(path, options = {}) {
      // Never fall back to a cloud API or accidentally send the credential elsewhere.
      if (!token || connectionLost) throw new Error("Connect your Windows helper to continue.");
      try {
        const response = await helperRequest(path, options);
        if (response.status === 401 || response.status === 403) {
          connectionLost = true;
          showGate("This browser's connection is no longer authorized. Create a new pairing code in your helper.");
        }
        return response;
      } catch (error) {
        if (options.signal?.aborted) throw error;
        connectionLost = true;
        showGate("The helper is unavailable. Start it on this computer, allow local access if your browser asks, then select Check connection.");
        throw error;
      }
    }
  });
  async function startDashboard() {
    connectionLost = false;
    gate.hidden = true;
    document.body.classList.remove("website-awaiting");
    if (launched) return;
    launched = true;
    const privacy = document.querySelector(".privacy-pill");
    if (privacy) { privacy.textContent = "CONNECTED TO YOUR PC"; privacy.title = "The dashboard loads from this website. Credentials and reports are stored by your local Windows helper."; }
    const status = document.createElement("div");
    status.className = "website-connection-status";
    const label = document.createElement("span"); label.textContent = "Windows helper connected";
    const manage = document.createElement("button"); manage.textContent = "Connection";
    manage.addEventListener("click", () => { gate.hidden = false; say("This browser is paired. Close this panel to return to your dashboard."); });
    status.append(label, manage); document.body.append(status);
    const back = document.createElement("button"); back.textContent = "Back to dashboard";
    back.addEventListener("click", () => { if (!connectionLost) gate.hidden = true; else checkConnection(); });
    gate.querySelector(".connection-actions").append(back);
    document.addEventListener("click", async event => {
      const link = event.target.closest("a[href]");
      if (!link) return;
      const url = new URL(link.href, location.href);
      if (url.origin !== location.origin || !url.pathname.startsWith("/api/")) return;
      event.preventDefault();
      try {
        const response = await window.HandlerConnection.fetch(url.pathname + url.search);
        if (!response.ok) throw new Error("Download failed. Check your helper connection.");
        const objectUrl = URL.createObjectURL(await response.blob());
        const download = document.createElement("a"); download.href = objectUrl;
        download.download = link.getAttribute("download") || "handler-diagnostics.json";
        document.body.append(download); download.click(); download.remove();
        setTimeout(() => URL.revokeObjectURL(objectUrl), 30000);
      } catch (error) { showGate(error.message); }
    });
    const script = document.createElement("script"); script.src = "app.js?v=website-1";
    script.onerror = () => { launched = false; showGate("The dashboard could not load. Reload this page."); };
    document.body.append(script);
  }
  async function checkConnection() {
    say("Checking your Windows helper…");
    try {
      const status = await helperRequest("/bridge/status");
      if (!status.ok || (await status.json()).protocol !== 1) throw new Error("Install the website-enabled helper to connect.");
      if (!token) { say("Helper found. Open its connection page and enter the pairing code."); return; }
      const health = await helperRequest("/api/health");
      if (!health.ok) throw new Error("Your saved pairing is no longer valid. Create a fresh code in the helper.");
      gate.querySelector("#website-forget").hidden = false;
      // A disconnect can interrupt initial dashboard setup. Reload on recovery
      // rather than leaving a partially initialized dashboard or duplicate timers.
      if (launched && connectionLost) { location.reload(); return; }
      await startDashboard();
    } catch (error) {
      showGate(error instanceof TypeError || error.name === "TimeoutError"
        ? "Cannot reach your helper. Start it on this Windows computer and allow local network access if prompted. Chrome or Edge is recommended for this preview."
        : error.message);
    }
  }
  gate.querySelector("#website-reconnect").addEventListener("click", checkConnection);
  gate.querySelector("#website-pair-form").addEventListener("submit", async event => {
    event.preventDefault();
    const button = event.submitter; button.disabled = true;
    try {
      if (!database) database = await openDatabase();
      const response = await helperRequest("/bridge/connect", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ code: gate.querySelector("#website-pair-code").value.trim() }) });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error || "Pairing failed.");
      token = data.token;
      try { await storage("write", token); }
      catch (error) { await helperRequest("/bridge/disconnect", { method: "POST" }).catch(() => {}); token = null; throw error; }
      gate.querySelector("#website-pair-code").value = "";
      await checkConnection();
    } catch (error) { say(error.message, true); }
    finally { button.disabled = false; }
  });
  gate.querySelector("#website-forget").addEventListener("click", async () => {
    try {
      // Revoke on the helper before forgetting locally; never claim revocation offline.
      const response = await helperRequest("/bridge/disconnect", { method: "POST" });
      if (!response.ok && response.status !== 401) throw new Error("Start your helper to remove this connection, or revoke it on the helper's connection page.");
      await storage("clear"); token = null; location.reload();
    } catch (error) { say(error.message, true); }
  });
  try {
    database = await openDatabase(); token = await storage("read") || null;
    gate.querySelector("#website-forget").hidden = !token;
    // First local access requires an explicit click. Remembered browsers reconnect
    // automatically only if the browser reports permission was already granted.
    if (token) {
      say("Pairing remembered. Select Check connection to open your dashboard.");
      try { if ((await navigator.permissions.query({ name: "loopback-network" })).state === "granted") await checkConnection(); } catch {}
    }
  } catch (error) { say(error.message, true); }
})();
