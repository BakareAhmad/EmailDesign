/*
 * DE-CHIMEX PRINT HUB — PWA client (v2, PWA task 2026-10-04).
 *
 * Responsibilities (all progressive enhancement; the site works identically when any of
 * this is unavailable):
 *   1. Service worker registration + safe update flow ("New version available" banner).
 *   2. Offline / reconnect toasts (honest: offline actions are NOT queued or faked).
 *   3. Install experience: Chromium gets an in-app install card (beforeinstallprompt);
 *      iPhone gets Add to Home Screen instructions; never shown inside the installed app.
 *   4. Test/QA hooks on window.DcxPwa so the E2E journey can render each surface.
 *
 * The customer shell loads this file; staff/admin shells do not (role-aware install UX).
 */
(function () {
    'use strict';

    var DISMISS_KEY = 'dcx-pwa-install-dismissed-at';
    var DISMISS_DAYS = 14;
    var refreshing = false;

    function isStandalone() {
        return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches)
            || window.navigator.standalone === true;
    }

    function isMobileWidth() { return window.matchMedia('(max-width: 1023px)').matches; }

    function isAppleMobile() {
        return /iphone|ipad|ipod/i.test(navigator.userAgent)
            || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
    }

    function dismissedRecently() {
        try {
            var at = parseInt(window.localStorage.getItem(DISMISS_KEY) || '0', 10);
            if (!at) return false;
            return (Date.now() - at) < DISMISS_DAYS * 24 * 60 * 60 * 1000;
        } catch (e) { return false; }
    }

    function noteDismissed() {
        try { window.localStorage.setItem(DISMISS_KEY, String(Date.now())); } catch (e) { /* private mode */ }
    }

    /* ---------------- toasts (uses the existing site toast if present) ---------------- */
    function toast(message, kind) {
        // DeChimexToast.show(title, message, link): title-only keeps the branded stack;
        // the plain fallback below still exists for shells without the toast lib.
        if (window.DeChimexToast && typeof window.DeChimexToast.show === 'function') { window.DeChimexToast.show(message); return; }
        if (typeof window.toast === 'function') { window.toast(message, kind); return; }
        // Last resort: transient status region (still announced to screen readers).
        var el = document.createElement('div');
        el.setAttribute('role', 'status');
        el.style.cssText = 'position:fixed;left:50%;transform:translateX(-50%);bottom:calc(74px + env(safe-area-inset-bottom,0px));' +
            'background:#111827;color:#fff;padding:10px 16px;border-radius:10px;font-size:13px;z-index:2147483000;max-width:90vw';
        el.textContent = message;
        document.body.appendChild(el);
        setTimeout(function () { el.remove(); }, 3500);
    }

    /* ---------------- 1. service worker registration + update banner ---------------- */
    if ('serviceWorker' in navigator) {
        window.addEventListener('load', function () {
            navigator.serviceWorker.register('/service-worker.js', { scope: '/' }).then(function (reg) {
                function announceWaiting(worker) {
                    if (worker && reg.active) showUpdateBanner();
                }
                // A worker is waiting when this page already has a controller (an update case).
                if (reg.waiting && navigator.serviceWorker.controller) announceWaiting(reg.waiting);
                reg.addEventListener('updatefound', function () {
                    var nw = reg.installing;
                    if (!nw) return;
                    nw.addEventListener('statechange', function () {
                        if (nw.state === 'installed' && navigator.serviceWorker.controller) showUpdateBanner();
                    });
                });
            }).catch(function () { /* progressive enhancement only */ });

            navigator.serviceWorker.addEventListener('controllerchange', function () {
                if (refreshing) { window.location.reload(); refreshing = false; }
            });

            // Deep-link routing for notification clicks on an already-open window.
            navigator.serviceWorker.addEventListener('message', function (event) {
                if (event.data && event.data.type === 'dcx:navigate' && event.data.url) {
                    window.location.href = event.data.url;
                }
            });
        });
    }

    function showUpdateBanner() {
        if (document.getElementById('dcx-update-banner')) return;
        var banner = document.createElement('div');
        banner.id = 'dcx-update-banner';
        banner.setAttribute('role', 'status');
        banner.innerHTML =
            '<div class="dcx-pwa-copy"><strong>New version available</strong>' +
            '<span>A newer version of DE-CHIMEX is ready.</span></div>' +
            '<div class="dcx-pwa-actions">' +
            '<button type="button" id="dcx-update-later" class="dcx-pwa-btn">Later</button>' +
            '<button type="button" id="dcx-update-refresh" class="dcx-pwa-btn dcx-pwa-btn-primary">Refresh</button></div>';
        document.body.appendChild(banner);
        banner.querySelector('#dcx-update-later').addEventListener('click', function () { banner.remove(); });
        banner.querySelector('#dcx-update-refresh').addEventListener('click', function () {
            refreshing = true;
            if (navigator.serviceWorker.controller) {
                navigator.serviceWorker.controller.postMessage('SKIP_WAITING');
            } else {
                window.location.reload();
            }
            // controllerchange above reloads once the new worker takes over.
            setTimeout(function () { window.location.reload(); }, 3000); // safety net
        });
    }

    /* ---------------- 2. offline / reconnect ---------------- */
    window.addEventListener('offline', function () {
        toast("You're offline. Browsing continues, but actions need a connection.", 'error');
    });
    window.addEventListener('online', function () {
        toast('Connection restored.', 'success');
    });

    /* ---------------- 3. install experience ---------------- */
    var deferredPrompt = null;

    function removeCard() {
        var c = document.getElementById('dcx-install-card');
        if (c) c.remove();
    }

    function buildCard(innerHtml) {
        var card = document.createElement('div');
        card.id = 'dcx-install-card';
        card.setAttribute('role', 'dialog');
        card.setAttribute('aria-label', 'Install DE-CHIMEX');
        card.innerHTML = innerHtml;
        document.body.appendChild(card);
        return card;
    }

    function showAndroidCard() {
        if (!deferredPrompt) return;
        var card = buildCard(
            '<div class="dcx-pwa-copy"><strong>Install DE-CHIMEX</strong>' +
            '<span>Get faster access to your orders, messages and updates.</span></div>' +
            '<div class="dcx-pwa-actions">' +
            '<button type="button" id="dcx-install-later" class="dcx-pwa-btn">Maybe later</button>' +
            '<button type="button" id="dcx-install-go" class="dcx-pwa-btn dcx-pwa-btn-primary">Install app</button></div>');
        card.querySelector('#dcx-install-later').addEventListener('click', function () { noteDismissed(); removeCard(); });
        card.querySelector('#dcx-install-go').addEventListener('click', function () {
            deferredPrompt.prompt();
            deferredPrompt.userChoice.then(function () { removeCard(); deferredPrompt = null; }).catch(function () { removeCard(); });
        });
    }

    function showIosCard() {
        var card = buildCard(
            '<div class="dcx-pwa-copy"><strong>Install DE-CHIMEX on iPhone</strong>' +
            '<span>In Safari, tap the <strong>Share</strong> button, scroll to ' +
            '<strong>Add to Home Screen</strong>, then tap <strong>Add</strong>.</span></div>' +
            '<div class="dcx-pwa-actions">' +
            '<a class="dcx-pwa-btn" href="/install-help">More help</a>' +
            '<button type="button" id="dcx-install-later" class="dcx-pwa-btn dcx-pwa-btn-primary">Got it</button></div>');
        card.querySelector('#dcx-install-later').addEventListener('click', function () { noteDismissed(); removeCard(); });
    }

    function maybeShowInstallCard() {
        if (isStandalone() || !isMobileWidth() || dismissedRecently()) return;
        if (document.getElementById('dcx-install-card')) return;
        if (isAppleMobile() && !/crios|fxios|edgios/i.test(navigator.userAgent)) { showIosCard(); return; }
        if (deferredPrompt) { showAndroidCard(); }
        // Chromium with no prompt captured yet: wait a little (the event usually fires early).
    }

    window.addEventListener('beforeinstallprompt', function (e) {
        e.preventDefault();
        deferredPrompt = e;
        setTimeout(maybeShowInstallCard, 4000);
    });

    // iOS Safari never fires beforeinstallprompt, so it needs its own trigger:
    // after a short settle delay the Add-to-Home-Screen instructions card appears
    // (maybeShowInstallCard still honours standalone / width / dismissal guards).
    if (isAppleMobile()) { setTimeout(maybeShowInstallCard, 6000); }

    window.addEventListener('appinstalled', function () {
        removeCard();
        noteDismissed();
        toast('DE-CHIMEX was installed.', 'success');
    });

    /* ---------------- 4. QA/E2E hooks ---------------- */
    window.DcxPwa = {
        isStandalone: isStandalone,
        // Render the card on demand with an explicit variant (used by tests/manual QA when
        // the browser does not fire beforeinstallprompt).
        showInstallCard: function (variant) {
            if (variant === 'ios') showIosCard(); else showAndroidCard();
        },
        showUpdateBanner: showUpdateBanner,
        state: function () {
            return {
                standalone: isStandalone(),
                swRegistered: !!navigator.serviceWorker && !!navigator.serviceWorker.controller,
                deferredPrompt: !!deferredPrompt,
                dismissedRecently: dismissedRecently()
            };
        }
    };
})();
