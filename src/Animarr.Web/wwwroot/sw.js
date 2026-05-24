// Animarr service worker.
//
// Blazor Server can't be made fully offline — every interactive click
// flows through SignalR. The pragmatic role of this SW:
//   1. App-shell cache: gives the user a branded splash + "offline"
//      page when the network is gone instead of the browser's default.
//   2. Static asset cache: fonts, CSS, JS, icons. Cuts repeat-load
//      time and avoids re-fetching when the device is on a slow link.
//   3. Image cache: /api/image and TMDB thumbnails — these are
//      content-addressed by path query string + cache-buster ticks,
//      so a stale-while-revalidate strategy works without staleness.
//
// We deliberately DO NOT cache:
//   • /_blazor SignalR endpoints (real-time)
//   • /api/video streaming (range requests, browser handles)
//   • Anything POST/PUT/PATCH/DELETE (mutations)

const VERSION       = 'animarr-v1';
const SHELL_CACHE   = `${VERSION}-shell`;
const STATIC_CACHE  = `${VERSION}-static`;
const IMAGE_CACHE   = `${VERSION}-images`;

// Minimal shell — only the entry point. The Blazor framework files have
// hashed names that change every deploy; the browser handles those via
// HTTP cache, and we'd just bloat ourselves keeping stale copies.
const SHELL_URLS = [
    '/',
    '/manifest.webmanifest',
    '/icons/icon-192.png',
    '/icons/icon-512.png',
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(SHELL_CACHE)
            .then((cache) => cache.addAll(SHELL_URLS))
            .catch(() => { /* don't block install on a missing icon */ })
    );
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const keys = await caches.keys();
        await Promise.all(keys
            .filter((k) => !k.startsWith(VERSION))
            .map((k) => caches.delete(k)));
        await self.clients.claim();
    })());
});

function isStatic(url) {
    return url.pathname.startsWith('/lib/')
        || url.pathname.startsWith('/_content/')
        || url.pathname.endsWith('.css')
        || url.pathname.endsWith('.js')
        || url.pathname.endsWith('.woff2')
        || url.pathname.endsWith('.ttf')
        || url.pathname.endsWith('.svg');
}

function isImage(url) {
    return url.pathname === '/api/image'
        || url.hostname === 'image.tmdb.org';
}

function shouldBypass(url, request) {
    // Mutations always go to network.
    if (request.method !== 'GET') return true;
    // SignalR + framework hot-paths.
    if (url.pathname.startsWith('/_blazor')) return true;
    if (url.pathname.startsWith('/_framework/blazor.server')) return true;
    // Video streaming uses Range requests — SW can't cache range responses cleanly.
    if (url.pathname === '/api/video') return true;
    // External backdrops / scripts (CDN-cached anyway).
    if (url.hostname && url.hostname !== self.location.hostname && !isImage(url)) return true;
    return false;
}

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    if (shouldBypass(url, event.request)) return;

    if (isStatic(url)) {
        // Cache-first — these are versioned via hashed filenames.
        event.respondWith(cacheFirst(event.request, STATIC_CACHE));
        return;
    }

    if (isImage(url)) {
        // Stale-while-revalidate — image-cache requests carry a `?t=ticks`
        // cache-buster on rescans, so the URL itself encodes freshness.
        event.respondWith(staleWhileRevalidate(event.request, IMAGE_CACHE));
        return;
    }

    // Default: network-first, fall back to shell cache for navigation.
    if (event.request.mode === 'navigate') {
        event.respondWith(networkFirstNavigation(event.request));
    }
});

async function cacheFirst(request, cacheName) {
    const cache  = await caches.open(cacheName);
    const cached = await cache.match(request);
    if (cached) return cached;
    try {
        const resp = await fetch(request);
        if (resp.ok) cache.put(request, resp.clone());
        return resp;
    } catch (err) {
        if (cached) return cached;
        throw err;
    }
}

async function staleWhileRevalidate(request, cacheName) {
    const cache  = await caches.open(cacheName);
    const cached = await cache.match(request);
    const network = fetch(request).then((resp) => {
        if (resp && resp.ok) cache.put(request, resp.clone());
        return resp;
    }).catch(() => cached);
    return cached || network;
}

async function networkFirstNavigation(request) {
    try {
        return await fetch(request);
    } catch (err) {
        const cache  = await caches.open(SHELL_CACHE);
        const cached = await cache.match('/');
        return cached || new Response(
            '<h1>Offline</h1><p>Animarr can\'t reach the server right now.</p>',
            { headers: { 'Content-Type': 'text/html; charset=utf-8' }, status: 503 }
        );
    }
}
