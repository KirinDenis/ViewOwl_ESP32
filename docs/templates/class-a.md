# Class A Templates

Class A is the simplest class from the author's perspective: write any HTML page, point ViewOwl at it, and the server renders it to BGR565 via headless Chromium. No canvas required.

---

## Minimal example

```html
<!DOCTYPE html>
<html>
<head>
  <meta charset="UTF-8">
  <style>
    body {
      margin: 0;
      background: #0a0a1a;
      display: flex;
      align-items: center;
      justify-content: center;
      height: 100vh;
      font-family: system-ui;
      color: #4dd0e1;
      font-size: 48px;
    }
  </style>
</head>
<body
  data-vow-class="A"
  data-vow-name="Hello World"
  data-vow-description="Minimal Class A example"
  data-vow-category="other"
  data-vow-refresh="5"
>
  Hello, display.
</body>
</html>
```

---

## What the server does

1. `DeviceTemplateRefreshWorker` triggers a grab for each connected device resolution.
2. `Chrome.cs` sets the viewport to the device resolution, reloads the page (so JS layout recalculates), and takes a PNG screenshot.
3. The PNG is resized (if needed) and converted to BGR565 by the registered pixel converter.
4. The result is written to `ExchangeFolder/{guid}.bin`.
5. On the next HELLO from the device, the UDP Server streams the `.bin`.

Source: [`DeviceTemplateRefreshWorker.cs`](../../ViewOwl.Grabber.WebAPI/BackgroundServices/DeviceTemplateRefreshWorker.cs), [`Chrome.cs`](../../ViewOwl.Grabber.Engine/Chrome.cs).

---

## Design tips

**Use `system-ui` for body text.** Browser-default serif fonts render poorly at 480×320 with BGR565 quantisation. `system-ui` or `monospace` hold up better.

**Avoid narrow colour gradients.** BGR565 has 32 red levels, 64 green, 32 blue. Wide gradients look fine; narrow stripes within a gradient produce visible banding on the physical display.

**Test at 480×320 pixels.** Open the template in a browser window resized to exactly 480×320 and zoom to 100%. What you see is what the device gets.

**External data:** A Class A template can `fetch()` an external API on page load — the server waits for `networkidle0` before screenshotting. When rendering **for a device**, the grab runs in the *server's* headless Chromium, so the fetch happens from the server, not from a visitor's browser — no end-user IP reaches the third party.

> **On a shared instance, mind browser-side previews.** A live preview in the dashboard gallery runs the template in the *visitor's* browser, so its `fetch()` sends that visitor's IP to the third-party API — a GDPR consideration. For that reason the hosted demo ([view.owlos.sk](https://view.owlos.sk)) does **not** serve external-API templates; they ship as examples under [`ExchangeFolder/External/`](../../ExchangeFolder/External/) for self-hosters. A planned **data-source proxy** will let templates fetch through the ViewOwl server (first-party) — removing the exposure and deduplicating repeated calls.

Free, no-key APIs these examples use: Open-Meteo (weather), transport.rest (trains), Frankfurter (EUR rates), CoinGecko (crypto), Overpass (maps). Respect each API's terms of use when self-hosting.

**Refresh rate:** `data-vow-refresh="5"` means the server re-grabs every 5 minutes. For templates with live data, match the refresh to how often the underlying data changes. `data-vow-refresh="0"` disables automatic re-grabs entirely.

---

## Monochrome mode

The admin panel exposes a **Monochrome** toggle per template (stored in the DB). When enabled, Chrome injects `html { filter: grayscale(1) }` before the screenshot. The result is a true greyscale BGR565 frame — useful for e-ink-style displays or when colour doesn't add information.
