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

var document = PdfDocument.Open(@"<PATH_TO_PDF_FILE>");
List<TextBlock> cashBlocks = [];
PdfRectangle transactionArea;
TransactionAreaColumns? columns = null;


foreach (var page in document.GetPages())
{
    var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
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
    var blocks = new DocstrumBoundingBoxes(options).GetBlocks(words);

    // TODO get transaction area
    var transactionAreaHeadingBox =
        blocks.FirstOrDefault(b => b.Text.ToLower().Trim() == "umsatzübersicht")?.BoundingBox;
    if (transactionAreaHeadingBox == null)
    {
        continue;
    }

    var nextSectionHeading = blocks.FirstOrDefault(b => b.Text.ToLower().Trim() == "TBD")?.BoundingBox;
    transactionArea = new PdfRectangle(
        nextSectionHeading?.TopLeft ?? new PdfPoint(0, 10),
        transactionAreaHeadingBox.GetValueOrDefault().BottomRight);

    var dateColumnHeadingBox =
        blocks.FirstOrDefault(b => b.Text.ToLower().Trim() == "datum" && b.BoundingBox.Top < transactionArea.Top)
            ?.BoundingBox;
    var typeColumnHeadingBox =
        blocks.FirstOrDefault(b => b.Text.ToLower().Trim() == "typ" && b.BoundingBox.Top < transactionArea.Top)
            ?.BoundingBox;
    var descriptionColumnHeadingBox =
        blocks.FirstOrDefault(b =>
            b.Text.ToLower().Trim() == "beschreibung" && b.BoundingBox.Top < transactionArea.Top)?.BoundingBox;
    var incomeColumnHeadingBox = blocks.FirstOrDefault(b =>
        b.Text.ToLower().Trim() == "zahlungseingang" && b.BoundingBox.Top < transactionArea.Top)?.BoundingBox;
    var expenseColumnHeadingBox = blocks.FirstOrDefault(b =>
        b.Text.ToLower().Trim() == "zahlungsausgang" && b.BoundingBox.Top < transactionArea.Top)?.BoundingBox;
    var balanceColumnHeadingBox =
        blocks.FirstOrDefault(b => b.Text.ToLower().Trim() == "saldo" && b.BoundingBox.Top < transactionArea.Top)
            ?.BoundingBox;
    if (dateColumnHeadingBox == null || typeColumnHeadingBox == null || descriptionColumnHeadingBox == null ||
        incomeColumnHeadingBox == null || expenseColumnHeadingBox == null || balanceColumnHeadingBox == null)
    {
        continue;
    }

    columns = new TransactionAreaColumns()
    {
        dateColumn = dateColumnHeadingBox.GetValueOrDefault(),
        typeColumn = typeColumnHeadingBox.GetValueOrDefault(),
        descriptionColumn = descriptionColumnHeadingBox.GetValueOrDefault(),
        incomeColumn = incomeColumnHeadingBox.GetValueOrDefault(),
        expenseColumn = expenseColumnHeadingBox.GetValueOrDefault(),
        balanceColumn = balanceColumnHeadingBox.GetValueOrDefault()
    };

    cashBlocks.AddRange(blocks.Where(b =>
        b.BoundingBox.Top < dateColumnHeadingBox.GetValueOrDefault().Bottom &&
        b.BoundingBox.Bottom > transactionArea.Bottom).ToList());
}

if (!columns.HasValue)
{
    Console.WriteLine("Could not find all necessary column headings. Aborting ...");
    return;
}

cashBlocks.Sort((a, b) =>
{
    var yComparison = b.BoundingBox.Centroid.Y.CompareTo(a.BoundingBox.Centroid.Y);
    return yComparison != 0 ? yComparison : b.BoundingBox.Centroid.X.CompareTo(a.BoundingBox.Centroid.X);
});

// TODO Process cash blocks row by row and parse them to have a ready to process structure wrt to mapping them to transactions
var rowHeightThreshold = 18;
var blocksPerRow = new List<List<TextBlock>>();
var currentRow = new List<TextBlock>()
{
    cashBlocks[0]
};
for (var i = 1; i < cashBlocks.Count; i++)
{
    if (cashBlocks[i - 1].BoundingBox.Top - cashBlocks[i].BoundingBox.Top > rowHeightThreshold)
    {
        blocksPerRow.Add(currentRow);
        currentRow = [];
    }

    currentRow.Add(cashBlocks[i]);
}

// TODO: Bring actual table rows into structure to gain 1 string per column / property
var foundColumns = columns.Value;
var transactions = new List<Transaction>();
var rowHeight = 7;
blocksPerRow.ForEach(blocks =>
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
        if (block.BoundingBox.Right < foundColumns.typeColumn.Left)
        {
            parsedRow.Date += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.descriptionColumn.Left)
        {
            parsedRow.Type += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.incomeColumn.Left)
        {
            parsedRow.Description += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.expenseColumn.Left)
        {
            parsedRow.Income += $" {parsedText}";
            return;
        }

        if (block.BoundingBox.Right < foundColumns.balanceColumn.Left)
        {
            parsedRow.Expense += $" {parsedText}";
            return;
        }

        parsedRow.Balance += $" {parsedText}";
    });

    transactions.Add(new Transaction(
        Guid.Empty,
        DateOnly.Parse(parsedRow.Date.Trim()),
        DateOnly.Parse(parsedRow.Date.Trim()),
        parsedRow.Type.Trim().ToLower().Contains("erträge") ? TransactionType.Income : TransactionType.Expense,
        TransactionProcess.BankTransfer,
        null,
        parsedRow.Description.Trim(),
        null,
        parsedRow.Income != null
            ? decimal.Parse(parsedRow.Income.Trim().Replace(".", "").Replace(",", ".").Replace("€", ""))
            : decimal.Parse(parsedRow.Expense.Trim().Replace(".", "").Replace(",", ".").Replace("€", "")),
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
    public PdfRectangle dateColumn;
    public PdfRectangle typeColumn;
    public PdfRectangle descriptionColumn;
    public PdfRectangle incomeColumn;
    public PdfRectangle expenseColumn;
    public PdfRectangle balanceColumn;
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