# Contributing to ViewOwl

Thanks for your interest. Contributions are welcome in several forms: new templates, bug fixes, hardware support, and documentation improvements.

---

## Ways to contribute

### Submit a template

The fastest way to contribute. Build an HTML template, test it locally, and open a PR adding it to the `ExchangeFolder/SitesTemplates/` folder.

Requirements:
- `data-vow-*` attributes correctly set on `<body>` — see [Templates overview](docs/templates/overview.md)
- Tested at 480×320 and/or 320×240 (depending on what it targets)
- No hardcoded Russian city names, coordinates, or Russian-language strings
- External APIs must be GDPR-compliant and require no API key. Approved: Open-Meteo, OpenSky, transport.rest, Frankfurter.app, CoinGecko. Avoid anything that requires registration, sends data to US-only servers, or is not free to use without limits.
- Class C only: no `Math.random()` — same frame index must always produce same pixels

### Fix a bug

1. Open an issue describing the bug and the affected component (Grabber, UDP Server, firmware)
2. Fork the repo, fix it on a branch named `fix/<short-desc>`
3. Open a PR with a description of what was wrong and how the fix works

### Add hardware support

New display controller? New ESP32 variant?

- Add a new `#ifdef` branch in [`lcd_init.c`](ViewOwl.ESP32.Client/main/lcd_init.c) for the driver
- Add a build step in [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml)
- Document the wiring in [`docs/hardware.md`](docs/hardware.md)

### Improve documentation

Documentation lives in `docs/`. All files are plain Markdown. Fixes, clarifications, and additional guides are all welcome — open a PR directly.

---

## Code style

### C# (.NET)

- Allman braces, XML doc comments on all public members
- Async methods end in `Async`
- Private fields: `_camelCase`
- No `static` mutable state in business logic
- `TreatWarningsAsErrors` is enabled — the build must pass cleanly

### C (ESP32 firmware)

- K&R braces, C99
- All functions `lower_snake_case`, macros `UPPER_SNAKE_CASE`
- Module-private symbols always `static`
- `ESP_LOGx(TAG, ...)` only — no `printf`
- Check every `esp_err_t` return value

### All files

- Comments in English only
- No Russian language anywhere — city names, strings, comments, API calls
- Conventional Commits: `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`

---

## Architecture overview

Before making changes, read [docs/architecture.md](docs/architecture.md). The three components (Grabber, UDP Server, firmware) communicate through the filesystem and UDP — understanding the data flow prevents most integration mistakes.

---

## Running locally

### Grabber + UDP Server

```bash
cd ViewOwl.Grabber.WebAPI
dotnet run
```

```bash
cd ViewOwl.UDP.Server
dotnet run
```

Both read from `appsettings.Development.json` by default. Set `Shared.ExchangeFolder` to a local path.

### Firmware

See [docs/getting-started.md](docs/getting-started.md) for flashing. For development builds, use `idf.py build flash monitor` with ESP-IDF 5.5.

---

## Opening an issue

Include:
- Component: Grabber / UDP Server / Firmware / Dashboard / Template
- Steps to reproduce
- Expected vs actual behaviour
- Firmware version (shown on device TUI boot screen)
- Display size and controller (e.g. 480×320 ST7796)
