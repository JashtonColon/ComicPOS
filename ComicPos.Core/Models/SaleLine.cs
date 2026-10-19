namespace ComicPos.Core.Models;

public class SaleLine
{
    public int Id { get; set; }

    public int SaleId { get; set; }
    public Sale Sale { get; set; } = null!;

    public int CoverId { get; set; }
    public Cover Cover { get; set; } = null!;

    public int Quantity { get; set; } = 1;
    public decimal PriceEach { get; set; }
    public decimal DiscountPercent { get; set; }
}