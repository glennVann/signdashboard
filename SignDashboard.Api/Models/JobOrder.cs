namespace SignDashboard.Api.Models;

public sealed class JobOrder
{
    public int Id { get; set; }
    public string JobNumber { get; set; } = string.Empty;
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public int? QuoteId { get; set; }
    public Quote? Quote { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "Artwork";
    public string AssignedTo { get; set; } = string.Empty;
    public string ProductionNotes { get; set; } = string.Empty;
    public bool DxfReady { get; set; }
    public bool BendFileReady { get; set; }
    public bool BacksReady { get; set; }
    public bool FacesReady { get; set; }
    public bool LedLayoutReady { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
