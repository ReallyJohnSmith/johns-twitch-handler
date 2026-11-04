"use strict";
async function localRequest(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { "Content-Type": "application/json" }, cache: "no-store" });
  if (!response.ok) throw new Error("The helper could not complete that request. Try again.");
  return response.status === 204 ? null : response.json();
}
async function loadPairings() {
  try {
    const data = await localRequest("/bridge/manage");
    document.querySelector("#website-origin").textContent = data.websiteOrigin || "This helper has no website configured.";
    document.querySelector("#create-code").disabled = !data.websiteOrigin;
    const link = document.querySelector("#open-website");
    link.hidden = !data.websiteOrigin;
    if (data.websiteOrigin) link.href = data.websiteOrigin;
    const list = document.querySelector("#paired-browsers");
    list.replaceChildren();
    if (!data.pairings.length) list.textContent = "No browsers connected yet.";
    for (const pairing of data.pairings) {
      const row = document.createElement("div");
      row.className = "connection-pairing";
      const label = document.createElement("span");
      label.textContent = `${pairing.origin} · Connected ${new Date(pairing.createdAt).toLocaleString()}`;
      const remove = document.createElement("button");
      remove.textContent = "Remove";
      remove.addEventListener("click", async () => {
        try { await localRequest(`/bridge/manage/${encodeURIComponent(pairing.id)}`, { method: "DELETE" }); await loadPairings(); }
        catch (error) { document.querySelector("#pair-message").textContent = error.message; }
      });
      row.append(label, remove); list.append(row);
    }
  } catch (error) { document.querySelector("#pair-message").textContent = error.message; }
}
document.querySelector("#create-code").addEventListener("click", async () => {
  try {
    const data = await localRequest("/bridge/manage/code", { method: "POST", body: "{}" });
    document.querySelector("#pair-code").textContent = data.code;
    document.querySelector("#code-help").hidden = false;
    document.querySelector("#pair-message").textContent = `Expires at ${new Date(data.expiresAt).toLocaleTimeString()}.`;
  } catch (error) { document.querySelector("#pair-message").textContent = error.message; }
});
loadPairings();
window.addEventListener("focus", loadPairings);
