/*
 * DE-CHIMEX PRINT HUB — service worker (v2, PWA task 2026-10-04).
 *
 * Caching policy (deliberately conservative, unchanged from v1):
 *   - NEVER intercepted/cached: /api/*, /hubs/*, /uploads/* (private, authorized uploads),
 *     /checkout/* (payment flows), any non-GET, any cross-origin request (Paystack).
 *   - Navigations (HTML documents): network-first, NEVER served from cache; on network
 *     failure the cached /offline.html fallback is shown (no fake data, no fake success).
 *   - Same-origin static assets (/css, /js, /img, /lib): stale-while-revalidate.
 *
 * Update strategy: a newly deployed worker installs and WAITS. The page (pwa-register.js)
 * detects the waiting worker and shows a "New version available" banner; refreshing is the
 * user's choice (never forced mid-payment or mid-edit). SKIP_WAITING is only accepted as an
 * explicit message from the page.
 *
 * Push: 'push' shows the server-sent notification; 'notificationclick' focuses an existing
 * window at the notification's route or opens one (deep link, never the homepage blindly).
 */
const SW_VERSION = 'dcx-v2';
const OFFLINE_URL = '/offline.html';
const PRECACHE = [OFFLINE_URL, '/img/icon-192.png', '/img/icon-512.png'];

const NEVER_TOUCH = /^\/(api|hubs|uploads|checkout)(\/|$)/i;
const STATIC_OK = /^\/(css|js|img|lib)\//i;

self.addEventListener('install', (event) => {
    event.waitUntil((async () => {
        const cache = await caches.open(SW_VERSION);
        await cache.addAll(PRECACHE);
        // No skipWaiting here on purpose: activation of an updated worker is user-approved
        // via the update banner (SKIP_WAITING message below).
    })());
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const names = await caches.keys();
        await Promise.all(names.filter(n => n !== SW_VERSION).map(n => caches.delete(n)));
        await self.clients.claim();
    })());
});

self.addEventListener('message', (event) => {
    if (event.data === 'SKIP_WAITING') self.skipWaiting();
});

self.addEventListener('fetch', (event) => {
    const request = event.request;

    // Only same-origin GETs are ever considered.
    if (request.method !== 'GET') return;
    const url = new URL(request.url);
    if (url.origin !== self.location.origin) return;

    // Hard exclusions — realtime, API, private uploads and payment pages always hit the network.
    if (NEVER_TOUCH.test(url.pathname)) return;

    // Navigations: network-first with the offline fallback.
    if (request.mode === 'navigate' ||
        (request.headers.get('accept') || '').includes('text/html')) {
        event.respondWith((async () => {
            try {
                return await fetch(request);
            } catch {
                const cache = await caches.open(SW_VERSION);
                const offline = await cache.match(OFFLINE_URL);
                return offline ?? new Response('You are offline.', {
                    status: 503, headers: { 'Content-Type': 'text/plain' }
                });
            }
        })());
        return;
    }

    // Static assets: stale-while-revalidate.
    if (STATIC_OK.test(url.pathname)) {
        event.respondWith((async () => {
            const cache = await caches.open(SW_VERSION);
            const cached = await cache.match(request);
            const network = fetch(request).then(response => {
                if (response && response.ok) cache.put(request, response.clone());
                return response;
            }).catch(() => undefined);
            return cached ?? (await network ?? Response.error());
        })());
    }
});

/* ---------------- Push notifications (server-sent, VAPID) ---------------- */

self.addEventListener('push', (event) => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = {}; }
    const title = typeof data.title === 'string' && data.title ? data.title : 'DE-CHIMEX PRINT HUB';
    const body = typeof data.body === 'string' ? data.body : '';
    event.waitUntil(self.registration.showNotification(title, {
        body: body,
        icon: '/img/icon-192.png',
        badge: '/img/icon-192.png',
        tag: typeof data.tag === 'string' && data.tag ? data.tag : undefined,
        data: { url: typeof data.url === 'string' && data.url ? data.url : '/' }
    }));
});

/* Route a notification click: focus an open window and point it at the notification's
   route; if none is open, open a new one there. Never just the homepage. */
async function handleNotificationClick(event) {
    event.notification.close();
    const target = (event.notification.data && event.notification.data.url) || '/';
    const url = new URL(target, self.location.origin).href;

    const clientList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of clientList) {
        if (client.url.startsWith(self.location.origin)) {
            // focus() can be refused (e.g. unfocusable windows); routing the tab is
            // the important part, focusing is best-effort
            try { await client.focus(); } catch (e) { /* fall through to navigate */ }
            client.postMessage({ type: 'dcx:navigate', url: url });
            return;
        }
    }
    await self.clients.openWindow(url);
}

self.addEventListener('notificationclick', (event) => event.waitUntil(handleNotificationClick(event)));

/* Test/QA hook: lets the E2E journey exercise the SAME routing logic headlessly (there is
   no OS notification surface to click in the test environment). Sends the routing command
   to the requesting client exactly like a real click would. */
self.addEventListener('message', (event) => {
    if (event.data && event.data.type === 'dcx:test-notificationclick') {
        event.waitUntil(handleNotificationClick({
            notification: { close() {}, data: { url: event.data.url } }
        }));
    }
});
