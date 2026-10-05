/* ============================================================
   DE-CHIMEX PRINT HUB V2 - portal notifications client
   Bell badge + dropdown + toasts for customers. Connects to
   /hubs/notifications (SignalR, cookie auth); the hub auto-joins the
   caller's user:{id} group on connect, so customers only ever receive
   their own direct notifications. Mirrors the admin notifications.js
   behaviour: badge count from the server, resync after reconnects.
   ============================================================ */
(function () {
  'use strict';

  var bell = document.getElementById('portalNotifBell');
  var badge = document.getElementById('portalNotifBadge');
  var drop = document.getElementById('portalNotifDrop');
  if (!bell || !badge || !drop || !window.signalR) return;

  var csrfMeta = document.querySelector('meta[name="csrf-token"]');
  var csrf = csrfMeta ? csrfMeta.getAttribute('content') : '';
  var listEl = drop.querySelector('.portal-notif-list');
  var loaded = false;

  /* ---------- badge ---------- */
  var unread = 0;
  function renderBadge() {
    if (unread > 0) {
      badge.textContent = unread > 99 ? '99+' : String(unread);
      badge.hidden = false;
    } else {
      badge.hidden = true;
    }
    updateOsBadge();
  }
  // Badging API: app-icon unread count on installed PWAs. Progressive enhancement only;
  // the in-app badge above stays the authoritative indicator.
  function updateOsBadge() {
    if (!navigator.setAppBadge) return;
    try {
      if (unread > 0) navigator.setAppBadge(unread > 99 ? 99 : unread);
      else navigator.clearAppBadge();
    } catch (e) { /* unsupported here */ }
  }
  function fetchUnreadCount() {
    fetch('/portal/notifications/unread-count', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (data && typeof data.count === 'number') { unread = data.count; renderBadge(); }
      })
      .catch(function () { /* keep last known count */ });
  }

  /* ---------- dropdown ---------- */
  function fmtDate(iso) {
    var dt = new Date(iso);
    if (isNaN(dt.getTime())) return '';
    var months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return dt.getDate() + ' ' + months[dt.getMonth()] + ' ' + pad(dt.getHours()) + ':' + pad(dt.getMinutes());
  }
  function rowNode(n) {
    var a = document.createElement('a');
    a.className = 'portal-notif-row' + (n.isRead ? '' : ' is-unread');
    a.href = n.link || '#';
    a.setAttribute('data-notif-id', n.id);
    var title = document.createElement('div');
    title.className = 'portal-notif-title';
    title.textContent = n.title || 'Notification';
    var msg = document.createElement('div');
    msg.className = 'portal-notif-msg';
    msg.textContent = n.message || '';
    var when = document.createElement('div');
    when.className = 'portal-notif-when';
    // The recent-list DTOs carry createdDateUtc; live hub pushes carry createdUtc.
    when.textContent = fmtDate(n.createdDateUtc || n.createdUtc);
    a.appendChild(title);
    a.appendChild(msg);
    a.appendChild(when);
    a.addEventListener('click', function (e) {
      // Mark-read is fire-and-forget: navigation proceeds regardless.
      if (!n.isRead && n.id) markRead(n.id);
    });
    return a;
  }
  function renderList(items) {
    if (!listEl) return;
    listEl.innerHTML = '';
    if (!items || items.length === 0) {
      var empty = document.createElement('div');
      empty.className = 'portal-notif-empty';
      empty.textContent = 'No notifications yet.';
      listEl.appendChild(empty);
      return;
    }
    items.slice(0, 10).forEach(function (n) { listEl.appendChild(rowNode(n)); });
  }
  function loadRecent() {
    fetch('/portal/notifications/recent', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        loaded = true;
        renderList(data);
      })
      .catch(function () { /* dropdown stays in its loading state */ });
  }
  function markRead(id) {
    fetch('/portal/notifications/' + id + '/read', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { RequestVerificationToken: csrf }
    }).then(function () {
      unread = Math.max(0, unread - 1);
      renderBadge();
    }).catch(function () { /* badge resyncs on next load */ });
  }
  function toggleDrop(force) {
    var open = typeof force === 'boolean' ? force : drop.hidden;
    drop.hidden = !open;
    bell.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (open && !loaded) loadRecent();
    if (open) ensurePushRow();
  }

  /* ---------- PWA push notifications (opt-in, never automatic) ---------- */
  var pushRowBuilt = false;
  function pushSupported() {
    return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
  }
  function ensurePushRow() {
    if (pushRowBuilt || !pushSupported()) return;
    var row = document.getElementById('portalNotifPush');
    if (!row) return;
    pushRowBuilt = true;
    renderPushRow();
  }
  function renderPushRow() {
    var row = document.getElementById('portalNotifPush');
    if (!row || !pushSupported()) return;
    row.textContent = '';
    var link = document.createElement('a');
    link.href = '#';
    link.style.cssText = 'display:flex;gap:8px;align-items:center;padding:10px 14px;border-top:1px solid var(--border);font-size:12.5px;font-weight:600;color:var(--n-700,#334155)';
    if (Notification.permission === 'denied') {
      var span = document.createElement('span');
      span.textContent = 'Notifications are turned off in your browser settings.';
      span.style.cssText = 'color:var(--n-400,#94a3b8);font-weight:500';
      link.appendChild(span);
      link.addEventListener('click', function (e) { e.preventDefault(); });
    } else {
      link.addEventListener('click', function (e) { e.preventDefault(); onPushToggle(); });
      // Label reflects the actual subscription state (fetched async).
      link.textContent = 'Checking notifications\u2026';
      navigator.serviceWorker.ready.then(function (reg) {
        return reg.pushManager.getSubscription();
      }).then(function (sub) {
        link.textContent = sub ? 'Turn off push notifications' : 'Turn on push notifications';
      }).catch(function () { link.textContent = 'Turn on push notifications'; });
    }
    row.appendChild(link);
  }
  function base64ToUint8Array(base64) {
    var padding = '='.repeat((4 - base64.length % 4) % 4);
    var b64 = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/');
    var raw = window.atob(b64);
    var out = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
    return out;
  }
  function onPushToggle() {
    navigator.serviceWorker.ready.then(function (reg) {
      return reg.pushManager.getSubscription().then(function (sub) {
        if (sub) {
          // Disable: tell the server first (it must stop sending), then drop locally.
          return postJson('/portal/notifications/push/unsubscribe', { endpoint: sub.endpoint })
            .then(function () { return sub.unsubscribe(); })
            .then(function () { window.DeChimexToast && window.DeChimexToast.show('Notifications', 'Push notifications turned off for this device.', null); renderPushRow(); });
        }
        // Enable: explicit user tap -> browser permission prompt -> subscription.
        return Notification.requestPermission().then(function (permission) {
          if (permission !== 'granted') { renderPushRow(); return; }
          return fetch('/portal/notifications/push/vapid-public-key', { credentials: 'same-origin' })
            .then(function (r) { return r.json(); })
            .then(function (data) {
              if (!data || !data.key) throw new Error('unavailable');
              return reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64ToUint8Array(data.key) });
            })
            .then(function (sub2) {
              var keys = sub2.toJSON().keys || {};
              return postJson('/portal/notifications/push/subscribe', {
                endpoint: sub2.endpoint, keysP256dh: keys.p256dh, keysAuth: keys.auth,
                userAgent: navigator.userAgent
              });
            })
            .then(function () {
              window.DeChimexToast && window.DeChimexToast.show('Notifications', "You'll get updates about your orders on this device.", null);
              renderPushRow();
            });
        });
      });
    }).catch(function () {
      window.DeChimexToast && window.DeChimexToast.show('Notifications', 'Push notifications are not available right now.', null);
    });
  }
  function postJson(url, body) {
    return fetch(url, {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', RequestVerificationToken: csrf },
      body: JSON.stringify(body)
    }).then(function (r) { if (!r.ok) throw new Error('push-post'); return r; });
  }
  bell.addEventListener('click', function (e) {
    e.stopPropagation();
    toggleDrop();
  });
  document.addEventListener('click', function (e) {
    if (!drop.hidden && !drop.contains(e.target)) toggleDrop(false);
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !drop.hidden) toggleDrop(false);
  });

  /* ---------- SignalR ---------- */
  var started = false;
  var connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/notifications', { withCredentials: true, transport: signalR.HttpTransportType.WebSockets })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('notification', function (n) {
    unread += 1;
    renderBadge();
    loaded = false; // refetch on next open so the row is DB-accurate
    if (listEl && !drop.hidden) loadRecent();
    // No toast when the open page is the notification's target: the page's own
    // live sync already surfaced the event (workspace shows its own toast).
    var onTarget = n && n.link && window.location.pathname === n.link;
    if (!onTarget && window.DeChimexToast) {
      window.DeChimexToast.show(n.title || 'Notification', n.message || '', n.link ? { href: n.link, text: 'View' } : null);
    }
  });

  connection.onreconnected(function () {
    fetchUnreadCount();
    loaded = false;
  });

  connection.start().then(function () {
    started = true;
    fetchUnreadCount();
  }).catch(function (err) {
    console.warn('Portal notifications hub not connected:', err && err.message ? err.message : err);
  });

  window.addEventListener('beforeunload', function () {
    if (started) connection.stop().catch(function () { });
  });
})();
