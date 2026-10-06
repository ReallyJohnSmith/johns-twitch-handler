// One starting layout for both appearances. Saved user sizes always override these defaults.
(() => {
  const order = Object.freeze([
    "report", "optimizer", "scenes", "audio", "chat", "alerts", "clips", "ads",
    "performance", "raids", "viewers", "commands", "automations", "spotify", "tangia",
    "streamelements", "tikfinity", "wardogs", "wow-addons", "wow-events", "stream-health"
  ]);
  const sizes = {
    report: [12, 640], optimizer: [12, 440],
    scenes: [7, 340], audio: [5, 340],
    chat: [7, 560], alerts: [5, 560],
    clips: [7, 380], ads: [5, 380],
    performance: [12, 500],
    raids: [6, 440], viewers: [6, 440], commands: [6, 440], automations: [6, 440],
    spotify: [6, 560], tangia: [6, 560], streamelements: [6, 440], tikfinity: [6, 440],
    wardogs: [12, 380], "wow-addons": [6, 480], "wow-events": [6, 480],
    // This is the optional guided demo, not the real live-health status strip. Keep its contents natural.
    "stream-health": [12, null]
  };

  function sizeFor(id, page) {
    const [span, height] = id === "clips" && page === "after" ? [12, 440] : sizes[id] || [6, 440];
    return { span, height };
  }

  function applySize(panel, page) {
    const size = sizeFor(panel.dataset.panelId, page);
    panel.dataset.panelDefaultSpan = String(size.span);
    panel.style.setProperty("--panel-default-span", String(size.span));
    panel.style.setProperty("--panel-default-height", size.height === null ? "auto" : `${size.height}px`);
    // Do not write data-panel-span/height: those identify deliberate user resizes when saving.
  }

  globalThis.HandlerPanelLayout = Object.freeze({ order, sizeFor, applySize });
})();
