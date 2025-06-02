using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
// using cAlgo.API.Indicators; // Removed as we assume custom ZigZag
using cAlgo.API.Internals;
using cAlgo.Indicators; // This should cover custom indicators in the default cAlgo.Indicators namespace

namespace cAlgo.Robots
{
    // Helper class to store ZigZag point details
    public class ZigZagPoint
    {
        public double Price { get; }
        public DateTime Time { get; }
        public int Index { get; }
        public bool IsHigh { get; }

        public ZigZagPoint(double price, DateTime time, int index, bool isHigh)
        {
            Price = price;
            Time = time;
            Index = index;
            IsHigh = isHigh;
        }

        public override string ToString()
        {
            return $"{(IsHigh ? "H" : "L")}: {Price} @ {Time} (Idx: {Index})";
        }
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MarketStructureBot : Robot
    {
        [Parameter("Risk per Trade (%)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercentage { get; set; }

        // Structure ZigZag Parameters
        [Parameter("Structure ZigZag Depth", DefaultValue = 12, MinValue = 1)]
        public int StructureZigZagDepth { get; set; }
        [Parameter("Structure ZigZag Deviation", DefaultValue = 5, MinValue = 1)]
        public int StructureZigZagDeviation { get; set; }
        [Parameter("Structure ZigZag Backstep", DefaultValue = 3, MinValue = 0)]
        public int StructureZigZagBackstep { get; set; }

        // H1 ZigZag Parameters (for SL/TP)
        [Parameter("H1 ZigZag Mode", DefaultValue = ZigZag.ModeZigZag.HighLow, Group = "H1 ZigZag")]
        public ZigZag.ModeZigZag H1ZigZagMode { get; set; }
        [Parameter("H1 ZigZag Depth", DefaultValue = 12, MinValue = 1)]
        public int H1ZigZagDepth { get; set; }
        [Parameter("H1 ZigZag Deviation", DefaultValue = 5, MinValue = 1)]
        public int H1ZigZagDeviation { get; set; }
        [Parameter("H1 ZigZag Backstep", DefaultValue = 3, MinValue = 0)]
        public int H1ZigZagBackstep { get; set; }

        [Parameter("Stop Loss Buffer (Pips)", DefaultValue = 2.0, MinValue = 0.0)]
        public double StopLossBufferPips { get; set; }

        [Parameter("Take Profit R:R", DefaultValue = 1.5, MinValue = 0.1)]
        public double TakeProfitRR { get; set; } // Will be kept for now, but TP is now based on H1 ZigZag

        [Parameter("Min StopLoss (Pips)", DefaultValue = 5.0, MinValue = 1.0)]
        public double MinStopLossPips { get; set; }

        [Parameter("Min TakeProfit (Pips)", DefaultValue = 5.0, MinValue = 1.0)]
        public double MinTakeProfitPips { get; set; }

        private enum MarketTrend
        {
            None,
            Uptrend,
            Downtrend
        }

        private MarketTrend _currentTrend = MarketTrend.None;

        // Store structure points: Price and Index of the bar
        private ZigZagPoint _lastHH = null;
        private ZigZagPoint _lastHL = null;
        private ZigZagPoint _lastLH = null;
        private ZigZagPoint _lastLL = null;

        private bool _tradeExecutedToday = false;
        private DateTime _lastTradeDate = DateTime.MinValue;

        // BoS related fields
        private bool _bosOccurredThisBar = false;
        private TradeType _bosTradeDirection;
        private double _bosSLPriceLevel; 

        // ZigZag Instances
        // private ZigZag _structureZigZag; // Removed as it's unused and its initialization was problematic
        private ZigZag _h1ZigZag;
        private Bars _h1Bars;

        protected override void OnStart()
        {
            Print("MarketStructureBot started with ZigZag logic.");
            Print($"Risk per trade: {RiskPercentage}%");
            Print($"Symbol: {Symbol.Name}, TimeFrame: {TimeFrame}");
            Print($"Structure ZigZag Params: Depth={StructureZigZagDepth}, Dev={StructureZigZagDeviation}, Backstep={StructureZigZagBackstep}");
            Print($"H1 ZigZag Params: Mode={H1ZigZagMode}, Depth={H1ZigZagDepth}, Dev={H1ZigZagDeviation}, Backstep={H1ZigZagBackstep}");

            // _structureZigZag = Indicators.GetIndicator<ZigZag>(StructureZigZagDepth, StructureZigZagDeviation, StructureZigZagBackstep); // Removed
            _h1Bars = MarketData.GetBars(TimeFrame.Hour);
            _h1ZigZag = Indicators.GetIndicator<ZigZag>(_h1Bars, H1ZigZagMode, H1ZigZagDepth, H1ZigZagDeviation, H1ZigZagBackstep);
        }

        protected override void OnBar()
        {
            // Trade per day logic
            if (_lastTradeDate.Date == Server.Time.Date)
            {
                _tradeExecutedToday = true;
            }
            else
            {
                _tradeExecutedToday = false;
            }
            
            _bosOccurredThisBar = false; // Reset BoS flag for the current bar processing

            IdentifyMarketStructure();

            if (!_tradeExecutedToday && BoSDetectedAndSignalPresent())
            {
                ExecuteTrade(); 
            }
        }

        private void IdentifyMarketStructure()
        {
            int currentIndex = Bars.ClosePrices.Count - 1;
            if (currentIndex < StructureZigZagDepth + 1) 
            {
                Print("Not enough historical data to identify swing points yet.");
                return;
            }

            int indexToCheck = currentIndex - StructureZigZagDepth; 

            // Swing High Detection
            double highToCheck = Bars.HighPrices[indexToCheck];
            bool isSwingHigh = true;
            for (int i = 1; i <= StructureZigZagDepth; i++)
            {
                if (Bars.HighPrices[indexToCheck - i] > highToCheck || Bars.HighPrices[indexToCheck + i] >= highToCheck)
                {
                    isSwingHigh = false;
                    break;
                }
            }
            // Ensure it's higher than immediate neighbors too if lookback is 1
            if (StructureZigZagDepth == 1 && (Bars.HighPrices[indexToCheck -1] > highToCheck || Bars.HighPrices[indexToCheck+1] >= highToCheck)) isSwingHigh = false;


            if (isSwingHigh)
            {
                var newSwingHigh = (Price: highToCheck, Index: indexToCheck, Time: Bars.OpenTimes[indexToCheck]);
                if (_lastHH == null || _lastHH.Price != newSwingHigh.Price || _lastHH.Index != newSwingHigh.Index)
                {
                    _lastHH = new ZigZagPoint(newSwingHigh.Price, newSwingHigh.Time, newSwingHigh.Index, true);
                    Print($"New Swing High: {_lastHH.Price} at {_lastHH.Time} (Idx: {_lastHH.Index})");
                    DrawSwingPoint(_lastHH.Index, _lastHH.Price, true, Color.DarkGreen);
                }
            }

            // Swing Low Detection
            double lowToCheck = Bars.LowPrices[indexToCheck];
            bool isSwingLow = true;
            for (int i = 1; i <= StructureZigZagDepth; i++)
            {
                if (Bars.LowPrices[indexToCheck - i] < lowToCheck || Bars.LowPrices[indexToCheck + i] <= lowToCheck)
                {
                    isSwingLow = false;
                    break;
                }
            }
            if (StructureZigZagDepth == 1 && (Bars.LowPrices[indexToCheck -1] < lowToCheck || Bars.LowPrices[indexToCheck+1] <= lowToCheck)) isSwingLow = false;

            if (isSwingLow)
            {
                var newSwingLow = (Price: lowToCheck, Index: indexToCheck, Time: Bars.OpenTimes[indexToCheck]);
                if (_lastLL == null || _lastLL.Price != newSwingLow.Price || _lastLL.Index != newSwingLow.Index)
                {
                    _lastLL = new ZigZagPoint(newSwingLow.Price, newSwingLow.Time, newSwingLow.Index, false);
                    Print($"New Swing Low: {_lastLL.Price} at {_lastLL.Time} (Idx: {_lastLL.Index})");
                    DrawSwingPoint(_lastLL.Index, _lastLL.Price, false, Color.DarkRed);
                }
            }
            
            // Sort by index before processing for structure
            var sortedSwingHighs = new List<ZigZagPoint> { _lastHH }.Concat(new List<ZigZagPoint> { _lastLL }).ToList();

            if (_currentTrend == MarketTrend.None)
            {
                // Try to establish initial trend from sorted swings
                var allSwings = sortedSwingHighs.Select(s => new { s.Price, s.Index, Type = "High" })
                    .Concat(sortedSwingHighs.Select(s => new { s.Price, s.Index, Type = "Low" }))
                    .OrderBy(s => s.Index).ToList();

                if (allSwings.Count >= 2)
                {
                    for (int i = 0; i < allSwings.Count - 1; i++)
                    {
                        var s1 = allSwings[i];
                        var s2 = allSwings[i + 1];

                        if (s1.Type == "Low" && s2.Type == "High" && s2.Price > s1.Price)
                        {
                            _lastHL = s1;
                            _lastHH = s2;
                            _currentTrend = MarketTrend.Uptrend;
                            Print($"Initial Structure: Uptrend. HL: {_lastHL.Price} @ {_lastHL.Index}, HH: {_lastHH.Price} @ {_lastHH.Index}");
                            MarkStructurePoint(_lastHL.Index, _lastHL.Price, "HL", Color.LightGreen);
                            MarkStructurePoint(_lastHH.Index, _lastHH.Price, "HH", Color.Green);
                            if (_lastHL.Index != -1) DrawBoSLine(_lastHL.Price, _lastHL.Index, currentIndex, false, "BoS_HL_");
                            break;
                        }
                        else if (s1.Type == "High" && s2.Type == "Low" && s2.Price < s1.Price)
                        {
                            _lastLH = s1;
                            _lastLL = s2;
                            _currentTrend = MarketTrend.Downtrend;
                            Print($"Initial Structure: Downtrend. LH: {_lastLH.Price} @ {_lastLH.Index}, LL: {_lastLL.Price} @ {_lastLL.Index}");
                            MarkStructurePoint(_lastLH.Index, _lastLH.Price, "LH", Color.Salmon);
                            MarkStructurePoint(_lastLL.Index, _lastLL.Price, "LL", Color.Red);
                            if(_lastLH.Index != -1) DrawBoSLine(_lastLH.Price, _lastLH.Index, currentIndex, true, "BoS_LH_");
                            break;
                        }
                    }
                }
            }
            else if (_currentTrend == MarketTrend.Uptrend)
            {
                if (_lastHL == null) return; // Should not happen if trend is Uptrend

                if (Bars.ClosePrices[currentIndex] < _lastHL.Price)
                {
                    Print($"BoS UP->DOWN: Close {Bars.ClosePrices[currentIndex]} < HL {_lastHL.Price}@{_lastHL.Index}");
                    
                    _bosOccurredThisBar = true;
                    _bosTradeDirection = TradeType.Sell;
                    _bosSLPriceLevel = _lastHH.Price; // SL will be above this HH (which becomes new LH)

                    _lastLH = _lastHH; // Previous HH becomes potential LH
                    
                    // Find the swing low that broke the structure or is the lowest after the break
                    var breakLLCandidate = sortedSwingHighs
                        .Where(s => s.Index > _lastHL.Index && s.Price < _lastHL.Price)
                        .OrderBy(s => s.Price).FirstOrDefault();

                    _lastLL = breakLLCandidate.Index != 0 ? new ZigZagPoint(breakLLCandidate.Price, Bars.OpenTimes[breakLLCandidate.Index], breakLLCandidate.Index, false) : new ZigZagPoint(Bars.LowPrices[currentIndex], Bars.OpenTimes[currentIndex], currentIndex, false);
                    
                    _currentTrend = MarketTrend.Downtrend;
                    Print($"Trend Change: UP -> DOWN. LH: {_lastLH.Price}@{_lastLH.Index}, LL: {_lastLL.Price}@{_lastLL.Index}");
                    
                    Chart.RemoveObject($"BoS_HL_{_lastHL.Index}");
                    MarkStructurePoint(_lastLH.Index, _lastLH.Price, "LH", Color.Salmon, true);
                    MarkStructurePoint(_lastLL.Index, _lastLL.Price, "LL", Color.Red, true);
                    if (_lastLH.Index != -1) DrawBoSLine(_lastLH.Price, _lastLH.Index, currentIndex, true, "BoS_LH_");
                    
                    _lastHH = null; _lastHL = null; // Reset uptrend points
                }
                else
                {
                    // Look for new HH
                    var potentialNewHH = sortedSwingHighs
                        .Where(s => s.Index > _lastHH.Index && s.Price > _lastHH.Price)
                        .OrderBy(s => s.Index).FirstOrDefault();

                    if (potentialNewHH != null) // Found a higher swing high
                    {
                        // Now find the HL before this new HH. It must be after the previous HH and higher than previous HL.
                        var potentialNewHL = sortedSwingHighs
                            .Where(s => s.Index > _lastHH.Index && s.Index < potentialNewHH.Index && s.Price > _lastHL.Price)
                            .OrderBy(s => s.Price).FirstOrDefault(); // Lowest point in pullback
                        
                        if (potentialNewHL != null)
                        {
                            Chart.RemoveObject($"BoS_HL_{_lastHL.Index}"); // Remove old BoS line
                            _lastHL = potentialNewHL;
                            _lastHH = potentialNewHH;
                            Print($"Uptrend Cont: New HL: {_lastHL.Price}@{_lastHL.Index}, New HH: {_lastHH.Price}@{_lastHH.Index}");
                            MarkStructurePoint(_lastHL.Index, _lastHL.Price, "HL", Color.LightGreen);
                            MarkStructurePoint(_lastHH.Index, _lastHH.Price, "HH", Color.Green);
                            DrawBoSLine(_lastHL.Price, _lastHL.Index, currentIndex, false, "BoS_HL_");
                        }
                        else if (potentialNewHH.Index > _lastHL.Index) // No new HL formed in between, but new HH is valid after current HL
                        {
                            // This means the impulse continued from the current _lastHL to form potentialNewHH
                            // If _lastHH was valid (Index != -1), remove the old line connecting _lastHL to it.
                            if (_lastHH.Index != -1) 
                            {
                                string oldLineName = $"Line_HL{_lastHL.Index}_HH{_lastHH.Index}";
                                Chart.RemoveObject(oldLineName);
                                Print($"Uptrend Ext: Removed old trend line: {oldLineName} connecting to old HH {_lastHH.Price}@{_lastHH.Index}");
                            }

                            _lastHH = potentialNewHH;
                            Print($"Uptrend Cont (Ext): New HH: {_lastHH.Price}@{_lastHH.Index} (HL at {_lastHL.Price}@{_lastHL.Index} holds)");
                            MarkStructurePoint(_lastHH.Index, _lastHH.Price, "HH", Color.Green);
                            // BoS line at _lastHL remains valid
                        }
                    }
                }
            }
            else if (_currentTrend == MarketTrend.Downtrend)
            {
                if (_lastLH == null) return;

                if (Bars.ClosePrices[currentIndex] > _lastLH.Price)
                {
                    Print($"BoS DOWN->UP: Close {Bars.ClosePrices[currentIndex]} > LH {_lastLH.Price}@{_lastLH.Index}");

                    _bosOccurredThisBar = true;
                    _bosTradeDirection = TradeType.Buy;
                    _bosSLPriceLevel = _lastLL.Price; // SL will be below this LL (which becomes new HL)

                    _lastHL = _lastLL; // Previous LL becomes potential HL
                    
                    var breakHHCandidate = sortedSwingHighs
                        .Where(s => s.Index > _lastLH.Index && s.Price > _lastLH.Price)
                        .OrderByDescending(s => s.Price).FirstOrDefault();
                    
                    _lastHH = breakHHCandidate.Index != 0 ? new ZigZagPoint(breakHHCandidate.Price, Bars.OpenTimes[breakHHCandidate.Index], breakHHCandidate.Index, true) : new ZigZagPoint(Bars.HighPrices[currentIndex], Bars.OpenTimes[currentIndex], currentIndex, true);

                    _currentTrend = MarketTrend.Uptrend;
                    Print($"Trend Change: DOWN -> UP. HL: {_lastHL.Price}@{_lastHL.Index}, HH: {_lastHH.Price}@{_lastHH.Index}");

                    Chart.RemoveObject($"BoS_LH_{_lastLH.Index}");
                    MarkStructurePoint(_lastHL.Index, _lastHL.Price, "HL", Color.LightGreen, true);
                    MarkStructurePoint(_lastHH.Index, _lastHH.Price, "HH", Color.Green, true);
                    if (_lastHL.Index != -1) DrawBoSLine(_lastHL.Price, _lastHL.Index, currentIndex, false, "BoS_HL_");

                    _lastLH = null; _lastLL = null; // Reset downtrend points
                }
                else
                {
                    // Look for new LL
                    var potentialNewLL = sortedSwingHighs
                        .Where(s => s.Index > _lastLL.Index && s.Price < _lastLL.Price)
                        .OrderBy(s => s.Index).FirstOrDefault();

                    if (potentialNewLL != null)
                    {
                        var potentialNewLH = sortedSwingHighs
                            .Where(s => s.Index > _lastLL.Index && s.Index < potentialNewLL.Index && s.Price < _lastLH.Price)
                            .OrderByDescending(s => s.Price).FirstOrDefault(); // Highest point in pullback
                        
                        if (potentialNewLH != null)
                        {
                            Chart.RemoveObject($"BoS_LH_{_lastLH.Index}");
                            _lastLH = potentialNewLH;
                            _lastLL = potentialNewLL;
                            Print($"Downtrend Cont: New LH: {_lastLH.Price}@{_lastLH.Index}, New LL: {_lastLL.Price}@{_lastLL.Index}");
                            MarkStructurePoint(_lastLH.Index, _lastLH.Price, "LH", Color.Salmon);
                            MarkStructurePoint(_lastLL.Index, _lastLL.Price, "LL", Color.Red);
                            DrawBoSLine(_lastLH.Price, _lastLH.Index, currentIndex, true, "BoS_LH_");
                        }
                        else if (potentialNewLL.Index > _lastLH.Index)
                        {
                             // If _lastLL was valid (Index != -1), remove the old line connecting _lastLH to it.
                            if (_lastLL.Index != -1)
                            {
                                string oldLineName = $"Line_LH{_lastLH.Index}_LL{_lastLL.Index}";
                                Chart.RemoveObject(oldLineName);
                                Print($"Downtrend Ext: Removed old trend line: {oldLineName} connecting to old LL {_lastLL.Price}@{_lastLL.Index}");
                            }
                             _lastLL = potentialNewLL;
                             Print($"Downtrend Cont (Ext): New LL: {_lastLL.Price}@{_lastLL.Index} (LH at {_lastLH.Price}@{_lastLH.Index} holds)");
                             MarkStructurePoint(_lastLL.Index, _lastLL.Price, "LL", Color.Red);
                        }
                    }
                }
            }
        }

        private bool BoSDetectedAndSignalPresent()
        {
            // TODO: Implement actual trading signal logic after BoS
            // e.g., BoS confirmed, pullback to broken level, entry candle pattern etc.
            // For now, just use the flag set during BoS detection
            return _bosOccurredThisBar; 
        }

        private void ExecuteTrade()
        {
            if (!_bosOccurredThisBar) return; // Ensure this is called only when BoS signal is active for this bar

            double entryPrice;
            double stopLossLevel;
            double takeProfitLevel;
            double stopLossInPips;
            double takeProfitInPips;

            string label = $"ms_bos_{Server.Time.ToShortDateString()}_{Server.Time.ToLongTimeString()}";
            string comment = $"Market Structure BoS Trade";
            
            DateTime currentTimeOnChartTF = Bars.OpenTimes[Bars.ClosePrices.Count - 1];

            if (_bosTradeDirection == TradeType.Buy)
            {
                entryPrice = Symbol.Ask;

                // SL: Below last H1 ZigZag Low
                var h1SwingLow = GetClassifiedZigZagPoints(_h1ZigZag, _h1Bars, currentTimeOnChartTF).FirstOrDefault(p => !p.IsHigh);
                if (h1SwingLow == null)
                {
                    Print("BUY Trade: No H1 ZigZag Low found for SL. No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
                stopLossLevel = h1SwingLow.Price - StopLossBufferPips * Symbol.PipSize;

                // TP: At last H1 ZigZag High
                var h1SwingHigh = GetClassifiedZigZagPoints(_h1ZigZag, _h1Bars, currentTimeOnChartTF).FirstOrDefault(p => p.IsHigh);
                if (h1SwingHigh == null || h1SwingHigh.Price <= entryPrice)
                {
                    Print($"BUY Trade: No valid H1 ZigZag High for TP (Found: {h1SwingHigh?.Price}, Entry: {entryPrice}). No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
                takeProfitLevel = h1SwingHigh.Price;

                stopLossInPips = (entryPrice - stopLossLevel) / Symbol.PipSize;
                takeProfitInPips = (takeProfitLevel - entryPrice) / Symbol.PipSize;

                if (stopLossInPips < MinStopLossPips || entryPrice <= stopLossLevel)
                {
                    Print($"Buy setup: SL pips {stopLossInPips:F1} too small or entry {entryPrice:F5} below/at SL {stopLossLevel:F5}. No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
                if (takeProfitInPips < MinTakeProfitPips)
                {
                    Print($"Buy setup: TP pips {takeProfitInPips:F1} too small. No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
            }
            else // Sell
            {
                entryPrice = Symbol.Bid;

                // SL: Above last H1 ZigZag High
                var h1SwingHigh = GetClassifiedZigZagPoints(_h1ZigZag, _h1Bars, currentTimeOnChartTF).FirstOrDefault(p => p.IsHigh);
                if (h1SwingHigh == null)
                {
                    Print("SELL Trade: No H1 ZigZag High found for SL. No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
                stopLossLevel = h1SwingHigh.Price + StopLossBufferPips * Symbol.PipSize;

                // TP: At last H1 ZigZag Low
                var h1SwingLow = GetClassifiedZigZagPoints(_h1ZigZag, _h1Bars, currentTimeOnChartTF).FirstOrDefault(p => !p.IsHigh);
                if (h1SwingLow == null || h1SwingLow.Price >= entryPrice)
                {
                    Print($"SELL Trade: No valid H1 ZigZag Low for TP (Found: {h1SwingLow?.Price}, Entry: {entryPrice}). No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
                takeProfitLevel = h1SwingLow.Price;

                stopLossInPips = (stopLossLevel - entryPrice) / Symbol.PipSize;
                takeProfitInPips = (entryPrice - takeProfitLevel) / Symbol.PipSize;
                
                if (stopLossInPips < MinStopLossPips || entryPrice >= stopLossLevel)
                {
                    Print($"Sell setup: SL pips {stopLossInPips:F1} too small or entry {entryPrice:F5} above/at SL {stopLossLevel:F5}. No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
                if (takeProfitInPips < MinTakeProfitPips)
                {
                    Print($"Sell setup: TP pips {takeProfitInPips:F1} too small. No trade.");
                    _bosOccurredThisBar = false; 
                    return;
                }
            }

            double lotsToTrade = CalculatePositionSize(stopLossInPips);
            if (lotsToTrade <= 0)
            {
                Print($"Volume calculation resulted in {lotsToTrade} lots. No trade.");
                 _bosOccurredThisBar = false; // Consume signal
                return;
            }

            double volumeInUnits = Symbol.QuantityToVolumeInUnits(lotsToTrade);
            Print($"Attempting to {_bosTradeDirection} {Symbol.Name} Vol: {volumeInUnits} ({lotsToTrade} lots) EP: {entryPrice:F5} SL: {stopLossLevel:F5} ({stopLossInPips:F1} pips) TP: {takeProfitLevel:F5} ({takeProfitInPips:F1} pips)");

            TradeResult result = ExecuteMarketOrder(_bosTradeDirection, Symbol.Name, volumeInUnits, label, stopLossInPips, takeProfitInPips, comment);

            if (result.IsSuccessful)
            {
                Print($"Trade executed successfully. Position ID: {result.Position.Id}");
                _lastTradeDate = Server.Time.Date;
                _tradeExecutedToday = true;
            }
            else
            {
                Print($"Trade execution failed: {result.Error}");
            }
            
            _bosOccurredThisBar = false; // Consume the BoS signal after attempting trade for this bar
        }

        private double CalculatePositionSize(double stopLossPips)
        {
            if (stopLossPips <= 0) return 0;

            double riskAmount = Account.Balance * (RiskPercentage / 100.0);
            double stopLossInQuoteCurrency = stopLossPips * Symbol.PipSize;
            
            // PipValue is the value of 1 pip for 1 lot in Account Currency.
            // So, PipValue * Lots = Value of 1 pip for 'Lots' size.
            // We want: RiskAmount = StopLossInPips * PipValue * Lots
            // Or: RiskAmount = StopLossInQuoteCurrency * (Lots * Symbol.LotSize / Symbol.TickSize) * TickValue - this is too complex.
            // Simpler: RiskAmount = StopLossInQuoteCurrency * VolumeInUnits * TickValue (if prices are in quote currency)
            // VolumeInUnits = Symbol.VolumeInUnitsToQuantity(lots)
            // For many pairs, SL distance * PipValue * Lots = Risk Amount
            // Lots = RiskAmount / (SL_pips * Symbol.PipValue) - Symbol.PipValue is for 1 lot.

            if (Symbol.PipValue == 0) return 0; // Should not happen for valid symbols
            double lots = riskAmount / (stopLossPips * Symbol.PipValue);
            
            return Symbol.NormalizeVolumeInUnits(lots, RoundingMode.Down);
        }

        private void DrawSwingPoint(int barIndex, double price, bool isHigh, Color color)
        {
            string name = $"SW_{(isHigh ? "H" : "L")}_{barIndex}";
            Chart.DrawText(name, isHigh ? "sH" : "sL", barIndex, isHigh ? price + Symbol.PipSize * 5 : price - Symbol.PipSize * 5, color);
        }
        
        private void MarkStructurePoint(int barIndex, double price, string label, Color color, bool isBosPoint = false)
        {
            if (barIndex < 0) return; // Guard against invalid index
            string objectName = $"{label}_{barIndex}";
             // Add BOS prefix if it's a point formed due to BoS, to differentiate if needed
            if (isBosPoint) objectName = $"BOS_{objectName}";


            Chart.DrawText(objectName, label, barIndex, label.Contains("H") ? price + Symbol.PipSize * 15 : price - Symbol.PipSize * 15, color);
            Print($"Marked: {label} at {price} on bar {Bars.OpenTimes[barIndex]} (index {barIndex})");

            // Basic line drawing: connect current point to the immediate previous structure point of the opposite type
            // This is a simplified ZigZag.
            Color lineColor;
            int p1Index, p2Index;

            if (label == "HH" && _lastHL.Index != -1 && _lastHL.Index < barIndex)
            {
                p1Index = _lastHL.Index; 
                p2Index = barIndex;    
                lineColor = Color.Blue;
                string lineName = $"TrendLine_{Math.Min(p1Index, p2Index)}_{Math.Max(p1Index, p2Index)}";
                Chart.DrawTrendLine(lineName, Bars.OpenTimes[p1Index], _lastHL.Price, Bars.OpenTimes[p2Index], price, lineColor, 2, LineStyle.Solid);
            }
            else if (label == "HL" && _lastHH.Index != -1 && _lastHH.Index < barIndex) // HL connects to previous HH
            {
                p1Index = _lastHH.Index; 
                p2Index = barIndex;     
                lineColor = Color.Blue;
                string lineName = $"TrendLine_{Math.Min(p1Index, p2Index)}_{Math.Max(p1Index, p2Index)}";
                Chart.DrawTrendLine(lineName, Bars.OpenTimes[p1Index], _lastHH.Price, Bars.OpenTimes[p2Index], price, lineColor, 2, LineStyle.Solid);
            }
            else if (label == "LL" && _lastLH.Index != -1 && _lastLH.Index < barIndex)
            {
                p1Index = _lastLH.Index; 
                p2Index = barIndex;     
                lineColor = Color.DarkSlateBlue;
                string lineName = $"TrendLine_{Math.Min(p1Index, p2Index)}_{Math.Max(p1Index, p2Index)}";
                Chart.DrawTrendLine(lineName, Bars.OpenTimes[p1Index], _lastLH.Price, Bars.OpenTimes[p2Index], price, lineColor, 2, LineStyle.Solid);
            }
            else if (label == "LH" && _lastLL.Index != -1 && _lastLL.Index < barIndex) // LH connects to previous LL
            {
                p1Index = _lastLL.Index; 
                p2Index = barIndex;     
                lineColor = Color.DarkSlateBlue;
                string lineName = $"TrendLine_{Math.Min(p1Index, p2Index)}_{Math.Max(p1Index, p2Index)}";
                Chart.DrawTrendLine(lineName, Bars.OpenTimes[p1Index], _lastLL.Price, Bars.OpenTimes[p2Index], price, lineColor, 2, LineStyle.Solid);
            }
        }

        private void DrawBoSLine(double level, int structPointIndex, int currentBarIndex, bool isResistance, string prefix)
        {
            // Horizontal line for BoS level
            string lineName = $"{prefix}{structPointIndex}";
            Chart.RemoveObject(lineName); 
            Chart.DrawHorizontalLine(lineName, level, isResistance ? Color.OrangeRed : Color.LimeGreen, 2, LineStyle.Dots);
            // Extend it visually a bit? cTrader horizontal lines are infinite.
            Print($"BoS Level drawn: {lineName} at {level} (from struct point index {structPointIndex})");
        }

        protected override void OnStop()
        {
            Print("MarketStructureBot stopped.");
            // Chart.RemoveAllObjects(); // Optional: Clean up chart on stop
        }

        private List<ZigZagPoint> GetClassifiedZigZagPoints(ZigZag zzIndicator, Bars barsSeries, DateTime beforeTime, int minPointsToClassify = 2)
        {
            var rawPoints = new List<(double Price, DateTime Time, int Index)>();
            var classifiedPoints = new List<ZigZagPoint>();

            if (zzIndicator == null || barsSeries == null || barsSeries.Count == 0 || zzIndicator.Result == null)
            {
                Print($"GetClassifiedZigZagPoints: Pre-condition failed. ZZ: {zzIndicator != null}, Bars: {barsSeries?.Count > 0}, ZZ.Result: {zzIndicator?.Result != null}");
                return classifiedPoints;
            }

            int barIndexBefore = -1;
            for (int i = barsSeries.Count - 1; i >= 0; i--)
            {
                if (barsSeries.OpenTimes[i] <= beforeTime)
                {
                    barIndexBefore = i;
                    break;
                }
            }

            if (barIndexBefore < 0)
            {
                Print($"GetClassifiedZigZagPoints: No bars found before/at {beforeTime} on series {barsSeries.TimeFrame}");
                return classifiedPoints;
            }

            for (int i = barIndexBefore; i >= 0; i--)
            {
                // The GURU ZigZag stores pivot prices in Result, or NaN/0.0 for non-pivot bars.
                double pivotPrice = zzIndicator.Result[i];
                if (!double.IsNaN(pivotPrice) && pivotPrice != 0.0) 
                {
                    rawPoints.Add((pivotPrice, barsSeries.OpenTimes[i], i));
                }
            }

            if (rawPoints.Count == 0)
            {
                //Print($"GetClassifiedZigZagPoints: No raw ZigZag points found before {beforeTime} on {barsSeries.TimeFrame}.");
                return classifiedPoints;
            }
            
            rawPoints.Reverse(); // Sort chronologically (oldest to newest)

            if (rawPoints.Count < minPointsToClassify)
            {
                if (rawPoints.Count == 1)
                {
                    var p = rawPoints[0];
                    if (p.Index < barsSeries.HighPrices.Count && p.Index < barsSeries.LowPrices.Count) 
                    {
                        bool isHighGuess = Math.Abs(p.Price - barsSeries.HighPrices[p.Index]) < Symbol.PipSize * 0.5 && p.Price > barsSeries.LowPrices[p.Index] + Symbol.PipSize * 0.5;
                        bool isLowGuess = Math.Abs(p.Price - barsSeries.LowPrices[p.Index]) < Symbol.PipSize * 0.5 && p.Price < barsSeries.HighPrices[p.Index] - Symbol.PipSize * 0.5;
                        if (isHighGuess && !isLowGuess) classifiedPoints.Add(new ZigZagPoint(p.Price, p.Time, p.Index, true));
                        else if (isLowGuess && !isHighGuess) classifiedPoints.Add(new ZigZagPoint(p.Price, p.Time, p.Index, false));
                    }
                }
                //Print($"GetClassifiedZigZagPoints: Only {rawPoints.Count} raw point(s). Min {minPointsToClassify} needed for robust classification. Returning {classifiedPoints.Count} points.");
                return classifiedPoints; 
            }

            // Classify based on comparison, assuming alternating nature for HighLow mode.
            // First point's nature is determined by comparing with the second.
            bool firstPointIsHigh = rawPoints[0].Price > rawPoints[1].Price;
            
            for (int i = 0; i < rawPoints.Count; i++)
            {
                var currentRawPt = rawPoints[i];
                bool isCurrentPointHigh;

                if (i == 0) 
                {
                    isCurrentPointHigh = firstPointIsHigh;
                }
                else 
                {
                    // Subsequent points should alternate the type of the *previous classified point*.
                    if (classifiedPoints.Any()) 
                        isCurrentPointHigh = !classifiedPoints.Last().IsHigh; 
                    else 
                    { 
                        // This case should ideally not be reached if i > 0 and classification started correctly.
                        // Fallback: compare with previous raw point, but this isn't strictly alternating.
                        isCurrentPointHigh = currentRawPt.Price > rawPoints[i-1].Price;
                    }
                }
                classifiedPoints.Add(new ZigZagPoint(currentRawPt.Price, currentRawPt.Time, currentRawPt.Index, isCurrentPointHigh));
            }
            //Print($"GetClassifiedZigZagPoints for {barsSeries.TimeFrame} before {beforeTime}: Found {rawPoints.Count} raw, classified {classifiedPoints.Count}. Last: {classifiedPoints.LastOrDefault()}");
            return classifiedPoints;
        }
    }
} 