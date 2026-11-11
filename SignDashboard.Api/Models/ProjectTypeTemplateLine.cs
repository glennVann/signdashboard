namespace SignDashboard.Api.Models;

public sealed class ProjectTypeTemplateLine
{
    public int Id { get; set; }
    public int ProjectTypeTemplateId { get; set; }
    public ProjectTypeTemplate? ProjectTypeTemplate { get; set; }
    public string Description { get; set; } = string.Empty;
    public int DefaultQuantity { get; set; } = 1;
    public decimal DefaultUnitPrice { get; set; }
    public int SortOrder { get; set; }
}
