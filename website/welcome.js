"use strict";
document.querySelectorAll("[data-preview-scene]").forEach(button => button.addEventListener("click", () => {
  document.querySelectorAll("[data-preview-scene]").forEach(item => {
    const selected = item === button;
    item.classList.toggle("selected", selected);
    item.setAttribute("aria-pressed", String(selected));
  });
  const status = document.querySelector("#scene-preview-status");
  status.replaceChildren(document.createTextNode("Sample scene: "));
  const label = document.createElement("strong"); label.textContent = button.dataset.previewScene;
  status.append(label);
}));

const download = document.querySelector("#installer-download");
const configuredDownload = window.HandlerWelcomeConfig?.installerUrl;
if (configuredDownload) {
  try {
    const url = new URL(configuredDownload);
    if (url.protocol !== "https:" || url.username || url.password) throw new Error("Expected a public HTTPS download URL.");
    download.href = url.href; download.hidden = false;
    document.querySelector("#download-availability").textContent = "Download the Windows installer, then follow the three steps below. Keep streaming and recording stopped for your first test.";
  } catch { /* Keep the invitation route if release configuration is invalid. */ }
}

document.querySelector("#copy-feedback").addEventListener("click", async () => {
  const template = "I tried John's Twitch Handler.\n\nI explored: website demo / installed app\nApp version, if installed:\nWhat I tried:\nWhat I expected:\nWhat happened:\nWhat felt easy:\nWhat confused me:\nWhat I would use again:";
  const status = document.querySelector("#feedback-copy-status");
  try { await navigator.clipboard.writeText(template); status.textContent = "Checklist copied. Paste it into your private reply to John."; }
  catch {
    const field = document.querySelector("#feedback-template"); field.hidden = false; field.value = template; field.focus(); field.select();
    status.textContent = "Select and copy the checklist below, then paste it into your private reply to John.";
  }
});
