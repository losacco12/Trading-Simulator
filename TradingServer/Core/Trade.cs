namespace TradingServer.Core;

public record Trade(
    string Symbol, 
    int Quantity, 
    decimal Price, 
    long BuyOrderId, 
    long SellOrderId, 
    string BuyerAccount, 
    string SellerAccount
);