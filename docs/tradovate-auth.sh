#!/bin/bash
# Tradovate Auth Flow — as used by NinjaTrader 8
#
# NinjaTrader authenticates against the Tradovate REST API using these
# embedded credentials. The response tokens are AES-encrypted using
# NinjaTrader.Core.Globals.Encrypt() before being written to Config.xml.
#
# These values were extracted by intercepting NinjaTrader's TLS traffic
# (see docs/intercepting-tradovate-auth.md) and by loading NinjaTrader.Core.dll
# via .NET reflection to read the obfuscated static fields and call
# Encrypt/Decrypt directly.

# --- NinjaTrader's embedded credentials (constant) ---
APP_ID="NinjaTrader8"
APP_VERSION="8.1.6.3"
CID="543"
SEC="ee3eb579-765f-4456-8bbe-5e393241ad20"

# --- User-provided values ---
NT_USERNAME="${NT_USERNAME:?Set NT_USERNAME}"
NT_PASSWORD="${NT_PASSWORD:?Set NT_PASSWORD}"
DEVICE_ID="${DEVICE_ID:?Set DEVICE_ID (MachineId2 from Config.xml)}"

# =============================================================================
# 1. Request access token
# =============================================================================
curl -s -X POST "https://live.tradovateapi.com/v1/auth/accesstokenrequest" \
  -H "Content-Type: application/json" \
  -d "{
    \"appId\": \"${APP_ID}\",
    \"appVersion\": \"${APP_VERSION}\",
    \"cid\": \"${CID}\",
    \"deviceId\": \"${DEVICE_ID}\",
    \"name\": \"${NT_USERNAME}\",
    \"password\": \"${NT_PASSWORD}\",
    \"sec\": \"${SEC}\"
  }"

# Response:
# {
#   "accessToken": "<JWT>",
#   "mdAccessToken": "<JWT>",
#   "expirationTime": "2026-03-18T04:21:55.059Z",
#   "userStatus": "Active",
#   "userId": 4766631,
#   "name": "MFFUKFVLXhtECC",
#   "hasLive": false,
#   "orgName": "MyFundedFutures"
# }

# =============================================================================
# 2. Renew access token (before expiration, ~90 min lifespan)
# =============================================================================
# ACCESS_TOKEN="<from step 1>"
# curl -s "https://live.tradovateapi.com/v1/auth/renewaccesstoken" \
#   -H "Authorization: Bearer ${ACCESS_TOKEN}"

# =============================================================================
# 3. Handle p-ticket (rate limit penalty)
# =============================================================================
# If step 1 returns {"p-ticket": "...", "p-time": 5, "p-captcha": false}:
#   - Wait p-time seconds
#   - Retry step 1 with the same body + "p-ticket": "<value>"
#   - If p-captcha is true, cannot automate — must wait ~1 hour

# =============================================================================
# 4. Encrypt tokens using NinjaTrader's own Globals.Encrypt()
# =============================================================================
# The encryption key is the same as SEC: ee3eb579-765f-4456-8bbe-5e393241ad20
# NinjaTrader uses AesCryptoServiceProvider internally. Rather than reimplementing
# the encryption, we call Globals.Encrypt() directly via a compiled .NET helper.
#
# Compile the helper (one-time, inside the container):
#
#   wine "C:\windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" \
#     /out:C:\NTEncrypt.exe \
#     /reference:"C:\Program Files\NinjaTrader 8\bin\NinjaTrader.Core.dll" \
#     Z:\home\ninjatrader\NTEncrypt.cs
#
# NTEncrypt.cs:
#
#   using System;
#   using System.Reflection;
#   class NTEncrypt
#   {
#       static void Main(string[] args)
#       {
#           // args[0] = "encrypt" or "decrypt"
#           // args[1] = text to encrypt/decrypt
#           var asm = Assembly.LoadFrom(@"C:\Program Files\NinjaTrader 8\bin\NinjaTrader.Core.dll");
#           var globalsType = asm.GetType("NinjaTrader.Core.Globals");
#           var authType = asm.GetType("NinjaTrader.Core.Authentication");
#           var keyField = authType.GetField("key", BindingFlags.Static | BindingFlags.NonPublic);
#           string key = (string)keyField.GetValue(null);
#           var method = globalsType.GetMethod(
#               args[0] == "encrypt" ? "Encrypt" : "Decrypt",
#               BindingFlags.Static | BindingFlags.Public);
#           string result = (string)method.Invoke(null, new object[] { args[1], key });
#           Console.Write(result);
#       }
#   }
#
# Usage:
#   wine C:\NTEncrypt.exe encrypt "MFFUKFVLXhtECC"
#   wine C:\NTEncrypt.exe decrypt "NfcNhHrU4SdfF+WyH2wKhIIvUkShMihnnw83cWGUV+0="

