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

    # Extract cid/sec dynamically from NinjaTrader's own auth request
    # by intercepting TLS traffic during a UI-driven login
    # Disable set -e for this block — many commands may return non-zero
    set +e
    echo "Extracting auth credentials from NinjaTrader..."
    SSLKEY_FILE="/tmp/sslkeys.log"
    PCAP_FILE="/tmp/auth_capture.pcap"

    # Launch NinjaTrader with SSLKEYLOGFILE to dump TLS session keys
    SSLKEYLOGFILE="$SSLKEY_FILE" wine "$NT_EXE" 2>/tmp/wine_err.log &
    NT_PID=$!

    # Start packet capture (runs as current user, needs cap_net_raw or tcpdump suid)
    tcpdump -i any -n port 443 -w "$PCAP_FILE" 2>/dev/null &
    TCPDUMP_PID=$!

    # Use AutoLogin to fill in credentials and click Login
    wine "$WINEPREFIX/drive_c/AutoLogin.exe" "$NT_USERNAME" "$NT_PASSWORD" 2>/dev/null &
    AUTOLOGIN_PID=$!

    # Wait for AutoLogin to finish (it exits after clicking Login + Launch)
    wait $AUTOLOGIN_PID 2>/dev/null

    # Give NinjaTrader a moment to complete the auth request
    sleep 5

    # Stop packet capture
    kill $TCPDUMP_PID 2>/dev/null || true
    wait $TCPDUMP_PID 2>/dev/null || true

    # Stop NinjaTrader — we'll relaunch it later with cached tokens
    wine taskkill /F /IM NinjaTrader.exe 2>/dev/null || true
    wineserver --wait || true
    wait $NT_PID 2>/dev/null || true

    # Decrypt captured traffic and extract the auth request
    AUTH_REQUEST_BODY=""
    AUTH_HTTP_JSON=""
    if [ -f "$PCAP_FILE" ] && [ -f "$SSLKEY_FILE" ]; then
        AUTH_FULL_JSON=$(tshark -r "$PCAP_FILE" \
            -o "tls.keylog_file:$SSLKEY_FILE" \
            -Y "http.request.uri contains \"accesstokenrequest\"" \
            -T json 2>/dev/null)

        AUTH_REQUEST_BODY=$(echo "$AUTH_FULL_JSON" \
            | jq -r '.[0]._source.layers.json["json.object"] // empty' 2>/dev/null)
        AUTH_HTTP_JSON=$(echo "$AUTH_FULL_JSON" \
            | jq '.[0]._source.layers.http // empty' 2>/dev/null)
    fi

    if [ -n "$AUTH_REQUEST_BODY" ]; then
        # Parse all fields from the captured request
        NT_CID=$(echo "$AUTH_REQUEST_BODY" | jq -r '.cid // empty' 2>/dev/null)
        NT_SEC=$(echo "$AUTH_REQUEST_BODY" | jq -r '.sec // empty' 2>/dev/null)
        NT_APP_ID=$(echo "$AUTH_REQUEST_BODY" | jq -r '.appId // empty' 2>/dev/null)
        NT_APP_VERSION=$(echo "$AUTH_REQUEST_BODY" | jq -r '.appVersion // empty' 2>/dev/null)
        DEVICE_ID=$(echo "$AUTH_REQUEST_BODY" | jq -r '.deviceId // empty' 2>/dev/null)
        echo "Extracted auth credentials: cid=${NT_CID} appId=${NT_APP_ID} appVersion=${NT_APP_VERSION} deviceId=${DEVICE_ID}"

        # Write the full curl command to a script file (mounted volume for host access)
        CURL_SCRIPT="/home/ninjatrader/auth/tradovate_auth_curl.sh"
        mkdir -p /home/ninjatrader/auth
        REQUEST_URI=$(echo "$AUTH_HTTP_JSON" | jq -r '.["http.request.full_uri"] // "https://live.tradovateapi.com/v1/auth/accesstokenrequest"' 2>/dev/null)
        CONTENT_TYPE=$(echo "$AUTH_HTTP_JSON" | jq -r '.["http.content_type"] // "application/json; charset=utf-8"' 2>/dev/null)

        cat > "$CURL_SCRIPT" << 'CURLEOF'
