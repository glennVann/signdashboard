namespace SignDashboard.Api.Models;

public sealed class ArtworkFile
{
    public int Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "In Design";
    public string UploadedBy { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public int? JobOrderId { get; set; }
    public JobOrder? JobOrder { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
