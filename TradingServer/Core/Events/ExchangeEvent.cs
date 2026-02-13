namespace TradingServer.Core.Events;

public record ExchangeEvent(
    long EventId,
    ExchangeEventType Type,
    string CreatedUtc,
    string? Account,
    long? OrderId,
    string DataJson
);
