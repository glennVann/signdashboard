namespace SignDashboard.Api.Models;

public sealed class Quote
{
    public int Id { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Status { get; set; } = "Draft";
    public string Scope { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public DateTime ValidUntil { get; set; } = DateTime.UtcNow.AddDays(30);
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<QuoteLineItem> LineItems { get; set; } = [];
}
