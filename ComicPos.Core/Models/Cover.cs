namespace ComicPos.Core.Models;

public class Cover{

    public Issue Issue { get; set; } = null!;

    public int Id { get; set; }
    public int IssueId { get; set; }
    
    public string BarCodeUPC { get; set; } = string.Empty;
    public int StockQuantity { get; set; }
    public decimal Price { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CoverArtist { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CoverLetter { get; set; } = string.Empty;

}