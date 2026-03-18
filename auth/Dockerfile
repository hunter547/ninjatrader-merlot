# Auth experimentation Dockerfile
# Builds on top of the ninjatrader-merlot image to add auth automation tooling.
#
# Build (from repo root):
#   docker build -t ninjatrader-merlot .
#   docker build -f auth/Dockerfile -t ninjatrader-auth .
#
# Run:
#   docker run --rm \
#     -e NT_USERNAME=... -e NT_PASSWORD=... \
#     -p 6080:6080 -p 5900:5900 \
#     -v ./ninjatrader-data:/home/ninjatrader/.wine/drive_c/users/ninjatrader/Documents/NinjaTrader\ 8 \
#     ninjatrader-auth

FROM ninjatrader-merlot

USER root
RUN apt-get update -o Acquire::AllowInsecureRepositories=true \
    && apt-get install -y --allow-unauthenticated --no-install-recommends curl jq \
    && rm -rf /var/lib/apt/lists/*
USER ninjatrader

# Compile NTEncrypt helper for token encryption via NinjaTrader.Core.dll
COPY --chown=ninjatrader:ninjatrader NTEncrypt.cs /home/ninjatrader/
RUN rm -f /tmp/.X99-lock && xvfb-run bash -c '\
    wine "C:\\windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe" \
        /out:"C:\\NTEncrypt.exe" \
        /reference:"C:\\Program Files\\NinjaTrader 8\\bin\\NinjaTrader.Core.dll" \
        "Z:\\home\\ninjatrader\\NTEncrypt.cs" 2>/dev/null; \
    wineserver --wait'
RUN test -f "$WINEPREFIX/drive_c/NTEncrypt.exe"

# Compile AutoLogin helper for UI Automation-based login
COPY --chown=ninjatrader:ninjatrader AutoLogin.cs /home/ninjatrader/
RUN rm -f /tmp/.X99-lock && xvfb-run bash -c '\
    wine "C:\\windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe" \
        /out:"C:\\AutoLogin.exe" \
        /reference:"C:\\windows\\Microsoft.NET\\Framework64\\v4.0.30319\\WPF\\UIAutomationClient.dll" \
        /reference:"C:\\windows\\Microsoft.NET\\Framework64\\v4.0.30319\\WPF\\UIAutomationTypes.dll" \
        "Z:\\home\\ninjatrader\\AutoLogin.cs" 2>/dev/null; \
    wineserver --wait'
RUN test -f "$WINEPREFIX/drive_c/AutoLogin.exe"

# Copy auth entrypoint
COPY --chown=ninjatrader:ninjatrader auth/entrypoint.sh /home/ninjatrader/entrypoint-auth.sh

ENTRYPOINT ["/bin/bash", "/home/ninjatrader/entrypoint-auth.sh"]
