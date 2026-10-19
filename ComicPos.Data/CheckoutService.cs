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
        foreach (var line in sale.Lines)
        {
            line.Cover.StockQuantity = Math.Max(0, line.Cover.StockQuantity - line.Quantity);
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
}