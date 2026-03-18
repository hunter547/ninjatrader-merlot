#!/bin/bash
set -e

SCREENSHOT_DIR="/home/ninjatrader/screenshots"
NT_EXE="$WINEPREFIX/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe"
NT_CONFIG="$WINEPREFIX/drive_c/users/ninjatrader/Documents/NinjaTrader 8/Config.xml"

mkdir -p "$SCREENSHOT_DIR"

# Seed NinjaTrader 8 documents directory if the bind mount is empty (first run, or dev deletes host machine mount)
NT_DOCS="$WINEPREFIX/drive_c/users/ninjatrader/Documents/NinjaTrader 8"
if [ -z "$(ls -A "$NT_DOCS" 2>/dev/null)" ]; then
    echo "First run detected — seeding NinjaTrader 8 documents from defaults..."
    cp -a /home/ninjatrader/NinjaTrader8-default/. "$NT_DOCS/"
fi

# Copy host Config.xml if mounted
if [ -f /tmp/host_config.xml ]; then
    cp /tmp/host_config.xml "$NT_CONFIG"
    echo "Loaded Config.xml from host."
fi

# Match host MachineGuid if provided
if [ -n "$HOST_MACHINE_GUID" ]; then
    Xvfb :99 -screen 0 1280x1024x24 &
    sleep 1
    wine reg add "HKLM\\SOFTWARE\\Microsoft\\Cryptography" \
        /v MachineGuid /t REG_SZ /d "$HOST_MACHINE_GUID" /f 2>/dev/null
    wineserver --wait
    kill %1 2>/dev/null
    wait 2>/dev/null
fi

# Start Xvfb
rm -f /tmp/.X99-lock
Xvfb :99 -screen 0 1280x1024x24 &
# Wait for display to be ready
for i in $(seq 1 10); do
    if xdpyinfo -display :99 >/dev/null 2>&1; then break; fi
    sleep 1
done

# Start VNC server (no password by default, use VNC_PASSWORD env to set one)
if [ -n "$VNC_PASSWORD" ]; then
    x11vnc -display :99 -forever -shared -rfbport 5900 -noxdamage -passwd "$VNC_PASSWORD" &
else
    x11vnc -display :99 -forever -shared -rfbport 5900 -noxdamage -nopw &
fi
sleep 1

# Start noVNC (web-based VNC client)
websockify --web /usr/share/novnc 6080 localhost:5900 &
sleep 1

echo "============================================"
echo " NinjaTrader Merlot"
echo "============================================"
echo " noVNC:  http://localhost:6080/vnc.html"
echo " VNC:    localhost:5900"
echo "============================================"

# Start window manager and compositor (fixes chart rendering + window stacking)
openbox &
sleep 1
convert -size 1x1 xc:black /tmp/black.png
feh --bg-fill /tmp/black.png
xcompmgr &
sleep 1

# Launch NinjaTrader
wine "$NT_EXE" 2>/tmp/wine_err.log &

# Auto-login via WPF UI Automation if credentials are provided
if [ -n "$NT_USERNAME" ] && [ -n "$NT_PASSWORD" ]; then
    echo "Starting AutoLogin (waiting for login window)..."
    wine "$WINEPREFIX/drive_c/AutoLogin.exe" "$NT_USERNAME" "$NT_PASSWORD" 2>/dev/null &
else
    echo "NinjaTrader launched. Open browser to log in."
fi

# Keep container running
wait
