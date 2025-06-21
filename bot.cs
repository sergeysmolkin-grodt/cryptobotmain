using cAlgo.API;
using cAlgo.API.Indicators;
using System;

namespace cAlgo
{
    // This cBot enters at most one intraday trade per day based on CCI crossings.
    // Risk per trade is fixed as a % of account balance, while SL & TP are fixed in pips.
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class IntradayCciBot : Robot
    {
        [Parameter("CCI Period", DefaultValue = 20)]
        public int CciPeriod { get; set; }

        [Parameter("CCI Upper Threshold", DefaultValue = 100)]
        public double CciUpper { get; set; }

        [Parameter("CCI Lower Threshold", DefaultValue = -100)]
        public double CciLower { get; set; }

        [Parameter("Risk Per Trade (%)", DefaultValue = 1.0)]
        public double RiskPercent { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 30)]
        public int StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 60)]
        public int TakeProfitPips { get; set; }

        [Parameter("Trading Time Frame", DefaultValue = "Minute15")]
        public TimeFrame TradingTimeFrame { get; set; }

        private Bars _tradeBars;
        private CommodityChannelIndex _cci;
        private DateTime _lastTradeDate;
        private const string Label = "IntradayCCI";

        protected override void OnStart()
        {
            _tradeBars = MarketData.GetBars(TradingTimeFrame);
            _cci = Indicators.CommodityChannelIndex(_tradeBars.ClosePrices, CciPeriod);
            _tradeBars.BarClosed += OnBarClosed;
            _lastTradeDate = DateTime.MinValue.Date;
        }

        private void OnBarClosed(BarClosedEventArgs args)
        {
            // Ensure we have enough bars for indicator
            if (_tradeBars.Count < CciPeriod + 2)
                return;

            // Skip if we already traded today
            if (_lastTradeDate == Server.Time.Date)
                return;

            // Skip if an open position with our label exists
            if (Positions.Find(Label) != null)
                return;

            double cciPrev = _cci.Result.Last(1);
            double cciCurr = _cci.Result.LastValue;

            if (cciPrev < CciLower && cciCurr > CciLower)
            {
                ExecuteTrade(TradeType.Buy);
            }
            else if (cciPrev > CciUpper && cciCurr < CciUpper)
            {
                ExecuteTrade(TradeType.Sell);
            }
        }

        private void ExecuteTrade(TradeType tradeType)
        {
            long volume = CalculateVolumeInUnits();
            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("Calculated volume {0} is less than minimum allowed {1}. Trade skipped.", volume, Symbol.VolumeInUnitsMin);
                return;
            }

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label, StopLossPips, TakeProfitPips);
            if (result.IsSuccessful)
            {
                _lastTradeDate = Server.Time.Date;
                Print("{0} order executed. Volume: {1} SL: {2} TP: {3}", tradeType, volume, StopLossPips, TakeProfitPips);
            }
            else
            {
                Print("Trade failed: {0}", result.Error);
            }
        }

        private long CalculateVolumeInUnits()
        {
            double riskAmount = Account.Balance * (RiskPercent / 100.0);

            // Value of 1 pip for 1 unit of the symbol
            double pipValuePerUnit = Symbol.PipValue / Symbol.LotSize;

            // Units required so that SL * pipValuePerUnit equals riskAmount
            double unitsRaw = riskAmount / (StopLossPips * pipValuePerUnit);

            long units = Symbol.NormalizeVolumeInUnits((long)Math.Round(unitsRaw), RoundingMode.ToNearest);
            return units;
        }

        [Obsolete] // The base method is obsolete but still required for compatibility
        protected override void OnPositionClosed(Position position)
        {
            if (position.Label != Label)
                return;

            Print("Position {0} closed with P/L: {1}", position.Id, position.GrossProfit);
        }
    }
}
