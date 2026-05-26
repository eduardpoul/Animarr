# Self-hosted fonts (Docker offline)

For offline Docker installs, drop the WOFF2 font files here and switch
`App.razor` to use [`fonts.css`](fonts.css) instead of the Google Fonts
CDN link.

## Required files

```
geist-400.woff2
geist-500.woff2
geist-600.woff2
geist-700.woff2
geist-mono-400.woff2
geist-mono-500.woff2
archivo-black-400.woff2
noto-serif-sc-700.woff2
noto-serif-sc-900.woff2
```

Sources:
- Geist + Geist Mono — https://github.com/vercel/geist-font (OFL-1.1)
- Archivo Black — https://github.com/Omnibus-Type/Archivo (OFL-1.1)
- Noto Serif SC — https://github.com/notofonts/noto-cjk (OFL-1.1)

All four families are open-source under OFL — bundle freely.

## To switch

In `Components/App.razor`, replace the three Google Fonts `<link>` tags
with a single line:

```html
<link rel="stylesheet" href="@Assets["lib/fonts/fonts.css"]" />
```

The CSS `@font-face` declarations in `fonts.css` resolve via the
relative `wwwroot/lib/fonts/*.woff2` paths.
