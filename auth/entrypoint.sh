#!/bin/bash
set -e

# Auth experimentation entrypoint
# This script extends the base NinjaTrader entrypoint with automated
# Tradovate API authentication and token caching.

SCREENSHOT_DIR="/home/ninjatrader/screenshots"
NT_EXE="$WINEPREFIX/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe"
NT_CONFIG="$WINEPREFIX/drive_c/users/ninjatrader/Documents/NinjaTrader 8/Config.xml"
NT_ENCRYPT="$WINEPREFIX/drive_c/NTEncrypt.exe"

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

# --- Automated login via Tradovate API ---
if [ -n "$NT_USERNAME" ] && [ -n "$NT_PASSWORD" ]; then
    echo "Authenticating via Tradovate API..."

    # Get MachineId2 from Config.xml (used as deviceId)
    DEVICE_ID=$(grep -oP '<MachineId2>\K[^<]+' "$NT_CONFIG" 2>/dev/null || echo "")
    if [ -z "$DEVICE_ID" ]; then
        # Generate a stable MachineId2 and write it to Config.xml
        DEVICE_ID=$(cat /proc/sys/kernel/random/uuid | tr -d '-')
        sed -i "s|</NinjaTrader>|  <MachineId2>${DEVICE_ID}</MachineId2>\n</NinjaTrader>|" "$NT_CONFIG"
        echo "Generated MachineId2: ${DEVICE_ID}"
    fi

    # Call Tradovate API
    AUTH_RESPONSE=$(curl -s -X POST "https://live.tradovateapi.com/v1/auth/accesstokenrequest" \
        -H "Content-Type: application/json" \
        -d "{
            \"appId\": \"NinjaTrader8\",
            \"appVersion\": \"8.1.6.3\",
            \"cid\": \"543\",
            \"deviceId\": \"${DEVICE_ID}\",
            \"name\": \"${NT_USERNAME}\",
            \"password\": \"${NT_PASSWORD}\",
            \"sec\": \"ee3eb579-765f-4456-8bbe-5e393241ad20\"
        }")

    # Check for p-ticket (rate limit)
    P_TICKET=$(echo "$AUTH_RESPONSE" | jq -r '.["p-ticket"] // empty')
    if [ -n "$P_TICKET" ]; then
        P_TIME=$(echo "$AUTH_RESPONSE" | jq -r '.["p-time"] // 5')
        P_CAPTCHA=$(echo "$AUTH_RESPONSE" | jq -r '.["p-captcha"] // false')
        if [ "$P_CAPTCHA" = "true" ]; then
            echo "WARNING: Captcha required. Cannot auto-login. Use noVNC to log in manually."
            AUTH_RESPONSE=""
        else
            echo "Rate limited. Waiting ${P_TIME}s..."
            sleep "$P_TIME"
            AUTH_RESPONSE=$(curl -s -X POST "https://live.tradovateapi.com/v1/auth/accesstokenrequest" \
                -H "Content-Type: application/json" \
                -d "{
                    \"appId\": \"NinjaTrader8\",
                    \"appVersion\": \"8.1.6.3\",
                    \"cid\": \"543\",
                    \"deviceId\": \"${DEVICE_ID}\",
                    \"name\": \"${NT_USERNAME}\",
                    \"password\": \"${NT_PASSWORD}\",
                    \"sec\": \"ee3eb579-765f-4456-8bbe-5e393241ad20\",
                    \"p-ticket\": \"${P_TICKET}\"
                }")
        fi
    fi

    # Parse response
    ACCESS_TOKEN=$(echo "$AUTH_RESPONSE" | jq -r '.accessToken // empty')
    MD_ACCESS_TOKEN=$(echo "$AUTH_RESPONSE" | jq -r '.mdAccessToken // empty')
    EXPIRATION=$(echo "$AUTH_RESPONSE" | jq -r '.expirationTime // empty')
    USER_ID=$(echo "$AUTH_RESPONSE" | jq -r '.userId // empty')
    USER_STATUS=$(echo "$AUTH_RESPONSE" | jq -r '.userStatus // empty')
    ORG_NAME=$(echo "$AUTH_RESPONSE" | jq -r '.orgName // empty')
    ERROR_TEXT=$(echo "$AUTH_RESPONSE" | jq -r '.errorText // empty')

    if [ -n "$ACCESS_TOKEN" ] && [ "$USER_STATUS" = "Active" ]; then
        echo "Login successful. userId=${USER_ID} org=${ORG_NAME}"

        # Convert expiration from ISO 8601 to MM/DD/YYYY HH:MM:SS
        EXPIRATION_FORMATTED=$(date -d "$EXPIRATION" "+%m/%d/%Y %H:%M:%S" 2>/dev/null || echo "$EXPIRATION")

        # Encrypt tokens using NinjaTrader's own encryption
        ENC_USERNAME=$(wine "$NT_ENCRYPT" encrypt "$NT_USERNAME" 2>/dev/null)
        ENC_ACCESS_TOKEN=$(wine "$NT_ENCRYPT" encrypt "$ACCESS_TOKEN" 2>/dev/null)
        ENC_MD_ACCESS_TOKEN=$(wine "$NT_ENCRYPT" encrypt "$MD_ACCESS_TOKEN" 2>/dev/null)

        if [ -n "$ENC_ACCESS_TOKEN" ]; then
            # Write tokens to Config.xml
            # Remove existing cached tokens and username if present
            sed -i '/<CachedConnectionTokens>/,/<\/CachedConnectionTokens>/d' "$NT_CONFIG"
            sed -i '/<Username>[^<]*<\/Username>/d' "$NT_CONFIG"

            # Insert before closing </NinjaTrader> tag
            sed -i "s|</NinjaTrader>|  <CachedConnectionTokens>\n    <${NT_USERNAME}>\n      <CachedUserName>${ENC_USERNAME}</CachedUserName>\n      <CachedAccessToken>${ENC_ACCESS_TOKEN}</CachedAccessToken>\n      <CachedMarketDataAccessToken>${ENC_MD_ACCESS_TOKEN}</CachedMarketDataAccessToken>\n      <CachedTokenExpiration>${EXPIRATION_FORMATTED}</CachedTokenExpiration>\n      <CachedUserId>${USER_ID}</CachedUserId>\n      <CachedUserOrg>${ORG_NAME}</CachedUserOrg>\n    </${NT_USERNAME}>\n  </CachedConnectionTokens>\n  <Username>${NT_USERNAME}</Username>\n</NinjaTrader>|" "$NT_CONFIG"

            echo "Cached tokens written to Config.xml."
        else
            echo "WARNING: Token encryption failed. Use noVNC to log in manually."
        fi
    else
        echo "WARNING: Login failed. ${ERROR_TEXT:-$AUTH_RESPONSE}"
        echo "Use noVNC to log in manually."
    fi
fi

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
echo " NinjaTrader Merlot (Auth)"
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

# Auto-login via UI Automation if credentials are provided
if [ -n "$NT_USERNAME" ] && [ -n "$NT_PASSWORD" ]; then
    echo "Starting AutoLogin (waiting for login window)..."
    wine "$WINEPREFIX/drive_c/AutoLogin.exe" "$NT_USERNAME" "$NT_PASSWORD" 2>/dev/null &
fi

echo "NinjaTrader launched."

# Keep container running
wait
