// App shell helpers shared by the web client and the MAUI app.
//
// Mobile app bar: transparent over the hero at the top of a page, acrylic
// once the page scrolls — toggles html.is-scrolled (see components.css).
(function () {
    var raf = 0;
    function update() {
        raf = 0;
        document.documentElement.classList.toggle('is-scrolled', window.scrollY > 8);
    }
    window.addEventListener('scroll', function () {
        if (!raf) raf = requestAnimationFrame(update);
    }, { passive: true });
    // Blazor swaps pages without a scroll event; re-check after navigation.
    window.addEventListener('Animarr:navigated', function () { setTimeout(update, 0); });
    update();
})();
