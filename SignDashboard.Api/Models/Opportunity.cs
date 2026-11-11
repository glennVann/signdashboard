namespace SignDashboard.Api.Models;

public sealed class Opportunity
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ProjectType { get; set; } = string.Empty;
    public string Stage { get; set; } = "Lead";
    public decimal EstimatedValue { get; set; }
    public int Probability { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime? ExpectedCloseDate { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
