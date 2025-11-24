// See https://aka.ms/new-console-template for more information

using System.Reflection.Metadata.Ecma335;
using System.Transactions;
using TradeRepublicTransactionExtractionTest;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Transaction = TradeRepublicTransactionExtractionTest.Transaction;

var options = new DocstrumBoundingBoxes.DocstrumBoundingBoxesOptions()
{
    // Vertical
    BetweenLineMultiplier = 0.5,
    // Horizontal
    WithinLineMultiplier = 0.78,
    // Make “is this the same horizontal line?” more strict
    WithinLineBounds = new DocstrumBoundingBoxes.AngleBounds(-15, 15),
    BetweenLineBounds = new DocstrumBoundingBoxes.AngleBounds(-15, 15)
};
var document = PdfDocument.Open(@"<PATH_TO_PDF>");
List<TextBlock> cashBlocks = [];
PdfRectangle transactionArea;
TransactionAreaColumns? columns = null;
// TODO: Can we determine this dynamically?
var rowHeightThreshold = 22;
var transactions = new List<Transaction>();
// TODO: Page break must be handled when transaction area spans multiple pages
foreach (var page in document.GetPages())
{
    var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
    var blocks = new DocstrumBoundingBoxes(options).GetBlocks(words);
    var blocksByText = blocks
        .GroupBy(b => b.Text.ToLower().Trim())
        .ToDictionary(g => g.Key, g => g.ToList());

    // TODO get transaction area
    var result = blocksByText.TryGetValue("umsatzübersicht", out var transactionAreaHeadingBoxes);
    if (!result || transactionAreaHeadingBoxes!.Count != 1)
    {
        continue;
    }

    var transactionAreaHeadingBox = transactionAreaHeadingBoxes.First()!.BoundingBox;

    var nextSectionHeading = blocksByText.TryGetValue("TBD", out var nextSectionHeadings)
        ? nextSectionHeadings
            .FirstOrDefault()?.BoundingBox
        : null;

    transactionArea = new PdfRectangle(
        // TODO: Fallback could be determined via Text "Seite 1 von 2"
        nextSectionHeading?.TopLeft ?? new PdfPoint(0, 50),
        transactionAreaHeadingBox.BottomRight);


    var columnKeys = new[] { "datum", "typ", "beschreibung", "zahlungseingang", "zahlungsausgang", "saldo" };
    var columnBoxes = columnKeys.Select(key =>
    {
        if (!blocksByText.TryGetValue(key, out var boxes))
        {
            return null;
        }

        return boxes.FirstOrDefault(b => b.BoundingBox.Top < transactionArea.Top)?.BoundingBox;
    }).ToList();
    if (columnBoxes.Any(box => box == null))
    {
        continue;
    }

    columns = new TransactionAreaColumns()
    {
        DateColumn = columnBoxes[0] ?? new PdfRectangle(),
        TypeColumn = columnBoxes[1] ?? new PdfRectangle(),
        DescriptionColumn = columnBoxes[2] ?? new PdfRectangle(),
        IncomeColumn = columnBoxes[3] ?? new PdfRectangle(),
        ExpenseColumn = columnBoxes[4] ?? new PdfRectangle(),
        BalanceColumn = columnBoxes[5] ?? new PdfRectangle()
    };

    cashBlocks.AddRange(blocks.Where(b =>
        b.BoundingBox.Top < columns.GetValueOrDefault().DateColumn.Bottom &&
        b.BoundingBox.Bottom > transactionArea.Bottom).ToList());
}

if (!columns.HasValue)
{
    Console.WriteLine("Could not find all necessary column headings. Aborting ...");
    return;
}

cashBlocks = cashBlocks
    .OrderByDescending(b => b.BoundingBox.Centroid.Y)
    .ThenBy(b => b.BoundingBox.Centroid.X)
    .ToList();

var groupedBlocksPerRow = cashBlocks
    .GroupBy(b =>
        cashBlocks.FirstOrDefault(cb => Math.Abs(cb.BoundingBox.Top - b.BoundingBox.Top) <= rowHeightThreshold)
            ?.BoundingBox.Top)
    .Select(g => g.ToList())
    .ToList();

var foundColumns = columns.Value;
const int rowHeight = 7;
groupedBlocksPerRow.ForEach(blocks =>
{
    blocks.Sort((a, b) =>
    {
        var isWithinSameLine = Math.Abs(a.BoundingBox.Centroid.Y - b.BoundingBox.Centroid.Y) < rowHeight;
        if (!isWithinSameLine)
        {
            return b.BoundingBox.Centroid.Y.CompareTo(a.BoundingBox.Centroid.Y);
        }

        return a.BoundingBox.Centroid.X.CompareTo(b.BoundingBox.Centroid.X);
    });

    var parsedRow = new CashRow();
    blocks.ForEach(block =>
    {
        var parsedText = block.Text.Replace(block.Separator, " ");
        if (block.BoundingBox.Right < foundColumns.TypeColumn.Left)
        {
            parsedRow.Date += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.DescriptionColumn.Left)
        {
            parsedRow.Type += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.IncomeColumn.Left)
        {
            parsedRow.Description += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.ExpenseColumn.Left)
        {
            parsedRow.Income += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.BalanceColumn.Left)
        {
            parsedRow.Expense += $" {parsedText}";
            return;
        }

        parsedRow.Balance += $" {parsedText}";
    });

    if (parsedRow.Date == null || !DateOnly.TryParse(parsedRow.Date.Trim(), out var bookingDate))
    {
        Console.WriteLine($"Invalid date format: '{parsedRow.Date}'");
        return;
    }

    var preparedAmount = (parsedRow.Income ?? parsedRow.Expense)?.Replace("€", "") ?? "";
    if (!decimal.TryParse(preparedAmount, System.Globalization.NumberStyles.Any,
            new System.Globalization.CultureInfo("de-DE"), out var amount))
    {
        Console.WriteLine($"Invalid amount format: '{parsedRow.Income ?? parsedRow.Expense}'");
        return;
    }

    if (parsedRow.Type == null)
    {
        Console.WriteLine($"Invalid type: '{parsedRow.Type}'");
        return;
    }

    transactions.Add(new Transaction(
        Guid.Empty,
        bookingDate,
        bookingDate,
        parsedRow.Type.Contains("erträge", StringComparison.CurrentCultureIgnoreCase)
            ? TransactionType.Income
            : TransactionType.Expense,
        TransactionProcess.BankTransfer,
        null,
        parsedRow.Description?.Trim() ?? "",
        null,
        amount,
        Guid.Empty
    ));
});

transactions.ForEach(transaction =>
{
    // TODO: Valdidate amount - must it be negative for expenses?
    Console.WriteLine(
        $"{transaction.BookingDate} {transaction.Type} {transaction.IntendedPurpose} {transaction.Amount}");
});


struct TransactionAreaColumns
{
    public PdfRectangle DateColumn;
    public PdfRectangle TypeColumn;
    public PdfRectangle DescriptionColumn;
    public PdfRectangle IncomeColumn;
    public PdfRectangle ExpenseColumn;
    public PdfRectangle BalanceColumn;
};

struct CashRow
{
    public string Date;
    public string Type;
    public string Description;
    public string Income;
    public string Expense;
    public string Balance;
}