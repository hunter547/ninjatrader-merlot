FROM debian:bookworm-slim

# Enable 32-bit architecture (required by Wine)
RUN dpkg --add-architecture i386

# Install Wine and minimal dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    wine \
    wine32 \
    wine64 \
    xvfb \
    xauth \
    cabextract \
    wget \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Download wine-mono 7.4.0 (required by Wine 8.0 for .NET support)
RUN mkdir -p /usr/share/wine/mono \
    && wget -O /usr/share/wine/mono/wine-mono-7.4.0-x86.msi \
    https://dl.winehq.org/wine/wine-mono/7.4.0/wine-mono-7.4.0-x86.msi

# Create a non-root user to run Wine
RUN useradd -m -s /bin/bash ninjatrader
USER ninjatrader
WORKDIR /home/ninjatrader

ENV WINEDEBUG=-all
ENV DISPLAY=:99
ENV WINEPREFIX=/home/ninjatrader/.wine

# Initialize Wine prefix
RUN xvfb-run wineboot --init && wineserver --wait

# Fake .NET 4.8 registry entry so NinjaTrader's launch condition is satisfied
RUN xvfb-run wine reg add \
    "HKLM\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full" \
    /v Release /t REG_DWORD /d 528040 /f && wineserver --wait

# Copy the installer
COPY --chown=ninjatrader:ninjatrader NinjaTrader.Install.msi .

# Install NinjaTrader - the installer hangs on WebView2 download (not needed),
# so we wait for NinjaTrader.exe to appear then kill the installer
RUN xvfb-run bash -c ' \
    msiexec /i NinjaTrader.Install.msi /qn /norestart & \
    for i in $(seq 1 120); do \
        if [ -f "$WINEPREFIX/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe" ]; then \
            sleep 2 && wineserver --kill && break; \
        fi; \
        sleep 1; \
    done'

# Verify installation
RUN test -f "$WINEPREFIX/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe"

# Clean up installer
RUN rm -f NinjaTrader.Install.msi

CMD ["xvfb-run", "wine", "/home/ninjatrader/.wine/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe"]
