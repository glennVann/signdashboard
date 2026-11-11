namespace SignDashboard.Api.Models;

public sealed class SignFile
{
    public int Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Category { get; set; } = "General";
    public string Notes { get; set; } = string.Empty;
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public int? JobOrderId { get; set; }
    public JobOrder? JobOrder { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
