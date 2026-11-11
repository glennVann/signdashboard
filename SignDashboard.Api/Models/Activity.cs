namespace SignDashboard.Api.Models;

public sealed class Activity
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }
    public string Type { get; set; } = "Call";
    public string Subject { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string AssignedTo { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
