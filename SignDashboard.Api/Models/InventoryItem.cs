namespace SignDashboard.Api.Models;

public sealed class InventoryItem
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int QuantityOnHand { get; set; }
    public int ReorderPoint { get; set; }
    public decimal UnitCost { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
