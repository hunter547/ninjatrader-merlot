FROM debian:bookworm-slim

# Enable 32-bit architecture (required by Wine)
RUN dpkg --add-architecture i386

# Install WineHQ development branch (newer Wine = better .NET/WPF support)
RUN apt-get update && apt-get install -y --no-install-recommends \
    gnupg2 \
    software-properties-common \
    wget \
    ca-certificates \
    xvfb \
    xauth \
    cabextract \
    imagemagick \
    p7zip-full \
    x11vnc \
    novnc \
    websockify \
    xcompmgr \
    openbox \
    x11-xserver-utils \
    feh

# Install wine
RUN wget -nc -O /etc/apt/keyrings/winehq-archive.key https://dl.winehq.org/wine-builds/winehq.key \
    && echo "deb [signed-by=/etc/apt/keyrings/winehq-archive.key] https://dl.winehq.org/wine-builds/debian/ bookworm main" \
       > /etc/apt/sources.list.d/winehq.list \
    && apt-get update \
    && apt-get install -y --install-recommends winehq-devel \
    && rm -rf /var/lib/apt/lists/*

# Install latest winetricks
RUN wget -O /usr/local/bin/winetricks https://raw.githubusercontent.com/Winetricks/winetricks/master/src/winetricks \
    && chmod +x /usr/local/bin/winetricks

# Create a non-root user to run Wine
RUN useradd -m -s /bin/bash ninjatrader
USER ninjatrader
WORKDIR /home/ninjatrader

ENV DISPLAY=:99
ENV WINEPREFIX=/home/ninjatrader/.wine
ENV WINEDLLOVERRIDES="mscoree=;mshtml="

# Initialize Wine prefix (64-bit) — skip mono/gecko install prompts
RUN xvfb-run bash -c 'wineboot --init 2>&1; wineserver --wait'

# Re-enable mscoree for .NET installation
ENV WINEDLLOVERRIDES=""

# Install .NET Framework 4.8 and core Microsoft fonts (Arial required by WPF PasswordBox)
# This takes a while — it downloads and installs .NET components sequentially
RUN rm -f /tmp/.X99-lock && xvfb-run winetricks -q dotnet48 corefonts && wineserver --wait

# Replace Wine's stub websocket.dll with native Windows version
# (Wine doesn't implement WebSocket Protocol Component API, which .NET Framework requires)
COPY DLLs/websocket64.dll $WINEPREFIX/drive_c/windows/system32/websocket.dll
COPY DLLs/websocket32.dll $WINEPREFIX/drive_c/windows/syswow64/websocket.dll
RUN rm -f /tmp/.X99-lock && xvfb-run bash -c '\
    wine reg add "HKCU\\Software\\Wine\\DllOverrides" /v websocket /t REG_SZ /d "native,builtin" /f 2>/dev/null; \
    wineserver --wait'

# Copy NinjaTrader
COPY --chown=ninjatrader:ninjatrader NinjaTrader.Install.msi .

# Install NinjaTrader - the installer hangs on WebView2 download (not needed),
# so we wait for NinjaTrader.exe to appear then kill the installer
RUN rm -f /tmp/.X99-lock && xvfb-run bash -c ' \
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

# Create default workspace files so NinjaTrader has a valid workspace on first launch
RUN echo '<?xml version="1.0" encoding="utf-8"?>\n<NinjaTrader>\n  <ActiveWorkspace>Default</ActiveWorkspace>\n  <OpenWorkspaces>Default<OpenWorkspace>Default</OpenWorkspace></OpenWorkspaces>\n</NinjaTrader>' \
    > "$WINEPREFIX/drive_c/users/ninjatrader/Documents/NinjaTrader 8/workspaces/_Workspaces.xml" \
    && echo '<?xml version="1.0" encoding="utf-8"?>\n<NinjaTrader />' \
    > "$WINEPREFIX/drive_c/users/ninjatrader/Documents/NinjaTrader 8/workspaces/Default.xml"

# Preserve default NinjaTrader documents so they can seed an empty bind mount
RUN cp -a "$WINEPREFIX/drive_c/users/ninjatrader/Documents/NinjaTrader 8" /home/ninjatrader/NinjaTrader8-default

# Compile AutoLogin helper for automated UI login via WPF UI Automation
COPY --chown=ninjatrader:ninjatrader AutoLogin.cs .
RUN rm -f /tmp/.X99-lock && xvfb-run bash -c '\
    wine "C:\\windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe" \
        /out:"C:\\AutoLogin.exe" \
        /reference:"C:\\windows\\Microsoft.NET\\Framework64\\v4.0.30319\\WPF\\UIAutomationClient.dll" \
        /reference:"C:\\windows\\Microsoft.NET\\Framework64\\v4.0.30319\\WPF\\UIAutomationTypes.dll" \
        "Z:\\home\\ninjatrader\\AutoLogin.cs" 2>/dev/null; \
    wineserver --wait'
RUN test -f "$WINEPREFIX/drive_c/AutoLogin.exe"

# Copy entrypoint script
COPY --chown=ninjatrader:ninjatrader entrypoint.sh .

# Screenshots are saved here — mount a volume to access them on the host
VOLUME /home/ninjatrader/screenshots

# noVNC web interface on 6080, VNC on 5900, NinjaTraderBridge REST/SSE/WS API on 5000
EXPOSE 6080 5900 5000

ENTRYPOINT ["/bin/bash", "/home/ninjatrader/entrypoint.sh"]
