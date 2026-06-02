namespace SignDashboard.Api.Models;

public sealed class StorageJob
{
    public int Id { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string PayloadJson { get; set; } = string.Empty;
    public string AssignedAgentName { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public string LastError { get; set; } = string.Empty;
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
