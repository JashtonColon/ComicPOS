using ComicPos.Core.Models;
using ComicPos.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ComicPos.Data;

public class CheckoutService : ICheckoutService
{
    private readonly ComicDbContext _db;
    private readonly IInventoryService _inventory;

    public CheckoutService(ComicDbContext db, IInventoryService inventory)
    {
        _db = db;
        _inventory = inventory;
    }

    public Sale StartSale(int? customerId = null)
    {
        return new Sale { CustomerId = customerId };
    }

    public bool ScanItem(Sale sale, string barcode, int quantity = 1)
    {
        var cover = _inventory.FindByBarcode(barcode);
        if (cover == null)
            return false;

        var existingLine = sale.Lines.FirstOrDefault(l => l.CoverId == cover.Id);
        if (existingLine != null)
        {
            existingLine.Quantity += quantity;
        }
        else
        {
            sale.Lines.Add(new SaleLine
            {
                Cover = cover,
                CoverId = cover.Id,
                Quantity = quantity,
                PriceEach = cover.Price
            });
        }

        return true;
    }

    public void CompleteSale(Sale sale)
    {
        // Deduct the full quantity sold, even past zero. Clamping this at 0
        // used to throw away the real oversell amount (stock=2, sell 5 ->
        // clamped to 0), which made VoidSale's symmetric "+= line.Quantity"
        // restore inflate stock on void (0 + 5 = 5 instead of back to 2).
        // Letting stock go negative keeps deduct/restore exact opposites of
        // each other and doubles as an honest "oversold by N" signal.
        foreach (var line in sale.Lines)
        {
            line.Cover.StockQuantity -= line.Quantity;
        }

        _db.Sales.Add(sale);
        _db.SaveChanges();
    }

    public List<string> GetLowStockWarnings(Sale sale)
    {
        var warnings = new List<string>();
        foreach (var line in sale.Lines)
        {
            if (line.Quantity > line.Cover.StockQuantity)
            {
                warnings.Add($"{line.Cover.Issue.Series.Name} #{line.Cover.Issue.IssueNumber} Cover {line.Cover.CoverLetter}: only {line.Cover.StockQuantity} in stock, selling {line.Quantity}");
            }
        }
        return warnings;
    }

    public void VoidSale(int saleId)
    {
        var sale = _db.Sales
            .Include(s => s.Lines)
            .ThenInclude(l => l.Cover)
            .FirstOrDefault(s => s.Id == saleId);

        if (sale == null)
            throw new InvalidOperationException($"Sale {saleId} not found.");

        if (sale.IsVoided)
            throw new InvalidOperationException("This sale is already voided.");

        foreach (var line in sale.Lines)
            line.Cover.StockQuantity += line.Quantity;

        sale.IsVoided = true;
        _db.SaveChanges();
    }
}