using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None, AddIndicators = true)]
    public class rsitrendlineIndicator : Robot
    {
        [Parameter("Volume (Units)", DefaultValue = 10000)]
        public int VolumeInUnits { get; set; }

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

        [Parameter("Renko Wicks aktivieren", DefaultValue = false)]
        public bool EnableRenkoWicks { get; set; }

        [Parameter("Ticks laden ab", DefaultValue = cAlgo.RsiTrendlinesAuto.LoadTickFromData.Today)]
        public cAlgo.RsiTrendlinesAuto.LoadTickFromData LoadTickFrom { get; set; }

        [Parameter("Lade-Strategie", DefaultValue = cAlgo.RsiTrendlinesAuto.LoadTickStrategy.OnChartEndAsync)]
        public cAlgo.RsiTrendlinesAuto.LoadTickStrategy TickLoadStrategy { get; set; }

        [Parameter("Benutzerdefiniertes Datum (dd/MM/yyyy)", DefaultValue = "00/00/0000")]
        public string CustomTickDate { get; set; }

        [Parameter("Benachrichtigungen", DefaultValue = cAlgo.RsiTrendlinesAuto.LoadTickNotify.Minimal)]
        public cAlgo.RsiTrendlinesAuto.LoadTickNotify TickNotifyMode { get; set; }

        [Parameter("Wick-Linienstärke", DefaultValue = 1, MinValue = 1, MaxValue = 5)]
        public int WickThickness { get; set; }

        private cAlgo.RsiTrendlinesAuto _rsiTl;
        private string _label;
        private DateTime _lastHandledSignalTime = DateTime.MinValue;
        private int _lastHandledSignalBar = -1;

        protected override void OnStart()
        {
            try
            {
                _rsiTl = Indicators.GetIndicator<cAlgo.RsiTrendlinesAuto>(
                    Period,
                    Swing,
                    SearchBack,
                    ExtendBars,
                    ShowPivotMarkers,
                    ShowSignalMarkers,
                    EnableRenkoWicks,
                    LoadTickFrom,
                    TickLoadStrategy,
                    CustomTickDate,
                    TickNotifyMode,
                    WickThickness
                );

                if (_rsiTl == null)
                    Print("RSI Trendline Indicator konnte nicht geladen werden.");
            }
            catch (Exception ex)
            {
                Print($"Fehler bei Indicator-Initialisierung: {ex.Message}");
            }

            _label = $"RSI_TL_BOT_{SymbolName}_{TimeFrame}";
        }

        protected override void OnBar()
        {
            if (_rsiTl == null)
            {
                Print("OnBar: Indikator nicht geladen.");
                return;
            }

            int lastClosed = Bars.Count - 2;
            if (lastClosed < 0)
            {
                Print("OnBar: Keine abgeschlossene Kerze verfügbar.");
                return;
            }

            int signalBar = lastClosed;
            DateTime signalTime = Bars.OpenTimes[lastClosed];
            int signal = _rsiTl.GetSignalForTime(signalTime);

            if (signal == 0)
            {
                signalBar = _rsiTl.LastSignalBarIndex;
                signalTime = _rsiTl.LastSignalTime;
                signal = _rsiTl.LastSignalDirection;
            }

            if (signal == 0 || signalBar < 0)
            {
                Print($"OnBar: Signal 0 auf Bar {lastClosed}");
                return;
            }

            if (signalTime <= _lastHandledSignalTime && signalBar == _lastHandledSignalBar)
            {
                Print($"OnBar: Signal {signal} auf Bar {signalBar} bereits verarbeitet");
                return;
            }

            Print($"OnBar: Signal {signal} auf Bar {signalBar}");

            _lastHandledSignalTime = signalTime;
            _lastHandledSignalBar = signalBar;

            var tradeType = signal == 1 ? TradeType.Buy : TradeType.Sell;
            var result = ExecuteMarketOrder(tradeType, SymbolName, VolumeInUnits, _label, 5, 5);

            if (result != null && !result.IsSuccessful && result.Error.HasValue)
                Print($"Order error: {result.Error.Value}");
        }

        protected override void OnStop()
        {
            // Handle cBot stop here
        }
    }
}
