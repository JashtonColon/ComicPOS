namespace ComicPos.Core.Models;

public class Sale
{
    public int Id { get; set; }

    public DateTime SoldAt { get; set; } = DateTime.Now;

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public List<SaleLine> Lines { get; set; } = new();
}