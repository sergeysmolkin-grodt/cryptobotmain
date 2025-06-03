using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
// using cAlgo.API.Indicators; // Removed as we assume custom ZigZag
using cAlgo.API.Internals;
// using cAlgo.Indicators; // Removed: ZigZag class will be embedded

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

    // Embedded ZigZag Indicator Class
    // Based on "ZigZag" v1.0.6 by cTrader GURU
    // [Indicator(IsOverlay = true, AccessRights = AccessRights.None)] // This attribute might be optional or problematic for embedded classes used via GetIndicator. Testing needed.
    // For now, let's assume GetIndicator<T> handles it if T inherits Indicator.
    public class ZigZag : Indicator // Inheriting from Indicator is necessary for GetIndicator<T>
    {
        #region Enums
        public enum ModeZigZag
        {
            HighLow,
            OpenClose
        }
        #endregion

        #region Identity
        public const string NAME = "ZigZag";
        public const string VERSION = "1.0.6";
        #endregion

        #region Params
        [Parameter(NAME + " " + VERSION, Group = "Identity", DefaultValue = "https://www.google.com/search?q=ctrader+guru+zigzag")]
        public string ProductInfo { get; set; }

        [Parameter("Mode", DefaultValue = ModeZigZag.HighLow, Group = "Params")]
        public ModeZigZag MyModeZigZag { get; set; }

        [Parameter("Depth", DefaultValue = 12, Group = "Params", MinValue = 1)] // Added MinValue based on typical usage
        public int Depth { get; set; }

        [Parameter("Deviation", DefaultValue = 5, Group = "Params", MinValue = 1)] // Added MinValue based on typical usage
        public int Deviation { get; set; }

        [Parameter("BackStep", DefaultValue = 3, Group = "Params", MinValue = 0)] // Added MinValue based on typical usage
        public int BackStep { get; set; }

        [Parameter("Show Labels", DefaultValue = true, Group = "Label")] // Changed "Show" to "Show Labels" for clarity
        public bool ShowLabel { get; set; }

        [Parameter("Color High", DefaultValue = "DodgerBlue", Group = "Label")]
        public Color ColorHigh { get; set; }

        [Parameter("Color Low", DefaultValue = "Red", Group = "Label")]
        public Color ColorLow { get; set; }

        [Output("ZigZag", LineColor = "DodgerBlue", LineStyle = LineStyle.Lines)]
        public IndicatorDataSeries Result { get; set; }
        #endregion

        #region Property
        private double _lastLow;
        private double _lastHigh;
        private double _low;
        private double _high;
        private int _lastHighIndex;
        private int _lastLowIndex;
        private int _type;
        private double _point;
        private double _currentLow;
        private double _currentHigh;

        private int _countHigh = 0;
        private int _countLow = 0;
        private int _waiting = 0;

        private IndicatorDataSeries _highZigZags;
        private IndicatorDataSeries _lowZigZags;
        #endregion

        #region Indicator Events
        protected override void Initialize()
        {
            // Print($"ZigZag Indicator ({VERSION}) Initialized. Mode: {MyModeZigZag}, Depth: {Depth}, Dev: {Deviation}, Backstep: {BackStep}");
            _highZigZags = CreateDataSeries();
            _lowZigZags = CreateDataSeries();
            if (Symbol != null) // Symbol might be null if GetIndicator is called without a Bars series that has a symbol.
                 _point = Symbol.TickSize;
            else if (Chart != null && Chart.Symbol != null) // Fallback for Chart context if available
                 _point = Chart.Symbol.TickSize;
            // else _point might remain 0.0, which could be an issue if Deviation is small.
            // Consider adding a check in Calculate or a parameter for minimum point distance if Symbol is not available.
        }

        public override void Calculate(int index)
        {
            if (_point == 0.0 && Symbol != null) // Re-check if symbol became available
            {
                _point = Symbol.TickSize;
            }
            if (_point == 0.0 && Chart != null && Chart.Symbol != null)
            {
                 _point = Chart.Symbol.TickSize;
            }
            if (_point == 0.0) // If still zero, cannot calculate deviation correctly
            {
                // Print("ZigZag: Symbol.TickSize is 0, cannot calculate Deviation correctly. Skipping calculation.");
                Result[index] = double.NaN; // Or 0, depending on desired behavior for error
                return;
            }

            switch (MyModeZigZag)
            {
                case ModeZigZag.HighLow:
                    PerformIndicatorHighLow(index);
                    break;
                case ModeZigZag.OpenClose:
                    PerformIndicatorOpenClose(index);
                    break;
            }

            // Original GURU label logic (slightly adapted)
            if (_highZigZags[index] == _lastHigh && _waiting > -1 && _lastHigh > 0) // Added _lastHigh > 0 to avoid labeling initial 0s
            {
                _waiting = -1;
                _countHigh++;
            }
            
            if (_lowZigZags[index] == _lastLow && _waiting < 1 && _lastLow > 0) // Added _lastLow > 0
            {
                _waiting = 1;
                _countLow++;
            }

            if (ShowLabel) // Removed IsChartObjectManipulationAllowed check
            {
                if (_highZigZags[index] > 0 && _lastHighIndex == index) // Only draw if this bar is the confirmed peak
                {
                    string textName = $"zzh-{_countHigh}-{index}"; // Make name more unique
                    // Chart.RemoveObject(textName); // Remove previous if any, to avoid clutter (optional)
                    ChartText cth = Chart.DrawText(textName, _highZigZags[index].ToString("N" + Symbol.Digits), index, _highZigZags[index], ColorHigh);
                    cth.HorizontalAlignment = HorizontalAlignment.Center;
                    cth.VerticalAlignment = VerticalAlignment.Top;
                }

                if (_lowZigZags[index] > 0 && _lastLowIndex == index) // Only draw if this bar is the confirmed trough
                {
                    string textName = $"zzl-{_countLow}-{index}"; // Make name more unique
                    // Chart.RemoveObject(textName); // Remove previous if any (optional)
                    ChartText ctl = Chart.DrawText(textName, _lowZigZags[index].ToString("N" + Symbol.Digits), index, _lowZigZags[index], ColorLow);
                    ctl.HorizontalAlignment = HorizontalAlignment.Center;
                    ctl.VerticalAlignment = VerticalAlignment.Bottom;
                }
            }            
        }
        #endregion

        #region Private Methods
        private void PerformIndicatorHighLow(int index)
        {
            if (index < Depth)
            {
                Result[index] = double.NaN; // Use NaN for uncalculated points
                _highZigZags[index] = 0;
                _lowZigZags[index] = 0;
                return;
            }

            _currentLow = Bars.LowPrices.Minimum(Depth);

            if (Math.Abs(_currentLow - _lastLow) < double.Epsilon)
                _currentLow = 0.0;
            else
            {
                _lastLow = _currentLow;
                if ((Bars.LowPrices[index] - _currentLow) > (Deviation * _point))
                    _currentLow = 0.0;
                else
                {
                    for (int i = 1; i <= BackStep; i++)
                    {
                        if (index - i >= 0 && Math.Abs(_lowZigZags[index - i]) > double.Epsilon && _lowZigZags[index - i] > _currentLow)
                            _lowZigZags[index - i] = 0.0;
                    }
                }
            }
            if (Math.Abs(Bars.LowPrices[index] - _currentLow) < double.Epsilon && _currentLow != 0.0) // Ensure _currentLow is a valid swing
                _lowZigZags[index] = _currentLow;
            else
                _lowZigZags[index] = 0.0;

            _currentHigh = Bars.HighPrices.Maximum(Depth);

            if (Math.Abs(_currentHigh - _lastHigh) < double.Epsilon)
                _currentHigh = 0.0;
            else
            {
                _lastHigh = _currentHigh;
                if ((_currentHigh - Bars.HighPrices[index]) > (Deviation * _point))
                    _currentHigh = 0.0;
                else
                {
                    for (int i = 1; i <= BackStep; i++)
                    {
                        if (index - i >= 0 && Math.Abs(_highZigZags[index - i]) > double.Epsilon && _highZigZags[index - i] < _currentHigh)
                            _highZigZags[index - i] = 0.0;
                    }
                }
            }

            if (Math.Abs(Bars.HighPrices[index] - _currentHigh) < double.Epsilon && _currentHigh != 0.0) // Ensure _currentHigh is a valid swing
                _highZigZags[index] = _currentHigh;
            else
                _highZigZags[index] = 0.0;

            // Simplified Logic for Result based on identified _highZigZags and _lowZigZags
            // The original GURU logic for 'Result' is complex and seems to manage trend state (_type)
            // For use with GetClassifiedZigZagPoints, we just need to populate Result when a high or low is confirmed at 'index'
            
            Result[index] = double.NaN; // Default to NaN

            if (_type == 0) // Initial state or after a clear pattern
            {
                if (_highZigZags[index] > 0)
                {
                    _high = _highZigZags[index];
                    _lastHighIndex = index;
                    _type = -1; // Expecting a low next
                    Result[index] = _high;
                }
                else if (_lowZigZags[index] > 0)
                {
                    _low = _lowZigZags[index];
                    _lastLowIndex = index;
                    _type = 1; // Expecting a high next
                    Result[index] = _low;
                }
            }
            else if (_type == 1) // Was a low, looking for a high
            {
                if (_lowZigZags[index] > 0 && _lowZigZags[index] < _low) // New lower low
                {
                    if (_lastLowIndex != index && _lastLowIndex < Result.Count) Result[_lastLowIndex] = double.NaN; // Invalidate previous
                    _low = _lowZigZags[index];
                    _lastLowIndex = index;
                    Result[index] = _low;
                    // _type remains 1, still looking for a high to follow this low sequence
                }
                else if (_highZigZags[index] > 0) // Found a high
                {
                     // Ensure this high is higher than the last confirmed low to be a valid reversal point
                    if (_low == 0 || _highZigZags[index] > _low) {
                        _high = _highZigZags[index];
                        _lastHighIndex = index;
                        Result[index] = _high;
                        _type = -1; // Now expecting a low
                    } else {
                         // High not strong enough, might be noise. Keep looking for a proper high or lower low.
                        _highZigZags[index] = 0; // Invalidate this high for now
                    }
                }
            }
            else if (_type == -1) // Was a high, looking for a low
            {
                if (_highZigZags[index] > 0 && _highZigZags[index] > _high) // New higher high
                {
                    if (_lastHighIndex != index && _lastHighIndex < Result.Count) Result[_lastHighIndex] = double.NaN; // Invalidate previous
                    _high = _highZigZags[index];
                    _lastHighIndex = index;
                    Result[index] = _high;
                    // _type remains -1, still looking for a low to follow this high sequence
                }
                else if (_lowZigZags[index] > 0) // Found a low
                {
                    // Ensure this low is lower than the last confirmed high
                    if (_high == 0 || _lowZigZags[index] < _high) {
                        _low = _lowZigZags[index];
                        _lastLowIndex = index;
                        Result[index] = _low;
                        _type = 1; // Now expecting a high
                    } else {
                        // Low not strong enough.
                        _lowZigZags[index] = 0; // Invalidate this low
                    }
                }
            }
        }

        private void PerformIndicatorOpenClose(int index) // Similar structure to HighLow, using OpenPrices
        {
            if (index < Depth)
            {
                Result[index] = double.NaN;
                _highZigZags[index] = 0;
                _lowZigZags[index] = 0;
                return;
            }

            _currentLow = Bars.OpenPrices.Minimum(Depth); // Changed to OpenPrices

            if (Math.Abs(_currentLow - _lastLow) < double.Epsilon)
                _currentLow = 0.0;
            else
            {
                _lastLow = _currentLow;
                if ((Bars.OpenPrices[index] - _currentLow) > (Deviation * _point)) // Changed to OpenPrices
                    _currentLow = 0.0;
                else
                {
                    for (int i = 1; i <= BackStep; i++)
                    {
                         if (index - i >= 0 && Math.Abs(_lowZigZags[index - i]) > double.Epsilon && _lowZigZags[index - i] > _currentLow)
                            _lowZigZags[index - i] = 0.0;
                    }
                }
            }
            if (Math.Abs(Bars.OpenPrices[index] - _currentLow) < double.Epsilon && _currentLow != 0.0) // Changed to OpenPrices
                _lowZigZags[index] = _currentLow;
            else
                _lowZigZags[index] = 0.0;

            _currentHigh = Bars.OpenPrices.Maximum(Depth); // Changed to OpenPrices

            if (Math.Abs(_currentHigh - _lastHigh) < double.Epsilon)
                _currentHigh = 0.0;
            else
            {
                _lastHigh = _currentHigh;
                if ((_currentHigh - Bars.OpenPrices[index]) > (Deviation * _point)) // Changed to OpenPrices
                    _currentHigh = 0.0;
                else
                {
                    for (int i = 1; i <= BackStep; i++)
                    {
                        if (index - i >= 0 && Math.Abs(_highZigZags[index - i]) > double.Epsilon && _highZigZags[index - i] < _currentHigh)
                            _highZigZags[index - i] = 0.0;
                    }
                }
            }

            if (Math.Abs(Bars.OpenPrices[index] - _currentHigh) < double.Epsilon && _currentHigh != 0.0) // Changed to OpenPrices
                _highZigZags[index] = _currentHigh;
            else
                _highZigZags[index] = 0.0;
            
            // Result assignment logic (same as PerformIndicatorHighLow, but uses OpenPrices indirectly via _high/_lowZigZags)
            Result[index] = double.NaN;

            if (_type == 0)
            {
                if (_highZigZags[index] > 0)
                {
                    _high = _highZigZags[index]; // Value already from OpenPrices if mode is OpenClose
                    _lastHighIndex = index;
                    _type = -1; 
                    Result[index] = _high;
                }
                else if (_lowZigZags[index] > 0)
                {
                    _low = _lowZigZags[index]; // Value already from OpenPrices
                    _lastLowIndex = index;
                    _type = 1; 
                    Result[index] = _low;
                }
            }
            else if (_type == 1) 
            {
                if (_lowZigZags[index] > 0 && _lowZigZags[index] < _low) 
                {
                    if (_lastLowIndex != index && _lastLowIndex < Result.Count) Result[_lastLowIndex] = double.NaN;
                    _low = _lowZigZags[index];
                    _lastLowIndex = index;
                    Result[index] = _low;
                }
                else if (_highZigZags[index] > 0)
                {
                    if (_low == 0 || _highZigZags[index] > _low) {
                        _high = _highZigZags[index];
                        _lastHighIndex = index;
                        Result[index] = _high;
                        _type = -1; 
                    } else {
                        _highZigZags[index] = 0;
                    }
                }
            }
            else if (_type == -1)
            {
                if (_highZigZags[index] > 0 && _highZigZags[index] > _high)
                {
                    if (_lastHighIndex != index && _lastHighIndex < Result.Count) Result[_lastHighIndex] = double.NaN;
                    _high = _highZigZags[index];
                    _lastHighIndex = index;
                    Result[index] = _high;
                }
                else if (_lowZigZags[index] > 0)
                {
                     if (_high == 0 || _lowZigZags[index] < _high) {
                        _low = _lowZigZags[index];
                        _lastLowIndex = index;
                        Result[index] = _low;
                        _type = 1; 
                    } else {
                        _lowZigZags[index] = 0;
                    }
                }
            }
        }
        #endregion
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
        // Adjusted to use the embedded ZigZag class's ModeZigZag
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
        private ZigZag _h1ZigZag; // This will now refer to the embedded ZigZag class
        private Bars _h1Bars;

        protected override void OnStart()
        {
            Print("MarketStructureBot started with ZigZag logic.");
            Print($"Risk per trade: {RiskPercentage}%");
            Print($"Symbol: {Symbol.Name}, TimeFrame: {TimeFrame}");
            Print($"Structure ZigZag Params: Depth={StructureZigZagDepth}, Dev={StructureZigZagDeviation}, Backstep={StructureZigZagBackstep}");
            Print($"H1 ZigZag Params: Mode={H1ZigZagMode}, Depth={H1ZigZagDepth}, Dev={H1ZigZagDeviation}, Backstep={H1ZigZagBackstep}");

            _h1Bars = MarketData.GetBars(TimeFrame.Hour);
            // This should now correctly instantiate the embedded ZigZag class
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
            // var sortedSwingHighs = new List<ZigZagPoint> { _lastHH }.Concat(new List<ZigZagPoint> { _lastLL }).ToList();
            // The above line was problematic as _lastHH or _lastLL could be null initially or after a BoS.
            // We need a list of *valid* (non-null) swing points for structure analysis.

            var currentValidSwings = new List<ZigZagPoint>();
            if (_lastHH != null) currentValidSwings.Add(_lastHH);
            if (_lastLL != null) currentValidSwings.Add(_lastLL);
            // To keep the old logic somewhat intact, we only use the most recent HH and LL for initial structure.
            // A more robust ZigZag integration would use a list of ZigZag points directly.
            // For now, this mimics the previous swing detection feeding into structure logic.

            if (_currentTrend == MarketTrend.None)
            {
                // Try to establish initial trend from the two most recent (if available) HH and LL points
                if (_lastHL == null && _lastLL != null && _lastHH != null && _lastLL.Index < _lastHH.Index && _lastHH.Price > _lastLL.Price)
                {
                    _lastHL = _lastLL; // Tentative HL
                    // _lastHH is already set
                    _currentTrend = MarketTrend.Uptrend;
                    Print($"Initial Structure (from swings): Uptrend. HL: {_lastHL.Price} @ {_lastHL.Time}, HH: {_lastHH.Price} @ {_lastHH.Time}");
                    MarkStructurePoint(_lastHL.Index, _lastHL.Price, "HL", Color.LightGreen);
                    MarkStructurePoint(_lastHH.Index, _lastHH.Price, "HH", Color.Green);
                    DrawBoSLine(_lastHL.Price, _lastHL.Index, currentIndex, false, "BoS_HL_");
                }
                else if (_lastLH == null && _lastHH != null && _lastLL != null && _lastHH.Index < _lastLL.Index && _lastLL.Price < _lastHH.Price)
                {
                    _lastLH = _lastHH; // Tentative LH
                    // _lastLL is already set
                    _currentTrend = MarketTrend.Downtrend;
                    Print($"Initial Structure (from swings): Downtrend. LH: {_lastLH.Price} @ {_lastLH.Time}, LL: {_lastLL.Price} @ {_lastLL.Time}");
                    MarkStructurePoint(_lastLH.Index, _lastLH.Price, "LH", Color.Salmon);
                    MarkStructurePoint(_lastLL.Index, _lastLL.Price, "LL", Color.Red);
                    DrawBoSLine(_lastLH.Price, _lastLH.Index, currentIndex, true, "BoS_LH_");
                }
            }
            else if (_currentTrend == MarketTrend.Uptrend)
            {
                if (_lastHL == null || _lastHH == null) return; // Essential points for uptrend must exist

                if (Bars.ClosePrices[currentIndex] < _lastHL.Price)
                {
                    Print($"BoS UP->DOWN: Close {Bars.ClosePrices[currentIndex]} < HL {_lastHL.Price}@{_lastHL.Time}");
                    
                    _bosOccurredThisBar = true;
                    _bosTradeDirection = TradeType.Sell;
                    _bosSLPriceLevel = _lastHH.Price; 

                    _lastLH = _lastHH; // Previous HH becomes new LH
                    
                    _lastLL = new ZigZagPoint(Bars.LowPrices[currentIndex], Bars.OpenTimes[currentIndex], currentIndex, false);
                    if (_lastHH != null && _lastLL.Index == _lastHH.Index) 
                    {
                        int lookBackRange = Math.Min(currentIndex, 10);
                        double minLowInRange = double.MaxValue;
                        int tempSubsequentLowBarIndex = currentIndex; 
                        for (int k = 0; k < lookBackRange; k++)
                        {
                            int barToExamine = currentIndex - k;
                            // Ensure we are looking after the last confirmed structure point (new LH, former HH)
                            // and that barToExamine is a valid index
                            if (barToExamine >= 0 && barToExamine > _lastLH.Index) 
                            {
                                if (Bars.LowPrices[barToExamine] < minLowInRange)
                                {
                                    minLowInRange = Bars.LowPrices[barToExamine];
                                    tempSubsequentLowBarIndex = barToExamine;
                                }
                            }
                        }
                        if (tempSubsequentLowBarIndex != currentIndex && tempSubsequentLowBarIndex > _lastLH.Index) // check if a different, valid index was found
                           _lastLL = new ZigZagPoint(Bars.LowPrices[tempSubsequentLowBarIndex], Bars.OpenTimes[tempSubsequentLowBarIndex], tempSubsequentLowBarIndex, false);
                    }
                                        
                    _currentTrend = MarketTrend.Downtrend;
                    Print($"Trend Change: UP -> DOWN. New LH: {_lastLH.Price}@{_lastLH.Time}, New LL: {_lastLL.Price}@{_lastLL.Time}");
                    
                    Chart.RemoveObject($"BoS_HL_{_lastHL.Index}");
                    MarkStructurePoint(_lastLH.Index, _lastLH.Price, "LH", Color.Salmon, true);
                    MarkStructurePoint(_lastLL.Index, _lastLL.Price, "LL", Color.Red, true);
                    DrawBoSLine(_lastLH.Price, _lastLH.Index, currentIndex, true, "BoS_LH_");
                    
                    _lastHH = null; _lastHL = null; 
                }
                else if (_lastHH != null && _lastHL != null) // Check for new HH if still in uptrend
                { 
                    // Is the latest swing high a new HH?
                    if (_lastHH.Index > _lastHL.Index && _lastHH.Price > _lastHH.Price) // This condition _lastHH.Price > _lastHH.Price seems wrong, should be compared to previous _lastHH if we had a list
                    {
                        // The current _lastHH from swing detection IS the potential new HH.
                        // We need to find if a new HL formed before it.
                        // This requires a list of swings, current logic with single _lastHH/_lastLL is insufficient for robust new HL/HH sequence. 
                        // Simplified: if a new swing high (_lastHH) is detected higher than previous _lastHL.Price, consider it a continuation for now.
                        // And the _lastLL detected (if it's after _lastHL and before new _lastHH) could be the new _lastHL.
                        
                        // This part of logic needs a proper list of zig zag points to correctly identify new HH/HL sequences.
                        // The current single _lastHH/_lastLL from basic swing logic is insufficient here.
                        // For now, we assume if a new _lastHH (swing high) is detected, and it's higher than _lastHL, trend continues.
                        // The BoS line at _lastHL remains key.
                    }
                }
            }
            else if (_currentTrend == MarketTrend.Downtrend)
            {
                if (_lastLH == null || _lastLL == null) return; // Essential points for downtrend must exist

                if (Bars.ClosePrices[currentIndex] > _lastLH.Price)
                {
                    Print($"BoS DOWN->UP: Close {Bars.ClosePrices[currentIndex]} > LH {_lastLH.Price}@{_lastLH.Time}");

                    _bosOccurredThisBar = true;
                    _bosTradeDirection = TradeType.Buy;
                    _bosSLPriceLevel = _lastLL.Price; 

                    _lastHL = _lastLL; // Previous LL becomes new HL
                    
                    _lastHH = new ZigZagPoint(Bars.HighPrices[currentIndex], Bars.OpenTimes[currentIndex], currentIndex, true);
                     if (_lastLL != null && _lastHH.Index == _lastLL.Index)
                    {
                        int lookBackRangeMax = Math.Min(currentIndex, 10);
                        double maxHighInRange = double.MinValue;
                        int tempSubsequentHighBarIndex = currentIndex; 
                        for (int k = 0; k < lookBackRangeMax; k++)
                        {
                            int barToExamine = currentIndex - k;
                            // Ensure we are looking after the last confirmed structure point (new HL, former LL)
                            // and that barToExamine is a valid index
                            if (barToExamine >= 0 && barToExamine > _lastHL.Index) 
                            {
                                if (Bars.HighPrices[barToExamine] > maxHighInRange)
                                {
                                    maxHighInRange = Bars.HighPrices[barToExamine];
                                    tempSubsequentHighBarIndex = barToExamine;
                                }
                            }
                        }
                         if (tempSubsequentHighBarIndex != currentIndex && tempSubsequentHighBarIndex > _lastHL.Index) // check if a different, valid index was found
                            _lastHH = new ZigZagPoint(Bars.HighPrices[tempSubsequentHighBarIndex], Bars.OpenTimes[tempSubsequentHighBarIndex], tempSubsequentHighBarIndex, true);
                    }

                    _currentTrend = MarketTrend.Uptrend;
                    Print($"Trend Change: DOWN -> UP. New HL: {_lastHL.Price}@{_lastHL.Time}, New HH: {_lastHH.Price}@{_lastHH.Time}");

                    Chart.RemoveObject($"BoS_LH_{_lastLH.Index}");
                    MarkStructurePoint(_lastHL.Index, _lastHL.Price, "HL", Color.LightGreen, true);
                    MarkStructurePoint(_lastHH.Index, _lastHH.Price, "HH", Color.Green, true);
                    DrawBoSLine(_lastHL.Price, _lastHL.Index, currentIndex, false, "BoS_HL_");

                    _lastLH = null; _lastLL = null; 
                }
                 else if (_lastLL != null && _lastLH != null) // Check for new LL
                {
                    // Similar to uptrend, this part needs a proper list of swings for robust new LH/LL detection.
                    // Current logic with single _lastLL (newest swing low) is used.
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

            for (int i = barIndexBefore; i >= 0 && i < zzIndicator.Result.Count; i--) // Added boundary check for zzIndicator.Result.Count
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
