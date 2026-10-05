/* ============================================================
   DE-CHIMEX PRINT HUB V2 - shared toast notifications
   Tiny dependency-free toast stack used by the workspace live-sync,
   the portal bell and the admin notification clients. Toasts are
   purely informational: every event they describe is also persisted
   (notification row / timeline entry) and survives a reload.
   ============================================================ */
(function () {
  'use strict';

  var CSS =
    '.dcx-toasts{position:fixed;right:16px;bottom:16px;z-index:1300;display:flex;' +
    'flex-direction:column;gap:8px;max-width:min(360px,calc(100vw - 32px))}' +
    '.dcx-toast{background:#171b2b;color:#f3f4f6;border-radius:12px;padding:10px 12px;' +
    'box-shadow:0 12px 32px rgba(0,0,0,.35);border:1px solid rgba(255,255,255,.08);' +
    'display:flex;gap:10px;align-items:flex-start;animation:dcx-toast-in .18s ease-out}' +
    '.dcx-toast-title{font-weight:600;font-size:13.5px}' +
    '.dcx-toast-msg{font-size:12.5px;color:#c6cad6;margin-top:2px;word-break:break-word}' +
    '.dcx-toast-link{font-size:12.5px;color:#c4b5fd;margin-top:4px;display:inline-block}' +
    '.dcx-toast-x{margin-left:auto;background:transparent;border:0;color:#9aa0b4;font-size:15px;' +
    'cursor:pointer;line-height:1;padding:2px;flex-shrink:0}' +
    '@keyframes dcx-toast-in{from{transform:translateY(8px);opacity:0}to{transform:none;opacity:1}}';

  var stack = null;

  function ensure() {
    if (stack) return;
    if (!document.getElementById('dcx-toast-css')) {
      var st = document.createElement('style');
      st.id = 'dcx-toast-css';
      st.textContent = CSS;
      document.head.appendChild(st);
    }
    stack = document.createElement('div');
    stack.className = 'dcx-toasts';
    stack.setAttribute('aria-live', 'polite');
    document.body.appendChild(stack);
  }

  function show(title, message, link) {
    ensure();
    while (stack.children.length >= 4) stack.removeChild(stack.firstChild);
    var t = document.createElement('div');
    t.className = 'dcx-toast';
    t.setAttribute('role', 'status');
    var body = document.createElement('div');
    body.style.minWidth = '0';
    var h = document.createElement('div');
    h.className = 'dcx-toast-title';
    h.textContent = title || 'Update';
    body.appendChild(h);
    if (message) {
      var m = document.createElement('div');
      m.className = 'dcx-toast-msg';
      m.textContent = message;
      body.appendChild(m);
    }
    if (link && link.href) {
      var a = document.createElement('a');
      a.className = 'dcx-toast-link';
      a.href = link.href;
      a.textContent = link.text || 'View';
      body.appendChild(a);
    }
    var x = document.createElement('button');
    x.type = 'button';
    x.className = 'dcx-toast-x';
    x.setAttribute('aria-label', 'Dismiss');
    x.textContent = '×';
    x.addEventListener('click', function () { t.remove(); });
    t.appendChild(body);
    t.appendChild(x);
    stack.appendChild(t);
    window.setTimeout(function () { if (t.parentNode) t.remove(); }, 7000);
  }

  window.DeChimexToast = { show: show };
})();
