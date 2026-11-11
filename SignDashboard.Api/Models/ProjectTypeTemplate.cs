namespace SignDashboard.Api.Models;

public sealed class ProjectTypeTemplate
{
    public int Id { get; set; }
    public string ProjectType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public List<ProjectTypeTemplateLine> Lines { get; set; } = [];
}
