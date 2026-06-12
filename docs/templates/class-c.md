# Class C Templates

Class C templates are multi-frame animations stored entirely in the ESP32's flash. The server pre-renders each frame and transfers the full batch over UDP. Once stored, the device plays the animation in a loop at the specified FPS — no server connection needed during playback.

---

## How the grab works

The server appends `?vow_frame=N` to the template URL and takes one screenshot per frame. The template reads this parameter and renders exactly that frame:

```javascript
const frameParam = new URLSearchParams(location.search).get('vow_frame');
const N = frameParam !== null ? parseInt(frameParam, 10) : 0;
```

When `vow_frame` is absent (direct browser preview), the template runs a normal animation loop using `requestAnimationFrame`.

**Critical rule:** `?vow_frame=N` must be the **only** source of variation. The same N must always produce the same pixels. `Math.random()` is forbidden — it breaks the CRC check that prevents unnecessary re-renders. Use `Math.sin(N)`, `Math.cos(N)`, or a seeded PRNG instead.

---

## Required body attributes

```html
<body
  data-vow-class="C"
  data-vow-name="My Animation"
  data-vow-description="..."
  data-vow-category="scifi"
  data-vow-frames="12"
  data-vow-fps="3"
  data-vow-refresh="0"
>
```

- `data-vow-frames` — total frame count, clamped to [1..16]
- `data-vow-fps` — playback speed on device, clamped to [1..30]
- `data-vow-refresh="0"` — never auto-re-grab (use `>0` only if frames show live data)

---

## Dev preview pattern

Open the HTML directly in a browser to preview the animation. When `vow_frame` is absent, drive the animation with `requestAnimationFrame`:

```javascript
const FRAMES = 12;
const FPS    = 3;
let N = 0;

function render() { /* draw frame N */ }

const frameParam = new URLSearchParams(location.search).get('vow_frame');
if (frameParam !== null) {
  N = parseInt(frameParam, 10);
  render();
} else {
  // Dev preview — animate in browser.
  let last = 0;
  (function tick(ts) {
    if (ts - last > 1000 / FPS) { last = ts; N = (N + 1) % FRAMES; render(); }
    requestAnimationFrame(tick);
  })(0);
}
```

---

## 3D Wireframe recipe

The SciFi wireframe templates (e.g. the Nostromo schematic) follow a standard pattern documented here. The result is a phosphor CRT aesthetic with progressive edge reveal and camera tilt animation.

### Geometry DSL

```javascript
const V = [], E = [];
const v    = (x, y, z) => (V.push([x, y, z]), V.length - 1);  // vertex
const e    = (a, b)    =>  E.push([a, b]);                     // edge
const poly = (...pts)  => { for (let i = 0; i < pts.length; i++) e(pts[i], pts[(i+1)%pts.length]); };
const rect = (x1, z1, x2, z2, y = 1) => {
  const [a,b,c,d] = [v(x1,y,z1), v(x2,y,z1), v(x2,y,z2), v(x1,y,z2)];
  poly(a,b,c,d);
};
const hl = (x1, z, x2, y=2) => e(v(x1,y,z), v(x2,y,z));   // horizontal line
const vl = (x, z1, z2, y=2) => e(v(x,y,z1), v(x,y,z2));   // vertical line
```

**Coordinate system:**
- X = right (+) / left (−)
- Y = up — keep thin (±2–4) for a flat slab that reads as 3D when tilted
- Z = depth — aft (+) / fore (−)

### Near-orthographic projection

```javascript
const VW = 480, VH = 320;

function project(pt, pitch, yaw) {
  const [x, y, z] = pt;
  const cy = Math.cos(yaw),  sy = Math.sin(yaw);
  const x1 =  x * cy + z * sy;
  const z1 = -x * sy + z * cy;
  const cp = Math.cos(pitch), sp = Math.sin(pitch);
  const y2 = y * cp - z1 * sp;
  const z2 = y * sp + z1 * cp;
  const fov = 2200;  // large value → near-orthographic, fills screen without distortion
  const d   = fov / (fov + z2 + 280);
  return [VW / 2 + x1 * d, VH / 2 + y2 * d];
}
```

**Sizing rule:** outer hull ≈ ±215 X, ±130 Z fills 480×320 edge-to-edge at a top-down view.

### Per-frame parameters

