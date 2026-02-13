namespace TradingServer.Core.Events;

public enum ExchangeEventType
{
    OrderAccepted,
    TradeExecuted,
    OrderCanceled,
    OrderKilled,
    OrderPartialCanceled
}
