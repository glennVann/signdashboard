using Microsoft.EntityFrameworkCore;
using SignDashboard.Api.Models;

namespace SignDashboard.Api.Data;

public sealed class SignDashboardDbContext(DbContextOptions<SignDashboardDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLineItem> QuoteLineItems => Set<QuoteLineItem>();
    public DbSet<JobOrder> JobOrders => Set<JobOrder>();
    public DbSet<SignFile> SignFiles => Set<SignFile>();
    public DbSet<ArtworkFile> ArtworkFiles => Set<ArtworkFile>();
    public DbSet<ProofFile> ProofFiles => Set<ProofFile>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<StorageAgent> StorageAgents => Set<StorageAgent>();
    public DbSet<StorageJob> StorageJobs => Set<StorageJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().HasIndex(customer => customer.CompanyName);
        modelBuilder.Entity<InventoryItem>().HasIndex(item => item.Sku).IsUnique();
        modelBuilder.Entity<Quote>().HasIndex(quote => quote.QuoteNumber).IsUnique();
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

        modelBuilder.Entity<Customer>().HasData(
            new Customer { Id = 1, CompanyName = "North Star Retail", ContactName = "Mia Chen", Email = "mia@northstar.example", Phone = "555-0142", Address = "102 Main Street", Notes = "Prefers acrylic lobby signage." },
            new Customer { Id = 2, CompanyName = "Prairie Dental", ContactName = "Evan Brooks", Email = "evan@prairiedental.example", Phone = "555-0188", Address = "44 Market Avenue", Notes = "Annual vinyl refresh customer." });

        modelBuilder.Entity<InventoryItem>().HasData(
            new InventoryItem { Id = 1, Sku = "VINYL-WHT-54", Name = "White gloss vinyl 54in", Category = "Vinyl", QuantityOnHand = 8, ReorderPoint = 3, UnitCost = 129.00m, Supplier = "Sign Supply Co." },
            new InventoryItem { Id = 2, Sku = "ACM-3MM-BLK", Name = "Black ACM 3mm sheet", Category = "Substrate", QuantityOnHand = 22, ReorderPoint = 10, UnitCost = 48.50m, Supplier = "Panel Depot" });
    }
}
