#!/bin/bash
set -e

SCREENSHOT_DIR="/home/ninjatrader/screenshots"
NT_EXE="$WINEPREFIX/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe"
NT_CONFIG="$WINEPREFIX/drive_c/users/ninjatrader/Documents/NinjaTrader 8/Config.xml"

mkdir -p "$SCREENSHOT_DIR"

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
Xvfb :99 -screen 0 1280x1024x24 &
sleep 1

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

# Start compositor (fixes chart rendering)
convert -size 1x1 xc:black /tmp/black.png
feh --bg-fill /tmp/black.png
xcompmgr &
sleep 1

# Launch NinjaTrader
wine "$NT_EXE" 2>/tmp/wine_err.log &

echo "NinjaTrader launched. Open browser to log in."

# Keep container running
wait
