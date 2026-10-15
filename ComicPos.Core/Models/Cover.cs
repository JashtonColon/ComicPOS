namespace ComicPos.Core.Models;

public class Cover
{
    public int Id { get; set; }

    public int IssueId { get; set; }
    public Issue Issue { get; set; } = null!;

    public string Barcode { get; set; } = string.Empty;
    public string CoverLetter { get; set; } = "A";
    public string? Description { get; set; }
    public string? CoverArtist { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
}