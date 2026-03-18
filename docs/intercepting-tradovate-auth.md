# Intercepting NinjaTrader's Tradovate Auth Traffic

NinjaTrader authenticates against the [Tradovate API](https://api.tradovate.com). The auth logic inside `NinjaTrader.Core.dll` is obfuscated with AgileDotNet, so the `cid`, `sec`, and API endpoints can't be read from the binary directly. This document describes how to intercept the plaintext HTTPS traffic to extract those values.

## Prerequisites

- A running `ninjatrader-merlot` container (`docker compose up -d`)
- Valid NinjaTrader login credentials

## Step 1: Install capture tools in the container

```bash
docker exec -u root ninjatrader-merlot bash -c \
  'apt-get update -o Acquire::AllowInsecureRepositories=true && \
   apt-get install -y --allow-unauthenticated tcpdump tshark'
```

## Step 2: Restart NinjaTrader with TLS key logging

Kill the running NinjaTrader process and relaunch it with the `SSLKEYLOGFILE` environment variable. Wine's GnuTLS respects this variable and dumps TLS session keys to the specified file.

```bash
docker exec ninjatrader-merlot bash -c '
  DISPLAY=:99 wine taskkill /F /IM NinjaTrader.exe 2>/dev/null
  sleep 3
  rm -f /home/ninjatrader/sslkeys.log
  SSLKEYLOGFILE=/home/ninjatrader/sslkeys.log \
    DISPLAY=:99 wine "/home/ninjatrader/.wine/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe" 2>/dev/null &
  echo "NinjaTrader relaunched with TLS key logging"'
```

## Step 3: Start packet capture

```bash
docker exec -u root -d ninjatrader-merlot \
  tcpdump -i any -n port 443 -w /home/ninjatrader/capture.pcap
```

## Step 4: Log in

Open http://localhost:6080/vnc.html and log into NinjaTrader with your credentials.

## Step 5: Stop capture and decrypt

```bash
# Stop tcpdump
docker exec -u root ninjatrader-merlot kill $(docker exec -u root ninjatrader-merlot pidof tcpdump)

# Decrypt and display all HTTP requests/responses
docker exec ninjatrader-merlot tshark \
  -r /home/ninjatrader/capture.pcap \
  -o tls.keylog_file:/home/ninjatrader/sslkeys.log \
  -Y "http || http2" \
  -T fields \
  -e http.request.method \
  -e http.request.full_uri \
  -e http.content_type \
  -e http.file_data
```

## Results

The first request will be the auth call:

```
POST  https://live.tradovateapi.com/v1/auth/accesstokenrequest
```

With a JSON body containing:

| Field        | Description                                      |
|-------------|--------------------------------------------------|
| `appId`     | `NinjaTrader8`                                    |
| `appVersion`| `8.1.6.3`                                         |
| `cid`       | `543` (NinjaTrader's client ID)                   |
| `sec`       | `ee3eb579-765f-4456-8bbe-5e393241ad20` (NinjaTrader's API secret) |
| `deviceId`  | Value of `MachineId2` from Config.xml             |
| `name`      | Your NinjaTrader username                         |
| `password`  | Your NinjaTrader password                         |

The response contains `accessToken`, `mdAccessToken`, `expirationTime`, `userId`, and other account details.

## Why this works

.NET Framework 4.8 running under Wine uses Wine's native GnuTLS for TLS. GnuTLS respects the `SSLKEYLOGFILE` environment variable, writing TLS session keys that can be used to decrypt captured traffic.

## What doesn't work

- **MITM proxy**: NinjaTrader's `.exe.config` disables system proxy (`usesystemdefault="False"`), and modifying it crashes the app.
- **WINEDEBUG tracing**: `+winhttp`, `+ws2_32`, `+schannel` — .NET's `HttpClient` bypasses Wine's winhttp/schannel and none of these show request bodies.
- **System.Net tracing in .exe.config**: Crashes the CLR under Wine.
- **LD_PRELOAD on SSL_write/gnutls_record_send**: .NET's managed `SslStream` doesn't call these shared library functions directly.
- **tcpdump from the host**: Docker networking doesn't route container packets through host-visible interfaces. Must capture **inside** the container.
