# Defensive Publication

## Prior Art Disclosure — Public Domain

This project includes a formal defensive publication that establishes prior art
for the core system concept, preventing third parties from patenting the described
methods and systems.

**Title:** System and Method for Protocol-Agnostic Delivery of Browser-Rendered
Visual Content to Resource-Constrained Display Devices via a Server-Side Format Broker

**Author:** Denys Kirin — Independent Researcher, Ukraine

**Published:** May 21, 2026

**DOI:** [10.5281/zenodo.20325797](https://doi.org/10.5281/zenodo.20325797)

**Full text:** https://zenodo.org/records/20325797

**License:** CC Zero (CC0) — No Rights Reserved — Public Domain

---

## What this covers

The defensive publication establishes prior art for the following core ideas:

- A **rendering host** (browser, headless engine, or any graphics library) produces
  pixel data from arbitrary content (HTML/CSS/JS, templates, live data)
- A **server-side format broker** holds device capability profiles and dynamically
  selects pixel format and compression strategy (raw, palette+RLE, palette+LZ4, tiled, etc.)
  based on target device constraints (RAM, display format, transport bandwidth)
- A **protocol-agnostic transport** delivers the adapted frame data over any channel:
  UDP, UART, SPI, I²C, Bluetooth, LoRa, or any other serial or packet-based protocol
- The **target device** is any resource-constrained embedded system (microcontroller,
  Arduino, ESP32, STM32, RP2040, etc.) with any display type (TFT, OLED, e-ink, etc.)
  — no OS or transcoding capability required on the device

This covers all variants and combinations, including single-frame and multi-frame
animation batch delivery with CRC-based deduplication.

---

## Why defensive publication, not a patent

A defensive publication dedicates the idea to the public domain. This means:

- **Anyone** can freely implement, fork, and build upon these techniques
- **No one** (including the authors) can patent the described methods
- The publication date (May 21, 2026) serves as the legally recognized prior art date
- Patent offices worldwide are required to reject any future claims covering this disclosure

This approach was chosen deliberately to keep the technology free and open for
the entire embedded/IoT community.

---

## Community

- GitHub: https://github.com/KirinDenis/ViewOwl_ESP32
- Facebook: https://www.facebook.com/groups/OWLOS
