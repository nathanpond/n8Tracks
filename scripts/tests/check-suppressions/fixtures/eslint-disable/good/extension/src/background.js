// eslint-disable-next-line no-undef -- `chrome` is injected by the browser at run time
chrome.runtime.onInstalled.addListener(() => {});
/* eslint-disable
  no-undef,
  no-console
  -- the service worker has no module scope and logs to the extension console
*/