# =============================================================================
# 5. Config.xml — CachedConnectionTokens
# =============================================================================
# After encrypting the tokens, write them to Config.xml in this format:
#
# <CachedConnectionTokens>
#   <USERNAME_HERE>
#     <CachedUserName>ENCRYPTED_USERNAME</CachedUserName>
#     <CachedAccessToken>ENCRYPTED_ACCESS_TOKEN</CachedAccessToken>
#     <CachedMarketDataAccessToken>ENCRYPTED_MD_ACCESS_TOKEN</CachedMarketDataAccessToken>
#     <CachedTokenExpiration>MM/DD/YYYY HH:MM:SS</CachedTokenExpiration>
#     <CachedUserId>USER_ID</CachedUserId>
#     <CachedUserOrg>ORG_NAME</CachedUserOrg>
#   </USERNAME_HERE>
# </CachedConnectionTokens>
# <Username>USERNAME_HERE</Username>

# =============================================================================
# 6. Config.xml — ConnectOptions
# =============================================================================
# These are the default connection entries NinjaTrader expects:
#
# <ConnectOptions>
#   <SimulatorOptions>
#     <CanManageOrders>true</CanManageOrders>
#     <ConnectOnStartup>false</ConnectOnStartup>
#     <DisableL2Data>false</DisableL2Data>
#     <IsHdsEnabled>false</IsHdsEnabled>
#     <IsPasswordRequiredOnStartup>false</IsPasswordRequiredOnStartup>
#     <RunAsProcess>false</RunAsProcess>
#     <TypeName>NinjaTrader.Cbi.SimulatorOptions</TypeName>
#     <Mode>Live</Mode>
#     <Name>Simulated Data Feed</Name>
#     <Password />
#     <Provider>Simulator</Provider>
#     <User />
#     <TicksPerSecond>2</TicksPerSecond>
#     <TimerMilliseconds>500</TimerMilliseconds>
#   </SimulatorOptions>
#   <KinetickOptions>
#     <CanManageOrders>true</CanManageOrders>
#     <ConnectOnStartup>false</ConnectOnStartup>
#     <DisableL2Data>false</DisableL2Data>
#     <IsHdsEnabled>false</IsHdsEnabled>
#     <IsPasswordRequiredOnStartup>false</IsPasswordRequiredOnStartup>
#     <RunAsProcess>false</RunAsProcess>
#     <TypeName>NinjaTrader.Cbi.KinetickOptions</TypeName>
#     <Mode>Live</Mode>
#     <Name>Kinetick – End Of Day (Free)</Name>
#     <Password />
#     <Provider>Provider7</Provider>
#     <User />
#     <GlobexNonProFees>false</GlobexNonProFees>
#     <BackadjustContinuousContracts>false</BackadjustContinuousContracts>
#     <Free>true</Free>
#     <NewsDaysBack>1</NewsDaysBack>
#     <UseFundamentalData>true</UseFundamentalData>
#   </KinetickOptions>
#   <PlaybackOptions>
#     <CanManageOrders>true</CanManageOrders>
#     <ConnectOnStartup>false</ConnectOnStartup>
#     <DisableL2Data>false</DisableL2Data>
#     <IsHdsEnabled>false</IsHdsEnabled>
#     <IsPasswordRequiredOnStartup>false</IsPasswordRequiredOnStartup>
#     <RunAsProcess>false</RunAsProcess>
#     <TypeName>NinjaTrader.Cbi.PlaybackOptions</TypeName>
#     <Mode>Live</Mode>
#     <Name>Playback Connection</Name>
#     <Password />
#     <Provider>Playback</Provider>
#     <User />
#   </PlaybackOptions>
# </ConnectOptions>
