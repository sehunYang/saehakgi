// saehakgi cookie bridge — talks to the native host (com.saehakgi.host).
const HOST = 'com.saehakgi.host';

function sendNative(message) {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(HOST, message, (resp) => {
      const err = chrome.runtime.lastError;
      if (err) { reject(new Error(err.message)); return; }
      if (!resp || !resp.ok) { reject(new Error((resp && resp.error) || '호스트 응답 오류')); return; }
      resolve(resp);
    });
  });
}

async function exportCookies({ path, passphrase }) {
  const cookies = await chrome.cookies.getAll({});
  const resp = await sendNative({ cmd: 'put_cookies', path, passphrase, cookies });
  return { count: resp.count };
}

function cookieUrl(c) {
  const host = (c.domain || '').replace(/^\./, '');
  return (c.secure ? 'https://' : 'http://') + host + (c.path || '/');
}

async function importCookies({ path, passphrase }) {
  const resp = await sendNative({ cmd: 'get_cookies', path, passphrase });
  const cookies = resp.cookies || [];
  let ok = 0, fail = 0;
  for (const c of cookies) {
    const details = {
      url: cookieUrl(c),
      name: c.name,
      value: c.value,
      path: c.path,
      secure: c.secure,
      httpOnly: c.httpOnly,
      sameSite: c.sameSite,
      storeId: c.storeId,
    };
    // Host-only cookies must NOT carry a domain; domain cookies keep theirs.
    if (!c.hostOnly && c.domain) details.domain = c.domain;
    if (!c.session && c.expirationDate) details.expirationDate = c.expirationDate;
    try { await chrome.cookies.set(details); ok++; } catch (e) { fail++; }
  }
  return { ok, fail, total: cookies.length };
}

chrome.runtime.onMessage.addListener((req, _sender, sendResponse) => {
  (async () => {
    try {
      if (req.action === 'export') sendResponse({ ok: true, result: await exportCookies(req) });
      else if (req.action === 'import') sendResponse({ ok: true, result: await importCookies(req) });
      else sendResponse({ ok: false, error: '알 수 없는 동작' });
    } catch (e) {
      sendResponse({ ok: false, error: e.message });
    }
  })();
  return true; // keep the message channel open for the async response
});
