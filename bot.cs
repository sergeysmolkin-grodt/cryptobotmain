using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
//using cAlgo.Indicators; // No longer needed as the indicator is now a helper class

namespace cAlgo
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class CryptoBot : Robot
    {
        [Parameter("Instance Label", DefaultValue = "CryptoBot_01")]
        public string InstanceLabel { get; set; }

        [Parameter("Take Profit RR", DefaultValue = 2, MinValue = 0.1)]
        public double TakeProfitRR { get; set; }
        
        [Parameter("Swing Period", DefaultValue = 5, MinValue = 2)]
        public int SwingPeriod { get; set; }

        private MarketStructure _marketStructure;
        private int _lastTradedBosIndex = -1;

        protected override void OnStart()
        {
            _marketStructure = new MarketStructure(this, SwingPeriod);
        }

        protected override void OnBar()
        {
            _marketStructure.Update();
            
            // Ensure there are no open positions managed by this bot instance
            if (Positions.FindAll(InstanceLabel).Length > 0)
            {
                return;
            }

            var lastConfirmedBos = _marketStructure.BosEvents
                .Where(b => b.IsConfirmed)
                .OrderBy(b => b.EndIndex)
                .LastOrDefault();

            if (lastConfirmedBos != null && lastConfirmedBos.EndIndex > _lastTradedBosIndex)
            {
                // Prevent trading the same signal again
                _lastTradedBosIndex = lastConfirmedBos.EndIndex;

                if (lastConfirmedBos.IsBullish)
                {
                    var lastSwingLow = _marketStructure.StructurePoints
                        .Where(p => p.Type == SwingType.Low && p.Index < lastConfirmedBos.EndIndex)
                        .OrderBy(p => p.Index)
                        .LastOrDefault();
                    
                    if (lastSwingLow != null)
                    {
                        var stopLossPrice = lastSwingLow.Price;
                        var entryPrice = Symbol.Ask;
                        var stopLossPips = (entryPrice - stopLossPrice) / Symbol.PipSize;

                        if (stopLossPips <= 0) return;

                        var volumeInUnits = Symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, 1, stopLossPips, RoundingMode.Down);
                        if (volumeInUnits == 0) return;

                        var takeProfitPips = stopLossPips * TakeProfitRR;

                        ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeInUnits, InstanceLabel, stopLossPips, takeProfitPips);
                    }
                }
                else // IsBearish
                {
                    var lastSwingHigh = _marketStructure.StructurePoints
                        .Where(p => p.Type == SwingType.High && p.Index < lastConfirmedBos.EndIndex)
                        .OrderBy(p => p.Index)
                        .LastOrDefault();

                    if (lastSwingHigh != null)
                    {
                        var stopLossPrice = lastSwingHigh.Price;
                        var entryPrice = Symbol.Bid;
                        var stopLossPips = (stopLossPrice - entryPrice) / Symbol.PipSize;

                        if (stopLossPips <= 0) return;

                        var volumeInUnits = Symbol.VolumeForProportionalRisk(ProportionalAmountType.Balance, 1, stopLossPips, RoundingMode.Down);
                        if (volumeInUnits == 0) return;
                        
                        var takeProfitPips = stopLossPips * TakeProfitRR;
                        
                        ExecuteMarketOrder(TradeType.Sell, SymbolName, volumeInUnits, InstanceLabel, stopLossPips, takeProfitPips);
                    }
                }
            }
        }
    }

    // This is no longer a standalone Indicator, but a helper class for the Robot.
    public class MarketStructure
    {
        private readonly Robot _robot;
        private readonly Bars _bars;
        private readonly int _swingPeriod;

        public List<SwingPoint> StructurePoints { get; } = new List<SwingPoint>();
        private readonly List<SwingPoint> _allPivots = new List<SwingPoint>();
        private readonly HashSet<string> _processedPivots = new HashSet<string>();
        public List<BosEvent> BosEvents { get; } = new List<BosEvent>();

        public MarketStructure(Robot robot, int swingPeriod)
        {
            _robot = robot;
            _bars = robot.Bars;
            _swingPeriod = swingPeriod;
        }

        public void Update()
        {
            _allPivots.Clear();
            _processedPivots.Clear();

            for (int i = _swingPeriod; i < _bars.Count - _swingPeriod; i++)
            {
                if (IsHighPivot(i))
                {
                    AddPivot(new SwingPoint { Index = i, Price = _bars.HighPrices[i], Type = SwingType.High });
                }
                else if (IsLowPivot(i))
                {
                    AddPivot(new SwingPoint { Index = i, Price = _bars.LowPrices[i], Type = SwingType.Low });
                }
            }

            BuildStructure();
            DetectBos();
        }

        private bool IsHighPivot(int index)
        {
            var price = _bars.HighPrices[index];
            for (int i = 1; i <= _swingPeriod; i++)
            {
                if (price < _bars.HighPrices[index - i] || price < _bars.HighPrices[index + i])
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsLowPivot(int index)
        {
            var price = _bars.LowPrices[index];
            for (int i = 1; i <= _swingPeriod; i++)
            {
                if (price > _bars.LowPrices[index - i] || price > _bars.LowPrices[index + i])
                {
                    return false;
                }
            }
            return true;
        }

        private void AddPivot(SwingPoint newPivot)
        {
            var key = $"{(newPivot.Type == SwingType.High ? "H" : "L")}_{newPivot.Index}";
            if (_processedPivots.Add(key))
            {
                _allPivots.Add(newPivot);
                _allPivots.Sort((a, b) => a.Index.CompareTo(b.Index));
            }
        }
        
        private void BuildStructure()
        {
            StructurePoints.Clear();
            if (_allPivots.Count == 0) return;

            var lastPoint = _allPivots[0];
            StructurePoints.Add(lastPoint);

            for (int i = 1; i < _allPivots.Count; i++)
            {
                var currentPoint = _allPivots[i];
                if (currentPoint.Type != lastPoint.Type)
                {
                    StructurePoints.Add(currentPoint);
                    lastPoint = currentPoint;
                }
                else
                {
                    if ((currentPoint.Type == SwingType.High && currentPoint.Price > lastPoint.Price) ||
                        (currentPoint.Type == SwingType.Low && currentPoint.Price < lastPoint.Price))
                    {
                        StructurePoints[StructurePoints.Count - 1] = currentPoint;
                        lastPoint = currentPoint;
                    }
                }
            }
        }

        private void DetectBos()
        {
            BosEvents.Clear();

            for (int i = 2; i < StructurePoints.Count; i++)
            {
                var prevPoint = StructurePoints[i - 2];
                var midPoint = StructurePoints[i - 1];
                var currentPoint = StructurePoints[i];
                
                if (currentPoint.Type == SwingType.High && midPoint.Type == SwingType.Low && prevPoint.Type == SwingType.High)
                {
                    if (currentPoint.Price > prevPoint.Price)
                    {
                        var bos = new BosEvent
                        {
                            Level = prevPoint.Price,
                            StartIndex = prevPoint.Index,
                            EndIndex = currentPoint.Index,
                            IsConfirmed = true,
                            IsBullish = true
                        };
                        BosEvents.Add(bos);
                    }
                }
                else if (currentPoint.Type == SwingType.Low && midPoint.Type == SwingType.High && prevPoint.Type == SwingType.Low)
                {
                    if (currentPoint.Price < prevPoint.Price)
                    {
                         var bos = new BosEvent
                        {
                            Level = prevPoint.Price,
                            StartIndex = prevPoint.Index,
                            EndIndex = currentPoint.Index,
                            IsConfirmed = true,
                            IsBullish = false
                        };
                        BosEvents.Add(bos);
                    }
                }
            }
            
            if (StructurePoints.Count > 1)
            {
                var lastStructurePoint = StructurePoints.Last();
                var lastPriceBarIndex = _bars.Count - 1;
                var lastPrice = _bars.ClosePrices.LastValue;

                SwingPoint lastMajorPivot = null;
                if(lastStructurePoint.Type == SwingType.Low)
                {
                    lastMajorPivot = StructurePoints.Where(p => p.Type == SwingType.High).LastOrDefault();
                }
                else 
                {
                    lastMajorPivot = StructurePoints.Where(p => p.Type == SwingType.Low).LastOrDefault();
                }

                if (lastMajorPivot != null)
                {
                    bool isBullishBos = lastStructurePoint.Type == SwingType.Low && lastPrice > lastMajorPivot.Price;
                    bool isBearishBos = lastStructurePoint.Type == SwingType.High && lastPrice < lastMajorPivot.Price;

                    if (isBullishBos || isBearishBos)
                    {
                        BosEvents.Add(new BosEvent
                        {
                            Level = lastMajorPivot.Price,
                            StartIndex = lastMajorPivot.Index,
                            EndIndex = lastPriceBarIndex,
                            IsConfirmed = false,
                            IsBullish = isBullishBos
                        });
                    }
                }
            }
        }
    }

    public class SwingPoint
    {
        public int Index { get; set; }
        public double Price { get; set; }
        public SwingType Type { get; set; }
    }

    public enum SwingType
    {
        High,
        Low
    }

    public class BosEvent
    {
        public double Level { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public bool IsConfirmed { get; set; }
        public bool IsBullish { get; set; }
    }
}
