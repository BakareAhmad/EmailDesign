/* ============================================================
   DE-CHIMEX PRINT HUB V2 - workspace conversation (composer)
   Owns everything the author does in the chat card: fetch-submit
   (no reload; the form still POSTs natively without JS), replies,
   attachment staging, voice-note recording, typing signals,
   presence display and batched read beacons. Bubble rendering and
   the SignalR connection live in order-workspace.js, reached here
   through window.DeChimexWorkspace. Realtime policy:
     SignalR : message / read+delivered ticks / typing / presence
     fetch   : send, read beacons, history/state (normal req/resp)
     never   : upload progress (sender-local bar only), keystrokes
   ============================================================ */
(function () {
  'use strict';

  var ws = window.DeChimexWorkspace;
  if (!ws) return;
  var chatLog = ws.chatLog();
  var form = document.getElementById('chatForm');
  var input = document.getElementById('chatInput');
  if (!chatLog || !form || !input) return;

  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) {
    var d = document.createElement(tag);
    if (cls) d.className = cls;
    if (text != null) d.textContent = text;
    return d;
  }
  function toast(title, message) {
    if (window.DeChimexToast) window.DeChimexToast.show(title, message);
  }
  function fmtSize(bytes) {
    var b = Number(bytes) || 0;
    if (b < 1024) return b + ' B';
    if (b < 1024 * 1024) return (b / 1024).toFixed(1) + ' KB';
    return (b / 1024 / 1024).toFixed(1) + ' MB';
  }
  function fmtClock(totalSeconds) {
    var s = Math.max(0, Math.floor(totalSeconds));
    return Math.floor(s / 60) + ':' + ('0' + (s % 60)).slice(-2);
  }
  function relTime(iso) {
    var then = Date.parse(iso);
    if (isNaN(then)) return '';
    var mins = Math.max(0, Math.floor((Date.now() - then) / 60000));
    if (mins < 1) return 'just now';
    if (mins < 60) return mins + 'm ago';
    var hours = Math.floor(mins / 60);
    if (hours < 24) return hours + 'h ago';
    var days = Math.floor(hours / 24);
    if (days < 7) return days + 'd ago';
    return new Date(then).toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
  }

  /* ---------- error line (draft is always kept; Retry resubmits) ---------- */
  var chatError = $('chatError');
  var errText = el('span');
  var errRetry = el('button', 'btn btn-sm btn-outline', 'Retry');
  errRetry.type = 'button';
  errRetry.style.marginLeft = '8px';
  errRetry.addEventListener('click', function () {
    hideError();
    if (form.requestSubmit) form.requestSubmit();
  });
  function showError(msg, retry) {
    if (!chatError) return;
    chatError.innerHTML = '';
    errText.textContent = msg;
    chatError.appendChild(errText);
    if (retry) chatError.appendChild(errRetry);
    chatError.hidden = false;
  }
  function hideError() { if (chatError) chatError.hidden = true; }

  /* ---------- replies ---------- */
  var replyBar = $('replyBar'), replyBarName = $('replyBarName'),
      replyBarText = $('replyBarText'), replyCancel = $('replyCancel'),
      replyToInput = $('replyToInput');
  function bubbleSnippet(bubble) {
    var body = bubble.querySelector('.msg-body');
    if (body && body.textContent.trim()) {
      var t = body.textContent.trim();
      return t.length > 140 ? t.slice(0, 140) + '…' : t;
    }
    if (bubble.querySelector('.msg-img')) return 'Photo';
    if (bubble.querySelector('video')) return 'Video';
    if (bubble.querySelector('.msg-voice-ic')) return 'Voice note';
    if (bubble.querySelector('audio')) return 'Audio';
    var file = bubble.querySelector('.msg-file-name');
    return file ? file.textContent : 'Message';
  }
  function startReply(id, author, bubble) {
    if (!replyBar) return;
    replyBarName.textContent = author || 'Message';
    replyBarText.textContent = bubble ? bubbleSnippet(bubble) : '';
    replyToInput.value = id;
    replyBar.hidden = false;
    input.focus();
  }
  function clearReply() {
    if (!replyBar) return;
    replyBar.hidden = true;
    replyBarName.textContent = '';
    replyBarText.textContent = '';
    replyToInput.value = '';
  }
  if (replyCancel) replyCancel.addEventListener('click', clearReply);

  function gotoOriginal(id) {
    var target = chatLog.querySelector('[data-msg-id="' + id + '"]');
    if (!target) {
      toast('Original message unavailable', 'The quoted message is not in this conversation.');
      return;
    }
    target.scrollIntoView({ behavior: 'smooth', block: 'center' });
    target.classList.remove('msg-flash');
    // Restart the highlight animation even on repeated clicks.
    void target.offsetWidth;
    target.classList.add('msg-flash');
    setTimeout(function () { target.classList.remove('msg-flash'); }, 1700);
  }

  chatLog.addEventListener('click', function (e) {
    var more = e.target && e.target.closest ? e.target.closest('.msg-more') : null;
    if (more) {
      var body = more.parentElement.querySelector('.msg-body');
      var open = body && body.classList.toggle('expanded');
      more.textContent = open ? 'Show less' : 'Show more';
      return;
    }
    var replyBtn = e.target && e.target.closest ? e.target.closest('[data-reply-to]') : null;
    if (replyBtn) {
      startReply(replyBtn.getAttribute('data-reply-to'),
        replyBtn.getAttribute('data-reply-author'),
        replyBtn.closest('[data-msg-id]'));
      return;
    }
    var quote = e.target && e.target.closest ? e.target.closest('[data-quote-for]') : null;
    if (quote) gotoOriginal(quote.getAttribute('data-quote-for'));
  });

  /* ---------- attachment staging (preview + remove before send) ---------- */
  var attachBtn = $('attachBtn'), attachInput = $('attachInput'), stageBar = $('stageBar');
  var staged = []; // { file, kind }
  var MAX_BYTES = 10 * 1024 * 1024; // mirrors StorageOptions; server re-enforces
  function kindOf(file) {
    var t = (file.type || '').toLowerCase();
    if (t.indexOf('image/') === 0) return 'image';
    if (t.indexOf('video/') === 0) return 'video';
    if (t.indexOf('audio/') === 0) return 'audio';
    return 'file';
  }
  function renderStaged() {
    if (!stageBar) return;
    stageBar.innerHTML = '';
    stageBar.hidden = staged.length === 0;
    staged.forEach(function (s, i) {
      var item = el('div', 'stage-item');
      if (s.kind === 'image' && s.url) {
        var img = document.createElement('img');
        img.src = s.url;
        img.alt = '';
        item.appendChild(img);
      } else {
        item.appendChild(el('span', 'stage-ic', s.kind === 'video' ? '🎬' : (s.kind === 'audio' ? '🎵' : '📄')));
      }
      var meta = el('div', 'stage-meta');
      meta.appendChild(el('span', 'stage-name', s.file.name));
      meta.appendChild(el('span', 'stage-sub', fmtSize(s.file.size)));
      item.appendChild(meta);
      var x = el('button', 'stage-x', '✕');
      x.type = 'button';
      x.title = 'Remove attachment';
      x.setAttribute('aria-label', 'Remove ' + s.file.name);
      x.addEventListener('click', function () {
        if (s.url) URL.revokeObjectURL(s.url);
        staged.splice(i, 1);
        renderStaged();
      });
      item.appendChild(x);
      stageBar.appendChild(item);
    });
  }
  function stageFiles(list) {
    hideError();
    Array.prototype.forEach.call(list || [], function (f) {
      if (staged.length >= 6) {
        showError('A message carries at most 6 attachments.', false);
        return;
      }
      if (f.size > MAX_BYTES) {
        showError('"' + f.name + '" is larger than 10 MB and was skipped.', false);
        return;
      }
      if (f.size <= 0) return;
      var kind = kindOf(f);
      staged.push({
        file: f,
        kind: kind,
        url: kind === 'image' ? URL.createObjectURL(f) : null
      });
    });
    renderStaged();
  }
  if (attachBtn && attachInput) {
    attachBtn.addEventListener('click', function () { attachInput.click(); });
    attachInput.addEventListener('change', function () {
      stageFiles(attachInput.files);
      attachInput.value = '';
    });
  }
  function clearStaged() {
    staged.forEach(function (s) { if (s.url) URL.revokeObjectURL(s.url); });
    staged = [];
    renderStaged();
  }

  /* ---------- voice notes (MediaRecorder; hidden when unsupported) ---------- */
  var micBtn = $('micBtn'), recBar = $('recBar'), recTime = $('recTime'),
      recStop = $('recStop'), recCancel = $('recCancel'),
      voiceBar = $('voiceBar'), voicePreview = $('voicePreview'), voiceDiscard = $('voiceDiscard');
  var recorder = null, recChunks = [], recStream = null, recTimer = null,
      recStartedAt = 0, voiceBlob = null, voiceMime = '', voiceSeconds = 0,
      voiceUrl = null, micDenied = false;
  var VOICE_MIMES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4'];
  function voiceExt() { return voiceMime.indexOf('mp4') >= 0 ? 'm4a' : 'webm'; }
  function recorderSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
  }
  function pickMime() {
    for (var i = 0; i < VOICE_MIMES.length; i++) {
      try {
        if (MediaRecorder.isTypeSupported(VOICE_MIMES[i])) return VOICE_MIMES[i];
      } catch (e) { /* keep probing */ }
    }
    return '';
  }
  if (micBtn && recorderSupported()) micBtn.hidden = false;
  function stopTracks() {
    if (recStream) {
      recStream.getTracks().forEach(function (t) { try { t.stop(); } catch (e) {} });
      recStream = null;
    }
  }
  function recUi(active) {
    if (recBar) recBar.hidden = !active;
    if (micBtn) micBtn.disabled = active;
    if (attachBtn) attachBtn.disabled = active;
  }
  function startRecording() {
    if (recorder || !recorderSupported()) return;
    hideError();
    var mime = pickMime();
    navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
      recStream = stream;
      recChunks = [];
      try {
        recorder = mime ? new MediaRecorder(stream, { mimeType: mime }) : new MediaRecorder(stream);
      } catch (e) {
        stopTracks();
        showError('Voice recording is not supported in this browser.', false);
        return;
      }
      voiceMime = recorder.mimeType || mime || 'audio/webm';
      recorder.ondataavailable = function (e) {
        if (e.data && e.data.size > 0) recChunks.push(e.data);
      };
      recorder.onstop = finishRecording;
      recStartedAt = Date.now();
      if (recTime) recTime.textContent = '0:00';
      recUi(true);
      recTimer = setInterval(function () {
        var s = Math.floor((Date.now() - recStartedAt) / 1000);
        if (recTime) recTime.textContent = fmtClock(s);
        if (s >= 300 && recorder) stopRecording(); // 5-minute cap, then preview
      }, 250);
      recorder.start();
    }).catch(function (err) {
      micDenied = true;
      var name = (err && err.name) || '';
      showError(name === 'NotAllowedError' || name === 'SecurityError'
        ? 'Microphone access was denied, so the voice note was not recorded.'
        : 'No microphone is available on this device.', false);
    });
  }
  function stopRecording() {
    if (recTimer) { clearInterval(recTimer); recTimer = null; }
    if (recorder && recorder.state !== 'inactive') {
      try { recorder.stop(); } catch (e) { finishRecording(); }
    } else {
      finishRecording();
    }
  }
  function finishRecording() {
    if (recTimer) { clearInterval(recTimer); recTimer = null; }
    var r = recorder;
    recorder = null;
    recUi(false);
    stopTracks();
    if (!r) return;
    voiceSeconds = Math.max(1, Math.round((Date.now() - recStartedAt) / 1000));
    voiceBlob = new Blob(recChunks, { type: voiceMime.split(';')[0] });
    recChunks = [];
    if (!voiceBlob.size) {
      voiceBlob = null;
      showError('The recording came out empty. Please try again.', false);
      return;
    }
    if (voiceUrl) URL.revokeObjectURL(voiceUrl);
    voiceUrl = URL.createObjectURL(voiceBlob);
    if (voicePreview) {
      voicePreview.src = voiceUrl;
      voicePreview.load();
    }
    if (voiceBar) voiceBar.hidden = false;
  }
  function cancelRecording() {
    if (recTimer) { clearInterval(recTimer); recTimer = null; }
    if (recorder) {
      recorder.onstop = null;
      try { if (recorder.state !== 'inactive') recorder.stop(); } catch (e) {}
      recorder = null;
    }
    recChunks = [];
    recUi(false);
    stopTracks();
  }
  function discardVoice() {
    if (voiceUrl) { URL.revokeObjectURL(voiceUrl); voiceUrl = null; }
    if (voicePreview) { voicePreview.removeAttribute('src'); voicePreview.load(); }
    voiceBlob = null;
    voiceSeconds = 0;
    if (voiceBar) voiceBar.hidden = true;
  }
  if (micBtn) micBtn.addEventListener('click', startRecording);
  if (recStop) recStop.addEventListener('click', stopRecording);
  if (recCancel) recCancel.addEventListener('click', cancelRecording);
  if (voiceDiscard) voiceDiscard.addEventListener('click', discardVoice);

  /* ---------- fetch submit (draft + staged files survive failures) ---------- */
  var sendBtn = $('chatSend');
  var sendProgress = $('sendProgress'), sendFill = $('sendProgressFill');
  var sending = false;
  function setSending(on) {
    sending = on;
    if (sendBtn) sendBtn.disabled = on;
    if (input) input.disabled = on;
    if (attachBtn) attachBtn.disabled = on || !!recorder;
    if (micBtn) micBtn.disabled = on || !!recorder;
    if (sendBtn) sendBtn.textContent = on ? 'Sending…' : 'Send';
  }
  function clearComposer() {
    input.value = '';
    clearStaged();
    discardVoice();
    clearReply();
    hideError();
    if (sendProgress) sendProgress.hidden = true;
    if (sendFill) sendFill.style.width = '0';
  }
  form.addEventListener('submit', function (e) {
    e.preventDefault();
    if (sending || recorder) return;
    hideError();
    var body = input.value;
    if (!body.trim() && staged.length === 0 && !voiceBlob) {
      showError('Write a message, attach a file, or record a voice note first.', false);
      return;
    }
    setTyping(false);
    var fd = new FormData();
    fd.append('body', body);
    if (replyToInput && replyToInput.value) fd.append('replyToMessageId', replyToInput.value);
    staged.forEach(function (s) {
      fd.append('files', s.file);
      fd.append('kinds', s.kind);
      fd.append('durations', '');
    });
    if (voiceBlob) {
      var name = 'voice-note.' + voiceExt();
      var vf = new File([voiceBlob], name, { type: voiceMime.split(';')[0] || 'audio/webm' });
      fd.append('files', vf);
      fd.append('kinds', 'voice');
      fd.append('durations', String(voiceSeconds));
    }
    fd.append('__RequestVerificationToken', ws.csrf());

    var hasUpload = staged.length > 0 || !!voiceBlob;
    if (sendProgress) sendProgress.hidden = !hasUpload;
    if (sendFill) sendFill.style.width = '0';
    setSending(true);

    var xhr = new XMLHttpRequest();
    xhr.open('POST', form.action);
    xhr.setRequestHeader('Accept', 'application/json');
    xhr.timeout = 120000;
    if (xhr.upload && hasUpload) {
      xhr.upload.onprogress = function (pe) {
        if (pe.lengthComputable && sendFill)
          sendFill.style.width = Math.round((pe.loaded / pe.total) * 100) + '%';
      };
    }
    xhr.onload = function () {
      setSending(false);
      if (sendProgress) sendProgress.hidden = true;
      var data = null;
      try { data = JSON.parse(xhr.responseText); } catch (err) { data = null; }
      if (xhr.status >= 200 && xhr.status < 300 && data && data.message) {
        ws.appendMessage(data.message); // echo arrives via SignalR too; id dedupes
        clearComposer();
        return;
      }
      var msg = (data && data.error) || ('The message could not be sent (HTTP ' + xhr.status + ').');
      showError(msg + ' Your draft was kept. Press Send to retry.', true);
    };
    xhr.onerror = function () {
      setSending(false);
      if (sendProgress) sendProgress.hidden = true;
      showError('The connection dropped before the message was sent. Your draft was kept. Press Send to retry.', true);
    };
    xhr.ontimeout = xhr.onerror;
    xhr.send(fd);
  });

  /* ---------- typing out (start/stop only — never keystrokes) ---------- */
  var typingSent = false, typingTimer = null;
  function setTyping(on) {
    if (on === typingSent) return;
    typingSent = on;
    try {
      var p = ws.connection.invoke('Typing', ws.orderId, on);
      if (p && p.catch) p.catch(function () { /* offline: stays local */ });
    } catch (e) { /* hub not connected yet */ }
  }
  input.addEventListener('input', function () {
    setTyping(true);
    if (typingTimer) clearTimeout(typingTimer);
    typingTimer = setTimeout(function () { setTyping(false); }, 3000);
  });
  input.addEventListener('blur', function () { setTyping(false); });

  /* ---------- typing in + presence in (transient; nothing persisted) ---------- */
  var typingRow = $('typingRow'), typingText = $('typingText');
  var typingHideTimer = null;
  function hideTyping() {
    if (typingRow) typingRow.hidden = true;
    if (typingHideTimer) { clearTimeout(typingHideTimer); typingHideTimer = null; }
  }
  try {
    ws.connection.on('orderTyping', function (evt) {
      if (!evt || typeof evt.isTyping !== 'boolean') return;
      if (!evt.isTyping) { hideTyping(); return; }
      // The server sends the typer's chat identity (staff username, never
      // name/email); the role label is only a fallback for older payloads.
      var label = (evt.name && String(evt.name)) || (evt.role === ws.myRole
        ? 'Someone'
        : (evt.role === 'staff' ? 'Staff' : 'Customer'));
      if (typingText) typingText.textContent = label + ' is typing…';
      if (typingRow) typingRow.hidden = false;
      if (typingHideTimer) clearTimeout(typingHideTimer);
      typingHideTimer = setTimeout(hideTyping, 6000); // a lost "stop" never sticks
    });
  } catch (e) { /* hub events unavailable */ }

  var presenceLine = $('presenceLine');
  function renderPresence(evt) {
    if (!presenceLine || !evt) return;
    var online = ws.isStaff ? !!evt.customerOnline : !!evt.staffOnline;
    var seen = ws.isStaff ? evt.customerLastSeenUtc : evt.staffLastSeenUtc;
    var label = ws.isStaff ? 'Customer' : 'Staff';
    if (online) {
      presenceLine.textContent = '● ' + label + ' online';
      presenceLine.classList.add('is-online');
    } else if (seen) {
      presenceLine.textContent = label + ' last active ' + relTime(seen);
      presenceLine.classList.remove('is-online');
    } else {
      presenceLine.textContent = '';
      presenceLine.classList.remove('is-online');
    }
  }
  try { ws.connection.on('orderPresence', renderPresence); } catch (e) { /* hub events unavailable */ }

  /* ---------- read beacons (batched; only messages actually rendered) ---------- */
  var pendingRead = {}, sentRead = {}, beaconTimer = null;
  function flushRead() {
    beaconTimer = null;
    var ids = Object.keys(pendingRead);
    pendingRead = {};
    if (!ids.length || !ws.markReadUrl || document.visibilityState !== 'visible') {
      if (ids.length && document.visibilityState !== 'visible') {
        ids.forEach(function (id) { pendingRead[id] = true; }); // parked while hidden
      }
      return;
    }
    fetch(ws.markReadUrl, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': ws.csrf()
      },
      body: JSON.stringify({ ids: ids })
    }).then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        (data && data.read || []).forEach(function (id) {
          sentRead[id] = true;
          var b = chatLog.querySelector('[data-msg-id="' + id + '"]');
          if (b && readObserver) readObserver.unobserve(b);
        });
      })
      .catch(function () {
        ids.forEach(function (id) { pendingRead[id] = true; });
        if (!beaconTimer) beaconTimer = setTimeout(flushRead, 10000); // retry, still batched
      });
  }
  function queueRead(id) {
    if (!id || sentRead[id] || pendingRead[id]) return;
    pendingRead[id] = true;
    if (!beaconTimer) beaconTimer = setTimeout(flushRead, 2000);
  }
  var readObserver = null;
  function watchBubble(bubble) {
    if (!bubble || bubble.classList.contains('mine')) return; // own sends need no beacon
    var id = bubble.getAttribute('data-msg-id');
    if (!id || sentRead[id]) return;
    if (readObserver) readObserver.observe(bubble);
    else queueRead(id); // no IntersectionObserver: page-visible render counts
  }
  if ('IntersectionObserver' in window) {
    readObserver = new IntersectionObserver(function (entries) {
      if (document.visibilityState !== 'visible') return;
      entries.forEach(function (en) {
        if (en.isIntersecting) queueRead(en.target.getAttribute('data-msg-id'));
      });
    }, { threshold: 0.5 });
  }
  // Open at the latest messages (standard chat behavior): history sits above,
  // and the read observer beacons what is actually rendered on screen.
  try { chatLog.scrollTop = chatLog.scrollHeight; } catch (e) { /* non-critical */ }
  Array.prototype.forEach.call(chatLog.querySelectorAll('[data-msg-id]'), watchBubble);
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'visible' && !beaconTimer)
      beaconTimer = setTimeout(flushRead, 500);
  });

  window.DeChimexChat = {
    onBubbleAdded: function (bubble) { watchBubble(bubble); }
  };
})();
