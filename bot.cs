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

        private cAlgo.RsiTrendlinesAuto _rsiTl;
        private string _label;

        protected override void OnStart()
        {
            _rsiTl = Chart.Indicators.Add<cAlgo.RsiTrendlinesAuto>(
                Period,
                Swing,
                SearchBack,
                ExtendBars,
                ShowPivotMarkers,
                ShowSignalMarkers
            );
            _label = $"RSI_TL_BOT_{SymbolName}_{TimeFrame}";
        }

        protected override void OnBar()
        {
            int lastClosed = Bars.Count - 2;
            if (lastClosed < 1)
                return;

            int signal = _rsiTl.SignalAtLastClose;

            if (signal == 1)
            {
                ExecuteMarketOrder(TradeType.Buy, SymbolName, VolumeInUnits, _label);
            }
            else if (signal == -1)
            {
                ExecuteMarketOrder(TradeType.Sell, SymbolName, VolumeInUnits, _label);
            }
        }

        protected override void OnStop()
        {
            // Handle cBot stop here
        }
    }
}
