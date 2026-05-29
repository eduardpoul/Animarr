// Animarr backdrop slideshow
// Usage: initGlobalBackdrop(urls, intervalSec, blurPx, brightness) — called by MainLayout
//        initBackdrop(urls, intervalSec, blurPx, brightness)        — page override
//        restoreGlobalBackdrop()                                     — restore after page override
//        stopBackdrop()

let _backdropInterval = null;
let _slideIndex = 0;
let _slideUrls = [];

// Global (MainLayout) backdrop params — persisted so any page can restore them
let _globalUrls = [];
let _globalIntervalSec = 30;
let _globalBlurPx = 2;
let _globalBrightness = 60;

window.initGlobalBackdrop = function (urls, intervalSec, blurPx, brightness) {
    _globalUrls = urls ? [...urls] : [];
    _globalIntervalSec = intervalSec;
    _globalBlurPx = blurPx;
    _globalBrightness = brightness;
    window.initBackdrop(urls, intervalSec, blurPx, brightness);
};

window.restoreGlobalBackdrop = function () {
    if (_globalUrls && _globalUrls.length > 0) {
        window.initBackdrop(_globalUrls, _globalIntervalSec, _globalBlurPx, _globalBrightness);
    } else {
        window.stopBackdrop();
    }
};

// Resolve an image URL into something the renderer will actually paint as a
// CSS background-image. Catalog/fanart URLs are plain http:// (LAN server),
// but in the MAUI WebView the page is served from https://0.0.0.x/ and
// Chromium blocks http:// CSS background-images as MIXED CONTENT. The in-page
// proxy in index.html only rewrites <img src> (not background-image), so the
// backdrop silently failed on phone/TV. Here we fetch the bytes through the
// .NET bridge and hand back a data: URL on MAUI; on the web (https server) the
// URL is same-scheme and used as-is.
async function _resolveBgUrl(url) {
    if (!url) return '';
    try {
        if (url.toLowerCase().startsWith('http://')
            && window.DotNet && typeof window.DotNet.invokeMethodAsync === 'function') {
            const dataUrl = await window.DotNet.invokeMethodAsync(
                'Animarr.App', 'FetchImageAsDataUrl', url);
            if (dataUrl && dataUrl.length > 0) return dataUrl;
        }
    } catch (_) { /* bridge missing / fetch failed — fall back to the raw URL */ }
    return url;
}

window.initBackdrop = async function (urls, intervalSec, blurPx, brightness) {
    stopBackdrop();
    if (!urls || urls.length === 0) return;

    document.body.classList.add('has-backdrop');
    _slideUrls = urls;
    _slideIndex = 0;

    const a = document.getElementById('backdrop-slide-a');
    const b = document.getElementById('backdrop-slide-b');
    if (!a || !b) return;

    // CANVAS §01.6.8: blur(14) · brightness(38%) · saturate(0.95).
    // Saturate is hard-pinned at 0.95 (always desaturate-toward-neutral);
    // blur and brightness still come from settings so users can dial them.
    const filter = `blur(${blurPx}px) brightness(${brightness / 100}) saturate(0.95)`;
    const style = `position:absolute;inset:0;background-size:cover;background-position:center;transition:opacity 1.2s ease;pointer-events:none;filter:${filter};`;

    const first = _encUrl(await _resolveBgUrl(urls[0]));
    // A newer init() may have superseded us while we awaited the proxy fetch —
    // bail so we don't paint a stale backdrop over the current one.
    if (_slideUrls !== urls) return;

    a.style.cssText = style + `background-image:url('${first}');opacity:1;`;
    b.style.cssText = style + `opacity:0;`;

    if (urls.length < 2) return;

    let useA = true;
    _backdropInterval = setInterval(async () => {
        _slideIndex = (_slideIndex + 1) % _slideUrls.length;
        const nextUrl = _encUrl(await _resolveBgUrl(_slideUrls[_slideIndex]));
        if (useA) {
            b.style.backgroundImage = `url('${nextUrl}')`;
            b.style.opacity = '1';
            a.style.opacity = '0';
        } else {
            a.style.backgroundImage = `url('${nextUrl}')`;
            a.style.opacity = '1';
            b.style.opacity = '0';
        }
        useA = !useA;
    }, intervalSec * 1000);
};

window.stopBackdrop = function () {
    document.body.classList.remove('has-backdrop');
    if (_backdropInterval !== null) {
        clearInterval(_backdropInterval);
        _backdropInterval = null;
    }
    const a = document.getElementById('backdrop-slide-a');
    const b = document.getElementById('backdrop-slide-b');
    if (a) { a.style.opacity = '0'; a.style.backgroundImage = ''; }
    if (b) { b.style.opacity = '0'; b.style.backgroundImage = ''; }
};

function _encUrl(url) {
    // Escape single quotes in URLs to avoid breaking CSS. data: URLs (from the
    // MAUI proxy) contain no quotes, so this is a no-op for them.
    return url ? url.replace(/'/g, "%27") : '';
}
