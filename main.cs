using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class AmdBot : Robot
    {
        [Parameter("Symbol", DefaultValue = "BTCUSD")]
        public new string SymbolName { get; set; }

        [Parameter("Main TimeFrame (for AMD setup)", DefaultValue = "Hour1")]
        public TimeFrame MainTimeFrame { get; set; }

        [Parameter("Risk % per Trade", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercent { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 1, MinValue = 1)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("EMA Fast Period", DefaultValue = 20)]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Slow Period", DefaultValue = 50)]
        public int EmaSlowPeriod { get; set; }

        // Parameters for AMD Setup Detection
        [Parameter("AMD: Accumulation Lookback (Bars)", DefaultValue = 24, MinValue = 5, MaxValue = 100)] // e.g., 24 bars for H1 = 1 day
        public int AmdAccumulationLookback { get; set; }

        [Parameter("AMD: Accumulation Max Range (Pips)", DefaultValue = 150, MinValue = 20, MaxValue = 1000)] // Adjust for BTCUSD volatility
        public double AmdAccumulationMaxRangePips { get; set; }

        [Parameter("AMD: Manipulation Min Breakout (Pips)", DefaultValue = 30, MinValue = 5, MaxValue = 500)] // Min distance of sweep
        public double AmdManipulationMinBreakoutPips { get; set; }

        [Parameter("AMD: Manipulation Wick % Rejection", DefaultValue = 50, MinValue = 10, MaxValue = 100)] // Min % of manipulation candle body/wick showing rejection
        public double AmdManipulationRejectionPercent { get; set; }

        // Context TimeFrames
        private readonly TimeFrame _tf1w = TimeFrame.Weekly;
        private readonly TimeFrame _tf1d = TimeFrame.Daily;
        private readonly TimeFrame _tf4h = TimeFrame.Hour4;
        private TimeFrame _tf1h; // Will be set from MainTimeFrame

        // Bars for each context timeframe
        private Bars _bars1w, _bars1d, _bars4h, _bars1h, _barsMain;

        // EMAs for each context timeframe
        private ExponentialMovingAverage _emaFast1w, _emaSlow1w;
        private ExponentialMovingAverage _emaFast1d, _emaSlow1d;
        private ExponentialMovingAverage _emaFast4h, _emaSlow4h;
        private ExponentialMovingAverage _emaFast1h, _emaSlow1h;

        private int _tradesToday = 0;
        private DateTime _lastTradeDate = DateTime.MinValue;

        // AMD State Variables
        private double _accumulationHigh = 0;
        private double _accumulationLow = 0;
        private DateTime _accumulationStartTime = DateTime.MinValue;
        private DateTime _accumulationEndTime = DateTime.MinValue;
        private double _manipulationExtreme = 0; // Tracks the peak/trough of the manipulation
        private DateTime _manipulationTime = DateTime.MinValue;

        protected override void OnStart()
        {
            Print("Starting AMD Bot for {0}. Main AMD setup TimeFrame: {1}. Context TFs: W, D, H4, H1.", SymbolName, MainTimeFrame);

            _tf1h = MainTimeFrame; // Strategy specifies H1 for AMD setup and entry models
            if (MainTimeFrame != TimeFrame.Hour)
            {
                Print("Warning: The AMD strategy entry models are typically analyzed on H1. Current MainTimeFrame is {0}. Context will still use H1.", MainTimeFrame);
                _tf1h = TimeFrame.Hour; // Ensure _tf1h is H1 for context analysis as per requirement
            }

            // Initialize Bars
            _barsMain = MarketData.GetBars(MainTimeFrame, SymbolName); // For AMD setup detection and entries
            _bars1h = MarketData.GetBars(_tf1h, SymbolName);
            _bars4h = MarketData.GetBars(_tf4h, SymbolName);
            _bars1d = MarketData.GetBars(_tf1d, SymbolName);
            _bars1w = MarketData.GetBars(_tf1w, SymbolName);

            // Initialize EMAs for context
            _emaFast1h = Indicators.ExponentialMovingAverage(_bars1h.ClosePrices, EmaFastPeriod);
            _emaSlow1h = Indicators.ExponentialMovingAverage(_bars1h.ClosePrices, EmaSlowPeriod);

            _emaFast4h = Indicators.ExponentialMovingAverage(_bars4h.ClosePrices, EmaFastPeriod);
            _emaSlow4h = Indicators.ExponentialMovingAverage(_bars4h.ClosePrices, EmaSlowPeriod);

            _emaFast1d = Indicators.ExponentialMovingAverage(_bars1d.ClosePrices, EmaFastPeriod);
            _emaSlow1d = Indicators.ExponentialMovingAverage(_bars1d.ClosePrices, EmaSlowPeriod);

            _emaFast1w = Indicators.ExponentialMovingAverage(_bars1w.ClosePrices, EmaFastPeriod);
            _emaSlow1w = Indicators.ExponentialMovingAverage(_bars1w.ClosePrices, EmaSlowPeriod);

            // Subscribe to new bar event for MainTimeFrame to trigger AMD check once per bar
            _barsMain.BarOpened += OnMainBarOpened; 
        }

        private void OnMainBarOpened(BarOpenedEventArgs obj) // Renamed from obj to args for clarity
        {
            // This method will be called when a new bar opens on _barsMain (H1)
            // Perform AMD check here instead of OnTick for efficiency if logic is complex
            // For now, OnTick logic remains, but this is a good place for bar-based checks.
            Print("New bar opened on {0} for {1}. Time: {2}", _barsMain.TimeFrame, SymbolName, obj.Bars.OpenTimes.LastValue);

            // Reset daily trade counter if it's a new day
            if (Server.Time.Date != _lastTradeDate.Date)
            {
                _tradesToday = 0;
                _lastTradeDate = Server.Time;
                Print("New trading day ({0}). Trade counter reset.", Server.Time.Date.ToShortDateString());
            }

            if (_tradesToday >= MaxTradesPerDay)
            {
                Print("Max trades for the day reached. Waiting for next day.");
                return;
            }

            string marketContext = GetMarketContext();
            Print("Current Market Context: {0} (checked on new H1 bar)", marketContext);

            if (marketContext == "Neutral")
            {
                Print("Market context is Neutral. Waiting for clearer conditions.");
                return;
            }

            // Reset AMD state before checking for a new setup
            ResetAmdState();

            bool amdSetupDetected = CheckAmdSetup(marketContext);

            if (amdSetupDetected)
            {
                Print("AMD Setup DETECTED on {0} in {1} context! Proceeding to check entry models.", MainTimeFrame, marketContext);
                ExecuteTradeBasedOnEntryModel(marketContext); 
            }
            else
            {
                Print("No AMD Setup detected on {0} in {1} context on new bar.", MainTimeFrame, marketContext);
            }
        }

        protected override void OnTick()
        {
            // OnTick can be used for faster updates if needed, 
            // but primary logic moved to OnMainBarOpened for AMD check once per H1 bar.
            // For example, managing open positions or trailing stops could happen here.
        }

        private enum TrendDirection { Bullish, Bearish, Neutral }

        private TrendDirection GetTrendOnTimeframe(Bars bars, ExponentialMovingAverage emaFast, ExponentialMovingAverage emaSlow, string tfName)
        {
            double price = bars.ClosePrices.Last(1); // Use Last(1) to get previous closed candle for more stability
            double fast = emaFast.Result.Last(1);
            double slow = emaSlow.Result.Last(1);

            if (double.IsNaN(price) || double.IsNaN(fast) || double.IsNaN(slow))
            {
                Print("Warning: EMA data not ready for {0} on {1}", SymbolName, tfName);
                return TrendDirection.Neutral; // Not enough data yet
            }

            if (price > fast && fast > slow) return TrendDirection.Bullish;
            if (price < fast && fast < slow) return TrendDirection.Bearish;
            return TrendDirection.Neutral;
        }

        private string GetMarketContext()
        {
            TrendDirection trend1h = GetTrendOnTimeframe(_bars1h, _emaFast1h, _emaSlow1h, "H1");
            TrendDirection trend4h = GetTrendOnTimeframe(_bars4h, _emaFast4h, _emaSlow4h, "H4");
            TrendDirection trend1d = GetTrendOnTimeframe(_bars1d, _emaFast1d, _emaSlow1d, "D1");
            TrendDirection trend1w = GetTrendOnTimeframe(_bars1w, _emaFast1w, _emaSlow1w, "W1");

            // Simple scoring: Bullish = +1, Bearish = -1, Neutral = 0
            // Weights: H1=1, H4=2, D1=3, W1=4 (higher TFs have more weight)
            int score = 0;
            score += (trend1h == TrendDirection.Bullish ? 1 : (trend1h == TrendDirection.Bearish ? -1 : 0)) * 1;
            score += (trend4h == TrendDirection.Bullish ? 1 : (trend4h == TrendDirection.Bearish ? -1 : 0)) * 2;
            score += (trend1d == TrendDirection.Bullish ? 1 : (trend1d == TrendDirection.Bearish ? -1 : 0)) * 3;
            score += (trend1w == TrendDirection.Bullish ? 1 : (trend1w == TrendDirection.Bearish ? -1 : 0)) * 4;

            // Print individual TF trends for debugging
            // Print("Trends: W1={0}, D1={1}, H4={2}, H1={3}. Score: {4}", trend1w, trend1d, trend4h, trend1h, score);

            // Define thresholds for overall context based on score
            // Max possible score: 1*1 + 1*2 + 1*3 + 1*4 = 10
            // Min possible score: -10
            if (score >= 5) return "Bullish"; // Strong majority bullish
            if (score <= -5) return "Bearish"; // Strong majority bearish
            
            // More nuanced: if H4, D1, W1 align, that's a strong signal regardless of H1 for overall context.
            // For AMD, we might want H1 to align with HTF or be showing initial signs of turning.
            
            // Example of stronger alignment check:
            if (trend4h == TrendDirection.Bullish && trend1d == TrendDirection.Bullish && trend1w == TrendDirection.Bullish)
                return "Bullish";
            if (trend4h == TrendDirection.Bearish && trend1d == TrendDirection.Bearish && trend1w == TrendDirection.Bearish)
                return "Bearish";

            // If score is not decisive and HTFs don't strongly align, consider it neutral or wait.
            // For the AMD setup, we need a clear context from HTFs.
            // If D1 and W1 are bullish, and 4H is bullish, we primarily look for bullish AMD.
            // If D1 and W1 are bearish, and 4H is bearish, we primarily look for bearish AMD.

            if (trend1d == TrendDirection.Bullish && trend1w == TrendDirection.Bullish && trend4h != TrendDirection.Bearish) // Allow 4H to be neutral or bullish
                return "Bullish";
            if (trend1d == TrendDirection.Bearish && trend1w == TrendDirection.Bearish && trend4h != TrendDirection.Bullish) // Allow 4H to be neutral or bearish
                return "Bearish";

            return "Neutral";
        }

        private void ResetAmdState()
        {
            _accumulationHigh = 0;
            _accumulationLow = 0;
            _accumulationStartTime = DateTime.MinValue;
            _accumulationEndTime = DateTime.MinValue;
            _manipulationExtreme = 0;
            _manipulationTime = DateTime.MinValue;
            Print("AMD state has been reset.");
        }

        private bool CheckAmdSetup(string marketContext)
        {
            Print("Checking for AMD setup on {0} ({1} bars lookback) in {2} context...", MainTimeFrame, AmdAccumulationLookback, marketContext);
            
            if (_barsMain.Count < AmdAccumulationLookback + 5) 
            {
                Print("Not enough bars on {0} to check for AMD setup. Bars available: {1}", MainTimeFrame, _barsMain.Count);
                return false;
            }

            // --- 1. Identify Accumulation Phase ---
            int accumulationAnalysisEndIndex = _barsMain.Count - 2; 
            int accumulationAnalysisStartIndex = Math.Max(0, accumulationAnalysisEndIndex - AmdAccumulationLookback + 1);

            if (accumulationAnalysisEndIndex - accumulationAnalysisStartIndex + 1 < AmdAccumulationLookback / 2) 
            {
                Print("Not enough bars for accumulation analysis window.");
                return false;
            }

            double tempAccumulationHigh = double.MinValue;
            double tempAccumulationLow = double.MaxValue;
            for (int k = accumulationAnalysisStartIndex; k <= accumulationAnalysisEndIndex; k++)
            {
                if (_barsMain.HighPrices[k] > tempAccumulationHigh)
                    tempAccumulationHigh = _barsMain.HighPrices[k];
                if (_barsMain.LowPrices[k] < tempAccumulationLow)
                    tempAccumulationLow = _barsMain.LowPrices[k];
            }

            double accumulationRangePips = (tempAccumulationHigh - tempAccumulationLow) / Symbol.PipSize;

            if (accumulationRangePips <= AmdAccumulationMaxRangePips && accumulationRangePips > 0)
            {
                _accumulationHigh = tempAccumulationHigh;
                _accumulationLow = tempAccumulationLow;
                _accumulationStartTime = _barsMain.OpenTimes[accumulationAnalysisStartIndex];
                _accumulationEndTime = _barsMain.OpenTimes[accumulationAnalysisEndIndex]; 
                Print("Accumulation Phase Identified: High={0}, Low={1}, Range={2} pips. From {3} to {4} ({5} bars)",
                        _accumulationHigh, _accumulationLow, Math.Round(accumulationRangePips, 2),
                        _accumulationStartTime, _accumulationEndTime, accumulationAnalysisEndIndex - accumulationAnalysisStartIndex + 1);
            }
            else
            {
                Print("No clear accumulation phase found. Calculated range: {0} pips. Max allowed: {1} pips",
                        Math.Round(accumulationRangePips, 2), AmdAccumulationMaxRangePips);
                return false;
            }

            // --- 2. Identify Manipulation Phase ---
            int manipulationCheckStartIndex = accumulationAnalysisEndIndex + 1; 
            int manipulationCheckEndIndex = _barsMain.Count - 2; 

            if (manipulationCheckStartIndex > manipulationCheckEndIndex)
            {
                Print("Not enough subsequent bars to check for manipulation after accumulation period ending at {0}", _accumulationEndTime);
                return false;
            }

            bool manipulationFound = false;
            double minBreakoutDistance = AmdManipulationMinBreakoutPips * Symbol.PipSize;

            for (int i = manipulationCheckStartIndex; i <= manipulationCheckEndIndex; i++)
            {
                Bar currentBar = new Bar { Open = _barsMain.OpenPrices[i], High = _barsMain.HighPrices[i], Low = _barsMain.LowPrices[i], Close = _barsMain.ClosePrices[i], OpenTime = _barsMain.OpenTimes[i] };
                
                if (marketContext == "Bullish")
                {
                    if (currentBar.Low < _accumulationLow && (_accumulationLow - currentBar.Low) >= minBreakoutDistance)
                    {
                        Print("Potential bullish manipulation: Bar at {0} went below AccLow ({1}) to {2}. Required breakout: {3} pips.",
                                currentBar.OpenTime, _accumulationLow, currentBar.Low, AmdManipulationMinBreakoutPips);
                        double wickSize = currentBar.Close - currentBar.Low;
                        double candleRange = currentBar.High - currentBar.Low;
                        if (candleRange == 0) continue; 
                        double rejectionPercent = (wickSize / candleRange) * 100;

                        if (currentBar.Close > currentBar.Low && rejectionPercent >= AmdManipulationRejectionPercent) 
                        {
                            manipulationFound = true;
                            _manipulationExtreme = currentBar.Low;
                            _manipulationTime = currentBar.OpenTime;
                            Print("BULLISH Manipulation CONFIRMED at {0}. Extreme: {1}. Rejection: {2}% (min {3}%).",
                                    _manipulationTime, _manipulationExtreme, Math.Round(rejectionPercent, 1), AmdManipulationRejectionPercent);
                            break;
                        }
                        else
                        {
                            Print("Manipulation sweep detected at {0} (Low: {1}), but rejection criteria not met (Close: {2}, Rejection: {3}%).",
                               currentBar.OpenTime, currentBar.Low, currentBar.Close, Math.Round(rejectionPercent, 1));
                        }
                    }
                }
                else if (marketContext == "Bearish")
                {
                    if (currentBar.High > _accumulationHigh && (currentBar.High - _accumulationHigh) >= minBreakoutDistance)
                    {
                        Print("Potential bearish manipulation: Bar at {0} went above AccHigh ({1}) to {2}. Required breakout: {3} pips.",
                               currentBar.OpenTime, _accumulationHigh, currentBar.High, AmdManipulationMinBreakoutPips);

                        double wickSize = currentBar.High - currentBar.Close;
                        double candleRange = currentBar.High - currentBar.Low;
                        if (candleRange == 0) continue;
                        double rejectionPercent = (wickSize / candleRange) * 100;

                        if (currentBar.Close < currentBar.High && rejectionPercent >= AmdManipulationRejectionPercent)
                        {
                            manipulationFound = true;
                            _manipulationExtreme = currentBar.High;
                            _manipulationTime = currentBar.OpenTime;
                            Print("BEARISH Manipulation CONFIRMED at {0}. Extreme: {1}. Rejection: {2}% (min {3}%).",
                                   _manipulationTime, _manipulationExtreme, Math.Round(rejectionPercent, 1), AmdManipulationRejectionPercent);
                            break;
                        }
                        else
                        {
                            Print("Manipulation sweep detected at {0} (High: {1}), but rejection criteria not met (Close: {2}, Rejection: {3}%).",
                               currentBar.OpenTime, currentBar.High, currentBar.Close, Math.Round(rejectionPercent, 1));
                        }
                    }
                }
            }

            if (manipulationFound)
            {
                return true; 
            }
            else
            {
                Print("Manipulation phase not clearly identified after accumulation ending {0}.", _accumulationEndTime);
                return false;
            }
        }

        private struct Bar // Helper struct for easier candle analysis
        {
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
            public DateTime OpenTime { get; set; }
        }

        private void ExecuteTradeBasedOnEntryModel(string marketContext)
        {
            Print("Attempting to execute trade based on entry model in {0} context.", marketContext);

            if (_tradesToday >= MaxTradesPerDay)
            {
                Print("Max trades for today already reached.");
                return;
            }

            double actualStopLossPips = 0;
            double actualTakeProfitPips = 0;
            TradeType tradeTypeToExecute = TradeType.Buy; // Default, will be set
            double entryPrice;
            double stopLossPrice;

            if (_manipulationExtreme == 0) 
            {
                Print("Error: Manipulation extreme not set. Cannot calculate SL for entry.");
                return;
            }
            
            entryPrice = _barsMain.ClosePrices[_barsMain.Count - 2]; 

            if (marketContext == "Bullish")
            {
                tradeTypeToExecute = TradeType.Buy;
                stopLossPrice = _manipulationExtreme - Symbol.PipSize * 2; 
                actualStopLossPips = (entryPrice - stopLossPrice) / Symbol.PipSize;
                if (actualStopLossPips <= 0) { Print("Invalid SL pips ({0}) for Bullish entry. Entry: {1}, SL Price: {2}", actualStopLossPips, entryPrice, stopLossPrice); return; }
                actualTakeProfitPips = actualStopLossPips * 2; 
                Print("Bullish Entry Setup: Entry Price (approx): {0}, SL Price: {1} ({2} pips), TP target: {3} pips",
                   entryPrice, stopLossPrice, Math.Round(actualStopLossPips,2), Math.Round(actualTakeProfitPips,2));
            }
            else if (marketContext == "Bearish") 
            {
                tradeTypeToExecute = TradeType.Sell;
                stopLossPrice = _manipulationExtreme + Symbol.PipSize * 2; 
                actualStopLossPips = (stopLossPrice - entryPrice) / Symbol.PipSize;
                if (actualStopLossPips <= 0) { Print("Invalid SL pips ({0}) for Bearish entry. Entry: {1}, SL Price: {2}", actualStopLossPips, entryPrice, stopLossPrice); return; }
                actualTakeProfitPips = actualStopLossPips * 2; 
                Print("Bearish Entry Setup: Entry Price (approx): {0}, SL Price: {1} ({2} pips), TP target: {3} pips",
                    entryPrice, stopLossPrice, Math.Round(actualStopLossPips,2), Math.Round(actualTakeProfitPips,2));
            }
            else
            {
                Print("Market context is neither Bullish nor Bearish, cannot determine trade type for execution.");
                return; // Should not happen if OnMainBarOpened filters Neutral context
            }

            double volumeInUnits = CalculateVolume(actualStopLossPips);
            if (volumeInUnits <= 0)
            {
                Print("Volume calculation failed or resulted in zero.");
                return;
            }

            Print("Attempting to place {0} order. Volume: {1}, SL: {2} pips, TP: {3} pips",
                tradeTypeToExecute, volumeInUnits, Math.Round(actualStopLossPips,2), Math.Round(actualTakeProfitPips,2));
            var result = ExecuteMarketOrder(tradeTypeToExecute, SymbolName, volumeInUnits, "AmdBotEntry_" + tradeTypeToExecute, actualStopLossPips, actualTakeProfitPips);
            if (result.IsSuccessful)
            {
                Print("{0} Trade opened successfully. Position ID: {1}", tradeTypeToExecute, result.Position.Id);
                _tradesToday++;
                _lastTradeDate = Server.Time; 
            }
            else
            {
                Print("Error opening {0} trade: {1}", tradeTypeToExecute, result.Error);
            }
        }
        
        private double CalculateVolume(double stopLossPips)
        {
            if (stopLossPips <= 0)
            {
                Print("Stop Loss pips must be greater than 0 to calculate volume.");
                return 0;
            }

            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            
            if (Symbol.LotSize == 0) 
            {
                Print("Error: Symbol.LotSize is zero. Cannot calculate pip value per unit.");
                return 0;
            }
            double pipValuePerUnit = Symbol.PipValue / Symbol.LotSize;

            if (pipValuePerUnit <= 0 || stopLossPips <= 0) 
            {
                Print("Error: Pip value per unit ({0}) or Stop Loss pips ({1}) is invalid for volume calculation.", pipValuePerUnit, stopLossPips);
                return 0;
            }

            double quantityInUnits = riskAmount / (stopLossPips * pipValuePerUnit);
            double volumeInUnits = Symbol.NormalizeVolumeInUnits(quantityInUnits, RoundingMode.ToNearest);

            Print("Account Balance: {0}, Risk %: {1}, Risk Amount: {2}", Account.Balance, RiskPercent, riskAmount);
            Print("SL Pips: {0}, Symbol PipValue: {1}, Symbol LotSize: {2}, PipValuePerUnit: {3}",
                stopLossPips, Symbol.PipValue, Symbol.LotSize, pipValuePerUnit);
            Print("Calculated Quantity (units): {0}, Normalized Volume (units): {1}", quantityInUnits, volumeInUnits);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print("Calculated volume ({0}) is less than minimum ({1}). Adjusting to minimum.", volumeInUnits, Symbol.VolumeInUnitsMin);
                volumeInUnits = Symbol.VolumeInUnitsMin;
            }
            else if (volumeInUnits > Symbol.VolumeInUnitsMax)
            {
                Print("Calculated volume ({0}) is greater than maximum ({1}). Adjusting to maximum.", volumeInUnits, Symbol.VolumeInUnitsMax);
                volumeInUnits = Symbol.VolumeInUnitsMax;
            }

            double actualRiskAmount = stopLossPips * pipValuePerUnit * volumeInUnits;
            Print("Final Volume: {0} units. Estimated Risk Amount for this volume: {1} {2}",
                    volumeInUnits, actualRiskAmount, Account.Asset.Name);

            if (volumeInUnits == Symbol.VolumeInUnitsMin && actualRiskAmount > riskAmount * 1.2) 
            {
                Print("Warning: Minimum volume still results in risk ({0} {1}) significantly higher than desired ({2} {3}). Trade not advised.",
                    actualRiskAmount, Account.Asset.Name, riskAmount, Account.Asset.Name);
            }

            return volumeInUnits;
        }

        protected override void OnStop()
        {
            Print("AMD Bot stopped.");
        }
    }
}
