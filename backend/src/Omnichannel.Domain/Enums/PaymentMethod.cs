namespace Omnichannel.Domain.Enums;

/// <summary>How an order was paid, as the person who records the payment states it.</summary>
public enum PaymentMethod
{
    Cash,
    BankTransfer,
    Card,
    EWallet
}
