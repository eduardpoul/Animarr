// ====== Main App ======
const { useState: useStateA, useEffect: useEffectA, useMemo: useMemoA, useCallback: useCallbackA, useRef: useRefA } = React;

const TWEAK_DEFAULTS = /*EDITMODE-BEGIN*/{
  "accent": "crimson",
  "showBackdrop": true,
  "backdropBlur": 14,
  "backdropBrightness": 38,
  "rotateSec": 18,
  "density": "comfortable",
  "heroStyle": "card"
}/*EDITMODE-END*/;

const ACCENT_MAP = {
  crimson: { base: "oklch(0.66 0.20 25)",  hi: "oklch(0.74 0.21 25)",  line: "oklch(0.66 0.20 25 / 0.40)", soft: "oklch(0.66 0.20 25 / 0.16)" },
  amber:   { base: "oklch(0.72 0.17 60)",  hi: "oklch(0.80 0.16 60)",  line: "oklch(0.72 0.17 60 / 0.40)", soft: "oklch(0.72 0.17 60 / 0.16)" },
  green:   { base: "oklch(0.74 0.15 150)", hi: "oklch(0.82 0.15 150)", line: "oklch(0.74 0.15 150 / 0.40)", soft: "oklch(0.74 0.15 150 / 0.16)" },
  blue:    { base: "oklch(0.66 0.16 240)", hi: "oklch(0.74 0.15 240)", line: "oklch(0.66 0.16 240 / 0.40)", soft: "oklch(0.66 0.16 240 / 0.16)" },
  violet:  { base: "oklch(0.66 0.20 290)", hi: "oklch(0.74 0.20 290)", line: "oklch(0.66 0.20 290 / 0.40)", soft: "oklch(0.66 0.20 290 / 0.16)" },
};

const App = () => {
  const [route, setRoute] = useStateA("catalog");
  const [openId, setOpenId] = useStateA(null);
  const [tweaks, setTweak] = window.useTweaks(TWEAK_DEFAULTS);

  // ── Single source of truth for the page backdrop ─────────────────────────
  // Screens push to this via setBdImage(url, hue). The global blurred backdrop
  // and the in-page hero read from this state — they always show the SAME image.
  const firstItem = window.LIBRARY[0];
  const [bdImage, setBdImageRaw] = useStateA({ url: firstItem.bd, hue: firstItem.hue });
  const bdImage_ref = useRefA(bdImage);
  bdImage_ref.current = bdImage;
  const setBdImage = useCallbackA((url, hue) => {
    if (!url) return;
    if (bdImage_ref.current.url === url) return;
    setBdImageRaw({ url, hue: hue ?? 0 });
  }, []);

  // apply accent live
  useEffectA(() => {
    const a = ACCENT_MAP[tweaks.accent] || ACCENT_MAP.crimson;
    const r = document.documentElement.style;
    r.setProperty("--accent",      a.base);
    r.setProperty("--accent-hi",   a.hi);
    r.setProperty("--accent-line", a.line);
    r.setProperty("--accent-soft", a.soft);
  }, [tweaks.accent]);

  const handleOpen  = (id) => { window.scrollTo?.(0, 0); setOpenId(id); };
  const handleBack  = () => { setOpenId(null); };
  const handleRoute = (r) => { setOpenId(null); setRoute(r); };

  return (
    <div style={{ display:"flex", height:"100vh", width:"100vw", position:"relative" }}>
      {tweaks.showBackdrop && (
        <Backdrop image={bdImage.url} blur={tweaks.backdropBlur} brightness={tweaks.backdropBrightness} hue={bdImage.hue} />
      )}
      <Sidebar route={route} onRoute={handleRoute} />
      <main style={{
        flex: 1, height:"100%", overflow:"auto",
        position:"relative", zIndex: 2,
      }}>
        <div style={{ paddingBottom: 100 }}>
          {openId
            ? <MediaDetailScreen id={openId} onBack={handleBack} density={tweaks.density} setBdImage={setBdImage} />
            : route === "catalog"  ? <CatalogScreen onOpen={handleOpen} heroStyle={tweaks.heroStyle} density={tweaks.density} rotateSec={tweaks.rotateSec} setBdImage={setBdImage} />
            : route === "torrents" ? <Container density={tweaks.density} top><TorrentsScreen /></Container>
            : route === "settings" ? <Container density={tweaks.density} top><SettingsScreen /></Container>
            : <Container density={tweaks.density} top><CatalogScreen onOpen={handleOpen} heroStyle={tweaks.heroStyle} density={tweaks.density} rotateSec={tweaks.rotateSec} setBdImage={setBdImage} /></Container>}
        </div>
      </main>

      <TweaksUI tweaks={tweaks} setTweak={setTweak} />
    </div>
  );
};

const TweaksUI = ({ tweaks, setTweak }) => {
  if (!window.TweaksPanel) return null;
  return (
    <window.TweaksPanel title="Tweaks">
      <window.TweakSection label="Theme">
        <window.TweakSelect
          label="Accent"
          value={tweaks.accent}
          onChange={v => setTweak("accent", v)}
          options={[
            { value: "crimson", label: "Crimson (default)" },
            { value: "amber",   label: "Amber"             },
            { value: "green",   label: "Green"             },
            { value: "blue",    label: "Blue"              },
            { value: "violet",  label: "Violet"            },
          ]}
        />
        <window.TweakSelect
          label="Catalog hero"
          value={tweaks.heroStyle}
          onChange={v => setTweak("heroStyle", v)}
          options={[
            { value: "fullbleed", label: "Full-bleed cinematic" },
            { value: "card",      label: "Contained card"       },
            { value: "split",     label: "Magazine split"       },
          ]}
        />
        <window.TweakRadio
          label="Density"
          value={tweaks.density}
          onChange={v => setTweak("density", v)}
          options={[
            { value:"comfortable", label:"Comfortable" },
            { value:"compact",     label:"Compact" },
          ]}
        />
      </window.TweakSection>

      <window.TweakSection label="Backdrop">
        <window.TweakToggle
          label="Show on all pages"
          value={tweaks.showBackdrop}
          onChange={v => setTweak("showBackdrop", v)}
        />
        <window.TweakSlider
          label="Blur"
          value={tweaks.backdropBlur}
          min={0} max={30} step={1} unit="px"
          onChange={v => setTweak("backdropBlur", v)}
        />
        <window.TweakSlider
          label="Brightness"
          value={tweaks.backdropBrightness}
          min={10} max={80} step={1} unit="%"
          onChange={v => setTweak("backdropBrightness", v)}
        />
        <window.TweakSlider
          label="Hero rotate"
          value={tweaks.rotateSec}
          min={5} max={120} step={1} unit="s"
          onChange={v => setTweak("rotateSec", v)}
        />
      </window.TweakSection>
    </window.TweaksPanel>
  );
};

ReactDOM.createRoot(document.getElementById("root")).render(<App />);
