// Developer Portal client: confirmations, dashboard auto-refresh, realtime critical alerts.
(function () {
  "use strict";

  // 0) Phones: the nav is a horizontal strip - bring the current page's link into view.
  var nav = document.querySelector(".dv-nav"), active = nav && nav.querySelector("a.is-active");
  if (active && nav.scrollWidth > nav.clientWidth) {
    nav.scrollLeft = Math.max(0, active.offsetLeft - nav.offsetLeft - (nav.clientWidth - active.offsetWidth) / 2);
  }

  // 1) Confirmation for dangerous/maintenance forms (server ALSO requires the confirm checkbox).
  document.addEventListener("submit", function (e) {
    var form = e.target;
    var msg = form.getAttribute && form.getAttribute("data-confirm");
    if (msg && !window.confirm(msg)) e.preventDefault();
  });

  // 2) Dashboard auto-refresh (tile states) every 30s using the cached status endpoint.
  var grid = document.querySelector("[data-dv-tiles]");
  if (grid) {
    setInterval(function () {
      fetch("/dev/api/status", { credentials: "same-origin", headers: { "Accept": "application/json" } })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (data) {
          if (!data) return;
          data.tiles.forEach(function (t) {
            var el = grid.querySelector('[data-tile="' + t.key + '"]');
            if (!el) return;
            el.className = el.className.replace(/st-\w+/, "st-" + t.status);
            var b = el.querySelector(".dv-badge"); if (b) b.textContent = t.status;
            var s = el.querySelector(".t-sum"); if (s) s.textContent = t.summary;
            var rr = el.querySelector(".t-reason"); if (rr) rr.textContent = t.reason || "";
          });
          var o = document.querySelector("[data-dv-overall]");
          if (o) o.textContent = data.overall;
          var u = document.querySelector("[data-dv-updated]");
          if (u) u.textContent = "Updated " + new Date().toLocaleTimeString();
        }).catch(function () { /* offline: keep last state */ });
    }, 30000);
  }

  // 3) Realtime critical alerts over /hubs/dev (Error/Critical only, rate-limited server-side).
  var live = document.getElementById("dv-live");
  var toasts = document.getElementById("dv-toasts");
  if (window.signalR && live && toasts) {
    var conn = new signalR.HubConnectionBuilder().withUrl("/hubs/dev").withAutomaticReconnect().build();
    function setLive(on) { live.textContent = on ? "● alerts live" : "● alerts offline"; live.classList.toggle("is-on", on); }
    conn.on("devAlert", function (a) {
      var div = document.createElement("div");
      div.className = "dv-toast" + (a.severity === "Critical" ? " crit" : "");
      var strong = document.createElement("strong"); strong.textContent = a.severity + " · " + a.category;
      var p = document.createElement("div"); p.textContent = a.message;
      var link = document.createElement("a"); link.href = "/dev/errors/" + a.fingerprint; link.textContent = "Open error group";
      div.appendChild(strong); div.appendChild(p); div.appendChild(link);
      toasts.appendChild(div);
      setTimeout(function () { div.remove(); }, 15000);
    });
    conn.onreconnecting(function () { setLive(false); });
    conn.onreconnected(function () { setLive(true); });
    conn.onclose(function () { setLive(false); });
    conn.start().then(function () { setLive(true); }).catch(function () { setLive(false); });
  }
})();
