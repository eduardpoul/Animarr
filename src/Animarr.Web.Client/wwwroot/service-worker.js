// Self-destructing service worker.
//
// Animarr was briefly shipped as a PWA, so some browsers still have that old
// service worker registered. A stale SW serves the whole app — including
// index.html for every navigation — out of its own Cache Storage, which
// IGNORES the server's no-cache headers, survives hard refreshes, and even
// answers novel-query fresh-tab navigations from cache. Net effect: deploys
// never reach those devices and the player looks frozen at an old build.
//
// The current app registers NO service worker. So we ship this one purely to
// REPLACE any lingering old SW: on its periodic update check the browser
// fetches /service-worker.js (that request bypasses the SW), sees this
// byte-different script, installs + activates it, and it then purges every
// Cache Storage bucket, unregisters itself, and reloads open tabs so they
// fetch fresh from the network. On devices that never had a SW this file is
// simply never fetched — a harmless no-op.
self.addEventListener('install', () => {
    // Take over immediately instead of waiting for old tabs to close.
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        // 1) Nuke every cache the old SW populated.
        try {
            const keys = await caches.keys();
            await Promise.all(keys.map((k) => caches.delete(k)));
        } catch (e) { /* ignore */ }

        // 2) Remove ourselves so no SW controls the origin anymore.
        try { await self.registration.unregister(); } catch (e) { /* ignore */ }

        // 3) Force every open tab to re-navigate → fresh network fetch of the
        //    real (no-cache) index.html, which then loads the current player JS.
        try {
            const clients = await self.clients.matchAll({ type: 'window' });
            clients.forEach((c) => { try { c.navigate(c.url); } catch (e) {} });
        } catch (e) { /* ignore */ }
    })());
});

// Never serve from cache while we're (briefly) alive — always go to network.
self.addEventListener('fetch', (event) => {
    event.respondWith(fetch(event.request).catch(() => new Response('', { status: 504 })));
});
