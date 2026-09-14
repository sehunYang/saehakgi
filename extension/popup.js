const $ = (id) => document.getElementById(id);
const statusEl = $('status');

function setStatus(text, busy) {
  statusEl.textContent = text;
  $('export').disabled = busy;
  $('import').disabled = busy;
}

// Remember the last-used path (per browser profile) for convenience.
chrome.storage.local.get(['path'], (v) => { if (v.path) $('path').value = v.path; });

function run(action, label) {
  const path = $('path').value.trim();
  const passphrase = $('pass').value;
  if (!path) { setStatus('USB 파일 경로를 입력하세요.', false); return; }
  if (!passphrase) { setStatus('암호를 입력하세요.', false); return; }
  chrome.storage.local.set({ path });

  setStatus(label + ' 중…', true);
  chrome.runtime.sendMessage({ action, path, passphrase }, (resp) => {
    if (chrome.runtime.lastError) { setStatus('오류: ' + chrome.runtime.lastError.message, false); return; }
    if (!resp || !resp.ok) { setStatus('실패: ' + ((resp && resp.error) || '알 수 없음'), false); return; }
    const r = resp.result;
    if (action === 'export') setStatus(`내보내기 완료: 쿠키 ${r.count}개 저장`, false);
    else setStatus(`가져오기 완료: 성공 ${r.ok} / 실패 ${r.fail} (전체 ${r.total})`, false);
  });
}

$('export').addEventListener('click', () => run('export', '내보내기'));
$('import').addEventListener('click', () => run('import', '가져오기'));
