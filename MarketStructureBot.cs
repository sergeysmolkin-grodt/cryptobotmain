using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
// Предполагается, что ZigZag2SourceCode.cs компилируется в индикатор, доступный как cAlgo.Indicators.ZigZag
// Если ваш индикатор ZigZag находится в другом пространстве имен, нужно будет скорректировать using.
// Для предоставленного ZigZag2SourceCode.cs, он находится в пространстве имен cAlgo.Indicators.

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MarketStructureBot : Robot
    {
        [Parameter("Instance Name", DefaultValue = "MarketStructureBot01", Group = "General")]
        public string InstanceName { get; set; }

        // Параметры ZigZag - будут переданы в кастомный индикатор ZigZag
        // Обратите внимание, что enum ModeZigZag должен быть доступен. В вашем ZigZag2SourceCode.cs он определен внутри класса ZigZag.
        [Parameter("ZigZag Mode", DefaultValue = cAlgo.Indicators.ZigZag.ModeZigZag.HighLow, Group = "ZigZag")]
        public cAlgo.Indicators.ZigZag.ModeZigZag ZzMode { get; set; }

        [Parameter("ZigZag Depth", DefaultValue = 12, Group = "ZigZag", MinValue = 1)]
        public int ZzDepth { get; set; }

        [Parameter("ZigZag Deviation", DefaultValue = 5, Group = "ZigZag", MinValue = 1)]
        public int ZzDeviation { get; set; }

        [Parameter("ZigZag BackStep", DefaultValue = 3, Group = "ZigZag", MinValue = 0)]
        public int ZzBackStep { get; set; }

        // Торговые параметры
        [Parameter("Risk per Trade (%)", DefaultValue = 1.0, Group = "Trading", MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercentage { get; set; }

        private cAlgo.Indicators.ZigZag _zigZag; // Кастомный индикатор ZigZag из ZigZag2SourceCode.cs
        private DateTime _lastTradeDate = DateTime.MinValue;
        private int _tradesToday = 0;

        protected override void OnStart()
        {
            Print("Starting MarketStructureBot ({0}) for {1} on Account {2}", InstanceName, Symbol.Name, Account.Number);
            Print("Symbol: {0}, PipSize: {1}, TickSize: {2}, LotSize: {3}, VolumeStep: {4}", Symbol.Name, Symbol.PipSize, Symbol.TickSize, Symbol.LotSize, Symbol.VolumeInUnitsStep);
            Print("Account Balance: {0} {1}", Account.Balance, Account.Asset.Name);

            // Инициализация кастомного индикатора ZigZag
            // Параметры ZzMode, ZzDepth, ZzDeviation, ZzBackStep будут использованы для инициализации экземпляра индикатора.
            _zigZag = Indicators.GetIndicator<cAlgo.Indicators.ZigZag>(ZzMode, ZzDepth, ZzDeviation, ZzBackStep);

            if (_zigZag == null)
            {
                Print("Error: Could not initialize ZigZag indicator. Ensure ZigZag2SourceCode.cs is compiled and accessible as cAlgo.Indicators.ZigZag.");
                Stop(); // Останавливаем бота, если индикатор не может быть инициализирован
                return;
            }
            
            Print("In OnStart, after ZigZag Init: _zigZag.Result.Count = {0}", _zigZag.Result.Count);
            if (_zigZag.Result.Count > 1) 
            {
                Print("In OnStart, latest ZigZag results: LastValue={0}, SecondLastValue={1}", _zigZag.Result.LastValue, _zigZag.Result.Last(1));
            }
            else if (_zigZag.Result.Count == 1)
            {
                Print("In OnStart, only one ZigZag result: LastValue={0}", _zigZag.Result.LastValue);
            }
            
            // Проверка, установился ли режим ZigZag (MyModeZigZag - это свойство с [Parameter] в вашем индикаторе)
            // Это несколько сложнее проверить напрямую после GetIndicator, так как cTrader обрабатывает установку параметров.
            // Можно предположить, что если GetIndicator не вызвал ошибку и параметры переданы, то они установлены.
            Print("Custom ZigZag indicator initialized with Mode: {0}, Depth: {1}, Deviation: {2}, BackStep: {3}", ZzMode, ZzDepth, ZzDeviation, ZzBackStep);
            Print("MarketStructureBot started successfully.");
        }

        protected override void OnTick()
        {
            // 1. Проверка правила "1 сделка в день"
            if (Time.Date != _lastTradeDate.Date)
            {
                _tradesToday = 0; // Сбрасываем счетчик сделок для нового дня
                //Print("New trading day. Trades today reset to 0.");
            }

            if (_tradesToday >= 1)
            {
                // Print("Trade limit for today (1) reached.");
                return; 
            }

            if (Positions.Count > 0)
            {
                // Print("Already have an open position.");
                return; // Не открываем новую сделку, если уже есть активная
            }

            // 2. Получение значений ZigZag
            // В вашем индикаторе ZigZag2SourceCode.cs есть Output "Result"
            Print("Bot Tick: Bars.Count = {0}, _zigZag.Result.Count = {1}", Bars.Count, _zigZag.Result.Count);
            if (_zigZag.Result.Count > 5)
            {
                Print("Last 5 _zigZag.Result values: [{0}], [{1}], [{2}], [{3}], [{4}]", 
                    _zigZag.Result.Last(4), 
                    _zigZag.Result.Last(3), 
                    _zigZag.Result.Last(2), 
                    _zigZag.Result.Last(1), 
                    _zigZag.Result.LastValue);
            }
            else if (_zigZag.Result.Count > 0)
            {
                Print("Last _zigZag.Result value: [{0}]", _zigZag.Result.LastValue);
            }

            (double val, int idx) currentZigZagPoint = GetZigZagPoint(_zigZag.Result, 0); // Последняя сформированная точка
            (double val, int idx) prevZigZagPoint = GetZigZagPoint(_zigZag.Result, 1);    // Предпоследняя точка
            (double val, int idx) prevPrevZigZagPoint = GetZigZagPoint(_zigZag.Result, 2); // Пред-предпоследняя точка

            Print("ZigZag Points on Tick: Current({0} @ idx {1}), Prev({2} @ idx {3}), PrevPrev({4} @ idx {5})", 
                currentZigZagPoint.val, currentZigZagPoint.idx, 
                prevZigZagPoint.val, prevZigZagPoint.idx, 
                prevPrevZigZagPoint.val, prevPrevZigZagPoint.idx);

            if (double.IsNaN(currentZigZagPoint.val) || double.IsNaN(prevZigZagPoint.val) || double.IsNaN(prevPrevZigZagPoint.val))
            {
                Print("Waiting for sufficient ZigZag points (need at least 3).");
                return; // Недостаточно точек ZigZag для принятия решения
            }
            
            //Print("ZigZag points: Current({0} @ {1}), Prev({2} @ {3}), PrevPrev({4} @ {5})", 
            //    currentZigZagPoint.val, currentZigZagPoint.idx, 
            //    prevZigZagPoint.val, prevZigZagPoint.idx, 
            //    prevPrevZigZagPoint.val, prevPrevZigZagPoint.idx);

            // 3. Упрощенная логика входа (требует значительной доработки для анализа структуры рынка)
            TradeType? tradeType = null;
            double stopLossPrice = 0;

            // Попытка определить тренд по последним трем точкам ZigZag
            // HH (Higher High), HL (Higher Low), LH (Lower High), LL (Lower Low)
            // Uptrend: current > prevPrev (HH) AND prev > some_earlier_low (HL)
            // Downtrend: current < prevPrev (LL) AND prev < some_earlier_high (LH)

            // Пример очень упрощенной логики:
            // Если последняя точка (current) выше предпоследней (prev), а предпоследняя ниже пред-предпоследней (prevPrev) -> возможный разворот вверх или коррекция закончилась
            // currentZigZagPoint.val > prevZigZagPoint.val  (движение вверх от prev к current)
            // prevZigZagPoint.val < prevPrevZigZagPoint.val (prev был локальным минимумом по отношению к prevPrev)
            if (currentZigZagPoint.val > prevZigZagPoint.val && prevZigZagPoint.val < prevPrevZigZagPoint.val) // prev = Low, current = High
            {
                // Это означает: prevPrev был High, prev был Low, current стал High (выше чем prev)
                // Если current также ВЫШЕ чем prevPrev (т.е. Higher High) - это может быть сигналом к покупке
                if (currentZigZagPoint.val > prevPrevZigZagPoint.val) 
                {
                    tradeType = TradeType.Buy;
                    stopLossPrice = prevZigZagPoint.val; // SL за последний минимум (prev)
                    Print("Potential Buy Signal: HH ({0}) after HL ({1}). SL at {2}", currentZigZagPoint.val, prevZigZagPoint.val, stopLossPrice);
                }
            }
            // Если последняя точка (current) ниже предпоследней (prev), а предпоследняя выше пред-предпоследней (prevPrev) -> возможный разворот вниз или коррекция закончилась
            // currentZigZagPoint.val < prevZigZagPoint.val (движение вниз от prev к current)
            // prevZigZagPoint.val > prevPrevZigZagPoint.val (prev был локальным максимумом по отношению к prevPrev)
            else if (currentZigZagPoint.val < prevZigZagPoint.val && prevZigZagPoint.val > prevPrevZigZagPoint.val) // prev = High, current = Low
            {
                // Это означает: prevPrev был Low, prev был High, current стал Low (ниже чем prev)
                // Если current также НИЖЕ чем prevPrev (т.е. Lower Low) - это может быть сигналом к продаже
                if (currentZigZagPoint.val < prevPrevZigZagPoint.val)
                {
                    tradeType = TradeType.Sell;
                    stopLossPrice = prevZigZagPoint.val; // SL за последний максимум (prev)
                    Print("Potential Sell Signal: LL ({0}) after LH ({1}). SL at {2}", currentZigZagPoint.val, prevZigZagPoint.val, stopLossPrice);
                }
            }

            if (tradeType.HasValue)
            {
                double entryPrice = tradeType.Value == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
                double stopLossPips = 0;

                if (tradeType == TradeType.Buy)
                {
                    if (entryPrice <= stopLossPrice) { Print("Invalid SL for Buy: entry {0} <= SL {1}", entryPrice, stopLossPrice); return; }
                    stopLossPips = (entryPrice - stopLossPrice) / Symbol.PipSize;
                }
                else // Sell
                {
                    if (entryPrice >= stopLossPrice) { Print("Invalid SL for Sell: entry {0} >= SL {1}", entryPrice, stopLossPrice); return; }
                    stopLossPips = (stopLossPrice - entryPrice) / Symbol.PipSize;
                }

                if (stopLossPips <= 0) 
                {
                    Print("Calculated Stop Loss in pips is zero or negative ({0}). Skipping trade.", stopLossPips);
                    return;
                }

                // Расчет объема для заданного риска
                double accountRiskAmount = Account.Balance * (RiskPercentage / 100.0);
                double stopLossPriceDistance = stopLossPips * Symbol.PipSize; // Абсолютное расстояние SL в цене
                
                if (stopLossPriceDistance == 0) { Print("Stop loss distance is zero. Cannot calculate volume."); return; }

                double volumeInBaseCurrency = accountRiskAmount / stopLossPriceDistance;
                long volumeInUnits = (long)Symbol.NormalizeVolumeInUnits(volumeInBaseCurrency, RoundingMode.Down);
                
                Print("Account Risk Amount: {0} {1}", accountRiskAmount, Account.Asset.Name);
                Print("Stop Loss Price Distance: {0}", stopLossPriceDistance);
                Print("Calculated Volume (Base Currency): {0}", volumeInBaseCurrency);
                Print("Normalized Volume (Units): {0}", volumeInUnits);


                if (volumeInUnits < Symbol.VolumeInUnitsMin)
                {
                    Print("Calculated volume {0} is less than minimum {1}. Skipping trade.", volumeInUnits, Symbol.VolumeInUnitsMin);
                    return;
                }
                if (volumeInUnits > Symbol.VolumeInUnitsMax)
                {
                    volumeInUnits = (long)Symbol.VolumeInUnitsMax;
                    Print("Calculated volume exceeds maximum, using max volume: {0}", volumeInUnits);
                }
                
                string tradeLabel = string.Format("{0}-{1}-{2}", InstanceName, Symbol.Name, Time.ToShortDateString());
                Print("Attempting to {0} {1} of {2} at {3}. SL pips: {4:F1}. Volume: {5}", tradeType.Value, Symbol.Name, volumeInUnits, entryPrice, stopLossPips, volumeInUnits);
                
                var result = ExecuteMarketOrder(tradeType.Value, Symbol.Name, volumeInUnits, tradeLabel, stopLossPips, null); // TP пока не ставим

                if (result.IsSuccessful)
                {
                    Print("Trade executed successfully. Position ID: {0}, Price: {1}", result.Position.Id, result.Position.EntryPrice);
                    _tradesToday++;
                    _lastTradeDate = Time.Date; 
                }
                else
                {
                    Print("Error executing trade: {0}", result.Error);
                }
            }
        }

        // Вспомогательная функция для получения значения ZigZag, пропуская NaNs
        // indexFromEnd: 0 для последнего не-NaN значения, 1 для предпоследнего и т.д.
        private (double value, int index) GetZigZagPoint(IndicatorDataSeries series, int indexFromEnd)
        {
            int foundCount = 0;
            for (int i = series.Count - 1; i >= 0; i--)
            {
                if (!double.IsNaN(series[i]))
                {
                    if (foundCount == indexFromEnd)
                    {
                        return (series[i], i);
                    }
                    foundCount++;
                }
            }
            return (double.NaN, -1); // Недостаточно точек или серия пуста
        }

        protected override void OnStop()
        {
            Print("MarketStructureBot ({0}) stopped.", InstanceName);
            // Здесь можно добавить логику деинициализации, если необходимо
        }
    }
}
