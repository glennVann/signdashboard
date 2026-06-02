namespace SignDashboard.Api.Models;

public sealed class ProofFile
{
    public int Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "Draft";
    public string SentTo { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string Designer { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string ReviewToken { get; set; } = string.Empty;
    public DateTime? ReviewTokenExpiresAt { get; set; }
    public string CustomerDecision { get; set; } = string.Empty;
    public string CustomerComment { get; set; } = string.Empty;
    public DateTime? CustomerRespondedAt { get; set; }
    public string CustomerIpAddress { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public int? JobOrderId { get; set; }
    public JobOrder? JobOrder { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
