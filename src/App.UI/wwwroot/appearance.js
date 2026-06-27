// Appearance / settings store for the Epic RPF UI. Loaded synchronously in <head>
// so the saved theme + shape + outline are applied to <html> BEFORE the body paints
// (no flash of the default theme). Persists to localStorage (the WebView2 user-data
// folder survives restarts). The Settings dialog in app.js reads/writes through here.
window.Appearance = (function () {
  const KEY = 'epicrpf.appearance';

  const DEFAULTS = {
    theme: 'epic',          // epic | dark | light | ultradark | oled
    accent: '#4fd1c5',
    lang: 'en',
    corners: 'rounded',     // sharp | rounded | extra
    outline: false,
    outlineColor: '#4fd1c5',
    outlineWidth: 1,        // px
  };

  // Theme id + the swatch colours used to draw its preview tile (bg, panel, accent).
  const THEMES = [
    { id: 'epic',      sw: ['#0d0f14', '#141822', '#4fd1c5'] },
    { id: 'dark',      sw: ['#14161a', '#20242b', '#5ed6cb'] },
    { id: 'light',     sw: ['#f3f4f6', '#ffffff', '#2bb6aa'] },
    { id: 'ultradark', sw: ['#06070a', '#0d1015', '#4fd1c5'] },
    { id: 'oled',      sw: ['#000000', '#0c0c0c', '#4fd1c5'] },
  ];

  const ACCENTS = ['#4fd1c5', '#3b82f6', '#8b5cf6', '#ec4899', '#f59e0b', '#22c55e', '#ef4444', '#06b6d4', '#e5e7eb'];

  // r/lg = CSS control radii; win = OS-window corner radius in logical px (applied to the
  // actual frameless window via the winCorners bridge command — see applyWindowCorners()).
  const CORNERS = {
    sharp:   { r: '0px',  lg: '0px',  win: 0  },
    rounded: { r: '6px',  lg: '12px', win: 9  },
    extra:   { r: '12px', lg: '18px', win: 18 },
  };

  function windowRadius(corners) { return (CORNERS[corners] || CORNERS.rounded).win | 0; }

  // Black or white text for best contrast on the given accent (button labels).
  function inkFor(hex) {
    const m = /^#?([0-9a-f]{6})$/i.exec((hex || '').trim());
    if (!m) return '#07221f';
    const n = parseInt(m[1], 16), r = (n >> 16) & 255, g = (n >> 8) & 255, b = n & 255;
    const lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
    return lum > 0.6 ? '#07221f' : '#ffffff';
  }

  function get() {
    try { return Object.assign({}, DEFAULTS, JSON.parse(localStorage.getItem(KEY) || '{}')); }
    catch { return Object.assign({}, DEFAULTS); }
  }
  function save(s) { try { localStorage.setItem(KEY, JSON.stringify(s)); } catch { } }

  function apply(s) {
    const r = document.documentElement;
    r.setAttribute('data-theme', s.theme || 'epic');
    r.style.setProperty('--accent', s.accent || DEFAULTS.accent);
    r.style.setProperty('--accent-ink', inkFor(s.accent || DEFAULTS.accent));
    const c = CORNERS[s.corners] || CORNERS.rounded;
    r.style.setProperty('--radius', c.r);
    r.style.setProperty('--radius-lg', c.lg);
    // Outline corner radius = the OS-window radius + a small pad. The window is hard-clipped
    // by a region at exactly the window radius; matching that radius makes the clip eat the
    // anti-aliased CSS outline along the curve. Curving the outline a touch tighter keeps the
    // whole ring just inside the clip so it stays visible (straight edges are unaffected).
    const winR = c.win | 0;
    r.style.setProperty('--app-radius', (winR > 0 ? winR + 4 : 0) + 'px');
    r.style.setProperty('--app-outline-w', s.outline ? (s.outlineWidth || 1) + 'px' : '0px');
    r.style.setProperty('--app-outline-color', s.outline ? (s.outlineColor || DEFAULTS.accent) : 'transparent');
  }

  // Merge a patch, persist, re-apply, and (for language) re-translate the chrome.
  function set(patch) {
    const s = Object.assign(get(), patch || {});
    save(s); apply(s);
    if (patch && patch.lang && window.I18N) window.I18N.applyI18n(s.lang);
    return s;
  }

  function reset() { try { localStorage.removeItem(KEY); } catch { } apply(DEFAULTS); return Object.assign({}, DEFAULTS); }

  // Adopt the language chosen in the installer (window.__INSTALL_LANG, written to
  // {app}\install.lang and injected by the host). Applied once per install — even over an
  // existing config — and tracked via _installLang so it never fights a later manual choice.
  try {
    const il = window.__INSTALL_LANG;
    if (il && window.I18N && window.I18N.dict && window.I18N.dict[il]) {
      const cur = JSON.parse(localStorage.getItem(KEY) || '{}');
      if (cur._installLang !== il) { cur.lang = il; cur._installLang = il; save(Object.assign({}, DEFAULTS, cur)); }
    }
  } catch { }

  apply(get());   // no-FOUC: apply saved appearance during head parse

  return { KEY, DEFAULTS, THEMES, ACCENTS, CORNERS, get, set, save, apply, reset, inkFor, windowRadius };
})();