#!/bin/bash
# NinjaTrader Tradovate auth request — captured via TLS interception
#
# This is the exact request NinjaTrader sends to authenticate.
# Set NT_USERNAME and NT_PASSWORD env vars before running.

curl -s -X POST '__REQUEST_URI__' \
  -H 'Content-Type: __CONTENT_TYPE__' \
  -H 'Expect: 100-continue' \
  -A '' \
  -d "{
    \"appId\": \"__NT_APP_ID__\",
    \"appVersion\": \"__NT_APP_VERSION__\",
    \"cid\": \"__NT_CID__\",
    \"deviceId\": \"__DEVICE_ID__\",
    \"name\": \"${NT_USERNAME}\",
    \"password\": \"${NT_PASSWORD}\",
    \"sec\": \"__NT_SEC__\"
  }"
CURLEOF
        # Replace placeholders with captured values
        sed -i "s|__REQUEST_URI__|${REQUEST_URI}|g" "$CURL_SCRIPT"
        sed -i "s|__CONTENT_TYPE__|${CONTENT_TYPE}|g" "$CURL_SCRIPT"
        sed -i "s|__NT_APP_ID__|${NT_APP_ID}|g" "$CURL_SCRIPT"
        sed -i "s|__NT_APP_VERSION__|${NT_APP_VERSION}|g" "$CURL_SCRIPT"
        sed -i "s|__NT_CID__|${NT_CID}|g" "$CURL_SCRIPT"
        sed -i "s|__DEVICE_ID__|${DEVICE_ID}|g" "$CURL_SCRIPT"
        sed -i "s|__NT_SEC__|${NT_SEC}|g" "$CURL_SCRIPT"
        chmod +x "$CURL_SCRIPT"
        echo "Curl command written to $CURL_SCRIPT (mount /home/ninjatrader/auth to access on host)"
    fi

    # Fall back to empty check — if any value missing, extraction failed
    if [ -z "$NT_CID" ] || [ -z "$NT_SEC" ]; then
        echo "WARNING: Could not extract auth credentials from traffic capture."
        echo "Using known fallback values. These may be outdated if NinjaTrader was updated."
        NT_CID="${NT_CID:-543}"
        NT_SEC="${NT_SEC:-ee3eb579-765f-4456-8bbe-5e393241ad20}"
        NT_APP_ID="${NT_APP_ID:-NinjaTrader8}"
        NT_APP_VERSION="${NT_APP_VERSION:-8.1.6.3}"
    fi

    # Clean up capture files
    rm -f "$PCAP_FILE" "$SSLKEY_FILE"
    set -e

    # Call Tradovate API with extracted credentials
    AUTH_RESPONSE=$(curl -s -X POST "https://live.tradovateapi.com/v1/auth/accesstokenrequest" \
        -H "Content-Type: application/json; charset=utf-8" \
        -H "Expect: 100-continue" \
        -A "" \
        -d "{
            \"appId\": \"${NT_APP_ID}\",
            \"appVersion\": \"${NT_APP_VERSION}\",
            \"cid\": \"${NT_CID}\",
            \"deviceId\": \"${DEVICE_ID}\",
            \"name\": \"${NT_USERNAME}\",
            \"password\": \"${NT_PASSWORD}\",
            \"sec\": \"${NT_SEC}\"
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
                -H "Content-Type: application/json; charset=utf-8" \
        -H "Expect: 100-continue" \
        -A "" \
                -d "{
                    \"appId\": \"${NT_APP_ID}\",
                    \"appVersion\": \"${NT_APP_VERSION}\",
                    \"cid\": \"${NT_CID}\",
                    \"deviceId\": \"${DEVICE_ID}\",
                    \"name\": \"${NT_USERNAME}\",
                    \"password\": \"${NT_PASSWORD}\",
                    \"sec\": \"${NT_SEC}\",
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

echo "Auth extraction complete."