```javascript
const PARAMS = [
  { pitch: 0.02, yaw: -0.05, lines:  30 },   // top-down, few edges visible
  { pitch: 0.10, yaw: -0.05, lines:  80 },
  { pitch: 0.20, yaw: -0.05, lines: 150 },
  // ...
  { pitch: 0.66, yaw: -0.05, lines: 9999 },  // angled, all edges
];
```

- **pitch = 0** → looking straight down (floor plan)
- **pitch = π/2** → looking from the side
- **lines** → how many edges of `E[]` to draw — creates the progressive reveal effect
- **Animation top-down → angled:** pitch starts near 0, increases to ~0.6–0.7
- **Animation angled → top-down:** pitch starts ~1.3, decreases toward 0

### Draw loop with phosphor glow

```javascript
const p     = PARAMS[N];
const count = Math.min(p.lines, E.length);

for (let i = 0; i < count; i++) {
  const [a, b] = E[i];
  const [ax, ay] = project(V[a], p.pitch, p.yaw);
  const [bx, by] = project(V[b], p.pitch, p.yaw);
  const fresh = (i >= count - 4);   // last 4 edges = "being drawn now"

  // Glow halo
  ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by);
  ctx.strokeStyle = fresh ? 'rgba(140,255,155,0.32)' : 'rgba(65,185,78,0.11)';
  ctx.lineWidth   = fresh ? 5 : 2.8;
  ctx.stroke();

  // Core line
  ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx, by);
  ctx.strokeStyle = fresh ? 'rgba(210,255,218,0.96)' : 'rgba(95,224,112,0.78)';
  ctx.lineWidth   = fresh ? 1.5 : 0.9;
  ctx.stroke();
}

// Scanning cursor dot at the leading edge
if (count > 0) {
  const [, b] = E[count - 1];
  const [bx, by] = project(V[b], p.pitch, p.yaw);
  ctx.fillStyle = '#ffffff';
  ctx.fillRect(bx - 1, by - 1, 3, 3);
}
```

### CRT effects stack

```javascript
// 1. Scanlines
ctx.fillStyle = 'rgba(0,0,0,0.24)';
for (let y = 0; y < VH; y += 2) ctx.fillRect(0, y, VW, 1);

// 2. Vignette
const g = ctx.createRadialGradient(VW/2, VH/2, VH*0.22, VW/2, VH/2, VH*0.84);
g.addColorStop(0, 'rgba(0,0,0,0)');
g.addColorStop(1, 'rgba(0,0,0,0.92)');
ctx.fillStyle = g;
ctx.fillRect(0, 0, VW, VH);

// DO NOT use getImageData() for per-pixel grain — it reads 614 KB per frame,
// causes ARM64 headless Chrome timeouts during batch capture, and Math.random()
// breaks CRC dedup. Scanlines + vignette are sufficient CRT texture.
```

### Colour palette

| Role | Value |
|---|---|
| Background | `#010902` |
| Main lines | `rgba(95,224,112,0.78)` |
| Active / fresh lines | `rgba(210,255,218,0.96)` |
| Glow halo | `rgba(65,185,78,0.11)` |
| Cursor dot | `#ffffff` |

### Edge ordering strategy

Order `E[]` so that each partial render looks intentional:

1. Outer hull silhouette
2. Inner structural rings / frames
3. Major zones (bridge, engine room, cargo bay)
4. Room partitions and corridors
5. Interior detail (fixtures, hatches, minor structure)

Even `lines=30` (early frames) should look like a meaningful skeleton, not random lines.

### Octagonal hull (fills landscape screen)

```javascript
const OH = [
  v(-162, 4, -130), v( 162, 4, -130),  // fore flat
  v( 215, 4,  -78), v( 215, 4,   78),  // starboard flat
  v( 162, 4,  130), v(-162, 4,  130),  // aft flat
  v(-215, 4,   78), v(-215, 4,  -78),  // port flat
];
poly(...OH);
```

---

## Performance notes

- **No `getImageData()` / `putImageData()`** — these copy 614 KB of pixel data per frame on a canvas that is 480×320. Doing this 12 times in sequence on ARM64 headless Chrome without GPU acceleration causes the grabber's screenshot timeout to fire before all frames complete. The batch then never writes to the manifest and the device never receives the animation.
- **Keep draw calls per frame reasonable.** Hundreds of `stroke()` calls are fine. Thousands with shadow blur are not.
- **Test frame count:** 12 frames × ~200 ms per frame = ~2.4 s total grab time. If your template is complex, reduce frame count or simplify geometry.
