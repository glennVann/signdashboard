namespace SignDashboard.Api.Models;

public sealed class StorageAgent
{
    public int Id { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string CustomerFilesRoot { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = "Offline";
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
