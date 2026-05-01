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

window.initBackdrop = function (urls, intervalSec, blurPx, brightness) {
    stopBackdrop();
    if (!urls || urls.length === 0) return;

    document.body.classList.add('has-backdrop');
    _slideUrls = urls;
    _slideIndex = 0;

    const a = document.getElementById('backdrop-slide-a');
    const b = document.getElementById('backdrop-slide-b');
    if (!a || !b) return;

    const filter = `blur(${blurPx}px) brightness(${brightness / 100})`;
    const style = `position:absolute;inset:0;background-size:cover;background-position:center;transition:opacity 1.2s ease;pointer-events:none;filter:${filter};`;

    a.style.cssText = style + `background-image:url('${_encUrl(urls[0])}');opacity:1;`;
    b.style.cssText = style + `opacity:0;`;

    if (urls.length < 2) return;

    let useA = true;
    _backdropInterval = setInterval(() => {
        _slideIndex = (_slideIndex + 1) % _slideUrls.length;
        const nextUrl = _encUrl(_slideUrls[_slideIndex]);
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
    // Escape single quotes in URLs to avoid breaking CSS
    return url ? url.replace(/'/g, "%27") : '';
}
