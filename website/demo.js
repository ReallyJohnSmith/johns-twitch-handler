"use strict";
// An isolated, in-memory illustration. No fetch, helper transport, account APIs,
// or browser storage are used anywhere in this demo.
const demoState = { step: 0, explored: new Set(), scene: "Gameplay", muted: false, markers: 0, replays: 0, connectionEvents: 0, network: "32 ms · sample", hiccup: false, events: [] };
const nextLabels = ["Next: run the show", "Next: spot a hiccup", "Next: review the session", "Back to overview"];
const qs = selector => document.querySelector(selector);

function announce(message) { qs("#demo-status").textContent = message; }
function completeStep(step) {
  demoState.explored.add(step);
  document.querySelectorAll("[data-step]").forEach(button => button.classList.toggle("complete", demoState.explored.has(Number(button.dataset.step))));
  qs("#demo-progress").textContent = `${demoState.explored.size} of 4 steps explored`;
}
function addEvent(message) {
  demoState.events.push(message);
  if (demoState.events.length > 40) demoState.events.shift();
  renderSnapshot();
}
function renderSnapshot() {
  qs("#snapshot-scene").textContent = demoState.scene;
  qs("#snapshot-mic").textContent = demoState.muted ? "Muted" : "On";
  qs("#snapshot-markers").textContent = String(demoState.markers);
  qs("#snapshot-network").textContent = demoState.network;
  qs("#demo-marker-count").textContent = String(demoState.markers);
  qs("#demo-replay-count").textContent = String(demoState.replays);
  qs("#demo-event-count").textContent = String(demoState.connectionEvents);
  const list = qs("#demo-report-events"); list.replaceChildren();
  if (!demoState.events.length) { const empty = document.createElement("p"); empty.textContent = "Your sample actions will appear here. Try the controls or the connection step first."; list.append(empty); }
  demoState.events.slice(-6).forEach((message, index) => {
    const row = document.createElement("div"); row.className = "report-event";
    const number = document.createElement("span"); number.textContent = String(demoState.events.length - Math.min(6, demoState.events.length) + index + 1).padStart(2, "0");
    const text = document.createElement("span"); text.textContent = message;
    row.append(number, text); list.append(row);
  });
}
function goToStep(step, focus = true) {
  demoState.step = step;
  document.querySelectorAll(".step-panel").forEach((panel, index) => { panel.hidden = index !== step; });
  document.querySelectorAll("[data-step]").forEach(button => {
    if (Number(button.dataset.step) === step) button.setAttribute("aria-current", "step"); else button.removeAttribute("aria-current");
  });
  qs("#demo-next").replaceChildren(document.createTextNode(nextLabels[step] + " →"));
  if (step === 3) { completeStep(3); renderSnapshot(); }
  announce(["Run the sample checks to see readiness, then explore the controls.", "Choose a scene, mute the sample mic, or mark a highlight.", "Simulate a hiccup, then recover to see both events in your recap.", "Your sample actions are collected here. Ready to try your own setup?"][step]);
  if (focus) qs(`#step-heading-${step}`).focus();
}
document.querySelectorAll("[data-step]").forEach(button => button.addEventListener("click", () => goToStep(Number(button.dataset.step))));
qs("#demo-next").addEventListener("click", () => { if (demoState.step === 3) location.href = "index.html"; else goToStep(demoState.step + 1); });
qs("#demo-check").addEventListener("click", () => {
  qs("#demo-readiness").classList.add("ready");
  qs("#demo-readiness .readiness-icon").textContent = "✓";
  qs("#demo-readiness strong").textContent = "Sample setup looks ready";
  qs("#demo-readiness div > span").textContent = "OBS, the sample connection, and your reminders are checked.";
  ["#check-obs", "#check-internet", "#check-reminder"].forEach(selector => { qs(selector).textContent = "✓"; qs(selector).classList.add("checked"); });
  qs("#demo-check").textContent = "Run sample checks again";
  if (!demoState.explored.has(0)) addEvent("Sample readiness checks completed.");
  completeStep(0); announce("Sample checks passed. Choose Next: run the show to try the controls.");
});
document.querySelectorAll("[data-scene]").forEach(button => button.addEventListener("click", () => {
  demoState.scene = button.dataset.scene;
  document.querySelectorAll("[data-scene]").forEach(item => { item.classList.toggle("selected", item === button); item.setAttribute("aria-pressed", String(item === button)); });
  qs("#demo-current-scene").textContent = demoState.scene;
  addEvent(`Sample scene changed to ${demoState.scene}.`); completeStep(1);
  announce(`Sample scene: ${demoState.scene}. Your real OBS is unaffected.`);
}));
qs("#demo-mic").addEventListener("click", () => {
  demoState.muted = !demoState.muted;
  qs("#demo-mic").setAttribute("aria-pressed", String(demoState.muted));
  qs("#demo-mic strong").textContent = demoState.muted ? "Unmute sample mic" : "Mute sample mic";
  qs("#demo-mic-state").textContent = demoState.muted ? "Sample mic is muted" : "Sample mic is on";
  addEvent(`Sample microphone ${demoState.muted ? "muted" : "unmuted"}.`); completeStep(1);
  announce("Sample microphone updated. No audio devices were accessed.");
});
qs("#demo-marker").addEventListener("click", () => {
  demoState.markers++; addEvent(`Sample highlight ${demoState.markers}: a moment worth saving.`); completeStep(1);
  announce(`Sample highlight ${demoState.markers} added. Find it in Review the session.`);
});
qs("#demo-replay").addEventListener("click", () => {
  demoState.replays++; addEvent("Replay save simulated. No video file created."); completeStep(1);
  announce("Replay save simulated. In the app, this asks OBS to save its Replay Buffer.");
});
qs("#demo-hiccup").addEventListener("click", () => {
  if (demoState.hiccup) return;
  demoState.hiccup = true; demoState.connectionEvents++; demoState.network = "186 ms · sample";
  qs("#demo-network-display").classList.add("warning"); qs("#demo-network-value").innerHTML = "186 <small>ms</small>";
  qs("#demo-network-status").textContent = "! Slow sample response detected";
  qs("#demo-recover").disabled = false; qs("#demo-hiccup").disabled = true;
  addEvent("Sample connection warning: response time rose to 186 ms.");
  announce("Sample connection warning recorded. Choose Recover connection to record its recovery.");
});
qs("#demo-recover").addEventListener("click", () => {
  if (!demoState.hiccup) return;
  demoState.hiccup = false; demoState.connectionEvents++; demoState.network = "26 ms · sample";
  qs("#demo-network-display").classList.remove("warning"); qs("#demo-network-value").innerHTML = "26 <small>ms</small>";
  qs("#demo-network-status").textContent = "✓ Sample connection recovered";
  qs("#demo-recover").disabled = true; qs("#demo-hiccup").disabled = false;
  addEvent("Sample connection recovered at 26 ms."); completeStep(2);
  announce("Both the warning and recovery are in your sample recap. Choose Next: review the session.");
});
qs("#demo-copy-recap").addEventListener("click", async () => {
  const recap = `John's Twitch Handler — SAMPLE DEMO RECAP\nHighlights: ${demoState.markers}\nReplay saves simulated: ${demoState.replays}\nConnection events: ${demoState.connectionEvents}\n\n${demoState.events.join("\n")}\n\nSample data only. No real stream was monitored.`;
  try { await navigator.clipboard.writeText(recap); announce("Sample recap copied. Nothing was submitted."); }
  catch { const field = qs("#demo-recap-text"); field.value = recap; field.hidden = false; field.focus(); field.select(); announce("Select and copy the sample recap below."); }
});
qs("#demo-reset").addEventListener("click", () => {
  demoState.explored.clear(); demoState.scene = "Gameplay"; demoState.muted = false; demoState.markers = 0; demoState.replays = 0;
  demoState.connectionEvents = 0; demoState.network = "32 ms · sample"; demoState.hiccup = false; demoState.events = [];
  qs("#demo-readiness").classList.remove("ready"); qs("#demo-readiness .readiness-icon").textContent = "○";
  qs("#demo-readiness strong").textContent = "Ready to check your setup"; qs("#demo-readiness div > span").textContent = "Nothing has been tested in this sample session yet.";
  ["#check-obs", "#check-internet", "#check-reminder"].forEach(selector => { qs(selector).textContent = "○"; qs(selector).classList.remove("checked"); });
  qs("#demo-check").textContent = "Run sample checks →";
  document.querySelectorAll("[data-scene]").forEach(item => { const active = item.dataset.scene === "Gameplay"; item.classList.toggle("selected", active); item.setAttribute("aria-pressed", String(active)); });
  qs("#demo-current-scene").textContent = "Gameplay";
  qs("#demo-mic").setAttribute("aria-pressed", "false"); qs("#demo-mic strong").textContent = "Mute sample mic"; qs("#demo-mic-state").textContent = "Sample mic is on";
  qs("#demo-network-display").classList.remove("warning"); qs("#demo-network-value").innerHTML = "32 <small>ms</small>"; qs("#demo-network-status").textContent = "● Healthy sample connection";
  qs("#demo-hiccup").disabled = false; qs("#demo-recover").disabled = true; qs("#demo-recap-text").value = ""; qs("#demo-recap-text").hidden = true;
  document.querySelectorAll("[data-step]").forEach(button => button.classList.remove("complete")); qs("#demo-progress").textContent = "0 of 4 steps explored";
  renderSnapshot(); goToStep(0); announce("Demo reset. Start with Run sample checks, or choose any step.");
});
renderSnapshot();
