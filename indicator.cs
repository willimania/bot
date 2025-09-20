using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Controls;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo
{
    [Indicator(IsOverlay = false, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class RsiTrendlinesAuto : Indicator
    {
        [Parameter("RSI Period", DefaultValue = 14, MinValue = 2)]
        public int Period { get; set; }

        [Parameter("Swing (Pivot Breite)", DefaultValue = 5, MinValue = 1, MaxValue = 50)]
        public int Swing { get; set; }

        [Parameter("Max Rückblick Bars", DefaultValue = 300, MinValue = 50, MaxValue = 5000)]
        public int SearchBack { get; set; }

        [Parameter("Verlängerung rechts (Bars)", DefaultValue = 2, MinValue = 0, MaxValue = 50)]
        public int ExtendBars { get; set; }

        [Parameter("Pivot Marker anzeigen", DefaultValue = false)]
        public bool ShowPivotMarkers { get; set; }

        [Parameter("Signal Marker anzeigen", DefaultValue = true)]
        public bool ShowSignalMarkers { get; set; }

        [Parameter("Renko Wicks aktivieren", DefaultValue = false, Group = "Renko Wicks")]
        public bool EnableRenkoWicks { get; set; }

        public enum LoadTickFromData
        {
            Today,
            Yesterday,
            BeforeYesterday,
            OneWeek,
            TwoWeek,
            Monthly,
            Custom
        }

        [Parameter("Ticks laden ab", DefaultValue = LoadTickFromData.Today, Group = "Renko Wicks | Tick-Daten")]
        public LoadTickFromData LoadTickFrom { get; set; }

        public enum LoadTickStrategy
        {
            AtStartupSync,
            OnChartStartSync,
            OnChartEndAsync
        }

        [Parameter("Lade-Strategie", DefaultValue = LoadTickStrategy.OnChartEndAsync, Group = "Renko Wicks | Tick-Daten")]
        public LoadTickStrategy TickLoadStrategy { get; set; }

        [Parameter("Benutzerdefiniertes Datum (dd/MM/yyyy)", DefaultValue = "00/00/0000", Group = "Renko Wicks | Tick-Daten")]
        public string CustomTickDate { get; set; }

        public enum LoadTickNotify
        {
            Minimal,
            Detailed
        }

        [Parameter("Benachrichtigungen", DefaultValue = LoadTickNotify.Minimal, Group = "Renko Wicks | Tick-Daten")]
        public LoadTickNotify TickNotifyMode { get; set; }

        [Parameter("Wick-Linienstärke", DefaultValue = 1, MinValue = 1, MaxValue = 5, Group = "Renko Wicks")]
        public int WickThickness { get; set; }

        [Output("RSI", LineColor = "DodgerBlue", PlotType = PlotType.Line, Thickness = 2)]
        public IndicatorDataSeries RsiOut { get; set; }

        public double TrendHighAtLastClose { get; private set; } = double.NaN;
        public double TrendLowAtLastClose  { get; private set; } = double.NaN;
        public double TrendHighAtPrevClose { get; private set; } = double.NaN;
        public double TrendLowAtPrevClose  { get; private set; } = double.NaN;

        public int SignalAtLastClose { get; private set; }
        public int SignalAtPrevClose { get; private set; }

        private RelativeStrengthIndex _rsi;
        private bool _loggedException;
        private readonly Dictionary<string, int> _signalMarkers = new Dictionary<string, int>();

        // Renko Wicks
        private const string RenkoNotifyCaption = "Renko Wicks";
        private Bars _tickBars;
        private DateTime _firstTickTime;
        private DateTime _tickLoadFrom;
        private int _lastTickForWicks;
        private ProgressBar _syncTickProgressBar;
        private PopupNotification _asyncTickPopup;
        private bool _loadingAsyncTicks;
        private bool _loadingTicksComplete;
        private bool _requestAsyncLoad;
        private bool _renkoWrongTimeFrame;
        private bool _renkoInitialized;
        private Color _renkoUpColor;
        private Color _renkoDownColor;

        protected override void Initialize()
        {
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, Period);
        }

        public override void Calculate(int index)
        {
            try
            {
                if (Bars == null)
                    return;

                if (_rsi?.Result == null)
                {
                    if (RsiOut != null && index >= 0 && index < RsiOut.Count)
                        RsiOut[index] = double.NaN;

                    ResetState();
                    return;
                }

                if (index < 0 || index >= _rsi.Result.Count)
                    return;

                if (RsiOut != null)
                    RsiOut[index] = _rsi.Result[index];

                int lastClosed = index;
                if (index >= Bars.Count - 1)
                    lastClosed = Bars.Count - 2;

                if (lastClosed <= 0)
                {
                    ResetState();
                    return;
                }

                int prev = lastClosed - 1;

                if (lastClosed < Swing * 2 + 5 || prev < 0)
                {
                    ResetState();
                    return;
                }

                int from = Math.Max(Swing, Math.Max(0, lastClosed - SearchBack));
                int to = lastClosed - Swing;

                if (to < from)
                {
                    ResetState();
                    return;
                }

                var lastTwoHighs = FindLastTwoPivotHighs(_rsi.Result, from, to, Swing);
                var lastTwoLows  = FindLastTwoPivotLows (_rsi.Result, from, to, Swing);

                if (IndicatorArea != null)
                {
                    IndicatorArea.RemoveObject("RSI_TL_HIGH");
                    IndicatorArea.RemoveObject("RSI_TL_LOW");
                    IndicatorArea.RemoveObject("RSI_PIV_H1");
                    IndicatorArea.RemoveObject("RSI_PIV_H2");
                    IndicatorArea.RemoveObject("RSI_PIV_L1");
                    IndicatorArea.RemoveObject("RSI_PIV_L2");
                }

                TrendHighAtLastClose = double.NaN;
                TrendLowAtLastClose  = double.NaN;
                TrendHighAtPrevClose = double.NaN;
                TrendLowAtPrevClose  = double.NaN;

                if (lastTwoHighs.found)
                    ProcessTrendline(lastTwoHighs.i2, lastTwoHighs.i1, lastClosed, prev, true);

                if (lastTwoLows.found)
                    ProcessTrendline(lastTwoLows.i2, lastTwoLows.i1, lastClosed, prev, false);

                ComputeSignal(lastClosed, prev);

                if (index >= lastClosed)
                {
                    bool markersActive = ShowSignalMarkers && Chart != null;

                    if (markersActive)
                    {
                        RemoveOutdatedSignalMarkers(lastClosed - SearchBack);

                        if (SignalAtLastClose != 0)
                            DrawSignalMarker(lastClosed, SignalAtLastClose);
                    }
                    else
                        ClearSignalMarkers();
                }
            }
            catch (Exception ex)
            {
                ResetState();
                ClearSignalMarkers();

                if (!_loggedException)
                {
                    Print("RsiTrendlinesAuto error: {0}", ex.Message);
                    _loggedException = true;
                }
            }
        }

        private void ProcessTrendline(int iLeft, int iRight, int lastClosed, int prev, bool isHighLine)
        {
            if (iRight <= iLeft)
                return;

            double yLeft = _rsi.Result[iLeft];
            double yRight = _rsi.Result[iRight];
            double slope = (yRight - yLeft) / (iRight - iLeft);

            int drawEnd = Math.Min(Bars.Count - 1, Math.Max(iRight + ExtendBars, lastClosed));
            double yEnd = yLeft + slope * (drawEnd - iLeft);

            if (IndicatorArea != null)
            {
                IndicatorArea.DrawTrendLine(
                    isHighLine ? "RSI_TL_HIGH" : "RSI_TL_LOW",
                    iLeft,
                    yLeft,
                    drawEnd,
                    yEnd,
                    isHighLine ? Color.OrangeRed : Color.MediumSeaGreen,
                    2,
                    LineStyle.Solid
                );

                if (ShowPivotMarkers)
                {
                    string marker1 = isHighLine ? "RSI_PIV_H1" : "RSI_PIV_L1";
                    string marker2 = isHighLine ? "RSI_PIV_H2" : "RSI_PIV_L2";
                    string symbol  = isHighLine ? "▲" : "▼";
                    var color = isHighLine ? Color.OrangeRed : Color.MediumSeaGreen;

                    IndicatorArea.DrawText(marker1, symbol, iLeft, yLeft, color);
                    IndicatorArea.DrawText(marker2, symbol, iRight, yRight, color);
                }
            }

            if (lastClosed >= iLeft)
            {
                double valueAtLast = yLeft + slope * (lastClosed - iLeft);
                if (isHighLine) TrendHighAtLastClose = valueAtLast; else TrendLowAtLastClose = valueAtLast;
            }

            if (prev >= iLeft)
            {
                double valueAtPrev = yLeft + slope * (prev - iLeft);
                if (isHighLine) TrendHighAtPrevClose = valueAtPrev; else TrendLowAtPrevClose = valueAtPrev;
            }
        }

        private void ComputeSignal(int lastClosed, int prev)
        {
            double rsiLastVal = _rsi.Result[lastClosed];
            double rsiPrevVal = _rsi.Result[prev];

            int prevState = 0;

            if (!double.IsNaN(TrendHighAtPrevClose) && !double.IsNaN(rsiPrevVal) && rsiPrevVal > TrendHighAtPrevClose)
                prevState = 1;
            else if (!double.IsNaN(TrendLowAtPrevClose) && !double.IsNaN(rsiPrevVal) && rsiPrevVal < TrendLowAtPrevClose)
                prevState = -1;

            bool breakoutUp = !double.IsNaN(TrendHighAtLastClose)
                              && !double.IsNaN(rsiLastVal)
                              && rsiLastVal > TrendHighAtLastClose
                              && (!double.IsNaN(rsiPrevVal) && (double.IsNaN(TrendHighAtPrevClose) || rsiPrevVal <= TrendHighAtPrevClose));

            bool breakoutDown = !double.IsNaN(TrendLowAtLastClose)
                                && !double.IsNaN(rsiLastVal)
                                && rsiLastVal < TrendLowAtLastClose
                                && (!double.IsNaN(rsiPrevVal) && (double.IsNaN(TrendLowAtPrevClose) || rsiPrevVal >= TrendLowAtPrevClose));

            SignalAtLastClose = breakoutUp ? 1 : breakoutDown ? -1 : 0;
            SignalAtPrevClose = prevState;

            // no logging
        }

        private void DrawSignalMarker(int barIndex, int signal)
        {
            if (Chart == null || Bars == null)
                return;

            if (barIndex < 0 || barIndex >= Bars.Count)
                return;

            string markerName = $"RSI_BREAK_{Bars.OpenTimes[barIndex].ToBinary()}";

            if (_signalMarkers.TryGetValue(markerName, out _))
                Chart.RemoveObject(markerName);

            double closePrice = Bars.ClosePrices[barIndex];
            double pipSize = Symbol?.PipSize ?? 0;
            if (pipSize <= 0)
                pipSize = Math.Max(closePrice * 0.001, 1e-5);

            double offset = pipSize * 5;
            double y = signal > 0 ? closePrice + offset : closePrice - offset;

            Chart.DrawIcon(
                markerName,
                signal > 0 ? ChartIconType.UpTriangle : ChartIconType.DownTriangle,
                Bars.OpenTimes[barIndex],
                y,
                signal > 0 ? Color.OrangeRed : Color.MediumSeaGreen
            );

            _signalMarkers[markerName] = barIndex;
        }

        private void ClearSignalMarkers()
        {
            if (Chart == null || _signalMarkers.Count == 0)
                return;

            foreach (var kv in _signalMarkers)
                Chart.RemoveObject(kv.Key);

            _signalMarkers.Clear();
        }

        private void RemoveOutdatedSignalMarkers(int minIndex)
        {
            if (Chart == null || _signalMarkers.Count == 0)
                return;

            var toRemove = new List<string>();

            foreach (var kv in _signalMarkers)
            {
                if (minIndex > 0 && kv.Value < minIndex)
                {
                    Chart.RemoveObject(kv.Key);
                    toRemove.Add(kv.Key);
                }
            }

            foreach (var name in toRemove)
                _signalMarkers.Remove(name);
        }

        private void ResetState()
        {
            TrendHighAtLastClose = double.NaN;
            TrendLowAtLastClose  = double.NaN;
            TrendHighAtPrevClose = double.NaN;
            TrendLowAtPrevClose  = double.NaN;
            SignalAtLastClose = 0;
            SignalAtPrevClose = 0;
        }

        private (bool found, int i1, int i2) FindLastTwoPivotHighs(IndicatorDataSeries series, int from, int to, int swing)
        {
            int first = -1, second = -1;

            for (int i = to; i >= from; i--)
            {
                if (IsPivotHigh(series, i, swing))
                {
                    if (first == -1) first = i;
                    else { second = i; break; }
                }
            }

            return (first != -1 && second != -1, first, second);
        }

        private (bool found, int i1, int i2) FindLastTwoPivotLows(IndicatorDataSeries series, int from, int to, int swing)
        {
            int first = -1, second = -1;

            for (int i = to; i >= from; i--)
            {
                if (IsPivotLow(series, i, swing))
                {
                    if (first == -1) first = i;
                    else { second = i; break; }
                }
            }

            return (first != -1 && second != -1, first, second);
        }

        private bool IsPivotHigh(IndicatorDataSeries series, int index, int swing)
        {
            if (index - swing < 0 || index + swing >= series.Count)
                return false;

            double value = series[index];

            for (int k = index - swing; k <= index + swing; k++)
            {
                if (k == index)
                    continue;

                if (series[k] >= value)
                    return false;
            }

            return true;
        }

        private bool IsPivotLow(IndicatorDataSeries series, int index, int swing)
        {
            if (index - swing < 0 || index + swing >= series.Count)
                return false;

            double value = series[index];

            for (int k = index - swing; k <= index + swing; k++)
            {
                if (k == index)
                    continue;

                if (series[k] <= value)
                    return false;
            }

            return true;
        }
    }
}
