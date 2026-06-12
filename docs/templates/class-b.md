# Class B Templates

Class B templates use an HTML5 canvas as their only output surface. They can be grabbed by the server (like Class A) **or** pushed live from the admin panel browser at 1–5 second intervals — without involving Chrome at all.

The push path reads the canvas pixels directly in the browser, converts them to BGR565, and POSTs the compressed frame to the device via the API. This means a Class B template running in the admin panel can update the physical display in near real time.

---

## Minimal example

```html
<!DOCTYPE html>
<html>
<head><meta charset="UTF-8"></head>
<body
  data-vow-class="B"
  data-vow-name="Simple Clock"
  data-vow-description="Digital clock, canvas-based"
  data-vow-category="clock"
  data-vow-refresh="1"
>
<canvas id="c"></canvas>
<script>
  const canvas = document.getElementById('c');
  const ctx    = canvas.getContext('2d');

  // Always fill canvas to window size — never hardcode 480 or 320.
  canvas.width  = window.innerWidth;
  canvas.height = window.innerHeight;

  // Optional: draw at a logical 480×320 coordinate space regardless of actual size.
  const W = 480, H = 320;
  ctx.setTransform(canvas.width / W, 0, 0, canvas.height / H, 0, 0);

  function render() {
    ctx.fillStyle = '#000814';
    ctx.fillRect(0, 0, W, H);

    const now = new Date();
    const time = now.toTimeString().slice(0, 8);

    ctx.fillStyle = '#4dd0e1';
    ctx.font = 'bold 72px monospace';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(time, W / 2, H / 2);
  }

  render();
  setInterval(render, 1000);
</script>
</body>
</html>
```

---

## Canvas sizing rules

**Always** use `window.innerWidth` / `window.innerHeight` for canvas dimensions:

```javascript
canvas.width  = window.innerWidth;
canvas.height = window.innerHeight;
```

The server sets the Chrome viewport to the target device resolution before grabbing. If you hardcode `480` and the device is `320×240`, the grab crops or scales incorrectly.

For predictable coordinates, use a logical coordinate space via `setTransform`:

```javascript
// Draw as if the canvas is always 480×320.
// The transform scales to whatever the actual canvas size is.
ctx.setTransform(canvas.width / 480, 0, 0, canvas.height / 320, 0, 0);
```

---

## BGR565 palette constraints

BGR565 has:
- **32 levels** of red and blue (step = 8)
- **64 levels** of green (step = 4)

Colours that are "safe" (round-trip through BGR565 without visible shift) are multiples of these steps. Gradients work for wide transitions but produce visible banding in narrow ranges on the physical display.

**What works well:**
- Flat fills with a limited palette (6–12 colours)
- High-contrast line art and text
- Monochrome or duotone designs

**What produces artefacts:**
- CSS `box-shadow`, `text-shadow`, `filter: blur()` — alpha compositing produces intermediate colours that quantise badly
- Gradients across fewer than ~30 pixels
- Semi-transparent overlays

---

## Determinism and CRC dedup

The server skips writing a new `.bin` if the CRC of the rendered frame matches the previous one. This avoids unnecessary UDP transfers when nothing has changed.

For the dedup to work, the same render call must always produce the same pixels. Avoid:

- `Math.random()` — use `Math.sin(Date.now() / period)` for smooth variation instead
- `new Date()` in render paths that don't change the visible output
- Calls to external APIs that return different data on each render

---

## Push loop behaviour

When the admin panel's **Push** button is active, the browser:

1. Renders the template in a hidden iframe (always at 480×320)
2. Reads the canvas via `getImageData()`
3. Scales to the connected device's resolution if needed (offscreen canvas)
4. Converts RGBA → BGR565 with palette+RLE compression
5. POSTs the compressed frame to `POST /api/guest/device/{guid}/frame`
6. Repeats every 1–5 seconds

The push loop stops automatically if:
- The iframe has no canvas element (DOM-only templates are unsupported)
- The server returns a non-200 response
- A `SecurityError` is thrown (canvas tainted by cross-origin images)

**Note:** Google Fonts and other web fonts do **not** taint the canvas. Only cross-origin `<img>` elements do.
