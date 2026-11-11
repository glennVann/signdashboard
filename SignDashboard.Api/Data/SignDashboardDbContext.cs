using Microsoft.EntityFrameworkCore;
using SignDashboard.Api.Models;

namespace SignDashboard.Api.Data;

public sealed class SignDashboardDbContext(DbContextOptions<SignDashboardDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ProjectTypeTemplate> ProjectTypeTemplates => Set<ProjectTypeTemplate>();
    public DbSet<ProjectTypeTemplateLine> ProjectTypeTemplateLines => Set<ProjectTypeTemplateLine>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLineItem> QuoteLineItems => Set<QuoteLineItem>();
    public DbSet<JobWorker> JobWorkers => Set<JobWorker>();
    public DbSet<JobOrder> JobOrders => Set<JobOrder>();
    public DbSet<SignFile> SignFiles => Set<SignFile>();
    public DbSet<ArtworkFile> ArtworkFiles => Set<ArtworkFile>();
    public DbSet<ProofFile> ProofFiles => Set<ProofFile>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<StorageAgent> StorageAgents => Set<StorageAgent>();
    public DbSet<StorageJob> StorageJobs => Set<StorageJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().HasIndex(customer => new { customer.IsArchived, customer.CompanyName });
        modelBuilder.Entity<Contact>().HasIndex(contact => new { contact.CustomerId, contact.LastName, contact.FirstName });
        modelBuilder.Entity<Lead>().HasIndex(lead => new { lead.Status, lead.CreatedAt });
        modelBuilder.Entity<Opportunity>().HasIndex(opportunity => new { opportunity.CustomerId, opportunity.Stage });
        modelBuilder.Entity<Activity>().HasIndex(activity => new { activity.CustomerId, activity.CreatedAt });
        modelBuilder.Entity<Activity>().HasIndex(activity => activity.OpportunityId);
        modelBuilder.Entity<ProjectTypeTemplate>().HasIndex(template => template.ProjectType).IsUnique();
        modelBuilder.Entity<ProjectTypeTemplateLine>().HasIndex(line => new { line.ProjectTypeTemplateId, line.SortOrder });
        modelBuilder.Entity<InventoryItem>().HasIndex(item => item.Sku).IsUnique();
        modelBuilder.Entity<Quote>().HasIndex(quote => quote.QuoteNumber).IsUnique();
        modelBuilder.Entity<Quote>().HasIndex(quote => quote.OpportunityId);
        modelBuilder.Entity<JobWorker>().HasIndex(worker => new { worker.IsActive, worker.SortOrder });
        modelBuilder.Entity<JobOrder>().HasIndex(job => job.JobNumber).IsUnique();
        modelBuilder.Entity<AppSetting>().HasIndex(setting => setting.Key).IsUnique();
        modelBuilder.Entity<StorageAgent>().HasIndex(agent => agent.AgentName).IsUnique();
        modelBuilder.Entity<StorageJob>().HasIndex(job => new { job.Status, job.CreatedAt });
        modelBuilder.Entity<ArtworkFile>().HasIndex(file => new { file.TicketId, file.Version });
        modelBuilder.Entity<ArtworkFile>().HasIndex(file => new { file.JobOrderId, file.Version });
        modelBuilder.Entity<ProofFile>().HasIndex(file => new { file.TicketId, file.Version });
        modelBuilder.Entity<ProofFile>().HasIndex(file => new { file.JobOrderId, file.Version });

        modelBuilder.Entity<InventoryItem>().Property(item => item.UnitCost).HasPrecision(12, 2);
        modelBuilder.Entity<Quote>().Property(quote => quote.Subtotal).HasPrecision(12, 2);
        modelBuilder.Entity<Quote>().Property(quote => quote.Tax).HasPrecision(12, 2);
        modelBuilder.Entity<Quote>().Property(quote => quote.Total).HasPrecision(12, 2);
        modelBuilder.Entity<QuoteLineItem>().Property(item => item.UnitPrice).HasPrecision(12, 2);
        modelBuilder.Entity<QuoteLineItem>().Property(item => item.LineTotal).HasPrecision(12, 2);
        modelBuilder.Entity<Lead>().Property(lead => lead.EstimatedValue).HasPrecision(12, 2);
        modelBuilder.Entity<Opportunity>().Property(opportunity => opportunity.EstimatedValue).HasPrecision(12, 2);
        modelBuilder.Entity<ProjectTypeTemplateLine>().Property(line => line.DefaultUnitPrice).HasPrecision(12, 2);

        modelBuilder.Entity<Customer>().HasData(
            new Customer { Id = 1, CompanyName = "North Star Retail", ContactName = "Mia Chen", Email = "mia@northstar.example", Phone = "555-0142", Address = "102 Main Street", Notes = "Prefers acrylic lobby signage." },
            new Customer { Id = 2, CompanyName = "Prairie Dental", ContactName = "Evan Brooks", Email = "evan@prairiedental.example", Phone = "555-0188", Address = "44 Market Avenue", Notes = "Annual vinyl refresh customer." });

        modelBuilder.Entity<Contact>().HasData(
            new Contact { Id = 1, CustomerId = 1, FirstName = "Mia", LastName = "Chen", Title = "Marketing Director", Email = "mia@northstar.example", Phone = "555-0142", IsPrimaryContact = true, Notes = "Approves lobby and wayfinding packages." },
            new Contact { Id = 2, CustomerId = 2, FirstName = "Evan", LastName = "Brooks", Title = "Practice Manager", Email = "evan@prairiedental.example", Phone = "555-0188", IsPrimaryContact = true, Notes = "Coordinates annual vinyl updates and install windows." });

        modelBuilder.Entity<Lead>().HasData(
            new Lead { Id = 1, CompanyName = "Summit Fitness", ContactName = "Aria Patel", Email = "aria@summitfitness.example", Phone = "555-0199", Source = "Website", Status = "Qualified", EstimatedValue = 7800m, Notes = "Interested in exterior illuminated signage package." });

        modelBuilder.Entity<Opportunity>().HasData(
            new Opportunity { Id = 1, CustomerId = 1, Title = "Spring storefront refresh", ProjectType = "Channel Letters", Stage = "Quoted", EstimatedValue = 12450m, Probability = 70, Source = "Repeat Business", ExpectedCloseDate = DateTime.UtcNow.Date.AddDays(14), Notes = "Awaiting final budget sign-off after proof revision." },
            new Opportunity { Id = 2, CustomerId = 2, Title = "Reception and room signage upgrade", ProjectType = "Wayfinding Package", Stage = "Survey", EstimatedValue = 6800m, Probability = 45, Source = "Referral", ExpectedCloseDate = DateTime.UtcNow.Date.AddDays(21), Notes = "Site survey needed for room numbering and ADA compliance." });

        modelBuilder.Entity<Activity>().HasData(
            new Activity { Id = 1, CustomerId = 1, OpportunityId = 1, Type = "Email", Subject = "Proof follow-up", Notes = "Sent revised front-lit channel letter proof for budget approval.", AssignedTo = "Sales", Status = "Open", DueAt = DateTime.UtcNow.Date.AddDays(2), CreatedAt = DateTime.UtcNow.AddDays(-1) },
            new Activity { Id = 2, CustomerId = 2, OpportunityId = 2, Type = "Site Survey", Subject = "Book measurement visit", Notes = "Confirm install access and capture doorway widths.", AssignedTo = "Estimator", Status = "Open", DueAt = DateTime.UtcNow.Date.AddDays(3), CreatedAt = DateTime.UtcNow.AddDays(-2) });

        modelBuilder.Entity<ProjectTypeTemplate>().HasData(
            new ProjectTypeTemplate { Id = 1, ProjectType = "Channel Letters", Description = "Illuminated or non-illuminated storefront letter sets.", SortOrder = 1 },
            new ProjectTypeTemplate { Id = 2, ProjectType = "Cabinet Sign", Description = "Face changes and new cabinet sign builds.", SortOrder = 2 },
            new ProjectTypeTemplate { Id = 3, ProjectType = "Window Graphics", Description = "Perforated, frosted, and promotional window films.", SortOrder = 3 },
            new ProjectTypeTemplate { Id = 4, ProjectType = "Vehicle Wrap", Description = "Partial and full commercial vehicle graphics.", SortOrder = 4 },
            new ProjectTypeTemplate { Id = 5, ProjectType = "Wayfinding Package", Description = "Interior directories, ADA signs, and room IDs.", SortOrder = 5 });

        modelBuilder.Entity<ProjectTypeTemplateLine>().HasData(
            new ProjectTypeTemplateLine { Id = 1, ProjectTypeTemplateId = 1, Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 250m, SortOrder = 1 },
            new ProjectTypeTemplateLine { Id = 2, ProjectTypeTemplateId = 1, Description = "Shop drawings", DefaultQuantity = 1, DefaultUnitPrice = 180m, SortOrder = 2 },
            new ProjectTypeTemplateLine { Id = 3, ProjectTypeTemplateId = 1, Description = "Channel letter fabrication", DefaultQuantity = 1, DefaultUnitPrice = 2400m, SortOrder = 3 },
            new ProjectTypeTemplateLine { Id = 4, ProjectTypeTemplateId = 1, Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 900m, SortOrder = 4 },
            new ProjectTypeTemplateLine { Id = 5, ProjectTypeTemplateId = 2, Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 200m, SortOrder = 1 },
            new ProjectTypeTemplateLine { Id = 6, ProjectTypeTemplateId = 2, Description = "Cabinet fabrication", DefaultQuantity = 1, DefaultUnitPrice = 2100m, SortOrder = 2 },
            new ProjectTypeTemplateLine { Id = 7, ProjectTypeTemplateId = 2, Description = "Face / vinyl graphics", DefaultQuantity = 1, DefaultUnitPrice = 650m, SortOrder = 3 },
            new ProjectTypeTemplateLine { Id = 8, ProjectTypeTemplateId = 2, Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 850m, SortOrder = 4 },
            new ProjectTypeTemplateLine { Id = 9, ProjectTypeTemplateId = 3, Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 150m, SortOrder = 1 },
            new ProjectTypeTemplateLine { Id = 10, ProjectTypeTemplateId = 3, Description = "Print / production", DefaultQuantity = 1, DefaultUnitPrice = 480m, SortOrder = 2 },
            new ProjectTypeTemplateLine { Id = 11, ProjectTypeTemplateId = 3, Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 320m, SortOrder = 3 },
            new ProjectTypeTemplateLine { Id = 12, ProjectTypeTemplateId = 4, Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 350m, SortOrder = 1 },
            new ProjectTypeTemplateLine { Id = 13, ProjectTypeTemplateId = 4, Description = "Print / laminate", DefaultQuantity = 1, DefaultUnitPrice = 1900m, SortOrder = 2 },
            new ProjectTypeTemplateLine { Id = 14, ProjectTypeTemplateId = 4, Description = "Vehicle prep and installation", DefaultQuantity = 1, DefaultUnitPrice = 1200m, SortOrder = 3 },
            new ProjectTypeTemplateLine { Id = 15, ProjectTypeTemplateId = 5, Description = "Site survey", DefaultQuantity = 1, DefaultUnitPrice = 220m, SortOrder = 1 },
            new ProjectTypeTemplateLine { Id = 16, ProjectTypeTemplateId = 5, Description = "Design / schedule set", DefaultQuantity = 1, DefaultUnitPrice = 360m, SortOrder = 2 },
            new ProjectTypeTemplateLine { Id = 17, ProjectTypeTemplateId = 5, Description = "Fabrication", DefaultQuantity = 1, DefaultUnitPrice = 1600m, SortOrder = 3 },
            new ProjectTypeTemplateLine { Id = 18, ProjectTypeTemplateId = 5, Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 740m, SortOrder = 4 });

        modelBuilder.Entity<JobWorker>().HasData(
            new JobWorker { Id = 1, Name = "Aiden Cruz", Role = "Design", IsActive = true, SortOrder = 1 },
            new JobWorker { Id = 2, Name = "Maya Chen", Role = "Proofing", IsActive = true, SortOrder = 2 },
            new JobWorker { Id = 3, Name = "Leo Brooks", Role = "Production", IsActive = true, SortOrder = 3 },
            new JobWorker { Id = 4, Name = "Sofia Patel", Role = "Install", IsActive = true, SortOrder = 4 });

        modelBuilder.Entity<InventoryItem>().HasData(
            new InventoryItem { Id = 1, Sku = "VINYL-WHT-54", Name = "White gloss vinyl 54in", Category = "Vinyl", QuantityOnHand = 8, ReorderPoint = 3, UnitCost = 129.00m, Supplier = "Sign Supply Co." },
            new InventoryItem { Id = 2, Sku = "ACM-3MM-BLK", Name = "Black ACM 3mm sheet", Category = "Substrate", QuantityOnHand = 22, ReorderPoint = 10, UnitCost = 48.50m, Supplier = "Panel Depot" });
    }
}
