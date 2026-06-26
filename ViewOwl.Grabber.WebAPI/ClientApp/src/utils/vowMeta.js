/**
 * Parses data-vow-* attributes from an HTML template string.
 * Uses lightweight regex — no DOM dependency, safe to call anywhere.
 *
 * @param {string} html  Raw HTML content of a template.
 * @returns {VowMeta}
 *
 * @typedef {Object} VowMeta
 * @property {string} vowClass    - 'A' | 'B' | 'C'  (default 'A')
 * @property {string} name        - data-vow-name
 * @property {string} category    - data-vow-category (default 'other')
 * @property {string} description - data-vow-description
 * @property {number} frameCount  - data-vow-frames   (1-32, default 1)
 * @property {number} fps         - data-vow-fps      (1-30, default 10)
 * @property {number} refresh     - data-vow-refresh  minutes (default 5)
 */
export function parseVowMeta(html) {
  if (!html) return defaults()

  const attrs = {}
  const re = /data-vow-([\w-]+)\s*=\s*["']([^"']*)["']/gi
  let m
  while ((m = re.exec(html)) !== null) {
    attrs[m[1].toLowerCase()] = m[2]
  }

  const clamp = (v, min, max) => Math.min(max, Math.max(min, v))

  const vowClass = (attrs['class'] ?? 'A').toUpperCase()[0] ?? 'A'

  return {
    vowClass,
    name:        attrs['name']        ?? '',
    category:    (attrs['category']   ?? 'other').toLowerCase(),
    description: attrs['description'] ?? '',
    frameCount:  clamp(parseInt(attrs['frames']  ?? '1',  10), 1, 32),
    fps:         clamp(parseInt(attrs['fps']     ?? '10', 10), 1, 30),
    refresh:     Math.max(0, parseInt(attrs['refresh'] ?? '5', 10)),
  }
}

function defaults() {
  return { vowClass: 'A', name: '', category: 'other', description: '',
           frameCount: 1, fps: 10, refresh: 5 }
}

/** All recognised category slugs (display order). */
export const VOW_CATEGORIES = [
  { slug: 'all',     label: 'ALL' },
  { slug: 'weather', label: 'WEATHER' },
  { slug: 'clock',   label: 'CLOCK' },
  { slug: 'rates',   label: 'RATES' },
  { slug: 'menu',    label: 'MENU' },
  { slug: 'system',  label: 'SYSTEM' },
  { slug: 'scifi',   label: 'SCI-FI' },
  { slug: 'ambient', label: 'AMBIENT' },
  { slug: 'other',   label: 'OTHER' },
]

/** Injects a frame-index polyfill into an HTML string so that templates
 *  reading URLSearchParams('vow_frame') work correctly inside an iframe
 *  loaded via srcDoc (where location.search is always empty).
 *
 * @param {string} html       Template HTML.
 * @param {number} frameIndex Frame index to inject (0-based).
 * @returns {string}
 */
export function injectVowFrame(html, frameIndex) {
  const script = `<script>
(function(){
  var _f=${frameIndex};
  var _Q=window.URLSearchParams;
  window.URLSearchParams=function(s){
    var p=new _Q(s);
    var _g=p.get.bind(p);
    p.get=function(k){return k==='vow_frame'?String(_f):_g(k);};
    return p;
  };
})();
<\/script>`
  // Inject right after <head> or at the very start if no head tag.
  if (/<head[\s>]/i.test(html))
    return html.replace(/(<head[\s>][^>]*>)/i, `$1${script}`)
  return script + html
}
