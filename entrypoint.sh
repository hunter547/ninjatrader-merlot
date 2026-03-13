#!/bin/bash
set -e

SCREENSHOT_DIR="/home/ninjatrader/screenshots"
NT_EXE="$WINEPREFIX/drive_c/Program Files/NinjaTrader 8/bin/NinjaTrader.exe"

mkdir -p "$SCREENSHOT_DIR"

# Start Xvfb
Xvfb :99 -screen 0 1280x1024x24 &
XVFB_PID=$!
sleep 1

# Launch NinjaTrader
wine "$NT_EXE" &
NT_PID=$!

echo "NinjaTrader started (pid $NT_PID)"
echo ""
echo "Commands:"
echo "  screenshot  - take a screenshot (saved to $SCREENSHOT_DIR/)"
echo "  exit        - stop NinjaTrader and exit"
echo ""

screenshot() {
    local filename="screenshot_$(date +%Y%m%d_%H%M%S).png"
    import -window root "$SCREENSHOT_DIR/$filename"
    echo "Saved: $SCREENSHOT_DIR/$filename"
}

# Interactive loop
while true; do
    read -r -p "> " cmd
    case "$cmd" in
        screenshot)
            screenshot
            ;;
        exit|quit)
            echo "Shutting down..."
            wineserver --kill 2>/dev/null
            kill $XVFB_PID 2>/dev/null
            break
            ;;
        *)
            echo "Unknown command: $cmd (try 'screenshot' or 'exit')"
            ;;
    esac
done
