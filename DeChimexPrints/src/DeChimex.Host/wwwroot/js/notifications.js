/* ============================================================
   DE-CHIMEX PRINT HUB V2 - real-time notifications client
   Connects to /hubs/notifications (SignalR, cookie auth), keeps
   the sidebar unread badge current, and prepends live items to
   the /admin/notifications list when it is open.
   ============================================================ */
(function () {
  'use strict';

  var badge = document.getElementById('notifBadge');
  if (!badge || !window.signalR) return; // admin layout only; skip if the client failed to load

  var csrfMeta = document.querySelector('meta[name="csrf-token"]');
  var csrf = csrfMeta ? csrfMeta.getAttribute('content') : '';

  /* ---------- badge ---------- */
  var unread = 0;
  function renderBadge() {
    if (unread > 0) {
      badge.textContent = unread > 99 ? '99+' : String(unread);
      badge.hidden = false;
    } else {
      badge.hidden = true;
    }
  }
  function fetchUnreadCount() {
    fetch('/admin/notifications/unread-count', { credentials: 'same-origin', headers: { Accept: 'application/json' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (data && typeof data.count === 'number') { unread = data.count; renderBadge(); }
      })
      .catch(function () { /* keep last known count */ });
  }

  /* ---------- live row rendering (mirrors Index.cshtml markup) ---------- */
  var listBody = null;
  var onNotificationsPage = false;
  function isNotificationsListPage() {
    return window.location.pathname.replace(/\/+$/, '') === '/admin/notifications';
  }
  function esc(s) {
    var d = document.createElement('div');
    d.textContent = s == null ? '' : String(s);
    return d.innerHTML;
  }
  function fmtDate(iso) {
    var dt = new Date(iso);
    if (isNaN(dt.getTime())) return '';
    var months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    return dt.getDate() + ' ' + months[dt.getMonth()] + ' ' + dt.getFullYear() + ' ' + pad(dt.getHours()) + ':' + pad(dt.getMinutes());
  }
  function rowHtml(n) {
    var form = '';
    if (!n.isRead) {
      form = '<span class="badge badge-primary">New</span>' +
        '<form action="/admin/notifications/' + n.id + '/read" method="post" style="display:inline;margin-left:8px">' +
        '<input type="hidden" name="__RequestVerificationToken" value="' + esc(csrf) + '">' +
        '<button type="submit" class="btn btn-ghost btn-sm">Mark read</button>' +
        '</form>';
    } else {
      form = '<span class="badge badge-neutral">Read</span>';
    }
    return '<div class="flex items-start gap-3" style="padding:12px 0;border-bottom:1px solid var(--n-100)">' +
      '<div class="flex-1">' +
      '<div class="font-semibold" style="font-size:14px">' + esc(n.title) + '</div>' +
      '<div class="text-sm text-muted">' + esc(n.message) + '</div>' +
      '<div class="text-xs text-muted mt-1">' + fmtDate(n.createdUtc) + '</div>' +
      '</div><div>' + form + '</div></div>';
  }
  function prependLive(n) {
    if (!onNotificationsPage) return;
    if (!listBody) {
      listBody = document.querySelector('.card .card-body');
      if (!listBody) return;
      var empty = listBody.querySelector('.text-muted');
      if (empty && /^no notifications\.?$/i.test(empty.textContent.trim())) empty.remove();
    }
    var tpl = document.createElement('div');
    tpl.innerHTML = rowHtml(n).trim();
    var row = tpl.firstElementChild;
    if (row) listBody.insertBefore(row, listBody.firstChild);
  }

  /* ---------- toasts (skipped when the open page is the target) ---------- */
  function toast(n) {
    if (!window.DeChimexToast) return;
    // The workspace page surfaces its own richer toast for events on the open
    // order; a second generic toast here would be pure noise.
    if (n && n.link && window.location.pathname === n.link) return;
    window.DeChimexToast.show(n.title || 'Notification', n.message || '',
      n.link ? { href: n.link, text: 'View' } : null);
  }

  /* ---------- SignalR connection ---------- */
  var started = false;
  var connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/notifications', { withCredentials: true, transport: signalR.HttpTransportType.WebSockets })
    .withAutomaticReconnect([0, 2000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on('notification', function (n) {
    unread += 1;
    renderBadge();
    prependLive(n);
    toast(n);
  });

  connection.onreconnected(function () {
    fetchUnreadCount(); // resync after the gap; missed pushes are recovered by the full list on next load
  });

  connection.start().then(function () {
    started = true;
    fetchUnreadCount();
  }).catch(function (err) {
    // Anonymous or auth failure: the badge stays hidden, the page still works without real time.
    console.warn('Notifications hub not connected:', err && err.message ? err.message : err);
  });

  // This script loads on every admin page; only the list page renders rows live.
  onNotificationsPage = isNotificationsListPage();
  window.addEventListener('beforeunload', function () {
    if (started) connection.stop().catch(function () {});
  });
})();
