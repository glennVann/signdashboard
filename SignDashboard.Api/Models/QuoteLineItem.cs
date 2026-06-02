namespace SignDashboard.Api.Models;

public sealed class QuoteLineItem
{
    public int Id { get; set; }
    public int QuoteId { get; set; }
    public Quote? Quote { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}
