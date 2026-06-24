// ══════════════════════════════════════════════════════════════════════════════
//  PRECISION SNIPER [WillyAlgoTrader]  —  NinjaTrader 8 Port  v1.2.2
//  Original PineScript by Willy | WillyAlgoTrader
//  NT8 conversion: faithful logic port + SharpDX dashboard
//
//  NT8-specific notes vs. original PineScript:
//   • HTF Bias: simplified to current-TF EMA alignment.
//     For true higher-timeframe bias add a secondary data series
//     via AddDataSeries() in State.Configure and read BarsArray[1].
//   • EMA ribbon fill: NT8 cannot fill between two plots natively;
//     the two EMA lines are plotted and users can enable "Price Fill"
//     in the indicator's Plots tab manually if desired.
//   • TP/SL lines drawn as full-width HorizontalLines (NT8 cannot
//     draw partial future-extending lines from OnBarUpdate).
//   • Watermark: omitted (trivially added with a single Draw.Text call).
//   • Alert messages printed to NT8 Output window via Print().
//     Wire up NinjaTrader Alerts via the Alert tab to escalate further.
//
//  Installation:
//   1. Copy this file to  Documents\NinjaTrader 8\bin\Custom\Indicators\
//   2. In NinjaTrader: Tools → Edit NinjaScript → Indicators → Compile
//   3. Apply "PrecisionSniper" from the Indicators list
// ══════════════════════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class PrecisionSniper : Indicator
    {
        // ── Preset-resolved effective parameters ──────────────────────────
        private int    eEmaFast, eEmaSlow, eEmaTrend, eRsiLen, eAtrLen, eMinScore;
        private double eSLMult;
        private string resolvedPreset = "Default";

        // ── Sub-indicators ────────────────────────────────────────────────
        private EMA  _emaFastInd, _emaSlowInd, _emaTrendInd;
        private RSI  _rsiInd;
        private ATR  _atrInd;
        private MACD _macdInd;
        private ADX  _adxInd;
        private DM   _dmInd;
        private SMA  _volSmaInd, _atrSmaInd;

        // ── VWAP session accumulator ──────────────────────────────────────
        private double _vwapPV, _vwapV;

        // ── Trade state (equivalent of Pine's var declarations) ───────────
        private int    _lastDir;
        private double _entryPx, _slPx, _tp1Px, _tp2Px, _tp3Px, _trailPx, _tradeRisk;
        private int    _tradeDir, _entryBar;
        private bool   _tp1Hit, _tp2Hit, _tp3Hit, _slHit, _slHitPrev;
        private string _tradeStatus = "No Trade";
        private bool   _tradeActive;

        // ── Backtest tracker ──────────────────────────────────────────────
        private int    _btTotal, _btWins, _btLosses;
        private double _btTotalR, _btGrossWin, _btGrossLoss;
        private bool   _prevTradeOpen, _prevTP1, _prevTP2, _prevTP3, _prevSL;
        private int    _prevDir;

        // ── Per-bar computed values (read in OnRender for dashboard) ──────
        private double _bullScore, _bearScore;
        private double _adxVal, _diPlus, _diMinus;
        private double _rsiVal, _atrVal, _vwapVal;
        private double _emaFastVal, _emaSlowVal, _emaTrendVal;
        private double _macdLine, _macdSig, _macdHist;
        private bool   _volAboveAvg, _strongTrend;
        private string _volRegime  = "Normal";
        private int    _htfBias;
        private double _swingLow, _swingHigh;

        // ── SharpDX dashboard color constants ─────────────────────────────
        private static readonly SharpDX.Color4 Col_White   = new SharpDX.Color4(1.00f, 1.00f, 1.00f, 1f);
        private static readonly SharpDX.Color4 Col_Muted   = new SharpDX.Color4(0.62f, 0.62f, 0.62f, 1f);
        private static readonly SharpDX.Color4 Col_Green   = new SharpDX.Color4(0.00f, 0.90f, 0.46f, 1f);
        private static readonly SharpDX.Color4 Col_Red     = new SharpDX.Color4(1.00f, 0.32f, 0.32f, 1f);
        private static readonly SharpDX.Color4 Col_Yellow  = new SharpDX.Color4(1.00f, 0.92f, 0.23f, 1f);
        private static readonly SharpDX.Color4 Col_HdrBlue = new SharpDX.Color4(0.16f, 0.38f, 1.00f, 1f);
        private static readonly SharpDX.Color4 Col_HdrPurp = new SharpDX.Color4(0.38f, 0.00f, 0.92f, 1f);
        private static readonly SharpDX.Color4 Col_DashBG  = new SharpDX.Color4(0.075f, 0.09f, 0.133f, 0.93f);
        private static readonly SharpDX.Color4 Col_Border  = new SharpDX.Color4(0.165f, 0.18f, 0.224f, 1.00f);

        // ─────────────────────────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Precision Sniper [WillyAlgoTrader] — NT8 Port v1.2.2";
                Name         = "PrecisionSniper";
                Calculate    = Calculate.OnBarClose;
                IsOverlay    = true;
                DisplayInDataBox          = true;
                DrawOnPricePanel          = true;
                PaintPriceMarkers         = true;
                IsSuspendedWhileInactive  = true;

                // ── Main Settings ─────────────────────────────────────────
                // Valid: "Auto","Conservative","Default","Aggressive","Scalping","Swing","Crypto 24/7","Custom"
                Preset       = "Auto";

                // ── Entry Engine ──────────────────────────────────────────
                EmaFastLen   = 9;
                EmaSlowLen   = 21;
                EmaTrendLen  = 55;
                MinScore     = 5;
                RsiLen       = 13;
                // Valid: "All", "A+ and A", "A+ Only"
                GradeFilter  = "All";
                HideCGrade   = true;

                // ── Risk Management ───────────────────────────────────────
                AtrLen         = 14;
                SlMult         = 1.5;
                Tp1Mult        = 1.0;
                Tp2Mult        = 2.0;
                Tp3Mult        = 3.0;
                UseTrail       = true;
                UseStructureSL = true;
                SwingLookback  = 10;

                // ── Visual ────────────────────────────────────────────────
                ShowSignals  = true;
                ShowGrade    = true;
                ShowTPSL     = true;
                ShowRibbon   = true;
                ShowTrailVis = true;
                ShowDash     = true;
                ShowBtDash   = true;

                // ── Colors ────────────────────────────────────────────────
                BullBrush    = Brushes.LimeGreen;
                BearBrush    = Brushes.Red;
                NeutralBrush = Brushes.Yellow;

                // ── Plots: EMA Fast / Slow / Trend ────────────────────────
                AddPlot(new Stroke(Brushes.LimeGreen, 2), PlotStyle.Line, "EMA Fast");
                AddPlot(new Stroke(Brushes.Red,        2), PlotStyle.Line, "EMA Slow");
                AddPlot(new Stroke(Brushes.DimGray,    1), PlotStyle.Dot,  "EMA Trend");
            }
            else if (State == State.Configure)
            {
                // Resolve preset — BarsPeriod is available here
                if (Preset == "Auto")
                {
                    int tf = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute
                             ? BarsPeriod.Value : 1440;
                    resolvedPreset = tf <= 5   ? "Scalping"
                                   : tf <= 60  ? "Default"
                                   : tf <= 240 ? "Aggressive"
                                   : "Swing";
                }
                else
                {
                    resolvedPreset = Preset;
                }
                ApplyPreset(resolvedPreset);
            }
            else if (State == State.DataLoaded)
            {
                InitSubIndicators();
            }
        }

        // ── Apply preset parameter values ─────────────────────────────────
        private void ApplyPreset(string p)
        {
            switch (p)
            {
                case "Scalping":
                    eEmaFast=5;  eEmaSlow=13; eEmaTrend=34; eRsiLen=8;  eAtrLen=10; eMinScore=4; eSLMult=0.8; break;
                case "Aggressive":
                    eEmaFast=8;  eEmaSlow=18; eEmaTrend=50; eRsiLen=11; eAtrLen=12; eMinScore=3; eSLMult=1.2; break;
                case "Default":
                    eEmaFast=9;  eEmaSlow=21; eEmaTrend=55; eRsiLen=13; eAtrLen=14; eMinScore=5; eSLMult=1.5; break;
                case "Conservative":
                    eEmaFast=12; eEmaSlow=26; eEmaTrend=89; eRsiLen=14; eAtrLen=14; eMinScore=7; eSLMult=2.0; break;
                case "Swing":
                    eEmaFast=13; eEmaSlow=34; eEmaTrend=89; eRsiLen=21; eAtrLen=20; eMinScore=6; eSLMult=2.5; break;
                case "Crypto 24/7":
                    eEmaFast=9;  eEmaSlow=21; eEmaTrend=55; eRsiLen=14; eAtrLen=20; eMinScore=5; eSLMult=2.0; break;
                default: // "Custom" or fallback
                    eEmaFast=EmaFastLen; eEmaSlow=EmaSlowLen; eEmaTrend=EmaTrendLen;
                    eRsiLen=RsiLen; eAtrLen=AtrLen; eMinScore=MinScore; eSLMult=SlMult;
                    break;
            }
        }

        // ── Create sub-indicators with resolved lengths ───────────────────
        private void InitSubIndicators()
        {
            _emaFastInd  = EMA(Close, eEmaFast);
            _emaSlowInd  = EMA(Close, eEmaSlow);
            _emaTrendInd = EMA(Close, eEmaTrend);
            _rsiInd      = RSI(Close, eRsiLen, 1);
            _atrInd      = ATR(eAtrLen);
            _macdInd     = MACD(Close, 12, 26, 9);
            _adxInd      = ADX(14);
            _dmInd       = DM(14);     // DiPlus / DiMinus accessible via _dmInd.DiPlus[0] / _dmInd.DiMinus[0]
            _volSmaInd   = SMA(Volume, 20);
            _atrSmaInd   = SMA(_atrInd, 42);
        }

        // ─────────────────────────────────────────────────────────────────
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;

            // Require enough history for the longest EMA + buffer
            int warmup = Math.Max(eEmaTrend, 50) + 3;
            if (CurrentBar < warmup) return;

            // ── Core indicator values ─────────────────────────────────────
            _emaFastVal  = _emaFastInd[0];
            _emaSlowVal  = _emaSlowInd[0];
            _emaTrendVal = _emaTrendInd[0];
            _atrVal      = _atrInd[0];
            _rsiVal      = _rsiInd[0];

            _macdLine = _macdInd[0];        // MACD line (fast EMA - slow EMA)
            _macdSig  = _macdInd.Avg[0];    // Signal line
            _macdHist = _macdInd.Diff[0];   // Histogram

            double volSmaVal = CurrentBar >= 20 ? _volSmaInd[0] : (double)Volume[0];
            _volAboveAvg = Volume[0] > 0 ? (double)Volume[0] > volSmaVal * 1.2 : true;

            _adxVal      = _adxInd[0];
            _diPlus      = _dmInd.DiPlus[0];
            _diMinus     = _dmInd.DiMinus[0];
            _strongTrend = _adxVal > 20;

            // ── VWAP (session-reset) ──────────────────────────────────────
            if (Bars.IsFirstBarOfSession) { _vwapPV = 0.0; _vwapV = 0.0; }
            double hlc3 = (High[0] + Low[0] + Close[0]) / 3.0;
            _vwapPV += hlc3 * (double)Volume[0];
            _vwapV  += (double)Volume[0];
            _vwapVal = _vwapV > 0 ? _vwapPV / _vwapV : Close[0];

            // ── Volatility regime ─────────────────────────────────────────
            double atrSmaVal = CurrentBar >= 42 ? _atrSmaInd[0] : _atrVal;
            double volRatio  = atrSmaVal > 0 ? _atrVal / atrSmaVal : 1.0;
            _volRegime = volRatio > 1.3 ? "High" : volRatio < 0.7 ? "Low" : "Normal";

            // ── HTF Bias (simplified: current-TF EMA alignment) ──────────
            // NOTE: For true HTF bias identical to PineScript, add a secondary
            // data series in State.Configure via AddDataSeries() and compute
            // EMAs on BarsArray[1] using GetCurrentBidAsk or similar.
            _htfBias = _emaFastVal > _emaSlowVal ? 1 : _emaFastVal < _emaSlowVal ? -1 : 0;

            // ── Swing high / low ──────────────────────────────────────────
            _swingLow  = Low[0];
            _swingHigh = High[0];
            int lb = Math.Min(SwingLookback, CurrentBar);
            for (int i = 1; i <= lb; i++)
            {
                if (Low[i]  < _swingLow)  _swingLow  = Low[i];
                if (High[i] > _swingHigh) _swingHigh = High[i];
            }

            // ── Confluence Scoring ────────────────────────────────────────
            _bullScore = 0;
            _bullScore += _emaFastVal > _emaSlowVal                           ? 1.0 : 0.0;
            _bullScore += Close[0]    > _emaTrendVal                          ? 1.0 : 0.0;
            _bullScore += _rsiVal > 50 && _rsiVal < 75                        ? 1.0 : 0.0;
            _bullScore += _macdHist   > 0                                     ? 1.0 : 0.0;
            _bullScore += _macdLine   > _macdSig                              ? 1.0 : 0.0;
            _bullScore += Close[0]    > _vwapVal                              ? 1.0 : 0.0;
            _bullScore += _volAboveAvg                                        ? 1.0 : 0.0;
            _bullScore += _strongTrend && _diPlus > _diMinus                  ? 1.0 : 0.0;
            _bullScore += _htfBias == 1                                       ? 1.5 : 0.0;
            _bullScore += Close[0]    > _emaFastVal                           ? 0.5 : 0.0;

            _bearScore = 0;
            _bearScore += _emaFastVal < _emaSlowVal                           ? 1.0 : 0.0;
            _bearScore += Close[0]    < _emaTrendVal                          ? 1.0 : 0.0;
            _bearScore += _rsiVal < 50 && _rsiVal > 25                        ? 1.0 : 0.0;
            _bearScore += _macdHist   < 0                                     ? 1.0 : 0.0;
            _bearScore += _macdLine   < _macdSig                              ? 1.0 : 0.0;
            _bearScore += Close[0]    < _vwapVal                              ? 1.0 : 0.0;
            _bearScore += _volAboveAvg                                        ? 1.0 : 0.0;
            _bearScore += _strongTrend && _diMinus > _diPlus                  ? 1.0 : 0.0;
            _bearScore += _htfBias == -1                                      ? 1.5 : 0.0;
            _bearScore += Close[0]    < _emaFastVal                           ? 0.5 : 0.0;

            // ── Signal Logic ──────────────────────────────────────────────
            bool emaBullCross = CrossAbove(_emaFastInd, _emaSlowInd, 1);
            bool emaBearCross = CrossBelow(_emaFastInd, _emaSlowInd, 1);

            bool bullMom = Close[0] > _emaFastVal && Close[0] > _emaSlowVal;
            bool bearMom = Close[0] < _emaFastVal && Close[0] < _emaSlowVal;

            bool rawBuy  = emaBullCross && bullMom && _rsiVal < 75
                           && _bullScore >= eMinScore && PassesGradeFilter(_bullScore);
            bool rawSell = emaBearCross && bearMom && _rsiVal > 25
                           && _bearScore >= eMinScore && PassesGradeFilter(_bearScore);

            // Block duplicate signals in the same direction
            bool cBuy  = rawBuy  && _lastDir != 1;
            bool cSell = rawSell && _lastDir != -1;
            if (cBuy && cSell) cSell = false;   // bull takes precedence on same bar

            if      (cBuy)  _lastDir = 1;
            else if (cSell) _lastDir = -1;

            // ── Trade Management ──────────────────────────────────────────
            if (cBuy)
            {
                _entryPx   = Close[0];
                _tradeDir  = 1;
                _slPx      = CalcSL(true,  Close[0], _atrVal * eSLMult, _atrVal);
                _tradeRisk = Math.Abs(Close[0] - _slPx);
                _tp1Px     = Close[0] + _tradeRisk * Tp1Mult;
                _tp2Px     = Close[0] + _tradeRisk * Tp2Mult;
                _tp3Px     = Close[0] + _tradeRisk * Tp3Mult;
                _trailPx   = _slPx;
                _tp1Hit = _tp2Hit = _tp3Hit = _slHit = false;
                _entryBar  = CurrentBar;
            }
            else if (cSell)
            {
                _entryPx   = Close[0];
                _tradeDir  = -1;
                _slPx      = CalcSL(false, Close[0], _atrVal * eSLMult, _atrVal);
                _tradeRisk = Math.Abs(Close[0] - _slPx);
                _tp1Px     = Close[0] - _tradeRisk * Tp1Mult;
                _tp2Px     = Close[0] - _tradeRisk * Tp2Mult;
                _tp3Px     = Close[0] - _tradeRisk * Tp3Mult;
                _trailPx   = _slPx;
                _tp1Hit = _tp2Hit = _tp3Hit = _slHit = false;
                _entryBar  = CurrentBar;
            }

            // ── TP / SL hit detection ─────────────────────────────────────
            if (CurrentBar > _entryBar && _tradeDir != 0 && !_slHit)
            {
                double snapTrail = _trailPx;   // snapshot before mutating
                if (_tradeDir == 1)
                {
                    if (High[0] >= _tp1Px && !_tp1Hit) { _tp1Hit = true; if (UseTrail) _trailPx = _entryPx; }
                    if (High[0] >= _tp2Px && !_tp2Hit) { _tp2Hit = true; if (UseTrail) _trailPx = _tp1Px;   }
                    if (High[0] >= _tp3Px && !_tp3Hit) { _tp3Hit = true; if (UseTrail) _trailPx = _tp2Px;   }
                    if (Low[0]  <= snapTrail)           { _slHit  = true; }
                }
                else // short
                {
                    if (Low[0]  <= _tp1Px && !_tp1Hit) { _tp1Hit = true; if (UseTrail) _trailPx = _entryPx; }
                    if (Low[0]  <= _tp2Px && !_tp2Hit) { _tp2Hit = true; if (UseTrail) _trailPx = _tp1Px;   }
                    if (Low[0]  <= _tp3Px && !_tp3Hit) { _tp3Hit = true; if (UseTrail) _trailPx = _tp2Px;   }
                    if (High[0] >= snapTrail)           { _slHit  = true; }
                }
            }

            _tradeActive = _tradeDir != 0 && !_slHit;
            _tradeStatus = !_tradeActive  ? "No Trade"
                         : _tp3Hit ? "TP3 ✓ — Trail"
                         : _tp2Hit ? "TP2 ✓ — Trail"
                         : _tp1Hit ? "TP1 ✓ — Trail"
                         : "Active";

            // Reset direction gate on SL closure (rising edge only)
            if (_slHit && !_slHitPrev) _lastDir = 0;
            _slHitPrev = _slHit;

            // ── Backtest Tracker ──────────────────────────────────────────
            bool  justClosed = false;
            double closedR   = 0.0;

            // Case 1: SL fired this bar (wasn't fired last bar)
            if (_slHit && !_prevSL && _prevTradeOpen)
            {
                justClosed = true;
                if (UseTrail)
                    closedR = _prevTP3 || _tp3Hit ? Tp2Mult
                            : _prevTP2 || _tp2Hit ? Tp1Mult
                            : _prevTP1 || _tp1Hit ? 0.0
                            : -1.0;
                else
                    closedR = _prevTP3 || _tp3Hit ? Tp3Mult
                            : _prevTP2 || _tp2Hit ? Tp2Mult
                            : _prevTP1 || _tp1Hit ? Tp1Mult
                            : -1.0;
            }
            // Case 2: opposing signal closes the trade
            else if ((cBuy && _prevDir == -1 || cSell && _prevDir == 1) && _prevTradeOpen)
            {
                justClosed = true;
                closedR = _prevTP3 ? Tp3Mult
                        : _prevTP2 ? Tp2Mult
                        : _prevTP1 ? Tp1Mult
                        : 0.0;
            }

            if (justClosed)
            {
                _btTotal++;
                _btTotalR += closedR;
                if      (closedR > 0) { _btWins++;   _btGrossWin  += closedR; }
                else if (closedR < 0) { _btLosses++; _btGrossLoss += Math.Abs(closedR); }
            }

            _prevTradeOpen = _tradeActive;
            _prevDir       = _tradeDir;
            _prevTP1 = _tp1Hit; _prevTP2 = _tp2Hit; _prevTP3 = _tp3Hit; _prevSL = _slHit;

            // ── EMA Plot Values ───────────────────────────────────────────
            Values[0][0] = ShowRibbon ? _emaFastVal  : double.NaN;
            Values[1][0] = ShowRibbon ? _emaSlowVal  : double.NaN;
            Values[2][0] = ShowRibbon ? _emaTrendVal : double.NaN;

            // ── Signal Arrows + Grade Labels ──────────────────────────────
            if (ShowSignals)
            {
                if (cBuy)
                {
                    string gradeStr = ShowGrade ? " " + GetGrade(_bullScore) : "";
                    Draw.ArrowUp(this, "BuyArrow_" + CurrentBar, false,
                        0, Low[0] - _atrVal * 0.5, BullBrush);
                    Draw.Text(this, "BuyLabel_" + CurrentBar, false,
                        "Long" + gradeStr,
                        0, Low[0] - _atrVal * 1.3, 0,
                        Brushes.White,
                        new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9) { Bold = true },
                        System.Windows.TextAlignment.Center,
                        BullBrush, BullBrush, 70);
                }
                if (cSell)
                {
                    string gradeStr = ShowGrade ? " " + GetGrade(_bearScore) : "";
                    Draw.ArrowDown(this, "SellArrow_" + CurrentBar, false,
                        0, High[0] + _atrVal * 0.5, BearBrush);
                    Draw.Text(this, "SellLabel_" + CurrentBar, false,
                        "Short" + gradeStr,
                        0, High[0] + _atrVal * 1.3, 0,
                        Brushes.White,
                        new NinjaTrader.Gui.Tools.SimpleFont("Arial", 9) { Bold = true },
                        System.Windows.TextAlignment.Center,
                        BearBrush, BearBrush, 70);
                }
            }

            // ── TP / SL / Entry / Trail Horizontal Lines ──────────────────
            // HorizontalLine spans the full chart width — practical for NT8.
            // Lines are redrawn each bar (same tag = update in place).
            if (ShowTPSL)
            {
                if (_tradeActive)
                {
                    System.Windows.Media.Brush tp1B = _tp1Hit ? Brushes.Cyan : Brushes.LimeGreen;
                    System.Windows.Media.Brush tp2B = _tp2Hit ? Brushes.Cyan : Brushes.LimeGreen;
                    System.Windows.Media.Brush tp3B = _tp3Hit ? Brushes.Cyan : Brushes.LimeGreen;

                    Draw.HorizontalLine(this, "PS_Entry", false, _entryPx, Brushes.DodgerBlue,  DashStyleHelper.Solid, 2);
                    Draw.HorizontalLine(this, "PS_SL",    false, _slPx,    Brushes.Crimson,      DashStyleHelper.Solid, 2);
                    Draw.HorizontalLine(this, "PS_TP1",   false, _tp1Px,   tp1B,                 DashStyleHelper.Dash,  1);
                    Draw.HorizontalLine(this, "PS_TP2",   false, _tp2Px,   tp2B,                 DashStyleHelper.Dash,  1);
                    Draw.HorizontalLine(this, "PS_TP3",   false, _tp3Px,   tp3B,                 DashStyleHelper.Dash,  2);

                    if (ShowTrailVis && UseTrail)
                        Draw.HorizontalLine(this, "PS_Trail", false, _trailPx, Brushes.Orange, DashStyleHelper.Dot, 1);
                    else
                        RemoveDrawObject("PS_Trail");
                }
                else if (_slHit || _tradeDir == 0)
                {
                    // Clean up level lines once trade is over
                    RemoveDrawObject("PS_Entry");
                    RemoveDrawObject("PS_SL");
                    RemoveDrawObject("PS_TP1");
                    RemoveDrawObject("PS_TP2");
                    RemoveDrawObject("PS_TP3");
                    RemoveDrawObject("PS_Trail");
                }
            }

            // ── NT8 Alerts (Print to Output window) ───────────────────────
            if (cBuy)
            {
                string g = GetGrade(_bullScore);
                Print($"[PrecSniper] 🟢 LONG {g} | {Instrument.FullName} | "
                    + $"Entry:{Close[0]:F5} SL:{_slPx:F5} "
                    + $"TP1:{_tp1Px:F5} TP2:{_tp2Px:F5} TP3:{_tp3Px:F5} "
                    + $"Score:{_bullScore:F1} Preset:{resolvedPreset}");
            }
            if (cSell)
            {
                string g = GetGrade(_bearScore);
                Print($"[PrecSniper] 🔴 SHORT {g} | {Instrument.FullName} | "
                    + $"Entry:{Close[0]:F5} SL:{_slPx:F5} "
                    + $"TP1:{_tp1Px:F5} TP2:{_tp2Px:F5} TP3:{_tp3Px:F5} "
                    + $"Score:{_bearScore:F1} Preset:{resolvedPreset}");
            }
            if (_tp1Hit && !_prevTP1 && _tradeDir != 0) Print($"[PrecSniper] 🎯 TP1 HIT | {Instrument.FullName} | {Close[0]:F5}");
            if (_tp2Hit && !_prevTP2 && _tradeDir != 0) Print($"[PrecSniper] 🎯 TP2 HIT | {Instrument.FullName} | {Close[0]:F5}");
            if (_tp3Hit && !_prevTP3 && _tradeDir != 0) Print($"[PrecSniper] 🏆 TP3 HIT | {Instrument.FullName} | {Close[0]:F5}");
            if (_slHit  && !_slHitPrev && _tradeDir != 0) Print($"[PrecSniper] 🛑 SL HIT  | {Instrument.FullName} | {Close[0]:F5}");
        }

        // ── Helper: Calculate Stop-Loss ───────────────────────────────────
        private double CalcSL(bool isLong, double entry, double atrDist, double atr)
        {
            double atrStop = isLong ? entry - atrDist : entry + atrDist;
            if (!UseStructureSL) return atrStop;

            double structStop = isLong
                ? _swingLow  - atr * 0.2
                : _swingHigh + atr * 0.2;

            double finalStop = isLong
                ? Math.Max(atrStop, structStop)
                : Math.Min(atrStop, structStop);

            double minDist = atr * 0.5;
            if (Math.Abs(entry - finalStop) < minDist)
                finalStop = isLong ? entry - minDist : entry + minDist;

            return finalStop;
        }

        // ── Helper: Signal Grade ──────────────────────────────────────────
        private string GetGrade(double score)
            => score >= 8.0 ? "A+" : score >= 6.5 ? "A" : score >= 5.0 ? "B" : "C";

        // ── Helper: Grade filter ──────────────────────────────────────────
        private bool PassesGradeFilter(double score)
        {
            bool gradeOk = GradeFilter switch
            {
                "A+ Only"  => score >= 8.0,
                "A+ and A" => score >= 6.5,
                _          => true
            };
            return gradeOk && (!HideCGrade || score >= 5.0);
        }

        // ══════════════════════════════════════════════════════════════════
        //  DASHBOARD — rendered via SharpDX / Direct2D in OnRender
        //  (equivalent of the PineScript table.new / table.cell dashboard)
        // ══════════════════════════════════════════════════════════════════
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!ShowDash || IsInHitTest || ChartBars == null || RenderTarget == null) return;

            try { RenderDashboard(chartControl); }
            catch { /* silently swallow render errors during startup */ }
        }

        private void RenderDashboard(ChartControl cc)
        {
            const float margin  = 10f;
            const float boxW    = 200f;
            const float rowH    = 17f;
            const float colSplit = 0.52f;   // key column fraction of boxW

            int   btRows = ShowBtDash ? 7 : 0;
            float boxH   = (12 + btRows) * rowH + 8f;
            float boxX   = cc.CanvasRight - boxW - margin;
            float boxY   = margin;
            float col0   = boxX + 6f;
            float col1   = boxX + boxW * colSplit;
            float cw0    = boxW * (colSplit - 0.03f);
            float cw1    = boxW * (1f - colSplit - 0.03f);

            // Background + border
            using (var bgBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Col_DashBG))
                RenderTarget.FillRectangle(new SharpDX.RectangleF(boxX, boxY, boxW, boxH), bgBrush);
            using (var bdBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Col_Border))
                RenderTarget.DrawRectangle(new SharpDX.RectangleF(boxX, boxY, boxW, boxH), bdBrush, 1f);

            // Text formats (always dispose SharpDX objects)
            var tf     = new SharpDX.DirectWrite.TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 10.5f);
            var tfBold = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal, 10.5f);

            try
            {
                float ty = boxY + 3f;

                // ─ Header ─────────────────────────────────────────────────
                DrawHeaderRow(boxX, ty, boxW, rowH, "PrecisionSniper  v1.2.2", Col_HdrBlue, tfBold, col0);
                ty += rowH;

                // ─ Trend ──────────────────────────────────────────────────
                bool isBull = _emaFastVal > _emaSlowVal && Close[0] > _emaTrendVal;
                bool isBear = _emaFastVal < _emaSlowVal && Close[0] < _emaTrendVal;
                string trendStr = isBull ? "Bullish" : isBear ? "Bearish" : "Neutral";
                DrawDashRow(tf, "Trend", trendStr, col0, col1, ty, cw0, cw1,
                    Col_Muted, isBull ? Col_Green : isBear ? Col_Red : Col_Yellow);
                ty += rowH;

                // ─ Score ──────────────────────────────────────────────────
                double activeScore = _tradeDir == 1 ? _bullScore
                                   : _tradeDir == -1 ? _bearScore
                                   : Math.Max(_bullScore, _bearScore);
                SharpDX.Color4 scoreClr = activeScore >= 7 ? Col_Green
                                        : activeScore >= 5 ? Col_Yellow : Col_Red;
                DrawDashRow(tf, "Score", $"{activeScore:F1} / 10",
                    col0, col1, ty, cw0, cw1, Col_Muted, scoreClr);
                ty += rowH;

                // ─ Status ─────────────────────────────────────────────────
                SharpDX.Color4 statusClr = _tradeStatus == "No Trade" ? Col_Muted
                                         : _tradeStatus.Contains("TP") ? Col_Green : Col_Yellow;
                DrawDashRow(tf, "Status", _tradeStatus,
                    col0, col1, ty, cw0, cw1, Col_Muted, statusClr);
                ty += rowH;

                // ─ HTF Bias ───────────────────────────────────────────────
                string htfStr = _htfBias == 1 ? "Bullish" : _htfBias == -1 ? "Bearish" : "Neutral";
                SharpDX.Color4 htfClr = _htfBias == 1 ? Col_Green : _htfBias == -1 ? Col_Red : Col_Yellow;
                DrawDashRow(tf, "HTF Bias*", htfStr, col0, col1, ty, cw0, cw1, Col_Muted, htfClr);
                ty += rowH;

                // ─ Volatility ─────────────────────────────────────────────
                SharpDX.Color4 volClr = _volRegime == "High" ? Col_Red
                                      : _volRegime == "Low"  ? Col_Muted : Col_Green;
                DrawDashRow(tf, "Volatility", _volRegime, col0, col1, ty, cw0, cw1, Col_Muted, volClr);
                ty += rowH;

                // ─ RSI ────────────────────────────────────────────────────
                SharpDX.Color4 rsiClr = _rsiVal > 70 ? Col_Red
                                      : _rsiVal < 30 ? Col_Green : Col_White;
                DrawDashRow(tf, "RSI", $"{_rsiVal:F1}",
                    col0, col1, ty, cw0, cw1, Col_Muted, rsiClr);
                ty += rowH;

                // ─ ADX ────────────────────────────────────────────────────
                string adxDisplay = $"{_adxVal:F1}{(_strongTrend ? " ✓" : "")}";
                DrawDashRow(tf, "ADX", adxDisplay,
                    col0, col1, ty, cw0, cw1, Col_Muted, _strongTrend ? Col_Green : Col_Muted);
                ty += rowH;

                // ─ Timeframe ──────────────────────────────────────────────
                DrawDashRow(tf, "Timeframe", BarsPeriod.ToString(),
                    col0, col1, ty, cw0, cw1, Col_Muted, Col_White);
                ty += rowH;

                // ─ Preset ─────────────────────────────────────────────────
                string presetDisplay = Preset == "Auto" ? $"Auto → {resolvedPreset}" : resolvedPreset;
                DrawDashRow(tf, "Preset", presetDisplay,
                    col0, col1, ty, cw0, cw1, Col_Muted, Col_White);
                ty += rowH;

                // ─ Grade Filter ───────────────────────────────────────────
                SharpDX.Color4 gfClr = GradeFilter == "A+ Only"  ? Col_Green
                                     : GradeFilter == "A+ and A" ? Col_Yellow : Col_White;
                DrawDashRow(tf, "Grade Filter", GradeFilter,
                    col0, col1, ty, cw0, cw1, Col_Muted, gfClr);
                ty += rowH;

                // ─ Active Trade Levels ────────────────────────────────────
                // Show entry/SL/TP prices in dashboard when a trade is active
                if (_tradeActive)
                {
                    string dirLabel = _tradeDir == 1 ? "LONG" : "SHORT";
                    DrawDashRow(tf, dirLabel, $"E:{_entryPx:F5}",
                        col0, col1, ty, cw0, cw1,
                        _tradeDir == 1 ? Col_Green : Col_Red, Col_White);
                }
                else
                {
                    DrawDashRow(tf, "Trade", "—",
                        col0, col1, ty, cw0, cw1, Col_Muted, Col_Muted);
                }
                ty += rowH;

                // ─ Backtest Stats Section ─────────────────────────────────
                if (ShowBtDash)
                {
                    DrawHeaderRow(boxX, ty, boxW, rowH, "Backtest Stats", Col_HdrPurp, tfBold, col0);
                    ty += rowH;

                    DrawDashRow(tf, "Trades",
                        $"{_btTotal} ({_btWins}W / {_btLosses}L)",
                        col0, col1, ty, cw0, cw1, Col_Muted, Col_White);
                    ty += rowH;

                    double wr   = _btTotal > 0 ? _btWins * 100.0 / _btTotal : 0;
                    SharpDX.Color4 wrClr = wr >= 55 ? Col_Green : wr >= 45 ? Col_Yellow : Col_Red;
                    DrawDashRow(tf, "Win Rate", $"{wr:F1}%",
                        col0, col1, ty, cw0, cw1, Col_Muted, _btTotal > 0 ? wrClr : Col_Muted);
                    ty += rowH;

                    double avgR = _btTotal > 0 ? _btTotalR / _btTotal : 0;
                    SharpDX.Color4 avgRClr = avgR > 0.5 ? Col_Green : avgR > 0 ? Col_Yellow : Col_Red;
                    DrawDashRow(tf, "Avg R", $"{avgR:F2}R",
                        col0, col1, ty, cw0, cw1, Col_Muted, _btTotal > 0 ? avgRClr : Col_Muted);
                    ty += rowH;

                    double pf    = _btGrossLoss > 0 ? _btGrossWin / _btGrossLoss
                                 : (_btGrossWin > 0 ? 999.0 : 0.0);
                    string pfStr = pf >= 999 ? "∞" : $"{pf:F2}";
                    SharpDX.Color4 pfClr = pf >= 1.5 ? Col_Green : pf >= 1.0 ? Col_Yellow : Col_Red;
                    DrawDashRow(tf, "Profit Factor", pfStr,
                        col0, col1, ty, cw0, cw1, Col_Muted, _btTotal > 0 ? pfClr : Col_Muted);
                    ty += rowH;

                    SharpDX.Color4 trClr = _btTotalR > 0 ? Col_Green : _btTotalR < 0 ? Col_Red : Col_Muted;
                    DrawDashRow(tf, "Total R", $"{_btTotalR:F2}R",
                        col0, col1, ty, cw0, cw1, Col_Muted, _btTotal > 0 ? trClr : Col_Muted);
                }
            }
            finally
            {
                tf.Dispose();
                tfBold.Dispose();
            }
        }

        // ── SharpDX drawing helpers ───────────────────────────────────────

        private void DrawHeaderRow(float boxX, float ty, float boxW, float rowH,
            string text, SharpDX.Color4 bgColor,
            SharpDX.DirectWrite.TextFormat font, float textX)
        {
            using (var hb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, bgColor))
                RenderTarget.FillRectangle(new SharpDX.RectangleF(boxX, ty - 1f, boxW, rowH + 1f), hb);
            using (var tb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Col_White))
                RenderTarget.DrawText(text, font,
                    new SharpDX.RectangleF(textX, ty, boxW - 8f, rowH), tb);
        }

        private void DrawDashRow(SharpDX.DirectWrite.TextFormat font,
            string key, string val,
            float x0, float x1, float y, float w0, float w1,
            SharpDX.Color4 keyColor, SharpDX.Color4 valColor)
        {
            using (var kb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, keyColor))
                RenderTarget.DrawText(key, font, new SharpDX.RectangleF(x0, y, w0, 16f), kb);
            using (var vb = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, valColor))
                RenderTarget.DrawText(val, font, new SharpDX.RectangleF(x1, y, w1, 16f), vb);
        }

        // ══════════════════════════════════════════════════════════════════
        //  PROPERTIES
        // ══════════════════════════════════════════════════════════════════

        // ── Main Settings ─────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Preset",
            Description = "Auto | Conservative | Default | Aggressive | Scalping | Swing | Crypto 24/7 | Custom",
            GroupName = "Main Settings", Order = 0)]
        public string Preset { get; set; }

        // ── Entry Engine ──────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "EMA Fast Length", GroupName = "Entry Engine", Order = 0)]
        public int EmaFastLen { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "EMA Slow Length", GroupName = "Entry Engine", Order = 1)]
        public int EmaSlowLen { get; set; }

        [NinjaScriptProperty]
        [Range(20, 200)]
        [Display(Name = "EMA Trend Length", GroupName = "Entry Engine", Order = 2)]
        public int EmaTrendLen { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Min Confluence Score", GroupName = "Entry Engine", Order = 3)]
        public int MinScore { get; set; }

        [NinjaScriptProperty]
        [Range(5, 30)]
        [Display(Name = "RSI Length", GroupName = "Entry Engine", Order = 4)]
        public int RsiLen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Signal Grade Filter",
            Description = "All | A+ and A | A+ Only",
            GroupName = "Entry Engine", Order = 5)]
        public string GradeFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hide C-Grade Signals", GroupName = "Entry Engine", Order = 6)]
        public bool HideCGrade { get; set; }

        // ── Risk Management ───────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ATR Length", GroupName = "Risk Management", Order = 0)]
        public int AtrLen { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "SL ATR Multiplier", GroupName = "Risk Management", Order = 1)]
        public double SlMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "TP1 Risk:Reward", GroupName = "Risk Management", Order = 2)]
        public double Tp1Mult { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 8.0)]
        [Display(Name = "TP2 Risk:Reward", GroupName = "Risk Management", Order = 3)]
        public double Tp2Mult { get; set; }

        [NinjaScriptProperty]
        [Range(1.5, 12.0)]
        [Display(Name = "TP3 Risk:Reward", GroupName = "Risk Management", Order = 4)]
        public double Tp3Mult { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trailing Stop", GroupName = "Risk Management", Order = 5)]
        public bool UseTrail { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Structure-Based SL", GroupName = "Risk Management", Order = 6)]
        public bool UseStructureSL { get; set; }

        [NinjaScriptProperty]
        [Range(3, 30)]
        [Display(Name = "Swing Lookback", GroupName = "Risk Management", Order = 7)]
        public int SwingLookback { get; set; }

        // ── Visual ────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Show Buy/Sell Signals", GroupName = "Visual", Order = 0)]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Signal Grade", GroupName = "Visual", Order = 1)]
        public bool ShowGrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TP/SL Lines", GroupName = "Visual", Order = 2)]
        public bool ShowTPSL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA Ribbon", GroupName = "Visual", Order = 3)]
        public bool ShowRibbon { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Trailing Stop Line", GroupName = "Visual", Order = 4)]
        public bool ShowTrailVis { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Dashboard", GroupName = "Visual", Order = 5)]
        public bool ShowDash { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Backtest Stats", GroupName = "Visual", Order = 6)]
        public bool ShowBtDash { get; set; }

        // ── Colors ────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bull Color", GroupName = "Colors", Order = 0)]
        public System.Windows.Media.Brush BullBrush { get; set; }

        [Browsable(false)]
        public string BullBrushSerializable
        {
            get { return Serialize.BrushToString(BullBrush); }
            set { BullBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bear Color", GroupName = "Colors", Order = 1)]
        public System.Windows.Media.Brush BearBrush { get; set; }

        [Browsable(false)]
        public string BearBrushSerializable
        {
            get { return Serialize.BrushToString(BearBrush); }
            set { BearBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Neutral Color", GroupName = "Colors", Order = 2)]
        public System.Windows.Media.Brush NeutralBrush { get; set; }

        [Browsable(false)]
        public string NeutralBrushSerializable
        {
            get { return Serialize.BrushToString(NeutralBrush); }
            set { NeutralBrush = Serialize.StringToBrush(value); }
        }

        // ── Plot accessors (for external access / data box) ───────────────
        [Browsable(false)]
        [XmlIgnore]
        public Series<double> EMAFast  => Values[0];

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> EMASlow  => Values[1];

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> EMATrend => Values[2];
    }
}
