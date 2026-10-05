/* ============================================================
   DE-CHIMEX PRINT HUB V2 - attachment preview lightbox
   Progressive enhancement for workspace file links: anchors with
   [data-preview] open in an in-page viewer instead of a new tab.
   - images: rendered inline (<img>, CSP img-src allows self/data/https)
   - PDF / docs / anything else: info card with Open + Download actions
     (embedded <iframe> preview is intentionally NOT used: the app sends
     X-Frame-Options: DENY and CSP object-src 'none', so framing uploads
     is blocked by design; navigation/download still works).
   Without JS (or if this file fails to load) the links keep their plain
   target="_blank" behaviour - nothing depends on the lightbox.
   ============================================================ */
(function () {
  'use strict';

  var CSS =
    '.dcx-lightbox{position:fixed;inset:0;z-index:1200;display:flex;align-items:center;justify-content:center;' +
    'background:rgba(15,18,30,.72);padding:20px;box-sizing:border-box}' +
    '.dcx-lightbox[hidden]{display:none}' +
    '.dcx-lightbox-card{background:var(--surface,#fff);color:var(--text,#111827);border-radius:14px;' +
    'max-width:min(880px,96vw);max-height:92vh;display:flex;flex-direction:column;overflow:hidden;' +
    'box-shadow:0 24px 70px rgba(0,0,0,.45);width:100%}' +
    '.dcx-lightbox-head{display:flex;align-items:center;gap:10px;padding:10px 14px;' +
    'border-bottom:1px solid var(--border,#e5e7eb)}' +
    '.dcx-lightbox-title{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;' +
    'font-weight:600;font-size:14px}' +
    '.dcx-lightbox-close{border:1px solid var(--border,#e5e7eb);background:transparent;border-radius:8px;' +
    'width:32px;height:32px;font-size:16px;line-height:1;cursor:pointer;color:inherit;flex-shrink:0}' +
    '.dcx-lightbox-close:hover{background:var(--n-100,#f1f5f9)}' +
    '.dcx-lightbox-body{padding:16px;overflow:auto;display:flex;justify-content:center}' +
    '.dcx-lightbox-body img{max-width:100%;max-height:70vh;border-radius:8px;' +
    'border:1px solid var(--border,#e5e7eb)}' +
    '.dcx-lightbox-file{text-align:center;padding:18px 10px;max-width:420px}' +
    '.dcx-lightbox-icon{font-size:44px;line-height:1}' +
    '.dcx-lightbox-name{font-weight:600;margin:12px 0 4px;word-break:break-word}' +
    '.dcx-lightbox-meta{font-size:12.5px;color:var(--muted,#6b7280);margin-bottom:14px}' +
    '.dcx-lightbox-actions{display:flex;gap:8px;justify-content:center;flex-wrap:wrap}';

  var overlay = null;
  var titleEl = null;
  var bodyEl = null;
  var lastFocus = null;

  function ensureCss() {
    if (document.getElementById('dcx-lightbox-css')) return;
    var st = document.createElement('style');
    st.id = 'dcx-lightbox-css';
    st.textContent = CSS;
    document.head.appendChild(st);
  }

  function ensureDom() {
    if (overlay) return;
    ensureCss();
    overlay = document.createElement('div');
    overlay.className = 'dcx-lightbox';
    overlay.hidden = true;
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-modal', 'true');
    overlay.setAttribute('aria-label', 'Attachment preview');
    var card = document.createElement('div');
    card.className = 'dcx-lightbox-card';
    var head = document.createElement('div');
    head.className = 'dcx-lightbox-head';
    titleEl = document.createElement('div');
    titleEl.className = 'dcx-lightbox-title';
    var close = document.createElement('button');
    close.type = 'button';
    close.className = 'dcx-lightbox-close';
    close.setAttribute('aria-label', 'Close preview');
    close.textContent = '×';
    close.addEventListener('click', hide);
    head.appendChild(titleEl);
    head.appendChild(close);
    bodyEl = document.createElement('div');
    bodyEl.className = 'dcx-lightbox-body';
    card.appendChild(head);
    card.appendChild(bodyEl);
    overlay.appendChild(card);
    overlay.addEventListener('click', function (e) {
      if (e.target === overlay) hide();
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && overlay && !overlay.hidden) hide();
    });
    document.body.appendChild(overlay);
  }

  function show() {
    ensureDom();
    lastFocus = document.activeElement;
    overlay.hidden = false;
    document.body.style.overflow = 'hidden';
    var btn = overlay.querySelector('.dcx-lightbox-close');
    if (btn) btn.focus();
  }

  function hide() {
    if (!overlay) return;
    overlay.hidden = true;
    document.body.style.overflow = '';
    if (bodyEl) bodyEl.innerHTML = '';
    if (lastFocus && document.contains(lastFocus)) lastFocus.focus();
  }

  function isImage(ct, url) {
    if (ct && ct.toLowerCase().indexOf('image/') === 0) return true;
    return /\.(png|jpe?g|gif|webp|bmp|svg)(\?|#|$)/i.test(url || '');
  }

  function kindLabel(ct) {
    if (!ct) return 'file';
    if (ct === 'application/pdf') return 'PDF document';
    if (ct.indexOf('image/') === 0) return 'image';
    if (ct.indexOf('text/') === 0) return 'text file';
    if (/word|document/i.test(ct)) return 'document';
    if (/sheet|excel/i.test(ct)) return 'spreadsheet';
    return ct;
  }

  function fmtSize(bytes) {
    var n = parseInt(bytes, 10);
    if (isNaN(n) || n < 0) return '';
    if (n < 1024) return n + ' B';
    if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1048576).toFixed(1) + ' MB';
  }

  function el(tag, cls, text) {
    var d = document.createElement(tag);
    if (cls) d.className = cls;
    if (text != null) d.textContent = text;
    return d;
  }

  function open(link) {
    var url = link.getAttribute('href');
    if (!url) return;
    var ct = link.getAttribute('data-content-type') || '';
    var name = link.getAttribute('data-name') || link.textContent || 'Attachment';
    show();
    titleEl.textContent = name.trim();
    bodyEl.innerHTML = '';

    if (isImage(ct, url)) {
      var img = document.createElement('img');
      img.src = url;
      img.alt = name.trim();
      img.addEventListener('error', function () {
        // The file is gone (or was never an image): fall back to the info card
        // rather than leaving a broken image in the viewer.
        bodyEl.innerHTML = '';
        bodyEl.appendChild(fileCard(url, name.trim(), ct, link.getAttribute('data-size')));
      });
      bodyEl.appendChild(img);
      return;
    }
    bodyEl.appendChild(fileCard(url, name.trim(), ct, link.getAttribute('data-size')));
  }

  function fileCard(url, name, ct, size) {
    var wrap = el('div', 'dcx-lightbox-file');
    var icon = el('div', 'dcx-lightbox-icon', ct === 'application/pdf' ? '📄' : '📎');
    icon.setAttribute('aria-hidden', 'true');
    wrap.appendChild(icon);
    wrap.appendChild(el('div', 'dcx-lightbox-name', name));
    var meta = kindLabel(ct || '');
    var sizeText = fmtSize(size);
    if (sizeText) meta += ' · ' + sizeText;
    wrap.appendChild(el('div', 'dcx-lightbox-meta', meta + ' . Inline preview is not available for this file type.'));
    var actions = el('div', 'dcx-lightbox-actions');
    var openBtn = el('a', 'btn btn-primary btn-sm', 'Open in new tab');
    openBtn.href = url;
    openBtn.target = '_blank';
    openBtn.rel = 'noopener';
    var dl = el('a', 'btn btn-outline btn-sm', 'Download');
    dl.href = url;
    dl.setAttribute('download', name);
    actions.appendChild(openBtn);
    actions.appendChild(dl);
    wrap.appendChild(actions);
    return wrap;
  }

  function onClick(e) {
    var link = e.target && e.target.closest ? e.target.closest('a[data-preview]') : null;
    if (!link) return;
    e.preventDefault();
    open(link);
  }

  // Idempotent: safe to call for dynamically inserted rows (live file/proof pushes).
  function bind(root) {
    var scope = root || document;
    if (scope === document) {
      if (document.documentElement.getAttribute('data-dcx-preview') === '1') return;
      document.documentElement.setAttribute('data-dcx-preview', '1');
      document.addEventListener('click', onClick);
      return;
    }
    // Delegation lives on document, so nodes added later work without rebinding;
    // this branch exists so callers can be explicit without double-binding.
    bind(document);
  }

  window.DeChimexPreview = { bind: bind, open: open };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', function () { bind(document); });
  } else {
    bind(document);
  }
})();
