namespace TradeRepublicTransactionExtractionTest;

public class Transaction
{
    public Guid Id { get; private set; }
    public DateOnly BookingDate { get; private set; }
    public DateOnly Valuta { get; private set; }
    public TransactionType Type { get; private set; }
    public TransactionProcess Process { get; private set; }
    public string? PaymentPartner { get; private set; }
    public string? IntendedPurpose { get; private set; }
    public string? Reference { get; private set; }
    public decimal Amount { get; private set; }

    public Guid AccountId { get; private set; }

    public Transaction(
        Guid id,
        DateOnly bookingDate,
        DateOnly valuta,
        TransactionType type,
        TransactionProcess process,
        string? paymentPartner,
        string? intendedPurpose,
        string? reference,
        decimal amount,
        Guid accountId
    )
    {
        Id = id;
        BookingDate = bookingDate;
        Valuta = valuta;
        Type = type;
        Process = process;
        PaymentPartner = paymentPartner;
        IntendedPurpose = intendedPurpose;
        Reference = reference;
        Amount = amount;
        AccountId = accountId;
    }
}

public enum TransactionType
{
    Income,
    Expense,
}

public enum TransactionProcess
{
    BankTransfer,
    DirectDebit,
    AccountTransfer,
}