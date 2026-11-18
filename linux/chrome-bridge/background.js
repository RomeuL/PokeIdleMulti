chrome.storage.onChanged.addListener(async changes => {
  const command = changes.command?.newValue;
  if (!command || typeof command.url !== 'string') return;
  const tabs = await chrome.tabs.query({});
  for (const tab of tabs.filter(tab => tab.url === command.url)) {
    if (typeof command.muted === 'boolean') await chrome.tabs.update(tab.id, { muted: command.muted });
    if (typeof command.zoom === 'number') await chrome.tabs.setZoom(tab.id, Math.max(.5, Math.min(1.5, command.zoom)));
  }
});
