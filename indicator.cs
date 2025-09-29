using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
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
        public int LastSignalBarIndex { get; private set; } = -1;
        public int LastSignalDirection { get; private set; }
        public DateTime LastSignalTime { get; private set; } = DateTime.MinValue;

        private RelativeStrengthIndex _rsi;
        private bool _loggedException;
        private readonly Dictionary<string, int> _signalMarkers = new Dictionary<string, int>();
        private readonly Dictionary<int, double[]> _renkoWickCache = new Dictionary<int, double[]>();
        private readonly Dictionary<int, int> _signalHistory = new Dictionary<int, int>();
        private readonly Dictionary<long, int> _signalHistoryByTime = new Dictionary<long, int>();
        private readonly Dictionary<int, long> _signalIndexToTime = new Dictionary<int, long>();

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

            if (EnableRenkoWicks)
                InitializeRenkoWicks();
        }

        public override void Calculate(int index)
        {
            try
            {
                if (Bars == null)
                    return;

                if (EnableRenkoWicks)
                    ProcessRenkoWicks(index);

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

            UpdateSignalHistory(lastClosed, SignalAtLastClose);
            SignalAtPrevClose = prevState;

            if (Bars != null && lastClosed >= 0 && lastClosed < Bars.Count)
                Print($"Indicator: Signal {SignalAtLastClose} auf Bar {lastClosed}");

            if (SignalAtLastClose != 0)
            {
                LastSignalBarIndex = lastClosed;
                LastSignalDirection = SignalAtLastClose;
                LastSignalTime = Bars.OpenTimes[lastClosed];
            }

            // no logging
        }

        private void UpdateSignalHistory(int barIndex, int signal)
        {
            if (barIndex < 0 || Bars == null || barIndex >= Bars.Count)
                return;

            long timeKey = Bars.OpenTimes[barIndex].ToBinary();

            if (_signalHistory.TryGetValue(barIndex, out int existingByIndex))
            {
                if (existingByIndex != 0 && signal == 0)
                    return;

                if (existingByIndex == signal)
                    return;
            }
            else if (signal == 0)
                return;

            _signalHistory[barIndex] = signal;
            _signalIndexToTime[barIndex] = timeKey;

            if (_signalHistoryByTime.TryGetValue(timeKey, out int existingByTime))
            {
                if (existingByTime != 0 && signal == 0)
                    return;

                if (existingByTime == signal)
                    return;
            }
            else if (signal == 0)
                return;

            _signalHistoryByTime[timeKey] = signal;

            if (SearchBack <= 0)
                return;

            int minIndex = barIndex - SearchBack;
            if (minIndex <= 0)
                return;

            var outdated = _signalHistory.Keys.Where(k => k < minIndex).ToList();
            foreach (var key in outdated)
            {
                _signalHistory.Remove(key);
                if (_signalIndexToTime.TryGetValue(key, out long mappedTime))
                {
                    _signalHistoryByTime.Remove(mappedTime);
                    _signalIndexToTime.Remove(key);
                }
            }
        }

        public int GetSignalForBar(int barIndex)
        {
            if (barIndex < 0)
                return 0;

            if (_signalHistory.TryGetValue(barIndex, out int signal))
                return signal;

            if (Bars != null && barIndex < Bars.Count)
                return GetSignalForTime(Bars.OpenTimes[barIndex]);

            return 0;
        }

        public int GetSignalForTime(DateTime barOpenTime)
        {
            long timeKey = barOpenTime.ToBinary();
            return _signalHistoryByTime.TryGetValue(timeKey, out int signal) ? signal : 0;
        }

        public bool TryGetSignal(int barIndex, out int signal)
        {
            signal = 0;
            if (barIndex < 0)
                return false;

            if (_signalHistory.TryGetValue(barIndex, out signal))
                return true;

            if (Bars != null && barIndex < Bars.Count)
            {
                signal = GetSignalForTime(Bars.OpenTimes[barIndex]);
                return signal != 0;
            }

            return false;
        }

        private void ProcessRenkoWicks(int index)
        {
            if (!_renkoInitialized || _renkoWrongTimeFrame)
                return;

            if (_tickBars == null || Bars == null)
                return;

            bool loadOnChart = TickLoadStrategy != LoadTickStrategy.AtStartupSync;
            if (loadOnChart && !_loadingTicksComplete)
                LoadMoreTicksOnChart();

            bool asyncStrategy = TickLoadStrategy == LoadTickStrategy.OnChartEndAsync;
            if (asyncStrategy && !_loadingTicksComplete)
                return;

            if (!IsLastBar)
                DrawOnScreen(string.Empty);

            if (index < 2)
                return;

            double highest = Bars.HighPrices[index];
            double lowest = Bars.LowPrices[index];
            double open = Bars.OpenPrices[index];

            bool isBullish = Bars.ClosePrices[index] > Bars.OpenPrices[index];
            bool prevIsBullish = Bars.ClosePrices[index - 1] > Bars.OpenPrices[index - 1];
            bool priceGap = Bars.OpenTimes[index] == Bars[index - 1].OpenTime || Bars[index - 2].OpenTime == Bars[index - 1].OpenTime;
            DateTime currentOpenTime = Bars.OpenTimes[index];
            DateTime nextOpenTime = index + 1 < Bars.Count ? Bars.OpenTimes[index + 1] : Bars.OpenTimes[index];

            double[] wicks = GetRenkoWicks(index, currentOpenTime, nextOpenTime);
            StoreRenkoWicks(index, wicks);

            if (IsLastBar)
            {
                lowest = wicks[0];
                highest = wicks[1];
                open = Bars.ClosePrices[index - 1];
            }
            else
            {
                if (isBullish)
                    lowest = wicks[0];
                else
                    highest = wicks[1];
            }

            if (isBullish)
            {
                if (lowest < open && !priceGap)
                {
                    if (IsLastBar && !prevIsBullish && Bars.ClosePrices[index] > open)
                        open = Bars.OpenPrices[index];
                    var trendlineUp = Chart.DrawTrendLine($"UpWick_{index}", currentOpenTime, open, currentOpenTime, lowest, _renkoUpColor);
                    trendlineUp.Thickness = WickThickness;
                    Chart.RemoveObject($"DownWick_{index}");
                }
            }
            else
            {
                if (highest > open && !priceGap)
                {
                    if (IsLastBar && prevIsBullish && Bars.ClosePrices[index] < open)
                        open = Bars.OpenPrices[index];
                    var trendlineDown = Chart.DrawTrendLine($"DownWick_{index}", currentOpenTime, open, currentOpenTime, highest, _renkoDownColor);
                    trendlineDown.Thickness = WickThickness;
                    Chart.RemoveObject($"UpWick_{index}");
                }
            }
        }

        public bool TryGetRenkoWickRange(int barIndex, out double wickLow, out double wickHigh)
        {
            wickLow = double.NaN;
            wickHigh = double.NaN;

            if (Bars == null || barIndex < 0 || barIndex >= Bars.Count)
                return false;

            double fallbackLow = Bars.LowPrices[barIndex];
            double fallbackHigh = Bars.HighPrices[barIndex];

            if (_renkoWickCache.TryGetValue(barIndex, out double[] cached) && cached != null && cached.Length >= 2)
            {
                wickLow = cached[0];
                wickHigh = cached[1];
            }
            else
            {
                wickLow = fallbackLow;
                wickHigh = fallbackHigh;
                return false;
            }

            if (wickLow > wickHigh)
            {
                double temp = wickLow;
                wickLow = wickHigh;
                wickHigh = temp;
            }

            return true;
        }

        private void StoreRenkoWicks(int index, double[] wicks)
        {
            if (wicks == null || wicks.Length < 2)
                return;

            _renkoWickCache[index] = new[] { wicks[0], wicks[1] };

            if (_renkoWickCache.Count > 1000)
            {
                int threshold = Math.Max(0, index - SearchBack - 50);
                var obsoleteKeys = _renkoWickCache.Keys.Where(k => k < threshold).ToList();
                foreach (int key in obsoleteKeys)
                    _renkoWickCache.Remove(key);
            }
        }

        private void InitializeRenkoWicks()
        {
            if (Chart == null)
                return;

            _renkoWrongTimeFrame = false;

            string currentTimeframe = Chart.TimeFrame.ToString();
            if (!currentTimeframe.Contains("Renko"))
            {
                DrawOnScreen("Renko Wicks\n funktioniert nur im Renko-Chart!");
                _renkoWrongTimeFrame = true;
                return;
            }

            _tickBars = MarketData.GetBars(TimeFrame.Tick);
            _renkoUpColor = Chart.ColorSettings.BullOutlineColor;
            _renkoDownColor = Chart.ColorSettings.BearOutlineColor;
            _lastTickForWicks = 0;
            _loadingAsyncTicks = false;
            _loadingTicksComplete = TickLoadStrategy == LoadTickStrategy.AtStartupSync;
            _requestAsyncLoad = false;

            if (TickLoadStrategy != LoadTickStrategy.AtStartupSync)
            {
                if (TickLoadStrategy == LoadTickStrategy.OnChartStartSync)
                {
                    var panel = new StackPanel
                    {
                        Width = 200,
                        Orientation = Orientation.Vertical,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    _syncTickProgressBar = new ProgressBar { IsIndeterminate = true, Height = 12 };
                    panel.AddChild(_syncTickProgressBar);
                    Chart.AddControl(panel);
                }

                VolumeInitialize(true);
            }
            else
            {
                VolumeInitialize();
            }

            if (TickLoadStrategy != LoadTickStrategy.AtStartupSync)
            {
                Timer.Start(TimeSpan.FromSeconds(0.5));
                DrawOnScreen("Loading Ticks Data...\n oder\nBerechnung läuft...");
            }
            else
            {
                DrawOnScreen(string.Empty);
            }

            _renkoInitialized = true;
        }

        private double[] GetRenkoWicks(int barIndex, DateTime startTime, DateTime endTime)
        {
            double min = double.MaxValue;
            double max = double.MinValue;

            if (IsLastBar && _tickBars != null && _tickBars.Count > 0)
                endTime = _tickBars.LastBar.OpenTime;

            if (_tickBars == null)
                return new[] { Bars.LowPrices[barIndex], Bars.HighPrices[barIndex] };

            for (int tickIndex = _lastTickForWicks; tickIndex < _tickBars.Count; tickIndex++)
            {
                var tickBar = _tickBars[tickIndex];

                if (tickBar.OpenTime < startTime || tickBar.OpenTime > endTime)
                {
                    if (tickBar.OpenTime > endTime)
                    {
                        _lastTickForWicks = tickIndex;
                        break;
                    }
                    continue;
                }

                if (tickBar.Close < min)
                    min = tickBar.Close;
                if (tickBar.Close > max)
                    max = tickBar.Close;
            }

            if (min == double.MaxValue)
                min = Bars.LowPrices[barIndex];
            if (max == double.MinValue)
                max = Bars.HighPrices[barIndex];

            return new[] { min, max };
        }

        private void VolumeInitialize(bool onlyDate = false)
        {
            if (Bars == null)
                return;

            DateTime lastBarDate = Bars.LastBar.OpenTime.Date;

            if (LoadTickFrom == LoadTickFromData.Custom)
            {
                if (DateTime.TryParseExact(CustomTickDate, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var customDate))
                {
                    if (customDate > lastBarDate)
                        customDate = lastBarDate;
                    _tickLoadFrom = customDate;
                }
                else
                {
                    _tickLoadFrom = lastBarDate;
                    Notifications.ShowPopup(
                        RenkoNotifyCaption,
                        $"Ungültiges Datum '{CustomTickDate}'. Verwende {_tickLoadFrom:dd.MM.yyyy}",
                        PopupNotificationState.Error
                    );
                }
            }
            else
            {
                _tickLoadFrom = LoadTickFrom switch
                {
                    LoadTickFromData.Yesterday => MarketData.GetBars(TimeFrame.Daily).LastBar.OpenTime.Date,
                    LoadTickFromData.BeforeYesterday => MarketData.GetBars(TimeFrame.Daily).Last(1).OpenTime.Date,
                    LoadTickFromData.OneWeek => MarketData.GetBars(TimeFrame.Weekly).LastBar.OpenTime.Date,
                    LoadTickFromData.TwoWeek => MarketData.GetBars(TimeFrame.Weekly).Last(1).OpenTime.Date,
                    LoadTickFromData.Monthly => MarketData.GetBars(TimeFrame.Monthly).LastBar.OpenTime.Date,
                    _ => lastBarDate
                };
            }

            if (onlyDate)
            {
                DrawStartVolumeLine();
                return;
            }

            if (_tickBars == null || !_tickBars.Any())
                return;

            _firstTickTime = _tickBars.OpenTimes.FirstOrDefault();

            if (_firstTickTime >= _tickLoadFrom)
            {
                PopupNotification progressPopup = null;
                bool minimalNotify = TickNotifyMode == LoadTickNotify.Minimal;

                if (minimalNotify)
                {
                    progressPopup = Notifications.ShowPopup(
                        RenkoNotifyCaption,
                        $"[{Symbol.Name}] Lade Tick-Daten synchron...",
                        PopupNotificationState.InProgress
                    );
                }

                while (_tickBars.OpenTimes.FirstOrDefault() > _tickLoadFrom)
                {
                    int loadedCount = _tickBars.LoadMoreHistory();
                    if (TickNotifyMode == LoadTickNotify.Detailed)
                    {
                        Notifications.ShowPopup(
                            RenkoNotifyCaption,
                            $"[{Symbol.Name}] {loadedCount} Ticks geladen. Aktuelles Tick-Datum: {_tickBars.OpenTimes.FirstOrDefault():dd.MM.yyyy HH:mm:ss}",
                            PopupNotificationState.Partial
                        );
                    }

                    if (loadedCount == 0)
                        break;
                }

                if (minimalNotify && progressPopup != null)
                    progressPopup.Complete(PopupNotificationState.Success);
                else if (!minimalNotify)
                {
                    Notifications.ShowPopup(
                        RenkoNotifyCaption,
                        $"[{Symbol.Name}] Tick-Daten synchron geladen.",
                        PopupNotificationState.Success
                    );
                }
            }

            DrawStartVolumeLine();
            _loadingTicksComplete = true;
        }

        private void DrawStartVolumeLine()
        {
            if (Chart == null || _tickBars == null || !_tickBars.Any())
                return;

            try
            {
                DateTime firstTickDate = _tickBars.OpenTimes.FirstOrDefault();
                var line = Chart.DrawVerticalLine("RenkoTickStart", firstTickDate, Color.Red);
                line.LineStyle = LineStyle.Lines;
                int priceIndex = Bars.OpenTimes.GetIndexByTime(firstTickDate);
                double price = priceIndex >= 0 ? Bars.HighPrices[priceIndex] : Bars.HighPrices.LastValue;
                var text = Chart.DrawText(
                    "RenkoTickStartText",
                    "Tickdaten Endpunkt",
                    firstTickDate,
                    price,
                    Color.Red
                );
                text.HorizontalAlignment = HorizontalAlignment.Right;
                text.VerticalAlignment = VerticalAlignment.Top;
                text.FontSize = 8;
            }
            catch
            {
                // ignore drawing issues
            }
        }

        private void DrawFromDateLine()
        {
            if (Chart == null)
                return;

            try
            {
                var line = Chart.DrawVerticalLine("RenkoTickTarget", _tickLoadFrom, Color.Yellow);
                line.LineStyle = LineStyle.Lines;
                int priceIndex = Bars.OpenTimes.GetIndexByTime(_tickLoadFrom);
                double price = priceIndex >= 0 ? Bars.HighPrices[priceIndex] : Bars.HighPrices.LastValue;
                var text = Chart.DrawText(
                    "RenkoTickTargetText",
                    "Tick-Zieldatum",
                    _tickLoadFrom,
                    price,
                    Color.Yellow
                );
                text.HorizontalAlignment = HorizontalAlignment.Left;
                text.VerticalAlignment = VerticalAlignment.Center;
                text.FontSize = 8;
            }
            catch
            {
            }
        }

        private void LoadMoreTicksOnChart()
        {
            if (_tickBars == null)
                return;

            _firstTickTime = _tickBars.OpenTimes.FirstOrDefault();

            if (_firstTickTime > _tickLoadFrom)
            {
                bool minimalNotify = TickNotifyMode == LoadTickNotify.Minimal;
                PopupNotification progressPopup = null;

                if (TickLoadStrategy == LoadTickStrategy.OnChartStartSync)
                {
                    if (minimalNotify)
                    {
                        progressPopup = Notifications.ShowPopup(
                            RenkoNotifyCaption,
                            $"[{Symbol.Name}] Lade Tick-Daten synchron...",
                            PopupNotificationState.InProgress
                        );
                    }

                    while (_tickBars.OpenTimes.FirstOrDefault() > _tickLoadFrom)
                    {
                        int loadedCount = _tickBars.LoadMoreHistory();
                        if (TickNotifyMode == LoadTickNotify.Detailed)
                        {
                            Notifications.ShowPopup(
                                RenkoNotifyCaption,
                                $"[{Symbol.Name}] {loadedCount} Ticks geladen. Aktuelles Tick-Datum: {_tickBars.OpenTimes.FirstOrDefault():dd.MM.yyyy HH:mm:ss}",
                                PopupNotificationState.Partial
                            );
                        }

                        if (loadedCount == 0)
                            break;
                    }

                    if (minimalNotify && progressPopup != null)
                        progressPopup.Complete(PopupNotificationState.Success);
                    else if (!minimalNotify)
                    {
                        Notifications.ShowPopup(
                            RenkoNotifyCaption,
                            $"[{Symbol.Name}] Tick-Daten synchron geladen.",
                            PopupNotificationState.Success
                        );
                    }

                    UnlockChart();
                }
                else
                {
                    if (IsLastBar && !_loadingAsyncTicks)
                    {
                        _requestAsyncLoad = true;
                    }
                }
            }
            else
            {
                UnlockChart();
            }

            void UnlockChart()
            {
                if (_syncTickProgressBar != null)
                {
                    _syncTickProgressBar.IsIndeterminate = false;
                    _syncTickProgressBar.IsVisible = false;
                }
                _syncTickProgressBar = null;
                _loadingTicksComplete = true;
                DrawStartVolumeLine();
            }
        }

        private void DrawOnScreen(string message)
        {
            if (Chart == null)
                return;

            Chart.DrawStaticText("RenkoWicksMsg", message ?? string.Empty, VerticalAlignment.Top, HorizontalAlignment.Center, Color.LightBlue);
        }

        private void RecalculateRenkoWicks()
        {
            if (Bars == null || _tickBars == null)
                return;

            int startIndex = Bars.OpenTimes.GetIndexByTime(_tickBars.OpenTimes.FirstOrDefault());
            if (startIndex < 2)
                startIndex = 2;

            for (int index = startIndex; index < Bars.Count - 1; index++)
            {
                bool isBullish = Bars.ClosePrices[index] > Bars.OpenPrices[index];
                bool priceGap = Bars.OpenTimes[index] == Bars[index - 1].OpenTime || Bars[index - 2].OpenTime == Bars[index - 1].OpenTime;
                DateTime currentOpenTime = Bars.OpenTimes[index];
                DateTime nextOpenTime = index + 1 < Bars.Count ? Bars.OpenTimes[index + 1] : Bars.OpenTimes[index];

                double[] wicks = GetRenkoWicks(index, currentOpenTime, nextOpenTime);
                StoreRenkoWicks(index, wicks);

                if (isBullish)
                {
                    double lowest = wicks[0];
                    if (lowest < Bars.OpenPrices[index] && !priceGap)
                    {
                        var trendlineUp = Chart.DrawTrendLine($"UpWick_{index}", currentOpenTime, Bars.OpenPrices[index], currentOpenTime, lowest, _renkoUpColor);
                        trendlineUp.Thickness = WickThickness;
                        Chart.RemoveObject($"DownWick_{index}");
                    }
                }
                else
                {
                    double highest = wicks[1];
                    if (highest > Bars.OpenPrices[index] && !priceGap)
                    {
                        var trendlineDown = Chart.DrawTrendLine($"DownWick_{index}", currentOpenTime, Bars.OpenPrices[index], currentOpenTime, highest, _renkoDownColor);
                        trendlineDown.Thickness = WickThickness;
                        Chart.RemoveObject($"UpWick_{index}");
                    }
                }
            }
        }

        protected override void OnTimer()
        {
            if (!_renkoInitialized || !_requestAsyncLoad)
                return;

            if (!_loadingAsyncTicks)
            {
                string volumeHint = "=> Bitte herauszoomen und der gelben Linie folgen";
                _asyncTickPopup = Notifications.ShowPopup(
                    RenkoNotifyCaption,
                    $"[{Symbol.Name}] Lade Tick-Daten asynchron alle 0,5 Sekunden...\n{volumeHint}",
                    PopupNotificationState.InProgress
                );
                DrawFromDateLine();
            }

            if (!_loadingTicksComplete)
            {
                _tickBars.LoadMoreHistoryAsync(result =>
                {
                    if (result?.Bars == null || result.Bars.Count == 0)
                        return;

                    DrawStartVolumeLine();

                    DateTime currentDate = result.Bars.First().OpenTime;
                    if (currentDate <= _tickLoadFrom)
                    {
                        if (_asyncTickPopup != null && _asyncTickPopup.State != PopupNotificationState.Success)
                            _asyncTickPopup.Complete(PopupNotificationState.Success);

                        if (TickNotifyMode == LoadTickNotify.Detailed)
                        {
                            Notifications.ShowPopup(
                                RenkoNotifyCaption,
                                $"[{Symbol.Name}] Asynchrones Laden beendet.",
                                PopupNotificationState.Success
                            );
                        }

                        _loadingTicksComplete = true;
                    }
                });

                _loadingAsyncTicks = true;
            }
            else
            {
                Timer.Stop();
                DrawOnScreen(string.Empty);
                RecalculateRenkoWicks();
                _requestAsyncLoad = false;
                _loadingAsyncTicks = false;
            }
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
            _signalHistory.Clear();
            _signalHistoryByTime.Clear();
            _signalIndexToTime.Clear();
            _renkoWickCache.Clear();
            LastSignalBarIndex = -1;
            LastSignalDirection = 0;
            LastSignalTime = DateTime.MinValue;
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
