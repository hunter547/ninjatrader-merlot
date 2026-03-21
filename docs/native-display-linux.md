# Native Display Mode (Linux)

Run NinjaTrader windows directly on your Linux desktop instead of through the noVNC browser.

## Prerequisites

- **Linux** with an X11 or XWayland display
- **Native Docker Engine** (`docker.io`), not Docker Desktop

Docker Desktop runs containers in a VM which prevents sharing the host's X11 socket. If you have Docker Desktop installed, switch to native Docker:

```bash
# Remove Docker Desktop's snap (if installed)
sudo snap remove docker

# Install native Docker
sudo apt install -y docker.io

# Add your user to the docker group
sudo usermod -aG docker $USER

# Start Docker
sudo systemctl enable --now docker

# Apply group change (or log out and back in)
newgrp docker
```

If you have both Docker Desktop and native Docker installed, make sure you're using the native context:

```bash
docker context use default
```

## Usage

```bash
# Allow Docker containers to access your display (run once per session)
xhost +local:

# Start with native display
docker compose --profile native up ninjatrader-native -d

# With auto-login
NT_USERNAME=... NT_PASSWORD=... docker compose --profile native up ninjatrader-native -d
```

NinjaTrader windows will appear directly on your desktop as individual windows managed by your window manager.

## Troubleshooting

### No windows appear
- Make sure you ran `xhost +local:` before starting the container
- Verify you're on native Docker: `docker info | grep "Root Dir"` should show `/var/lib/docker`, not a snap path
- Check logs: `docker logs ninjatrader-merlot-native`

### Window flickering
The NinjaTrader Control Center may flicker on XWayland until you click any menu dropdown (e.g. "New" or "Tools"). This is a Wine rendering quirk on XWayland — the WPF rendering surface doesn't fully initialize until a menu interaction occurs. It's cosmetic only and does not affect functionality. Clicking any dropdown menu once will stop the flickering for the rest of the session.

### Docker credential errors
If you see `docker-credential-desktop` errors after switching from Docker Desktop, clear the credential store:
```bash
# Edit ~/.docker/config.json and set:
# "credsStore": ""
```
