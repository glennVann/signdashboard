namespace SignDashboard.Api.Models;

public sealed class Ticket
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string Priority { get; set; } = "Normal";
    public string AssignedTo { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public int SortOrder { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
