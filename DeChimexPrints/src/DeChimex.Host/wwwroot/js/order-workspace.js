/* ============================================================
   DE-CHIMEX PRINT HUB V2 - live workspace sync (portal + staff)
   Connects to /hubs/orderchat, joins the order group and applies
   targeted in-place updates for:
     orderMessage     chat bubble (dedupe, scroll-preserving)
     orderMessagesRead / orderMessagesDelivered  tick upgrades on own bubbles
     orderFile        attachment row appears
     orderFileRemoved attachment row vanishes
     orderProof       proof section appears
     orderProofStatus proof pill / actions update
     orderDesignReview      design-review card appears (customer or staff upload)
     orderDesignReviewStatus design-review pill / actions update (approve/reject)
     orderStatus      resync: status badge, timeline, totals, payment
   (Typing + presence + the composer live in workspace-chat.js, which talks to
   this module through window.DeChimexWorkspace.)
   Structural changes and reconnects re-fetch the workspace state
   endpoint (DB = source of truth) and reconcile by id, so live
   events can never duplicate server-rendered rows and a reconnect
   gap heals itself. Forms and inputs are never touched, so typed
   text and focus always survive. The page works fully without JS:
   every live event mirrors a page that also reloads correctly.
   ============================================================ */
(function () {
  'use strict';

  var cfg = document.getElementById('wsConfig');
  if (!cfg || !window.signalR) return;

  var orderId = cfg.getAttribute('data-order-id');
  var stateUrl = cfg.getAttribute('data-state-url');
  var myRole = cfg.getAttribute('data-my-role') === 'staff' ? 'staff' : 'customer';
  var isStaff = myRole === 'staff';
  if (!orderId || !stateUrl) return;

  /* ---------- helpers ---------- */
  function $(sel, root) { return (root || document).querySelector(sel); }
  function el(tag, cls, text) {
    var d = document.createElement(tag);
    if (cls) d.className = cls;
    if (text != null) d.textContent = text;
    return d;
  }
  function fmtDate(iso) {
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) + ', ' +
      d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  }
  var moneyFmt = null;
  function fmtMoney(n) {
    if (!moneyFmt) {
      try { moneyFmt = new Intl.NumberFormat('en-NG', { minimumFractionDigits: 2, maximumFractionDigits: 2 }); }
      catch (e) { moneyFmt = null; }
    }
    var v = Number(n);
    if (isNaN(v)) v = 0;
    return '\u20A6' + (moneyFmt ? moneyFmt.format(v) : v.toFixed(2));
  }
  function csrf() {
    var t = document.querySelector('input[name="__RequestVerificationToken"]');
    return t ? t.value : '';
  }
  function toast(title, message, linkHref, linkText) {
    if (window.DeChimexToast) window.DeChimexToast.show(title, message, linkHref ? { href: linkHref, text: linkText } : null);
  }
  function nearBottom(node) {
    return node.scrollHeight - node.scrollTop - node.clientHeight < 60;
  }

  /* ---------- known ids (seeded from server markup; live events reconcile) ---------- */
  function seedIds(attr) {
    var s = {};
    Array.prototype.forEach.call(document.querySelectorAll('[' + attr + ']'), function (n) {
      s[n.getAttribute(attr)] = true;
    });
    return s;
  }
  var knownMsg = seedIds('data-msg-id');
  var knownFile = seedIds('data-file-id');
  var knownEntry = seedIds('data-entry-id');
  var proofStatus = {};
  Array.prototype.forEach.call(document.querySelectorAll('[data-proof-id]'), function (n) {
    var pill = n.querySelector('[data-proof-status]');
    proofStatus[n.getAttribute('data-proof-id')] = pill ? pill.textContent.trim() : '';
  });

  /* ---------- chat ---------- */
  var chatLog = document.getElementById('chatLog');
  function refreshCounts() {
    var chatCard = document.getElementById('chatCard');
    if (chatCard && chatLog) {
      var c = chatCard.querySelector('.ws-count');
      if (c) c.textContent = String(chatLog.querySelectorAll('[data-msg-id]').length);
    }
    var timelineCard = document.getElementById('timelineCard');
    var timelineList = document.getElementById('timelineList');
    if (timelineCard && timelineList) {
      var t = timelineCard.querySelector('.ws-count');
      if (t) t.textContent = String(timelineList.querySelectorAll('[data-entry-id]').length);
    }
  }
  function fmtTime(iso) {
    var d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  }
  function fmtSize(bytes) {
    var b = Number(bytes) || 0;
    if (b < 1024) return b + ' B';
    if (b < 1024 * 1024) return (b / 1024).toFixed(1) + ' KB';
    return (b / 1024 / 1024).toFixed(1) + ' MB';
  }
  function fmtDuration(seconds) {
    var s = Math.max(0, Math.round(Number(seconds) || 0));
    return Math.floor(s / 60) + ':' + ('0' + (s % 60)).slice(-2);
  }
  // Same bubble contract as Views/Shared/_WorkspaceChat.cshtml (quote, collapsible
  // long body, inline media, time + ticks + reply). Ticks never downgrade: a late
  // delivered event can never overwrite a read state.
  function setTicks(msgId, state) {
    if (!chatLog || !msgId) return;
    var b = chatLog.querySelector('[data-msg-id="' + msgId + '"]');
    var t = b ? b.querySelector('[data-ticks]') : null;
    if (!t) return;
    var cur = t.getAttribute('data-tick-state') || 'sent';
    if (state !== 'read' && !(state === 'delivered' && cur === 'sent')) return;
    t.setAttribute('data-tick-state', state);
    t.textContent = state === 'sent' ? '\u2713' : '\u2713\u2713';
    t.title = state.charAt(0).toUpperCase() + state.slice(1);
    t.className = 'msg-ticks is-' + state;
  }
  function quoteNode(m) {
    if (!m.replyToMessageId) return null;
    var q = el('button', 'msg-quote');
    q.type = 'button';
    q.title = 'Go to the original message';
    q.setAttribute('data-quote-for', m.replyToMessageId);
    q.appendChild(el('span', 'msg-quote-name', m.replyToAuthorName || 'Message'));
    q.appendChild(el('span', 'msg-quote-text', m.replyToSnippet || 'Original message unavailable'));
    return q;
  }
  function previewAttrs(a, node) {
    node.setAttribute('data-preview', '');
    node.setAttribute('data-content-type', a.contentType || '');
    node.setAttribute('data-name', a.originalFileName || 'Attachment');
    node.setAttribute('data-size', String(a.sizeBytes || 0));
  }
  function mediaNode(a) {
    if (!a || !a.url) return null;
    if (a.kind === 'image') {
      var link = document.createElement('a');
      link.href = a.url;
      link.className = 'msg-img-link';
      previewAttrs(a, link);
      var img = document.createElement('img');
      img.src = a.url;
      img.alt = a.originalFileName || 'Photo';
      img.loading = 'lazy';
      img.className = 'msg-img';
      link.appendChild(img);
      return link;
    }
    if (a.kind === 'video') {
      var v = document.createElement('video');
      v.className = 'msg-video';
      v.controls = true;
      v.preload = 'metadata';
      v.src = a.url;
      return v;
    }
    if (a.kind === 'audio' || a.kind === 'voice') {
      var wrap = el('div', 'msg-voice');
      if (a.kind === 'voice') wrap.appendChild(el('span', 'msg-voice-ic', '\uD83C\uDF99'));
      var au = document.createElement('audio');
      au.controls = true;
      au.preload = 'metadata';
      au.src = a.url;
      wrap.appendChild(au);
      if (a.durationSeconds) wrap.appendChild(el('span', 'msg-dur', fmtDuration(a.durationSeconds)));
      return wrap;
    }
    var f = document.createElement('a');
    f.href = a.url;
    f.className = 'msg-file';
    previewAttrs(a, f);
    f.appendChild(el('span', 'msg-file-ic', '\uD83D\uDCC4'));
    f.appendChild(el('span', 'msg-file-name', a.originalFileName || 'Attachment'));
    f.appendChild(el('span', 'msg-file-size', fmtSize(a.sizeBytes || 0)));
    return f;
  }
  function appendMessage(m) {
    if (!m || !m.id || knownMsg[m.id] || !chatLog) return;
    knownMsg[m.id] = true;
    var empty = document.getElementById('chatEmpty');
    if (empty) empty.remove();
    var stick = nearBottom(chatLog);
    var mine = m.authorRole === myRole;
    var bubble = el('div', 'chat-bubble ' + (mine ? 'mine' : 'theirs'));
    bubble.style.alignSelf = mine ? 'flex-end' : 'flex-start';
    bubble.setAttribute('data-msg-id', m.id);
    bubble.setAttribute('data-author-role', m.authorRole || '');
    if (m.createdDateUtc) bubble.setAttribute('data-created', m.createdDateUtc);
    // Grouping (same contract as the server loop): same side within 5 minutes.
    var existing = chatLog.querySelectorAll('[data-msg-id]');
    var prevB = existing.length ? existing[existing.length - 1] : null;
    if (prevB && prevB.getAttribute('data-author-role') === (m.authorRole || '')) {
      var pt = Date.parse(prevB.getAttribute('data-created') || '');
      var ct = Date.parse(m.createdDateUtc || '');
      if (!isNaN(pt) && !isNaN(ct) && Math.abs(ct - pt) < 5 * 60 * 1000)
        bubble.className += ' grouped';
    }
    bubble.appendChild(el('div', 'msg-head text-xs text-muted',
      (m.authorName || '') + ' \u00B7 ' + fmtDate(m.createdDateUtc)));
    var quote = quoteNode(m);
    if (quote) bubble.appendChild(quote);
    if (m.body) {
      var longBody = m.body.length > 800;
      bubble.appendChild(el('div', 'msg-body' + (longBody ? ' is-long' : ''), m.body));
      if (longBody) {
        var more = el('button', 'msg-more', 'Show more');
        more.type = 'button';
        bubble.appendChild(more);
      }
    }
    if (m.attachments && m.attachments.length) {
      var media = el('div', 'msg-media');
      var anyMedia = false;
      m.attachments.forEach(function (a) {
        var node = mediaNode(a);
        if (node) { media.appendChild(node); anyMedia = true; }
      });
      if (anyMedia) bubble.appendChild(media);
    }
    var foot = el('div', 'msg-foot');
    foot.appendChild(el('span', 'msg-time', fmtTime(m.createdDateUtc)));
    if (mine) {
      var tick = m.readAtUtc ? 'read' : (m.deliveredAtUtc ? 'delivered' : 'sent');
      var ticks = el('span', 'msg-ticks is-' + tick, tick === 'sent' ? '\u2713' : '\u2713\u2713');
      ticks.setAttribute('data-ticks', '');
      ticks.setAttribute('data-tick-state', tick);
      ticks.title = tick.charAt(0).toUpperCase() + tick.slice(1);
      foot.appendChild(ticks);
    }
    var replyBtn = el('button', 'msg-reply-btn', 'Reply');
    replyBtn.type = 'button';
    replyBtn.title = 'Reply to this message';
    replyBtn.setAttribute('data-reply-to', m.id);
    replyBtn.setAttribute('data-reply-author', m.authorName || '');
    foot.appendChild(replyBtn);
    bubble.appendChild(foot);
    chatLog.appendChild(bubble);
    if (stick) chatLog.scrollTop = chatLog.scrollHeight;
    refreshCounts();
    if (window.DeChimexChat && window.DeChimexChat.onBubbleAdded) {
      try { window.DeChimexChat.onBubbleAdded(bubble); } catch (e) { /* observer-only */ }
    }
  }

  /* ---------- files ---------- */
  function fileRow(f) {
    var li = el('li');
    li.style.cssText = 'padding:6px 0;border-bottom:1px solid var(--border)';
    li.setAttribute('data-file-id', f.id);
    var a = el('a', null, f.originalFileName);
    a.href = f.url;
    a.target = '_blank';
    a.rel = 'noopener';
    a.setAttribute('data-preview', '');
    a.setAttribute('data-content-type', f.contentType || '');
    a.setAttribute('data-name', f.originalFileName || 'Attachment');
    a.setAttribute('data-size', String(f.sizeBytes || 0));
    li.appendChild(a);
    var meta = ' \u00B7 ' + (f.purpose || f.uploadedByRole || '');
    if (!isStaff) meta += ' \u00B7 ' + fmtDate(f.createdDateUtc);
    li.appendChild(el('span', 'text-xs text-muted', meta));
    if (isStaff) {
      var form = document.createElement('form');
      form.action = actionUrl('data-delete-url', f.id) || ('files/' + f.id + '/delete');
      form.method = 'post';
      form.style.cssText = 'display:inline;margin-left:8px';
      form.addEventListener('submit', function (e) {
        if (!window.confirm('Remove this file?')) e.preventDefault();
      });
      var tok = document.createElement('input');
      tok.type = 'hidden';
      tok.name = '__RequestVerificationToken';
      tok.value = csrf();
      var btn = el('button', 'btn btn-ghost btn-sm', 'Remove');
      btn.type = 'submit';
      btn.style.cssText = 'color:var(--danger);padding:0 6px';
      form.appendChild(tok);
      form.appendChild(btn);
      li.appendChild(form);
    }
    return li;
  }
  function addFile(f) {
    if (!f || !f.id || knownFile[f.id]) return;
    knownFile[f.id] = true;
    var list = document.getElementById('fileList');
    if (!list) return;
    var empty = document.getElementById('fileEmpty');
    if (empty) empty.remove();
    list.appendChild(fileRow(f));
  }
  function removeFileRow(fileId) {
    if (!fileId) return;
    delete knownFile[fileId];
    var list = document.getElementById('fileList');
    if (!list) return;
    var row = list.querySelector('[data-file-id="' + fileId + '"]');
    if (row) row.remove();
    if (list.querySelectorAll('[data-file-id]').length === 0 && !document.getElementById('fileEmpty')) {
      var empty = el('div', 'text-muted', 'No files yet.');
      empty.id = 'fileEmpty';
      empty.style.marginBottom = '8px';
      list.parentNode.insertBefore(empty, list);
    }
  }

  /* ---------- proofs ---------- */
  function proofBadgeClass(status) {
    if (status === 'Approved') return 'badge badge-success';
    if (status === 'Superseded') return 'badge badge-neutral';
    return 'badge badge-warning';
  }
  function isImageType(ct, url) {
    if (ct && ct.toLowerCase().indexOf('image/') === 0) return true;
    return /\.(png|jpe?g|gif|webp|bmp|svg)(\?|#|$)/i.test(url || '');
  }
  // Server-rendered proof media (shared markup contract: .proof-media holds the
  // visual, [data-proof-status] the pill, .proof-actions the customer buttons).
  function proofMedia(p) {
    var wrap = el('div', 'proof-media');
    wrap.style.marginTop = '10px';
    if (isImageType(p.contentType, p.url)) {
      var link = document.createElement('a');
      link.href = p.url;
      link.setAttribute('data-preview', '');
      link.setAttribute('data-content-type', p.contentType || '');
      link.setAttribute('data-name', p.originalFileName || ('Design proof v' + p.version));
      var img = document.createElement('img');
      img.src = p.url;
      img.alt = 'Design proof v' + p.version;
      img.style.cssText = 'width:100%;border-radius:8px;border:1px solid var(--border)';
      img.addEventListener('error', function () {
        // A proof whose bytes are gone (or were never an image) degrades to the
        // file card instead of a broken-image icon.
        wrap.innerHTML = '';
        wrap.appendChild(proofFileCard(p));
      });
      link.appendChild(img);
      wrap.appendChild(link);
    } else {
      wrap.appendChild(proofFileCard(p));
    }
    return wrap;
  }
  function proofFileCard(p) {
    var card = el('div');
    card.style.cssText = 'border:1px solid var(--border);border-radius:8px;padding:14px;text-align:center';
    card.appendChild(el('div', null, p.contentType === 'application/pdf' ? '\uD83D\uDCC4' : '\uD83D\uDCCE'));
    var link = el('a', 'font-bold', p.originalFileName || ('Proof v' + p.version));
    link.href = p.url;
    link.target = '_blank';
    link.rel = 'noopener';
    link.style.display = 'inline-block';
    link.style.margin = '6px 0';
    link.setAttribute('data-preview', '');
    link.setAttribute('data-content-type', p.contentType || '');
    link.setAttribute('data-name', p.originalFileName || ('Proof v' + p.version));
    card.appendChild(link);
    card.appendChild(el('div', 'text-xs text-muted', (p.contentType || 'file') + ' \u2014 click to preview or open'));
    return card;
  }
  function proofNode(p, open) {
    var det = document.createElement('details');
    det.className = 'ws-details proof-details';
    det.style.cssText = 'border:1px solid var(--border);border-radius:12px;padding:14px;margin-bottom:12px';
    if (open) det.open = true;
    det.setAttribute('data-proof-id', p.id);
    var sum = document.createElement('summary');
    sum.style.cssText = 'display:flex;align-items:center;gap:8px;justify-content:space-between' + (isStaff ? '' : ';flex-wrap:wrap');
    var left = document.createElement('span');
    var chev = el('span', 'chev', '\u25B8');
    chev.setAttribute('aria-hidden', 'true');
    left.appendChild(chev);
    left.appendChild(document.createTextNode(' '));
    left.appendChild(el('span', 'font-bold', 'Version ' + p.version));
    sum.appendChild(left);
    var pill;
    if (isStaff) {
      pill = el('span', proofBadgeClass(p.status), p.status);
    } else {
      pill = el('span', 'text-xs text-muted', p.status);
    }
    pill.setAttribute('data-proof-status', '');
    sum.appendChild(pill);
    det.appendChild(sum);
    var body = el('div');
    body.style.marginTop = '10px';
    if (!isStaff && p.approvedAtUtc) body.appendChild(el('span', 'badge badge-success', 'Approved'));
    if (p.createdDateUtc) body.appendChild(el('div', 'text-xs text-muted', 'Submitted ' + fmtDate(p.createdDateUtc)));
    if (p.approvedAtUtc) body.appendChild(el('div', 'text-xs text-muted proof-approved-at', 'Approved ' + fmtDate(p.approvedAtUtc)));
    body.appendChild(proofMedia(p));
    if (p.staffNotes) {
      var sn = el('div', 'text-sm');
      sn.style.marginTop = '8px';
      sn.appendChild(el('span', 'text-muted', isStaff ? 'Your note: ' : 'Designer note: '));
      sn.appendChild(document.createTextNode(p.staffNotes));
      body.appendChild(sn);
    }
    if (p.customerFeedback) {
      var fb = el('div', 'text-sm proof-feedback');
      fb.style.marginTop = '4px';
      fb.appendChild(el('span', 'text-muted', isStaff ? 'Customer: ' : 'Your feedback: '));
      fb.appendChild(document.createTextNode(p.customerFeedback));
      body.appendChild(fb);
    }
    if (!isStaff && p.status === 'PendingApproval') body.appendChild(proofActions(p.id));
    det.appendChild(body);
    return det;
  }
  // Server-rendered action URLs with an all-zero guid placeholder (the portal proof
  // routes nest under .../workspace/, which a naive relative URL would miss).
  var EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
  function actionUrl(attr, id) {
    var tpl = cfg.getAttribute(attr) || '';
    return tpl ? tpl.split(EMPTY_GUID).join(id) : '';
  }
  function proofActions(proofId) {
    var wrap = el('div', 'flex gap-2 proof-actions');
    wrap.style.cssText = 'margin-top:12px;flex-wrap:wrap';
    var approve = document.createElement('form');
    approve.action = actionUrl('data-approve-url', proofId);
    approve.method = 'post';
    approve.appendChild(csrfInput());
    var ok = el('button', 'btn btn-primary', 'Approve this design');
    ok.type = 'submit';
    approve.appendChild(ok);
    var changes = document.createElement('form');
    changes.action = actionUrl('data-changes-url', proofId);
    changes.method = 'post';
    changes.className = 'flex gap-2';
    changes.style.cssText = 'flex:1;min-width:260px';
    changes.appendChild(csrfInput());
    var input = document.createElement('input');
    input.className = 'input';
    input.type = 'text';
    input.name = 'feedback';
    input.placeholder = 'What should we change?';
    input.required = true;
    input.style.flex = '1';
    var ask = el('button', 'btn btn-outline', 'Request changes');
    ask.type = 'submit';
    changes.appendChild(input);
    changes.appendChild(ask);
    wrap.appendChild(approve);
    wrap.appendChild(changes);
    return wrap;
  }
  function csrfInput() {
    var tok = document.createElement('input');
    tok.type = 'hidden';
    tok.name = '__RequestVerificationToken';
    tok.value = csrf();
    return tok;
  }
  function addProof(p) {
    if (!p || !p.id || proofStatus[p.id] !== undefined) {
      if (p && p.id) syncProofStatus(p.id, p.status, p.customerFeedback, p.approvedAtUtc);
      return;
    }
    proofStatus[p.id] = p.status;
    var list = document.getElementById('proofList');
    if (!list) return;
    var empty = document.getElementById('proofEmpty');
    if (empty) empty.remove();
    list.appendChild(proofNode(p, true));
  }
  function syncProofStatus(proofId, status, feedback, approvedAtUtc) {
    if (!proofId || !status) return;
    var det = document.querySelector('[data-proof-id="' + proofId + '"]');
    if (!det) return;
    proofStatus[proofId] = status;
    var pill = det.querySelector('[data-proof-status]');
    if (pill) {
      pill.textContent = status;
      if (isStaff) pill.className = proofBadgeClass(status);
    }
    if (feedback && !det.querySelector('.proof-feedback')) {
      var fb = el('div', 'text-sm proof-feedback');
      fb.style.marginTop = '4px';
      fb.appendChild(el('span', 'text-muted', isStaff ? 'Customer: ' : 'Your feedback: '));
      fb.appendChild(document.createTextNode(feedback));
      var media = det.querySelector('.proof-media');
      if (media && media.parentNode) media.parentNode.insertBefore(fb, media.nextSibling);
      else det.appendChild(fb);
    }
    // Approved / superseded proofs offer no actions (posting anyway would fail
    // domain validation, so the buttons are removed instead of left stale).
    if (!isStaff && status !== 'PendingApproval') {
      var actions = det.querySelector('.proof-actions');
      if (actions) actions.remove();
      if (status === 'Approved' && !det.querySelector('.badge-success')) {
        var badge = el('span', 'badge badge-success', 'Approved');
        var first = det.querySelector('.proof-media');
        if (first && first.parentNode) first.parentNode.insertBefore(badge, first);
      }
    }
    // The decision time lands live too (server render shows it after reload).
    if (status === 'Approved' && approvedAtUtc && !det.querySelector('.proof-approved-at')) {
      var at = el('div', 'text-xs text-muted proof-approved-at', 'Approved ' + fmtDate(approvedAtUtc));
      var media = det.querySelector('.proof-media');
      if (media && media.parentNode) media.parentNode.insertBefore(at, media);
      else det.appendChild(at);
    }
  }

  /* ---------- design reviews ---------- */
  // The MVC state endpoint serializes DesignReviewStatus as a string
  // (JsonStringEnumConverter); SignalR's default JSON hub protocol sends the
  // DTO's enum as a number. Normalize both so live pushes and resyncs agree.
  function drStatusText(s) {
    if (s === 0) return 'Pending';
    if (s === 1) return 'Approved';
    if (s === 2) return 'Rejected';
    return String(s == null ? '' : s);
  }
  function drBadgeClass(status) {
    if (status === 'Approved') return 'badge badge-success';
    if (status === 'Rejected') return 'badge badge-danger';
    return 'badge badge-warning';
  }
  function drFileIcon(fileName) {
    var ext = (fileName || '').toLowerCase();
    var dot = ext.lastIndexOf('.');
    ext = dot >= 0 ? ext.slice(dot) : '';
    if (/^\.(png|jpe?g|webp|gif|tiff?)$/.test(ext)) return '\uD83D\uDDBC\uFE0F';
    if (ext === '.pdf') return '\uD83D\uDCC4';
    if (ext === '.svg') return '\uD83D\uDCD0';
    if (/^\.(psd|ai|eps)$/.test(ext)) return '\uD83C\uDFA8';
    if (ext === '.zip') return '\uD83D\uDDC2\uFE0F';
    return '\uD83D\uDCCE';
  }
  function drFileSize(bytes) {
    var b = Number(bytes);
    if (isNaN(b) || b <= 0) return '';
    if (b < 1024) return b + ' B';
    if (b < 1048576) return Math.round(b / 1024) + ' KB';
    if (b < 1073741824) return (b / 1048576).toFixed(1) + ' MB';
    return (b / 1073741824).toFixed(2) + ' GB';
  }
  // Preview image (when one exists) wrapped in a link to the ORIGINAL file,
  // or a download card. Non-previewable originals (PSD/AI/ZIP...) always get
  // the card so the file itself stays reachable — previews never replace it.
  function drMedia(dr) {
    var wrap = el('div', 'design-review-preview');
    wrap.style.marginTop = '10px';
    var ct = (dr.contentType || '').toLowerCase();
    if (dr.previewUrl && ct.indexOf('image/') === 0) {
      var link = document.createElement('a');
      link.href = dr.url || dr.previewUrl;
      link.target = '_blank';
      link.rel = 'noopener';
      link.setAttribute('data-preview', '');
      link.setAttribute('data-content-type', dr.contentType || '');
      link.setAttribute('data-name', dr.originalFileName || 'Design file');
      var img = document.createElement('img');
      img.src = dr.previewUrl;
      img.alt = 'Design review: ' + (dr.originalFileName || '');
      img.style.cssText = 'width:100%;max-height:300px;object-fit:contain;border-radius:8px;border:1px solid var(--border)';
      link.appendChild(img);
      wrap.appendChild(link);
    } else if (dr.previewUrl && ct === 'application/pdf') {
      var plink = document.createElement('a');
      plink.href = dr.url || dr.previewUrl;
      plink.target = '_blank';
      plink.rel = 'noopener';
      plink.setAttribute('data-preview', '');
      plink.setAttribute('data-content-type', dr.contentType || '');
      plink.setAttribute('data-name', dr.originalFileName || 'Design file');
      var pimg = document.createElement('img');
      pimg.src = dr.previewUrl;
      pimg.alt = 'PDF preview: ' + (dr.originalFileName || '');
      pimg.style.cssText = 'width:100%;max-height:300px;object-fit:contain;border-radius:8px;border:1px solid var(--border)';
      plink.appendChild(pimg);
      wrap.appendChild(plink);
    } else {
      var card = el('div', 'design-review-document');
      card.style.cssText = 'margin-top:10px;border:1px solid var(--border);border-radius:8px;padding:14px;text-align:center';
      card.appendChild(el('div', null, drFileIcon(dr.originalFileName)));
      var dl = el('a', 'font-bold', dr.originalFileName || 'Download file');
      dl.href = dr.url || '#';
      dl.target = '_blank';
      dl.rel = 'noopener';
      dl.style.display = 'inline-block';
      dl.style.margin = '6px 0';
      card.appendChild(dl);
      card.appendChild(el('div', 'text-xs text-muted', (dr.contentType || 'file') + ' \u2014 click to download'));
      wrap.appendChild(card);
    }
    return wrap;
  }
  function drNode(dr, open) {
    var status = drStatusText(dr.status);
    var det = document.createElement('details');
    det.className = 'ws-details design-review-details';
    det.style.cssText = 'border:1px solid var(--border);border-radius:12px;padding:14px;margin-bottom:12px';
    if (open || status === 'Pending') det.open = true;
    det.setAttribute('data-design-review-id', dr.id);
    var sum = document.createElement('summary');
    sum.style.cssText = 'display:flex;align-items:center;gap:8px;justify-content:space-between;flex-wrap:wrap';
    var left = document.createElement('span');
    var chev = el('span', 'chev', '\u25B8');
    chev.setAttribute('aria-hidden', 'true');
    left.appendChild(chev);
    left.appendChild(document.createTextNode(' '));
    left.appendChild(el('span', 'font-bold', dr.originalFileName || 'Design file'));
    sum.appendChild(left);
    var pill = el('span', drBadgeClass(status), status);
    pill.setAttribute('data-dr-status', '');
    sum.appendChild(pill);
    det.appendChild(sum);
    var body = el('div');
    body.style.marginTop = '10px';
    var meta = el('div', 'text-xs text-muted');
    meta.appendChild(document.createTextNode('Uploaded ' + fmtDate(dr.createdDateUtc) + ' by ' + (dr.uploadedByRole || 'customer')));
    var size = drFileSize(dr.sizeBytes);
    if (size) meta.appendChild(document.createTextNode(' \u00B7 ' + size));
    body.appendChild(meta);
    body.appendChild(drMedia(dr));
    if (dr.notes) {
      var nt = el('div', 'text-sm');
      nt.style.marginTop = '8px';
      nt.appendChild(el('span', 'text-muted', 'Notes: '));
      nt.appendChild(document.createTextNode(dr.notes));
      nt.className += ' design-review-notes';
      body.appendChild(nt);
    }
    if (dr.rejectionReason) body.appendChild(drReasonNode(dr.rejectionReason));
    if (status === 'Approved' && dr.approvedAtUtc) body.appendChild(drApprovedAtNode(dr.approvedAtUtc));
    if (isStaff && status === 'Pending') body.appendChild(drActions(dr.id, dr.originalFileName));
    if (!isStaff && status === 'Pending' && dr.uploadedByRole === 'customer') {
      var waitingNode = el('div', 'text-xs text-muted design-review-waiting', 'Waiting for the team to review');
      waitingNode.style.marginTop = '8px';
      body.appendChild(waitingNode);
    }
    det.appendChild(body);
    return det;
  }
  function drReasonNode(reason) {
    var r = el('div', 'text-sm design-review-reason');
    r.style.cssText = 'margin-top:8px;color:var(--danger)';
    // Same wording as the server-rendered card for each audience.
    r.appendChild(el('span', 'text-muted', isStaff ? 'Rejected: ' : 'Changes requested: '));
    r.appendChild(document.createTextNode(reason));
    return r;
  }
  function drApprovedAtNode(iso) {
    var a = el('div', 'text-xs text-muted design-review-approved-at', 'Approved ' + fmtDate(iso));
    a.style.marginTop = '8px';
    return a;
  }
  // Staff decision controls (server-rendered rows carry the same forms; these
  // cover rows that arrive live). URLs come from the wsConfig templates.
  function drActions(designReviewId, fileName) {
    var wrap = el('div', 'flex gap-2 design-review-actions');
    wrap.style.cssText = 'margin-top:12px;flex-wrap:wrap';
    var approve = document.createElement('form');
    approve.action = actionUrl('data-dr-approve-url', designReviewId);
    approve.method = 'post';
    approve.style.display = 'inline';
    approve.appendChild(csrfInput());
    var notes = document.createElement('input');
    notes.type = 'hidden';
    notes.name = 'approvalNotes';
    approve.appendChild(notes);
    var ok = el('button', 'btn btn-success btn-sm', 'Approve');
    ok.type = 'submit';
    approve.appendChild(ok);
    var reject = el('button', 'btn btn-outline btn-sm', 'Reject');
    reject.type = 'button';
    reject.addEventListener('click', function () {
      if (window.showRejectDesignReviewModal) window.showRejectDesignReviewModal(designReviewId, fileName || 'this file');
    });
    wrap.appendChild(approve);
    wrap.appendChild(reject);
    return wrap;
  }
  var knownDesignReview = seedIds('data-design-review-id');
  function addDesignReview(dr) {
    if (!dr || !dr.id || knownDesignReview[dr.id]) return;
    knownDesignReview[dr.id] = true;
    var list = document.getElementById('designReviewList');
    if (!list) return;
    var empty = document.getElementById('designReviewEmpty');
    if (empty) empty.remove();
    list.appendChild(drNode(dr, true));
  }
  function syncDesignReviewStatus(designReviewId, status, reason, approvedAtUtc, rejectedAtUtc) {
    if (!designReviewId) return;
    var st = drStatusText(status);
    if (!st) return;
    var det = document.querySelector('[data-design-review-id="' + designReviewId + '"]');
    if (!det) return;
    var pill = det.querySelector('[data-dr-status]');
    if (pill) {
      pill.textContent = st;
      pill.className = drBadgeClass(st);
    }
    if (reason && !det.querySelector('.design-review-reason')) det.appendChild(drReasonNode(reason));
    if (st === 'Approved' && approvedAtUtc && !det.querySelector('.design-review-approved-at')) {
      det.appendChild(drApprovedAtNode(approvedAtUtc));
    }
    if (st === 'Rejected' && rejectedAtUtc && !det.querySelector('.design-review-rejected-at')) {
      var rj = el('div', 'text-xs text-muted design-review-rejected-at', 'Rejected ' + fmtDate(rejectedAtUtc));
      rj.style.marginTop = '8px';
      det.appendChild(rj);
    }
    // Decided reviews offer no further actions (posting anyway would fail
    // domain validation: only pending reviews can be decided).
    if (st !== 'Pending') {
      var actions = det.querySelector('.design-review-actions');
      if (actions) actions.remove();
      var waiting = det.querySelector('.design-review-waiting');
      if (waiting) waiting.remove();
    }
  }

  /* ---------- timeline ---------- */
  function entryNode(t) {
    var li = el('li');
    li.style.marginBottom = '8px';
    li.setAttribute('data-entry-id', t.id);
    li.appendChild(el('span', 'text-xs text-muted', fmtDate(t.createdDateUtc)));
    li.appendChild(el('div', null, t.description));
    return li;
  }
  function prependEntry(t) {
    if (!t || !t.id || knownEntry[t.id]) return;
    knownEntry[t.id] = true;
    var list = document.getElementById('timelineList');
    if (!list) return;
    list.insertBefore(entryNode(t), list.firstChild);
    setLatest(t.description);
    refreshCounts();
  }
  function setLatest(description) {
    var card = document.getElementById('timelineCard');
    if (!card) return;
    var latest = card.querySelector('.ws-latest');
    if (latest) {
      latest.textContent = 'Latest: ' + description;
      latest.title = description;
    }
  }

  /* ---------- state resync (DB = source of truth) ---------- */
  function patchStatusBadges(w) {
    Array.prototype.forEach.call(document.querySelectorAll('[data-ws-status]'), function (n) {
      n.textContent = isStaff ? w.status : w.customerStatus;
    });
    if (!isStaff) {
      var banner = document.querySelector('[data-ws-status-text]');
      if (banner) banner.textContent = w.customerStatus;
      var next = document.querySelector('[data-ws-next-step]');
      if (next) next.textContent = w.customerNextStep;
      var action = document.getElementById('wsActionBadge');
      if (action) action.hidden = !w.customerActionRequired;
    }
  }
  function moneyRow(label, value, bold) {
    var tr = document.createElement('tr');
    tr.appendChild(el('td', bold ? 'font-bold' : 'text-muted', label));
    tr.appendChild(el('td'));
    tr.appendChild(el('td', bold ? 'font-bold' : null, fmtMoney(value)));
    return tr;
  }
  function patchSummary(w) {
    var body = document.getElementById('summaryBody');
    if (!body || isStaff) return;
    body.innerHTML = '';
    (w.items || []).forEach(function (i) {
      var tr = document.createElement('tr');
      tr.appendChild(el('td', null, i.productName));
      tr.appendChild(el('td', null, i.quantity + ' \u00D7 ' + fmtMoney(i.unitPrice)));
      tr.appendChild(el('td', null, fmtMoney(i.lineTotal)));
      body.appendChild(tr);
    });
    (w.customLines || []).forEach(function (c) {
      var tr = document.createElement('tr');
      tr.appendChild(el('td', null, c.description));
      tr.appendChild(el('td', null, c.quantity + ' \u00D7 ' + fmtMoney(c.unitPrice)));
      tr.appendChild(el('td', null, fmtMoney(c.lineTotal)));
      body.appendChild(tr);
    });
    body.appendChild(moneyRow('Subtotal', w.subtotal, false));
    body.appendChild(moneyRow('Delivery', w.deliveryFee, false));
    if (Number(w.discount) > 0) {
      var tr = document.createElement('tr');
      tr.appendChild(el('td', 'text-muted', 'Discount'));
      tr.appendChild(el('td'));
      tr.appendChild(el('td', null, '\u2212' + fmtMoney(w.discount)));
      body.appendChild(tr);
    }
    body.appendChild(moneyRow('Total', w.grandTotal, true));
  }
  function patchPayBlock(w, extras) {
    if (isStaff) return;
    var block = document.getElementById('payBlock');
    if (!block) return;
    block.innerHTML = '';
    if (extras.canPay) {
      var form = document.createElement('form');
      form.action = cfg.getAttribute('data-pay-url') || '';
      form.method = 'post';
      form.style.marginTop = '12px';
      // The checkout POST carries an antiforgery token like the server form.
      form.appendChild(csrfInput());
      var total = Number(w.grandTotal) + Number(extras.charge || 0);
      var btn = el('button', 'btn btn-primary btn-lg', 'Review & pay ' + fmtMoney(total));
      btn.type = 'submit';
      btn.style.width = '100%';
      form.appendChild(btn);
      block.appendChild(form);
      var note = el('div', 'text-xs text-muted', 'Includes a ' + fmtMoney(extras.charge || 0) + ' card-processing fee (Paystack).');
      note.style.marginTop = '6px';
      block.appendChild(note);
    }
  }
  function docsLinks(extras, list) {
    if (extras.invoiceToken) {
      var inv = el('a', 'btn btn-outline btn-sm', 'View invoice');
      inv.href = '/i/' + extras.invoiceToken;
      inv.target = '_blank';
      inv.rel = 'noopener';
      inv.style.cssText = 'margin-top:10px;width:100%';
      list.appendChild(inv);
    }
    if (extras.receiptToken) {
      var rec = el('a', 'btn btn-outline btn-sm', 'View receipt');
      rec.href = '/r/' + extras.receiptToken;
      rec.target = '_blank';
      rec.rel = 'noopener';
      rec.style.cssText = 'margin-top:8px;width:100%';
      list.appendChild(rec);
    }
  }
  function patchDocs(extras) {
    var block = document.getElementById('docsBlock');
    if (!block || isStaff) return;
    block.innerHTML = '';
    docsLinks(extras, block);
  }
  function patchPayment(extras) {
    if (!isStaff) return;
    var body = document.getElementById('paymentBody');
    if (!body) return;
    body.innerHTML = '';
    var p = extras.payment;
    if (p) {
      var row = el('div');
      row.appendChild(el('span', 'text-muted', 'Status: '));
      var badge = el('span', 'badge ' + (p.status === 'Succeeded' ? 'badge-success' : 'badge-neutral'), p.status);
      row.appendChild(badge);
      body.appendChild(row);
      var line = el('div', 'text-sm', fmtMoney(p.amount) + ' \u00B7 ' + p.reference);
      line.style.marginTop = '4px';
      body.appendChild(line);
    } else {
      body.appendChild(el('div', 'text-muted', 'No payment yet.'));
    }
    docsLinks(extras, body);
  }
  // Mirrors the server buttons exactly (same status sets as the Razor view), so
  // the transition card never offers a move the domain would reject.
  function patchTransitionCard(w) {
    if (!isStaff) return;
    var card = document.getElementById('transitionCard');
    var form = document.getElementById('transitionForm');
    if (!card || !form) return;
    var buttons = [];
    if (w.status === 'AwaitingReview') buttons = [['discussion', 'Start discussion'], ['confirm', 'Confirm requirements']];
    else if (w.status === 'RequirementsConfirmed') buttons = [['discussion', 'Move to discussion'], ['awaitapproval', 'Send for design approval']];
    else if (w.status === 'InDiscussion') buttons = [['confirm', 'Confirm requirements'], ['awaitapproval', 'Send for design approval']];
    if (buttons.length === 0) { card.hidden = true; return; }
    card.hidden = false;
    var token = form.querySelector('input[name="__RequestVerificationToken"]');
    form.innerHTML = '';
    if (token) form.appendChild(token);
    buttons.forEach(function (b) {
      var btn = el('button', 'btn btn-outline', b[1]);
      btn.type = 'submit';
      btn.name = 'status';
      btn.value = b[0];
      form.appendChild(btn);
    });
  }
  function patchFinalizeFoot(w) {
    if (!isStaff) return;
    var foot = document.getElementById('finalizeFoot');
    if (!foot) return;
    var can = w.status === 'RequirementsConfirmed' || w.status === 'Draft' ||
      w.status === 'AwaitingReview' || w.status === 'InDiscussion';
    foot.innerHTML = '';
    if (can) {
      var btn = el('button', 'btn btn-primary btn-lg', 'Finalize requirements & pricing');
      btn.type = 'submit';
      btn.style.cssText = 'width:100%;margin-top:12px';
      foot.appendChild(btn);
      var note = el('div', 'text-xs text-muted', 'The customer will review the quote and pay via Paystack.');
      note.style.marginTop = '6px';
      foot.appendChild(note);
    } else {
      var lock = el('div', 'text-muted text-sm', 'Finalization is locked while the order is ' + w.status + '.');
      lock.style.marginTop = '10px';
      foot.appendChild(lock);
    }
  }
  function patchFromState(w, extras) {
    if (!w) return;
    patchStatusBadges(w);
    (w.messages || []).forEach(appendMessage);
    (w.files || []).forEach(addFile);
    // Resync is authoritative: rows missing from state are gone (removed elsewhere).
    var liveFileIds = {};
    (w.files || []).forEach(function (f) { liveFileIds[f.id] = true; });
    Object.keys(knownFile).forEach(function (id) { if (!liveFileIds[id]) removeFileRow(id); });
    (w.proofs || []).forEach(function (p) { addProof(p); });
    (w.designReviews || []).forEach(function (dr) { addDesignReview(dr); });
    (w.timeline || []).slice().reverse().forEach(prependEntry);
    if ((w.timeline || []).length > 0) setLatest(w.timeline[0].description);
    refreshCounts();
    if (!isStaff) {
      patchSummary(w);
      patchPayBlock(w, extras);
      patchDocs(extras);
    } else {
      patchPayment(extras);
      patchTransitionCard(w);
      patchFinalizeFoot(w);
    }
  }
  var resyncing = false;
  function resync() {
    if (resyncing) return;
    resyncing = true;
    fetch(stateUrl, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        resyncing = false;
        if (!data) return;
        // Both state endpoints return { workspace, ...extras }.
        patchFromState(data.workspace || data, data);
      })
      .catch(function () { resyncing = false; });
  }

  /* ---------- SignalR ---------- */
  var conn = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/orderchat')
    .withAutomaticReconnect()
    .build();

  conn.on('orderMessage', function (m) { appendMessage(m); });

  conn.on('orderMessagesRead', function (evt) {
    (evt && evt.ids || []).forEach(function (id) { setTicks(id, 'read'); });
  });

  conn.on('orderMessagesDelivered', function (evt) {
    (evt && evt.ids || []).forEach(function (id) { setTicks(id, 'delivered'); });
  });

  conn.on('orderFile', function (evt) {
    if (evt && evt.file) addFile(evt.file);
  });

  conn.on('orderFileRemoved', function (evt) {
    if (evt && evt.fileId) removeFileRow(evt.fileId);
  });

  conn.on('orderProof', function (evt) {
    if (!evt || !evt.proof) return;
    addProof(evt.proof);
    toast(isStaff ? 'Proof uploaded' : 'Design proof ready',
      'Version ' + evt.proof.version + (isStaff ? ' was uploaded.' : ' is ready for your approval below.'));
  });

  conn.on('orderProofStatus', function (evt) {
    if (!evt || !evt.proofId) return;
    syncProofStatus(evt.proofId, evt.status, evt.feedback, evt.approvedAtUtc);
    if (isStaff) {
      toast(evt.status === 'Approved' ? 'Design approved' : 'Changes requested',
        evt.status === 'Approved'
          ? 'The customer approved design v' + evt.version + '.'
          : 'The customer requested changes on v' + evt.version + '.');
    }
  });

  conn.on('orderDesignReview', function (evt) {
    if (!evt || !evt.designReview) return;
    var dr = evt.designReview;
    addDesignReview(dr);
    var name = dr.originalFileName || 'a design file';
    if (isStaff) {
      toast('Design review uploaded',
        (dr.uploadedByRole === 'staff' ? name + ' was added below.' : name + ' was uploaded by the customer \u2014 review it below.'));
    } else {
      toast('Design review uploaded',
        dr.uploadedByRole === 'staff'
          ? name + ' was shared for your review below.'
          : 'Your file ' + name + ' was uploaded and is awaiting staff review.');
    }
  });

  conn.on('orderDesignReviewStatus', function (evt) {
    if (!evt || !evt.designReviewId) return;
    syncDesignReviewStatus(evt.designReviewId, evt.status, evt.reason, evt.approvedAtUtc, evt.rejectedAtUtc);
    if (isStaff) {
      toast(evt.status === 'Approved' ? 'Design review approved' : 'Design review rejected',
        evt.status === 'Approved'
          ? 'The design review was approved.'
          : 'The design review was rejected' + (evt.reason ? ': ' + evt.reason : '.'));
    } else {
      toast(evt.status === 'Approved' ? 'Design approved' : 'Design review needs changes',
        evt.status === 'Approved'
          ? 'Your design review was approved.'
          : 'Your design review was rejected' + (evt.reason ? ': ' + evt.reason : '.') + ' Please upload a revised file.');
    }
  });

  conn.on('orderStatus', function (evt) {
    if (evt && evt.timelineDescription) toast('Order update', evt.timelineDescription);
    resync(); // totals/badges/panels refresh from the DB projection
  });

  var liveNoteShown = false;
  function liveNote(err) {
    // The page remains fully usable (forms + reload); only live sync is off.
    if (!liveNoteShown) {
      liveNoteShown = true;
      toast('Live updates unavailable', 'This page will not refresh automatically. Reload to see changes.');
    }
    if (window.console) console.warn('workspace live sync offline', err);
  }

  // SignalR drops group membership on reconnect (new connection id), so the group
  // must be rejoined explicitly — otherwise live pushes silently stop arriving.
  conn.onreconnected(function () {
    conn.invoke('JoinOrder', orderId).catch(function () { /* resync still heals */ });
    resync();
  });
  conn.onclose(function (err) { liveNote(err); });

  conn.start()
    .then(function () { return conn.invoke('JoinOrder', orderId); })
    .catch(function (err) { liveNote(err); });

  // Bridge for workspace-chat.js (composer, replies, typing, presence, receipts).
  window.DeChimexWorkspace = {
    appendMessage: appendMessage,
    connection: conn,
    orderId: orderId,
    myRole: myRole,
    isStaff: isStaff,
    csrf: csrf,
    markReadUrl: cfg.getAttribute('data-mark-read-url') || '',
    chatLog: function () { return chatLog; }
  };
})();
