# Self-hosted fonts (Docker offline)

For offline / air-gapped installs, the font bundled here lets the app
render without reaching the Google Fonts CDN. Switch the hosts to load
[`fonts.css`](fonts.css) instead of the CDN `<link>` (see "To switch").

## Bundled files

```
hanken-grotesk-variable.ttf   — Hanken Grotesk, variable wght 100–900
hanken-grotesk-OFL.txt        — SIL Open Font License 1.1
```

One variable font covers every weight the app uses (400–800: UI, display
and labels). The per-theme families (Inter, JetBrains Mono, Quicksand,
Noto Serif SC / JP) still come from the Google Fonts CDN; self-host them
here the same way for a fully air-gapped install.

Source: https://github.com/google/fonts/tree/main/ofl/hankengrotesk (OFL-1.1)

> Note: the Google Fonts catalog name is **Hanken Grotesk** (not
> "Grotesque"). `family=Hanken+Grotesque` returns HTTP 400 from the CDN.

## To switch

Replace the Google Fonts `<link rel="stylesheet">` tag in both
`index.html` files with:

```html
<link rel="stylesheet" href="_content/Animarr.UI/lib/fonts/fonts.css" />
```

The `@font-face` in `fonts.css` resolves via the relative
`wwwroot/lib/fonts/hanken-grotesk-variable.ttf` path.
