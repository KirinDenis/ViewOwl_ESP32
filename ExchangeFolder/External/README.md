# External-API template examples

These templates fetch live data from **third-party APIs directly in the browser** —
weather, currency rates, crypto prices, transit boards, maps. They are kept here as
**examples**, deliberately separated from the main template set.

## Why they are here and not in `../SitesTemplates/`

When a template runs in a real visitor's browser (for example, a live preview in the
dashboard gallery), any third-party `fetch()` it makes transmits the **visitor's IP
address** to that API. On a public, multi-user instance that is a GDPR consideration —
a personal data flow to a third party without a clear legal basis.

So on the hosted demo ([view.owlos.sk](https://view.owlos.sk)) these templates are **not
seeded into the database and not served**. They are **not deleted** — they are reference
examples of how to pull external data into a ViewOwl template.

> On a **device**, these same templates are rendered server-side, so the fetch happens
> from the server, not from a data subject's browser. The exposure is specific to
> browser-side rendering on a shared instance.

## Using them on your own server

If you self-host ViewOwl, you decide your own data flows. To use any of these, copy the
file into `../SitesTemplates/` on your instance and restart — it will be seeded like any
other template.

## Roadmap — the "data source" proxy

A planned **data-source** layer will let templates fetch through the ViewOwl server
instead of calling third-party APIs from the browser. That solves two things at once:

- **Privacy** — the visitor's browser talks only to ViewOwl (first-party); no IP leaves
  to a third party.
- **Efficiency** — the server fetches each source once and fans the result out to every
  template and device, instead of ten templates hammering the same weather API.

When that lands, these examples can move back into the main set safely.

## APIs used by these examples (all free, no key)

| API | Used for | Templates (examples) |
|---|---|---|
| [Open-Meteo](https://open-meteo.com/) | weather | weather*, sf-weather-*, retro-meteo, storm, ocean … |
| [Frankfurter](https://www.frankfurter.app/) | EUR exchange rates | fx-terminal |
| [CoinGecko](https://www.coingecko.com/) | crypto prices | crypto |
| [transport.rest](https://transport.rest/) | transit departures | trains |
| [Overpass](https://overpass-api.de/) | map data | street_map |

Each API has its own terms of use — respect their rate limits and attribution when
self-hosting.
