# Self-Hosting

ViewOwl is designed to run on a Linux ARM64 server; x86-64 also works with minor changes to the deployment steps.

This guide covers a manual setup; wire the same steps into your CI of choice for automated deploys.

---

## Requirements

| Component | Version |
|---|---|
| OS | Ubuntu 22.04 LTS (ARM64 recommended) |
| .NET runtime | .NET 8 (included in the self-contained publish) |
| Chromium | Latest via `apt` (ARM64 package) |
| Disk | 2 GB minimum for exchange folder and logs |
| RAM | 1 GB minimum; 2 GB recommended |
| Ports | TCP 5000 (Grabber API), UDP 11000 (UDP Server) |

---

## Step 1 — Install Chromium

The server uses the system Chromium package, not the bundled Chrome-for-Testing binary (which is x64-only).

```bash
sudo apt update
sudo apt install -y chromium-browser
which chromium-browser   # confirm path
```

---

## Step 2 — Create the exchange folder

```bash
sudo mkdir -p /opt/viewowl/exchange/SitesTemplates
sudo chown -R $USER:$USER /opt/viewowl
```

---

## Step 3 — Configure disk hygiene

Logs and exchange files grow without bounds if left unconfigured. **Do this before starting the services.**

```bash
# Cap journald to 200 MB
sudo mkdir -p /etc/systemd/journald.conf.d
echo -e "[Journal]\nSystemMaxUse=200M" | sudo tee /etc/systemd/journald.conf.d/size.conf
sudo systemctl restart systemd-journald

# Clean up stale exchange files weekly (keeps last 7 days)
echo "0 3 * * 0 $USER find /opt/viewowl/exchange -name '*.bin' -mtime +7 -delete" | crontab -
```

See [`ops_disk_and_logs.md`](../../memory/ops_disk_and_logs.md) for background — a misconfigured server filled its entire disk with journald logs in production.

---

## Step 4 — Publish the binaries

On your build machine (or let CI do it):

```bash
# Grabber API
dotnet publish ViewOwl.Grabber.WebAPI \
  -c Release -r linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o ./publish/grabber

# UDP Server
dotnet publish ViewOwl.UDP.Server \
  -c Release -r linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o ./publish/udp
```

Copy the binaries and config to the server:

```bash
scp publish/grabber/ViewOwl.Grabber.WebAPI user@server:/opt/viewowl/
scp publish/udp/ViewOwl.UDP.Server         user@server:/opt/viewowl/
scp ExchangeFolder/SitesTemplates/*        user@server:/opt/viewowl/exchange/SitesTemplates/
```

---

## Step 5 — Configure appsettings

Create `/opt/viewowl/appsettings.Production.json`:

```json
{
  "Shared": {
    "ExchangeFolder": "/opt/viewowl/exchange/",
    "WebApiBaseUrl": "http://localhost:5000"
  },
  "Grabber": {
    "GrabbedListFileName": "GrabbedList.json"
  }
}
```

---

## Step 6 — Create systemd units

**Grabber** (`/etc/systemd/system/viewowl-grabber.service`):

```ini
[Unit]
Description=ViewOwl Grabber API
After=network.target

[Service]
Type=simple
User=YOUR_USER
WorkingDirectory=/opt/viewowl
ExecStart=/opt/viewowl/ViewOwl.Grabber.WebAPI
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:5000

[Install]
WantedBy=multi-user.target
```

**UDP Server** (`/etc/systemd/system/viewowl-udp.service`):

```ini
[Unit]
Description=ViewOwl UDP Server
After=network.target viewowl-grabber.service

[Service]
Type=simple
User=YOUR_USER
WorkingDirectory=/opt/viewowl
ExecStart=/opt/viewowl/ViewOwl.UDP.Server
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable viewowl-grabber viewowl-udp
sudo systemctl start  viewowl-grabber viewowl-udp
sudo systemctl status viewowl-grabber viewowl-udp
```

---

## Step 7 — Update firmware config

Edit [`ViewOwl.ESP32.Client/main/config.h`](../../ViewOwl.ESP32.Client/main/config.h) before flashing:

```c
#define SERVER_IP   "YOUR_SERVER_IP"
#define SERVER_PORT 11000
```

The device connects directly to UDP port 11000. Make sure this port is reachable from your WiFi network (not firewalled).

---

## Logs

```bash
# Grabber
journalctl -u viewowl-grabber -f

# UDP Server
journalctl -u viewowl-udp -f
```

---

## Firewall

Only UDP 11000 needs to be reachable by ESP32 devices. The Grabber API (TCP 5000) should be kept internal — it is not hardened for public exposure.

```bash
sudo ufw allow 11000/udp
sudo ufw allow ssh
sudo ufw enable
```

---

## Updating

```bash
sudo systemctl stop viewowl-grabber viewowl-udp
# copy new binaries
sudo systemctl start viewowl-grabber viewowl-udp
```

Both services restart cleanly — no data loss. The exchange folder persists between restarts.
