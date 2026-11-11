using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http.Features;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using System.Security.Cryptography;
using System.Text.Json;
using SignDashboard.Api.Data;
using SignDashboard.Api.Hubs;
using SignDashboard.Api.Models;

LoadEnvFiles();

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("MARIADB_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("MariaDb")
    ?? "Server=10.10.4.23;Port=3306;Database=sign;User=glenn;Password=glenn2722;";
var maxUploadBytes = ParseMaxUploadBytes(
    Environment.GetEnvironmentVariable("MAX_UPLOAD_BYTES")
    ?? builder.Configuration["MAX_UPLOAD_BYTES"]);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("Dashboard", policy =>
        policy.WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});
builder.Services.AddDbContext<SignDashboardDbContext>(options =>
    options.UseMySql(connectionString, new MariaDbServerVersion(new Version(10, 11, 0))));
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Dashboard");
app.UseStaticFiles();
app.MapHub<DashboardHub>("/hubs/dashboard");

var api = app.MapGroup("/api");

api.MapGet("/settings", async (SignDashboardDbContext db) =>
{
    var settings = await db.AppSettings.ToDictionaryAsync(setting => setting.Key, setting => setting.Value);
    settings.TryAdd("CUSTOMER_FILES_ROOT", Environment.GetEnvironmentVariable("CUSTOMER_FILES_ROOT") ?? string.Empty);
    settings.TryAdd("STORAGE_MODE", Environment.GetEnvironmentVariable("STORAGE_MODE") ?? "Direct");
    settings.TryAdd("LOCAL_AGENT_TOKEN", Environment.GetEnvironmentVariable("LOCAL_AGENT_TOKEN") ?? string.Empty);
    return Results.Ok(settings);
});

api.MapPut("/settings", async (SettingsRequest request, SignDashboardDbContext db) =>
{
    await UpsertSetting(db, "CUSTOMER_FILES_ROOT", (request.CustomerFilesRoot ?? string.Empty).Trim());
    await UpsertSetting(db, "STORAGE_MODE", NormalizeStorageMode(request.StorageMode));
    await UpsertSetting(db, "LOCAL_AGENT_TOKEN", (request.LocalAgentToken ?? string.Empty).Trim());
    await db.SaveChangesAsync();

    return Results.Ok(await db.AppSettings.ToDictionaryAsync(setting => setting.Key, setting => setting.Value));
});

api.MapGet("/job-workers", async (SignDashboardDbContext db) =>
    await db.JobWorkers
        .Where(worker => worker.IsActive)
        .OrderBy(worker => worker.SortOrder)
        .Select(worker => new
        {
            worker.Id,
            worker.Name,
            worker.Role,
            DisplayName = $"{worker.Name} / {worker.Role}"
        })
        .ToListAsync());

api.MapGet("/storage/summary", async (SignDashboardDbContext db) =>
{
    var now = DateTime.UtcNow;
    var latestAgent = await db.StorageAgents
        .OrderByDescending(agent => agent.LastSeenAt)
        .FirstOrDefaultAsync();
    var queue = await db.StorageJobs
        .GroupBy(job => job.Status)
        .Select(group => new { Status = group.Key, Count = group.Count() })
        .ToDictionaryAsync(item => item.Status, item => item.Count);
    var recentJobs = await db.StorageJobs
        .Include(job => job.Customer)
        .OrderByDescending(job => job.UpdatedAt)
        .Take(8)
        .Select(job => new
        {
            job.Id,
            job.JobType,
            job.Status,
            job.AssignedAgentName,
            job.Attempts,
            job.LastError,
            job.CreatedAt,
            job.UpdatedAt,
            job.CompletedAt,
            CustomerName = job.Customer == null ? string.Empty : job.Customer.CompanyName
        })
        .ToListAsync();

    return Results.Ok(new
    {
        Agent = latestAgent is null ? null : new
        {
            latestAgent.AgentName,
            latestAgent.CustomerFilesRoot,
            latestAgent.Version,
            Status = latestAgent.LastSeenAt >= now.AddMinutes(-2) ? "Online" : "Offline",
            latestAgent.LastSeenAt,
            latestAgent.UpdatedAt
        },
        Queue = new
        {
            Pending = queue.GetValueOrDefault("Pending"),
            InProgress = queue.GetValueOrDefault("InProgress"),
            Completed = queue.GetValueOrDefault("Completed"),
            Failed = queue.GetValueOrDefault("Failed")
        },
        RecentJobs = recentJobs
    });
});

api.MapGet("/agent/download", (IWebHostEnvironment env) =>
{
    var packagePath = Path.Combine(GetAgentDownloadsRoot(env), "SignDashboard.Agent.win-x64.zip");
    if (!File.Exists(packagePath))
    {
        return Results.Problem(
            "The local agent package has not been created yet. Run scripts\\package-agent.ps1, then restart or refresh this endpoint.",
            statusCode: StatusCodes.Status404NotFound);
    }

    return Results.File(
        packagePath,
        "application/zip",
        "SignDashboard.Agent.win-x64.zip");
});

api.MapPost("/agent/heartbeat", async (AgentHeartbeatRequest request, HttpRequest httpRequest, SignDashboardDbContext db) =>
{
    if (!await IsAuthorizedAgent(httpRequest, db))
    {
        return Results.Unauthorized();
    }

    var agentName = string.IsNullOrWhiteSpace(request.AgentName) ? "default-agent" : request.AgentName.Trim();
    var agent = await db.StorageAgents.FirstOrDefaultAsync(item => item.AgentName == agentName);
    if (agent is null)
    {
        agent = new StorageAgent { AgentName = agentName, CreatedAt = DateTime.UtcNow };
        db.StorageAgents.Add(agent);
    }

    agent.CustomerFilesRoot = (request.CustomerFilesRoot ?? string.Empty).Trim();
    agent.Version = (request.Version ?? string.Empty).Trim();
    agent.Status = "Online";
    agent.LastSeenAt = DateTime.UtcNow;
    agent.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(new { agent.AgentName, agent.Status, agent.LastSeenAt });
});

api.MapGet("/agent/jobs/next", async (string agentName, HttpRequest httpRequest, SignDashboardDbContext db) =>
{
    if (!await IsAuthorizedAgent(httpRequest, db))
    {
        return Results.Unauthorized();
    }

    var job = await db.StorageJobs
        .Where(item => item.Status == "Pending")
        .OrderBy(item => item.CreatedAt)
        .FirstOrDefaultAsync();

    if (job is null)
    {
        return Results.NoContent();
    }

    job.Status = "InProgress";
    job.AssignedAgentName = string.IsNullOrWhiteSpace(agentName) ? "default-agent" : agentName.Trim();
    job.Attempts += 1;
    job.StartedAt = DateTime.UtcNow;
    job.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        job.Id,
        job.JobType,
        job.PayloadJson,
        job.Attempts
    });
});

api.MapGet("/agent/jobs/{id:int}/content", async (int id, HttpRequest httpRequest, SignDashboardDbContext db, IWebHostEnvironment env) =>
{
    if (!await IsAuthorizedAgent(httpRequest, db))
    {
        return Results.Unauthorized();
    }

    var job = await db.StorageJobs.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (job.JobType is not "StoreArtworkFile" and not "StoreProofFile")
    {
        return Results.BadRequest("This storage job does not have downloadable content.");
    }

    var payload = JsonSerializer.Deserialize<FileStorageJobPayload>(
        job.PayloadJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (payload is null || string.IsNullOrWhiteSpace(payload.RelativePath))
    {
        return Results.BadRequest("Storage job payload is invalid.");
    }

    var stagedRoot = GetAgentStagingRoot(env);
    var fullPath = Path.GetFullPath(Path.Combine(stagedRoot, payload.RelativePath));
    var rootPath = Path.GetFullPath(stagedRoot);
    if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    return Results.File(fullPath, payload.ContentType, payload.OriginalFileName);
});

api.MapPost("/agent/jobs/{id:int}/complete", async (int id, AgentJobCompleteRequest request, HttpRequest httpRequest, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    if (!await IsAuthorizedAgent(httpRequest, db))
    {
        return Results.Unauthorized();
    }

    var job = await db.StorageJobs.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    job.Status = "Completed";
    job.LastError = string.Empty;
    job.CompletedAt = DateTime.UtcNow;
    job.UpdatedAt = DateTime.UtcNow;
    if (!string.IsNullOrWhiteSpace(request.AgentName))
    {
        job.AssignedAgentName = request.AgentName.Trim();
    }

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "storage.job_completed", job.Id);
    return Results.Ok(job);
});

api.MapPost("/agent/jobs/{id:int}/fail", async (int id, AgentJobFailRequest request, HttpRequest httpRequest, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    if (!await IsAuthorizedAgent(httpRequest, db))
    {
        return Results.Unauthorized();
    }

    var job = await db.StorageJobs.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    job.Status = "Failed";
    job.LastError = (request.ErrorMessage ?? string.Empty).Trim();
    job.UpdatedAt = DateTime.UtcNow;
    if (!string.IsNullOrWhiteSpace(request.AgentName))
    {
        job.AssignedAgentName = request.AgentName.Trim();
    }

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "storage.job_failed", job.Id);
    return Results.Ok(job);
});

api.MapGet("/dashboard", async (SignDashboardDbContext db) =>
{
    var openTickets = await db.Tickets.CountAsync(ticket => ticket.Status != "Done");
    var activeJobs = await db.JobOrders.CountAsync(job => job.Status != "Completed");
    var lowStock = await db.InventoryItems.CountAsync(item => item.QuantityOnHand <= item.ReorderPoint);
    var quoteTotal = await db.Quotes.Where(quote => quote.Status != "Rejected").SumAsync(quote => quote.Total);

    return Results.Ok(new { openTickets, activeJobs, lowStock, quoteTotal });
});

api.MapGet("/customers", async (SignDashboardDbContext db) =>
    await db.Customers
        .Where(customer => !customer.IsArchived)
        .OrderBy(customer => customer.CompanyName)
        .ToListAsync());

api.MapPost("/customers", async (CustomerRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var customer = new Customer
    {
        CompanyName = request.CompanyName,
        ContactName = request.ContactName,
        Email = request.Email,
        Phone = request.Phone,
        Address = request.Address,
        Notes = request.Notes
    };

    db.Customers.Add(customer);
    await db.SaveChangesAsync();
    await CreateCustomerStorage(db, customer);
    await BroadcastDashboardChange(hub, "customer.created", customer.Id);
    return Results.Created($"/api/customers/{customer.Id}", customer);
});

api.MapPut("/customers/{id:int}", async (int id, CustomerRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    customer.CompanyName = request.CompanyName;
    customer.ContactName = request.ContactName;
    customer.Email = request.Email;
    customer.Phone = request.Phone;
    customer.Address = request.Address;
    customer.Notes = request.Notes;
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "customer.updated", customer.Id);
    return Results.Ok(customer);
});

api.MapPut("/customers/{id:int}/archive", async (int id, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    customer.IsArchived = true;
    customer.ArchivedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "customer.archived", customer.Id);
    return Results.Ok(customer);
});

api.MapGet("/customers/{id:int}/crm", async (int id, SignDashboardDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var contacts = await db.Contacts
        .Where(contact => contact.CustomerId == id)
        .OrderByDescending(contact => contact.IsPrimaryContact)
        .ThenBy(contact => contact.LastName)
        .ThenBy(contact => contact.FirstName)
        .ToListAsync();

    var opportunities = await db.Opportunities
        .Where(opportunity => opportunity.CustomerId == id)
        .OrderByDescending(opportunity => opportunity.UpdatedAt)
        .ToListAsync();

    var activities = await db.Activities
        .Where(activity => activity.CustomerId == id)
        .Include(activity => activity.Opportunity)
        .OrderBy(activity => activity.Status == "Completed" ? 1 : 0)
        .ThenBy(activity => activity.DueAt ?? DateTime.MaxValue)
        .ThenByDescending(activity => activity.CreatedAt)
        .Select(activity => new
        {
            activity.Id,
            activity.Type,
            activity.Subject,
            activity.Notes,
            activity.AssignedTo,
            activity.Status,
            activity.DueAt,
            activity.CompletedAt,
            activity.CreatedAt,
            activity.OpportunityId,
            OpportunityTitle = activity.Opportunity == null ? string.Empty : activity.Opportunity.Title
        })
        .Take(24)
        .ToListAsync();

    var recentQuotes = await db.Quotes
        .Where(quote => quote.CustomerId == id)
        .OrderByDescending(quote => quote.CreatedAt)
        .Take(5)
        .Select(quote => new
        {
            quote.Id,
            quote.QuoteNumber,
            quote.OpportunityId,
            OpportunityTitle = quote.Opportunity == null ? string.Empty : quote.Opportunity.Title,
            quote.ProjectType,
            quote.Status,
            quote.Total,
            quote.ValidUntil,
            quote.CreatedAt
        })
        .ToListAsync();

    var recentJobs = await db.JobOrders
        .Where(job => job.Quote != null && job.Quote.CustomerId == id || job.Ticket != null && job.Ticket.CustomerId == id)
        .OrderByDescending(job => job.CreatedAt)
        .Take(5)
        .Select(job => new
        {
            job.Id,
            job.JobNumber,
            job.Title,
            job.Status,
            job.AssignedTo,
            job.DueDate,
            job.CreatedAt
        })
        .ToListAsync();

    var recentTickets = await db.Tickets
        .Where(ticket => ticket.CustomerId == id)
        .OrderByDescending(ticket => ticket.UpdatedAt)
        .Take(5)
        .Select(ticket => new
        {
            ticket.Id,
            ticket.Title,
            ticket.Status,
            ticket.Priority,
            ticket.AssignedTo,
            ticket.DueDate,
            ticket.UpdatedAt
        })
        .ToListAsync();

    var recentFiles = await db.SignFiles
        .Where(file => file.CustomerId == id)
        .OrderByDescending(file => file.UploadedAt)
        .Take(5)
        .Select(file => new
        {
            file.Id,
            file.OriginalFileName,
            file.ContentType,
            file.SizeBytes,
            file.UploadedAt
        })
        .ToListAsync();

    var jobStatusBreakdown = await db.JobOrders
        .Where(job => job.Quote != null && job.Quote.CustomerId == id || job.Ticket != null && job.Ticket.CustomerId == id)
        .GroupBy(job => job.Status)
        .Select(group => new
        {
            Label = group.Key,
            Count = group.Count()
        })
        .OrderByDescending(item => item.Count)
        .ToListAsync();

    var ticketStatusBreakdown = await db.Tickets
        .Where(ticket => ticket.CustomerId == id)
        .GroupBy(ticket => ticket.Status)
        .Select(group => new
        {
            Label = group.Key,
            Count = group.Count()
        })
        .OrderByDescending(item => item.Count)
        .ToListAsync();

    var opportunityStageBreakdown = opportunities
        .GroupBy(opportunity => opportunity.Stage)
        .Select(group => new
        {
            Label = group.Key,
            Count = group.Count(),
            Value = group.Sum(item => item.EstimatedValue)
        })
        .OrderByDescending(item => item.Count)
        .ToList();

    var pipelineValue = opportunities
        .Where(opportunity => opportunity.Stage is not ("Won" or "Lost"))
        .Sum(opportunity => opportunity.EstimatedValue);
    var totalQuotes = await db.Quotes.CountAsync(quote => quote.CustomerId == id);
    var activeJobs = await db.JobOrders.CountAsync(job => (job.Quote != null && job.Quote.CustomerId == id || job.Ticket != null && job.Ticket.CustomerId == id) && job.Status != "Completed");
    var openTickets = await db.Tickets.CountAsync(ticket =>
        ticket.CustomerId == id &&
        ticket.Status != "Done" &&
        ticket.Status != "Closed" &&
        ticket.Status != "Completed");

    return Results.Ok(new
    {
        account = customer,
        contacts,
        opportunities,
        activities,
        recentQuotes,
        recentJobs,
        recentTickets,
        recentFiles,
        charts = new
        {
            opportunityStageBreakdown,
            jobStatusBreakdown,
            ticketStatusBreakdown
        },
        metrics = new
        {
            openOpportunities = opportunities.Count(opportunity => opportunity.Stage is not ("Won" or "Lost")),
            pipelineValue,
            openActivities = activities.Count(activity => activity.Status != "Completed"),
            totalQuotes,
            activeJobs,
            openTickets,
            lastActivityAt = activities.FirstOrDefault()?.CreatedAt
        }
    });
});

api.MapPost("/customers/{id:int}/contacts", async (int id, ContactRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var shouldBePrimary = request.IsPrimaryContact || !await db.Contacts.AnyAsync(contact => contact.CustomerId == id);
    if (shouldBePrimary)
    {
        var existingPrimaryContacts = await db.Contacts.Where(contact => contact.CustomerId == id && contact.IsPrimaryContact).ToListAsync();
        foreach (var existingPrimaryContact in existingPrimaryContacts)
        {
            existingPrimaryContact.IsPrimaryContact = false;
        }
    }

    var contact = new Contact
    {
        CustomerId = id,
        FirstName = request.FirstName.Trim(),
        LastName = request.LastName.Trim(),
        Title = request.Title.Trim(),
        Email = request.Email.Trim(),
        Phone = request.Phone.Trim(),
        IsPrimaryContact = shouldBePrimary,
        Notes = request.Notes.Trim()
    };

    if (shouldBePrimary)
    {
        SyncCustomerFromPrimaryContact(customer, contact);
    }

    db.Contacts.Add(contact);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "contact.created", contact.Id);
    return Results.Created($"/api/customers/{id}/contacts/{contact.Id}", contact);
});

api.MapPut("/contacts/{id:int}", async (int id, ContactRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var contact = await db.Contacts.Include(item => item.Customer).FirstOrDefaultAsync(item => item.Id == id);
    if (contact is null)
    {
        return Results.NotFound();
    }

    var shouldBePrimary = request.IsPrimaryContact;
    if (shouldBePrimary)
    {
        var siblingContacts = await db.Contacts.Where(item => item.CustomerId == contact.CustomerId && item.Id != contact.Id && item.IsPrimaryContact).ToListAsync();
        foreach (var siblingContact in siblingContacts)
        {
            siblingContact.IsPrimaryContact = false;
        }
    }

    contact.FirstName = request.FirstName.Trim();
    contact.LastName = request.LastName.Trim();
    contact.Title = request.Title.Trim();
    contact.Email = request.Email.Trim();
    contact.Phone = request.Phone.Trim();
    contact.IsPrimaryContact = shouldBePrimary;
    contact.Notes = request.Notes.Trim();

    if (shouldBePrimary && contact.Customer is not null)
    {
        SyncCustomerFromPrimaryContact(contact.Customer, contact);
    }

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "contact.updated", contact.Id);
    return Results.Ok(contact);
});

api.MapGet("/leads", async (SignDashboardDbContext db) =>
    await db.Leads.OrderBy(lead => lead.Status).ThenByDescending(lead => lead.UpdatedAt).ToListAsync());

api.MapPost("/leads", async (LeadRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var lead = new Lead
    {
        CompanyName = request.CompanyName.Trim(),
        ContactName = request.ContactName.Trim(),
        Email = request.Email.Trim(),
        Phone = request.Phone.Trim(),
        Source = request.Source.Trim(),
        Status = string.IsNullOrWhiteSpace(request.Status) ? "New" : request.Status.Trim(),
        EstimatedValue = request.EstimatedValue,
        Notes = request.Notes.Trim(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Leads.Add(lead);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "lead.created", lead.Id);
    return Results.Created($"/api/leads/{lead.Id}", lead);
});

api.MapPut("/leads/{id:int}", async (int id, LeadRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var lead = await db.Leads.FindAsync(id);
    if (lead is null)
    {
        return Results.NotFound();
    }

    lead.CompanyName = request.CompanyName.Trim();
    lead.ContactName = request.ContactName.Trim();
    lead.Email = request.Email.Trim();
    lead.Phone = request.Phone.Trim();
    lead.Source = request.Source.Trim();
    lead.Status = string.IsNullOrWhiteSpace(request.Status) ? lead.Status : request.Status.Trim();
    lead.EstimatedValue = request.EstimatedValue;
    lead.Notes = request.Notes.Trim();
    lead.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "lead.updated", lead.Id);
    return Results.Ok(lead);
});

api.MapPost("/customers/{id:int}/opportunities", async (int id, OpportunityRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    var opportunity = new Opportunity
    {
        CustomerId = id,
        Title = request.Title.Trim(),
        ProjectType = request.ProjectType.Trim(),
        Stage = string.IsNullOrWhiteSpace(request.Stage) ? "Lead" : request.Stage.Trim(),
        EstimatedValue = request.EstimatedValue,
        Probability = Math.Clamp(request.Probability, 0, 100),
        Source = request.Source.Trim(),
        ExpectedCloseDate = request.ExpectedCloseDate,
        Notes = request.Notes.Trim(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Opportunities.Add(opportunity);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "opportunity.created", opportunity.Id);
    return Results.Created($"/api/customers/{id}/opportunities/{opportunity.Id}", opportunity);
});

api.MapGet("/customers/{id:int}/opportunities", async (int id, SignDashboardDbContext db) =>
    await db.Opportunities
        .Where(opportunity => opportunity.CustomerId == id)
        .OrderByDescending(opportunity => opportunity.UpdatedAt)
        .Select(opportunity => new
        {
            opportunity.Id,
            opportunity.Title,
            opportunity.ProjectType,
            opportunity.Stage,
            opportunity.EstimatedValue,
            opportunity.Probability,
            opportunity.ExpectedCloseDate
        })
        .ToListAsync());

api.MapPut("/opportunities/{id:int}", async (int id, OpportunityRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var opportunity = await db.Opportunities.FindAsync(id);
    if (opportunity is null)
    {
        return Results.NotFound();
    }

    opportunity.Title = request.Title.Trim();
    opportunity.ProjectType = request.ProjectType.Trim();
    opportunity.Stage = string.IsNullOrWhiteSpace(request.Stage) ? opportunity.Stage : request.Stage.Trim();
    opportunity.EstimatedValue = request.EstimatedValue;
    opportunity.Probability = Math.Clamp(request.Probability, 0, 100);
    opportunity.Source = request.Source.Trim();
    opportunity.ExpectedCloseDate = request.ExpectedCloseDate;
    opportunity.Notes = request.Notes.Trim();
    opportunity.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "opportunity.updated", opportunity.Id);
    return Results.Ok(opportunity);
});

api.MapGet("/quote-templates", async (SignDashboardDbContext db) =>
    await db.ProjectTypeTemplates
        .Include(template => template.Lines)
        .OrderBy(template => template.SortOrder)
        .Select(template => new
        {
            template.Id,
            template.ProjectType,
            template.Description,
            Lines = template.Lines
                .OrderBy(line => line.SortOrder)
                .Select(line => new
                {
                    line.Id,
                    line.Description,
                    Quantity = line.DefaultQuantity,
                    UnitPrice = line.DefaultUnitPrice
                })
                .ToList()
        })
        .ToListAsync());

api.MapPost("/customers/{id:int}/activities", async (int id, ActivityRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer is null)
    {
        return Results.NotFound();
    }

    if (request.OpportunityId is int opportunityId)
    {
        var opportunityBelongsToCustomer = await db.Opportunities.AnyAsync(opportunity => opportunity.Id == opportunityId && opportunity.CustomerId == id);
        if (!opportunityBelongsToCustomer)
        {
            return Results.BadRequest(new { message = "Selected opportunity does not belong to this account." });
        }
    }

    var normalizedStatus = NormalizeActivityStatus(request.Status);
    var activity = new Activity
    {
        CustomerId = id,
        OpportunityId = request.OpportunityId,
        Type = string.IsNullOrWhiteSpace(request.Type) ? "Call" : request.Type.Trim(),
        Subject = request.Subject.Trim(),
        Notes = request.Notes.Trim(),
        AssignedTo = request.AssignedTo.Trim(),
        Status = normalizedStatus,
        DueAt = request.DueAt,
        CompletedAt = normalizedStatus == "Completed" ? DateTime.UtcNow : null,
        CreatedAt = DateTime.UtcNow
    };

    db.Activities.Add(activity);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "activity.created", activity.Id);
    return Results.Created($"/api/customers/{id}/activities/{activity.Id}", activity);
});

api.MapPut("/activities/{id:int}", async (int id, ActivityRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var activity = await db.Activities.FindAsync(id);
    if (activity is null)
    {
        return Results.NotFound();
    }

    if (request.OpportunityId is int opportunityId)
    {
        var opportunityBelongsToCustomer = await db.Opportunities.AnyAsync(opportunity => opportunity.Id == opportunityId && opportunity.CustomerId == activity.CustomerId);
        if (!opportunityBelongsToCustomer)
        {
            return Results.BadRequest(new { message = "Selected opportunity does not belong to this account." });
        }
    }

    activity.OpportunityId = request.OpportunityId;
    activity.Type = string.IsNullOrWhiteSpace(request.Type) ? activity.Type : request.Type.Trim();
    activity.Subject = request.Subject.Trim();
    activity.Notes = request.Notes.Trim();
    activity.AssignedTo = request.AssignedTo.Trim();
    activity.Status = NormalizeActivityStatus(request.Status);
    activity.DueAt = request.DueAt;
    activity.CompletedAt = activity.Status == "Completed" ? activity.CompletedAt ?? DateTime.UtcNow : null;

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "activity.updated", activity.Id);
    return Results.Ok(activity);
});

api.MapGet("/tickets", async (SignDashboardDbContext db) =>
    await db.Tickets.Include(ticket => ticket.Customer)
        .OrderBy(ticket => ticket.Status)
        .ThenBy(ticket => ticket.SortOrder)
        .ThenByDescending(ticket => ticket.UpdatedAt)
        .ToListAsync());

api.MapPost("/tickets", async (TicketRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var ticket = new Ticket
    {
        Title = request.Title,
        Description = request.Description,
        Status = request.Status,
        Priority = request.Priority,
        AssignedTo = request.AssignedTo,
        DueDate = request.DueDate,
        SortOrder = await GetNextTicketSortOrder(db, request.Status),
        CustomerId = request.CustomerId
    };

    db.Tickets.Add(ticket);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "ticket.created", ticket.Id);
    return Results.Created($"/api/tickets/{ticket.Id}", ticket);
});

api.MapPut("/tickets/{id:int}", async (int id, TicketRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var ticket = await db.Tickets.FindAsync(id);
    if (ticket is null)
    {
        return Results.NotFound();
    }

    if (IsDesignStatus(ticket.Status) && request.Status == "Production")
    {
        var latestProof = await db.ProofFiles
            .Where(file => file.TicketId == ticket.Id)
            .OrderByDescending(file => file.Version)
            .FirstOrDefaultAsync();

        if (latestProof?.Status != "Approved")
        {
            return Results.BadRequest("Proof approval is required before moving this ticket to Production.");
        }
    }

    ticket.Title = request.Title;
    ticket.Description = request.Description;
    ticket.Status = request.Status;
    ticket.Priority = request.Priority;
    ticket.AssignedTo = request.AssignedTo;
    ticket.DueDate = request.DueDate;
    ticket.CustomerId = request.CustomerId;
    ticket.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "ticket.updated", ticket.Id);
    return Results.Ok(ticket);
});

api.MapPost("/tickets/{id:int}/position", async (int id, TicketPositionRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var ticket = await db.Tickets.FindAsync(id);
    if (ticket is null)
    {
        return Results.NotFound();
    }

    var targetStatus = request.Status.Trim();
    if (IsDesignStatus(ticket.Status) && targetStatus == "Production")
    {
        var latestProof = await db.ProofFiles
            .Where(file => file.TicketId == ticket.Id)
            .OrderByDescending(file => file.Version)
            .FirstOrDefaultAsync();

        if (latestProof?.Status != "Approved")
        {
            return Results.BadRequest("Proof approval is required before moving this ticket to Production.");
        }
    }

    var orderedTickets = await db.Tickets
        .Where(item => item.Status == targetStatus && item.Id != ticket.Id)
        .OrderBy(item => item.SortOrder)
        .ThenBy(item => item.Id)
        .ToListAsync();

    var insertIndex = orderedTickets.Count;
    if (request.BeforeTicketId is not null)
    {
        var beforeIndex = orderedTickets.FindIndex(item => item.Id == request.BeforeTicketId);
        if (beforeIndex >= 0)
        {
            insertIndex = beforeIndex;
        }
    }

    orderedTickets.Insert(insertIndex, ticket);
    ticket.Status = targetStatus;
    ticket.UpdatedAt = DateTime.UtcNow;

    for (var index = 0; index < orderedTickets.Count; index++)
    {
        orderedTickets[index].SortOrder = (index + 1) * 100;
    }

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "ticket.positioned", ticket.Id);
    return Results.Ok(ticket);
});

api.MapPost("/tickets/{id:int}/job-order", async (int id, JobFromTicketRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var ticket = await db.Tickets.FindAsync(id);
    if (ticket is null)
    {
        return Results.NotFound();
    }

    var job = new JobOrder
    {
        JobNumber = $"JOB-{DateTime.UtcNow:yyyyMMddHHmmss}",
        TicketId = ticket.Id,
        Title = request.Title.Length > 0 ? request.Title : ticket.Title,
        AssignedTo = request.AssignedTo,
        ProductionNotes = request.ProductionNotes,
        DueDate = request.DueDate,
        Status = "Artwork"
    };

    ticket.Status = "Production";
    ticket.UpdatedAt = DateTime.UtcNow;
    db.JobOrders.Add(job);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "job.created_from_ticket", job.Id);
    return Results.Created($"/api/job-orders/{job.Id}", job);
});

api.MapGet("/inventory", async (SignDashboardDbContext db) =>
    await db.InventoryItems.OrderBy(item => item.Name).ToListAsync());

api.MapPost("/inventory", async (InventoryRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var item = new InventoryItem
    {
        Sku = request.Sku,
        Name = request.Name,
        Category = request.Category,
        QuantityOnHand = request.QuantityOnHand,
        ReorderPoint = request.ReorderPoint,
        UnitCost = request.UnitCost,
        Supplier = request.Supplier
    };

    db.InventoryItems.Add(item);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "inventory.created", item.Id);
    return Results.Created($"/api/inventory/{item.Id}", item);
});

api.MapPut("/inventory/{id:int}", async (int id, InventoryRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var item = await db.InventoryItems.FindAsync(id);
    if (item is null)
    {
        return Results.NotFound();
    }

    item.Sku = request.Sku;
    item.Name = request.Name;
    item.Category = request.Category;
    item.QuantityOnHand = request.QuantityOnHand;
    item.ReorderPoint = request.ReorderPoint;
    item.UnitCost = request.UnitCost;
    item.Supplier = request.Supplier;
    item.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "inventory.updated", item.Id);
    return Results.Ok(item);
});

api.MapGet("/quotes", async (SignDashboardDbContext db) =>
    await db.Quotes
        .OrderByDescending(quote => quote.CreatedAt)
        .Select(quote => new
        {
            quote.Id,
            quote.QuoteNumber,
            quote.CustomerId,
            Customer = quote.Customer == null
                ? null
                : new
                {
                    quote.Customer.Id,
                    quote.Customer.CompanyName
                },
            quote.OpportunityId,
            Opportunity = quote.Opportunity == null
                ? null
                : new
                {
                    quote.Opportunity.Id,
                    quote.Opportunity.Title,
                    quote.Opportunity.ProjectType,
                    quote.Opportunity.Stage
                },
            quote.ProjectType,
            quote.Status,
            quote.Scope,
            quote.Subtotal,
            quote.Tax,
            quote.Total,
            quote.ValidUntil,
            quote.CreatedAt,
            LineItems = quote.LineItems
                .OrderBy(item => item.Id)
                .Select(item => new
                {
                    item.Id,
                    item.Description,
                    item.Quantity,
                    item.UnitPrice,
                    item.LineTotal
                })
                .ToList()
        })
        .ToListAsync());

api.MapPost("/quotes", async (QuoteRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    Opportunity? opportunity = null;
    if (request.OpportunityId is int opportunityId)
    {
        opportunity = await db.Opportunities.FirstOrDefaultAsync(item => item.Id == opportunityId);
        if (opportunity is null)
        {
            return Results.BadRequest(new { message = "The selected opportunity was not found." });
        }

        if (opportunity.CustomerId != request.CustomerId)
        {
            return Results.BadRequest(new { message = "The selected opportunity does not belong to the selected account." });
        }
    }

    var projectType = opportunity?.ProjectType?.Trim()
        ?? request.ProjectType.Trim();

    var subtotal = request.LineItems.Sum(item => item.Quantity * item.UnitPrice);
    var tax = Math.Round(subtotal * request.TaxRate, 2);
    var quote = new Quote
    {
        QuoteNumber = $"Q-{DateTime.UtcNow:yyyyMMddHHmmss}",
        CustomerId = request.CustomerId,
        OpportunityId = request.OpportunityId,
        ProjectType = projectType,
        Status = request.Status,
        Scope = request.Scope,
        Subtotal = subtotal,
        Tax = tax,
        Total = subtotal + tax,
        ValidUntil = request.ValidUntil,
        LineItems = request.LineItems.Select(item => new QuoteLineItem
        {
            Description = item.Description,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            LineTotal = item.Quantity * item.UnitPrice
        }).ToList()
    };

    db.Quotes.Add(quote);

    if (opportunity is not null)
    {
        var nextStage = request.Status switch
        {
            "Approved" => "Won",
            "Rejected" => "Lost",
            "Sent" => "Quoted",
            _ => null
        };

        if (nextStage is not null && !string.Equals(opportunity.Stage, nextStage, StringComparison.OrdinalIgnoreCase))
        {
            opportunity.Stage = nextStage;
            opportunity.UpdatedAt = DateTime.UtcNow;
        }
    }

    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "quote.created", quote.Id);
    var response = await db.Quotes
        .Where(item => item.Id == quote.Id)
        .Select(item => new
        {
            item.Id,
            item.QuoteNumber,
            item.CustomerId,
            Customer = item.Customer == null
                ? null
                : new
                {
                    item.Customer.Id,
                    item.Customer.CompanyName
                },
            item.OpportunityId,
            Opportunity = item.Opportunity == null
                ? null
                : new
                {
                    item.Opportunity.Id,
                    item.Opportunity.Title,
                    item.Opportunity.ProjectType,
                    item.Opportunity.Stage
                },
            item.ProjectType,
            item.Status,
            item.Scope,
            item.Subtotal,
            item.Tax,
            item.Total,
            item.ValidUntil,
            item.CreatedAt,
            LineItems = item.LineItems
                .OrderBy(line => line.Id)
                .Select(line => new
                {
                    line.Id,
                    line.Description,
                    line.Quantity,
                    line.UnitPrice,
                    line.LineTotal
                })
                .ToList()
        })
        .FirstAsync();
    return Results.Created($"/api/quotes/{quote.Id}", response);
});

api.MapGet("/job-orders", async (SignDashboardDbContext db) =>
    await db.JobOrders.Include(job => job.Ticket)
        .Include(job => job.Quote)
        .ThenInclude(quote => quote.Customer)
        .Include(job => job.Quote)
        .ThenInclude(quote => quote.Opportunity)
        .OrderByDescending(job => job.CreatedAt)
        .ToListAsync());

api.MapPost("/job-orders", async (JobOrderRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var generatedTitle = await BuildJobTitle(db, request.Title, request.QuoteId, request.TicketId);
    var job = new JobOrder
    {
        JobNumber = $"JOB-{DateTime.UtcNow:yyyyMMddHHmmss}",
        TicketId = request.TicketId,
        QuoteId = request.QuoteId,
        Title = generatedTitle,
        Status = NormalizeJobOrderStatus(request.Status),
        AssignedTo = request.AssignedTo,
        ProductionNotes = request.ProductionNotes,
        DxfReady = request.DxfReady,
        BendFileReady = request.BendFileReady,
        BacksReady = request.BacksReady,
        FacesReady = request.FacesReady,
        LedLayoutReady = request.LedLayoutReady,
        DueDate = request.DueDate
    };

    db.JobOrders.Add(job);
    var ticketCreated = await EnsureAssignedJobTicket(db, job);
    await db.SaveChangesAsync();
    if (ticketCreated && job.TicketId is not null)
    {
        await BroadcastDashboardChange(hub, "ticket.created", job.TicketId.Value);
    }
    await BroadcastDashboardChange(hub, "job.created", job.Id);
    return Results.Created($"/api/job-orders/{job.Id}", job);
});

api.MapPut("/job-orders/{id:int}", async (int id, JobOrderRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var job = await db.JobOrders.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    var hadTicketBeforeUpdate = job.TicketId is not null;
    var generatedTitle = await BuildJobTitle(db, request.Title, request.QuoteId, request.TicketId);
    job.TicketId = request.TicketId;
    job.QuoteId = request.QuoteId;
    job.Title = generatedTitle;
    job.Status = string.IsNullOrWhiteSpace(request.Status) ? NormalizeJobOrderStatus(job.Status) : NormalizeJobOrderStatus(request.Status);
    job.AssignedTo = request.AssignedTo;
    job.ProductionNotes = request.ProductionNotes;
    job.DxfReady = request.DxfReady;
    job.BendFileReady = request.BendFileReady;
    job.BacksReady = request.BacksReady;
    job.FacesReady = request.FacesReady;
    job.LedLayoutReady = request.LedLayoutReady;
    job.DueDate = request.DueDate;
    var ticketCreated = !hadTicketBeforeUpdate && request.TicketId is null
        && await EnsureAssignedJobTicket(db, job);
    await db.SaveChangesAsync();
    if (ticketCreated && job.TicketId is not null)
    {
        await BroadcastDashboardChange(hub, "ticket.created", job.TicketId.Value);
    }
    await BroadcastDashboardChange(hub, "job.updated", job.Id);
    return Results.Ok(job);
});

api.MapDelete("/job-orders/{id:int}", async (int id, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var job = await db.JobOrders.FindAsync(id);
    if (job is null)
    {
        return Results.NotFound();
    }

    var hasLinkedFiles = await db.SignFiles.AnyAsync(file => file.JobOrderId == id)
        || await db.ArtworkFiles.AnyAsync(file => file.JobOrderId == id)
        || await db.ProofFiles.AnyAsync(file => file.JobOrderId == id);
    if (hasLinkedFiles)
    {
        return Results.BadRequest(new { message = "This job order has linked files, artwork, or proofs and cannot be deleted." });
    }

    db.JobOrders.Remove(job);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "job.deleted", id);
    return Results.Ok(new { Id = id });
});

api.MapGet("/files", async (SignDashboardDbContext db) =>
    await db.SignFiles.Include(file => file.Customer)
        .Include(file => file.Ticket)
        .Include(file => file.JobOrder)
        .OrderByDescending(file => file.UploadedAt)
        .ToListAsync());

api.MapGet("/artwork", async (int? ticketId, int? jobOrderId, SignDashboardDbContext db) =>
{
    var query = db.ArtworkFiles.Include(file => file.Customer)
        .Include(file => file.Ticket)
        .Include(file => file.JobOrder)
        .AsQueryable();

    if (ticketId is not null)
    {
        query = query.Where(file => file.TicketId == ticketId);
    }

    if (jobOrderId is not null)
    {
        query = query.Where(file => file.JobOrderId == jobOrderId);
    }

    return await query.OrderByDescending(file => file.UploadedAt).ToListAsync();
});

api.MapPost("/artwork", async (HttpRequest request, SignDashboardDbContext db, IWebHostEnvironment env, IHubContext<DashboardHub> hub) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Expected multipart form data.");
    }

    var form = await request.ReadFormAsync();
    var upload = form.Files["file"];
    if (upload is null || upload.Length == 0)
    {
        return Results.BadRequest("An artwork file is required.");
    }

    if (!IsAllowedArtworkFile(upload.FileName))
    {
        return Results.BadRequest("Unsupported artwork file type.");
    }

    var ticketId = ParseNullableInt(form["ticketId"]);
    var jobOrderId = ParseNullableInt(form["jobOrderId"]);
    var customerId = ParseNullableInt(form["customerId"]);
    var ticket = ticketId is null ? null : await db.Tickets.Include(item => item.Customer).FirstOrDefaultAsync(item => item.Id == ticketId);
    var job = jobOrderId is null ? null : await db.JobOrders.Include(item => item.Ticket).Include(item => item.Quote).FirstOrDefaultAsync(item => item.Id == jobOrderId);

    customerId ??= ticket?.CustomerId;
    if (customerId is null && job?.TicketId is not null)
    {
        customerId = await db.Tickets.Where(item => item.Id == job.TicketId).Select(item => item.CustomerId).FirstOrDefaultAsync();
    }

    if (customerId is null && job?.QuoteId is not null)
    {
        customerId = await db.Quotes.Where(item => item.Id == job.QuoteId).Select(item => (int?)item.CustomerId).FirstOrDefaultAsync();
    }

    if (customerId is null)
    {
        return Results.BadRequest("Artwork must be linked to a customer, ticket, or job order.");
    }

    var customer = await db.Customers.FindAsync(customerId.Value);
    if (customer is null)
    {
        return Results.BadRequest("Customer was not found.");
    }

    var version = await GetNextArtworkVersion(db, ticketId, jobOrderId, customer.Id);
    var status = form["status"].ToString();
    var uploadedBy = form["uploadedBy"].ToString();
    var notes = form["notes"].ToString();
    ArtworkTarget target;
    ProofFile? autoCreatedProof = null;
    try
    {
        target = await PrepareArtworkTarget(db, env, customer, ticket, job, upload.FileName, version);
        await using var stream = File.Create(target.FullPath);
        await upload.CopyToAsync(stream);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
        return Results.Problem(
            $"Unable to save artwork to the configured file location. Check CUSTOMER_FILES_ROOT and file-server permissions. Details: {exception.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var artwork = new ArtworkFile
    {
        OriginalFileName = upload.FileName,
        StoredFileName = Path.GetFileName(target.FullPath),
        RelativePath = target.RelativePath,
        ContentType = upload.ContentType,
        SizeBytes = upload.Length,
        Version = version,
        Status = string.IsNullOrWhiteSpace(status) ? "In Design" : status,
        UploadedBy = uploadedBy,
        Notes = notes,
        CustomerId = customer.Id,
        TicketId = ticketId,
        JobOrderId = jobOrderId
    };

    db.ArtworkFiles.Add(artwork);
    try
    {
        autoCreatedProof = await CreateDraftProofFromArtwork(db, env, customer, ticket, job, artwork, target.FullPath);
        if (autoCreatedProof is not null)
        {
            db.ProofFiles.Add(autoCreatedProof);
        }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
        return Results.Problem(
            $"Artwork was uploaded, but the draft proof could not be created automatically. Check proof storage permissions. Details: {exception.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }
    await db.SaveChangesAsync();
    if (target.UsesLocalAgent)
    {
        EnqueueFileStorageJob(db, "StoreArtworkFile", target.RelativePath, artwork.OriginalFileName, artwork.ContentType, artwork.SizeBytes, customer.Id);
    }
    if (autoCreatedProof is not null)
    {
        if (target.UsesLocalAgent)
        {
            EnqueueFileStorageJob(db, "StoreProofFile", autoCreatedProof.RelativePath, autoCreatedProof.OriginalFileName, autoCreatedProof.ContentType, autoCreatedProof.SizeBytes, customer.Id);
        }
    }
    if (target.UsesLocalAgent || autoCreatedProof is not null)
    {
        await db.SaveChangesAsync();
    }
    await BroadcastDashboardChange(hub, "artwork.uploaded", artwork.Id);
    if (autoCreatedProof is not null)
    {
        await BroadcastDashboardChange(hub, "proof.uploaded", autoCreatedProof.Id);
    }
    return Results.Created($"/api/artwork/{artwork.Id}", artwork);
});

api.MapGet("/proofs", async (int? ticketId, int? jobOrderId, SignDashboardDbContext db) =>
{
    var query = db.ProofFiles.Include(file => file.Customer)
        .Include(file => file.Ticket)
        .Include(file => file.JobOrder)
        .AsQueryable();

    if (ticketId is not null)
    {
        query = query.Where(file => file.TicketId == ticketId);
    }

    if (jobOrderId is not null)
    {
        query = query.Where(file => file.JobOrderId == jobOrderId);
    }

    return await query.OrderByDescending(file => file.UploadedAt).ToListAsync();
});

api.MapPost("/proofs", async (HttpRequest request, SignDashboardDbContext db, IWebHostEnvironment env, IHubContext<DashboardHub> hub) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Expected multipart form data.");
    }

    var form = await request.ReadFormAsync();
    var upload = form.Files["file"];
    if (upload is null || upload.Length == 0)
    {
        return Results.BadRequest("A proof file is required.");
    }

    if (!IsAllowedProofFile(upload.FileName))
    {
        return Results.BadRequest("Unsupported proof file type.");
    }

    var ticketId = ParseNullableInt(form["ticketId"]);
    var jobOrderId = ParseNullableInt(form["jobOrderId"]);
    var customerId = ParseNullableInt(form["customerId"]);
    var ticket = ticketId is null ? null : await db.Tickets.Include(item => item.Customer).FirstOrDefaultAsync(item => item.Id == ticketId);
    var job = jobOrderId is null ? null : await db.JobOrders.Include(item => item.Ticket).Include(item => item.Quote).FirstOrDefaultAsync(item => item.Id == jobOrderId);

    customerId ??= ticket?.CustomerId;
    if (customerId is null && job?.TicketId is not null)
    {
        customerId = await db.Tickets.Where(item => item.Id == job.TicketId).Select(item => item.CustomerId).FirstOrDefaultAsync();
    }

    if (customerId is null && job?.QuoteId is not null)
    {
        customerId = await db.Quotes.Where(item => item.Id == job.QuoteId).Select(item => (int?)item.CustomerId).FirstOrDefaultAsync();
    }

    if (customerId is null)
    {
        return Results.BadRequest("Proof must be linked to a customer, ticket, or job order.");
    }

    var customer = await db.Customers.FindAsync(customerId.Value);
    if (customer is null)
    {
        return Results.BadRequest("Customer was not found.");
    }

    var version = await GetNextProofVersion(db, ticketId, jobOrderId, customer.Id);
    var status = form["status"].ToString();
    ArtworkTarget target;
    try
    {
        target = await PrepareProofTarget(db, env, customer, ticket, job, upload.FileName, version);
        await using var stream = File.Create(target.FullPath);
        await upload.CopyToAsync(stream);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
        return Results.Problem(
            $"Unable to save proof to the configured file location. Check CUSTOMER_FILES_ROOT and file-server permissions. Details: {exception.Message}",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var proof = new ProofFile
    {
        OriginalFileName = upload.FileName,
        StoredFileName = Path.GetFileName(target.FullPath),
        RelativePath = target.RelativePath,
        ContentType = upload.ContentType,
        SizeBytes = upload.Length,
        Version = version,
        Status = string.IsNullOrWhiteSpace(status) ? "Draft" : status,
        SentTo = form["sentTo"].ToString(),
        ApprovedBy = form["approvedBy"].ToString(),
        SentAt = ParseNullableDateTime(form["sentAt"]),
        ApprovedAt = ParseNullableDateTime(form["approvedAt"]),
        Designer = form["designer"].ToString(),
        Notes = form["notes"].ToString(),
        ReviewToken = CreateReviewToken(),
        ReviewTokenExpiresAt = DateTime.UtcNow.AddDays(30),
        CustomerId = customer.Id,
        TicketId = ticketId,
        JobOrderId = jobOrderId
    };

    db.ProofFiles.Add(proof);
    await db.SaveChangesAsync();
    var automationActivityIds = new List<int>();
    if (ShouldCreateProofSentFollowUp(proof))
    {
        var activity = await CreateProofAutomationActivity(
            db,
            proof,
            subject: $"Follow up on proof v{proof.Version:00}",
            notes: BuildProofSentActivityNotes(proof, ticket, job),
            dueAt: (proof.SentAt ?? DateTime.UtcNow).Date.AddDays(2),
            assignedTo: "Sales");
        automationActivityIds.Add(activity.Id);
    }

    if (target.UsesLocalAgent)
    {
        EnqueueFileStorageJob(db, "StoreProofFile", target.RelativePath, proof.OriginalFileName, proof.ContentType, proof.SizeBytes, customer.Id);
        await db.SaveChangesAsync();
    }
    await BroadcastDashboardChange(hub, "proof.uploaded", proof.Id);
    foreach (var activityId in automationActivityIds)
    {
        await BroadcastDashboardChange(hub, "activity.created", activityId);
    }
    return Results.Created($"/api/proofs/{proof.Id}", proof);
});

api.MapPost("/proofs/{id:int}/create-review-link", async (int id, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var proof = await db.ProofFiles.FindAsync(id);
    if (proof is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(proof.ReviewToken) || proof.ReviewTokenExpiresAt <= DateTime.UtcNow)
    {
        proof.ReviewToken = CreateReviewToken();
    }

    proof.ReviewTokenExpiresAt = DateTime.UtcNow.AddDays(30);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "proof.review_link_created", proof.Id);

    return Results.Ok(new
    {
        proof.Id,
        proof.ReviewToken,
        ReviewUrl = BuildProofReviewUrl(proof.ReviewToken),
        proof.ReviewTokenExpiresAt
    });
});

api.MapGet("/proofs/review/{token}", async (string token, SignDashboardDbContext db) =>
{
    var proof = await db.ProofFiles.Include(file => file.Customer)
        .Include(file => file.Ticket)
        .Include(file => file.JobOrder)
        .FirstOrDefaultAsync(file => file.ReviewToken == token);

    if (proof is null || proof.ReviewTokenExpiresAt <= DateTime.UtcNow)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        proof.Id,
        proof.OriginalFileName,
        proof.StoredFileName,
        proof.RelativePath,
        proof.ContentType,
        proof.SizeBytes,
        proof.Version,
        proof.Status,
        proof.SentTo,
        proof.Designer,
        proof.Notes,
        proof.CustomerDecision,
        proof.CustomerComment,
        proof.CustomerRespondedAt,
        UploadedAt = proof.UploadedAt,
        Customer = proof.Customer is null ? null : new { proof.Customer.Id, proof.Customer.CompanyName, proof.Customer.ContactName },
        Ticket = proof.Ticket is null ? null : new { proof.Ticket.Id, proof.Ticket.Title, proof.Ticket.Description },
        JobOrder = proof.JobOrder is null ? null : new { proof.JobOrder.Id, proof.JobOrder.JobNumber, proof.JobOrder.Title },
        DownloadUrl = $"/api/proofs/review/{token}/file"
    });
});

api.MapGet("/proofs/review/{token}/file", async (string token, SignDashboardDbContext db, IWebHostEnvironment env) =>
{
    var proof = await db.ProofFiles.FirstOrDefaultAsync(file => file.ReviewToken == token);
    if (proof is null || proof.ReviewTokenExpiresAt <= DateTime.UtcNow)
    {
        return Results.NotFound();
    }

    var fullPath = await ResolveReadableUploadPath(db, env, proof.RelativePath);
    if (fullPath is null)
    {
        return Results.NotFound();
    }

    return Results.File(fullPath, proof.ContentType, proof.OriginalFileName);
});

api.MapPost("/proofs/review/{token}", async (string token, ProofReviewRequest request, HttpContext httpContext, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var proof = await db.ProofFiles.FirstOrDefaultAsync(file => file.ReviewToken == token);
    if (proof is null || proof.ReviewTokenExpiresAt <= DateTime.UtcNow)
    {
        return Results.NotFound();
    }

    var decision = request.Decision.Trim();
    if (decision is not "Approved" and not "Revision Needed")
    {
        return Results.BadRequest("Decision must be Approved or Revision Needed.");
    }

    proof.Status = decision;
    proof.CustomerDecision = decision;
    proof.CustomerComment = request.Comment.Trim();
    proof.CustomerRespondedAt = DateTime.UtcNow;
    proof.CustomerIpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    if (decision == "Approved")
    {
        proof.ApprovedAt = DateTime.UtcNow;
        proof.ApprovedBy = string.IsNullOrWhiteSpace(request.ApprovedBy) ? "Customer" : request.ApprovedBy.Trim();
    }

    await db.SaveChangesAsync();
    var automationActivityIds = new List<int>();
    if (decision == "Revision Needed")
    {
        var activity = await CreateProofAutomationActivity(
            db,
            proof,
            subject: $"Revise proof v{proof.Version:00}",
            notes: BuildProofRevisionActivityNotes(proof),
            dueAt: DateTime.UtcNow.Date.AddDays(1),
            assignedTo: string.IsNullOrWhiteSpace(proof.Designer) ? "Design" : proof.Designer.Trim());
        automationActivityIds.Add(activity.Id);
    }

    await BroadcastDashboardChange(hub, "proof.review_submitted", proof.Id);
    foreach (var activityId in automationActivityIds)
    {
        await BroadcastDashboardChange(hub, "activity.created", activityId);
    }
    return Results.Ok(new
    {
        proof.Id,
        proof.Status,
        proof.CustomerDecision,
        proof.CustomerComment,
        proof.CustomerRespondedAt
    });
});

api.MapPost("/files", async (HttpRequest request, SignDashboardDbContext db, IWebHostEnvironment env, IHubContext<DashboardHub> hub) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Expected multipart form data.");
    }

    var form = await request.ReadFormAsync();
    var upload = form.Files["file"];
    if (upload is null || upload.Length == 0)
    {
        return Results.BadRequest("A file is required.");
    }

    var uploadRoot = Path.Combine(env.ContentRootPath, "Uploads");
    Directory.CreateDirectory(uploadRoot);
    var storedFileName = $"{Guid.NewGuid():N}{Path.GetExtension(upload.FileName)}";
    var storedPath = Path.Combine(uploadRoot, storedFileName);

    await using (var stream = File.Create(storedPath))
    {
        await upload.CopyToAsync(stream);
    }

    var signFile = new SignFile
    {
        OriginalFileName = upload.FileName,
        StoredFileName = storedFileName,
        ContentType = upload.ContentType,
        SizeBytes = upload.Length,
        Category = NormalizeSignFileCategory(form["category"]),
        Notes = form["notes"].ToString(),
        CustomerId = ParseNullableInt(form["customerId"]),
        TicketId = ParseNullableInt(form["ticketId"]),
        JobOrderId = ParseNullableInt(form["jobOrderId"])
    };

    db.SignFiles.Add(signFile);
    if (signFile.JobOrderId is int jobOrderId)
    {
        var job = await db.JobOrders.FindAsync(jobOrderId);
        if (job is not null && string.Equals(signFile.Category, "DXF", StringComparison.OrdinalIgnoreCase))
        {
            job.DxfReady = true;
            job.BendFileReady = true;
        }
    }
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "file.uploaded", signFile.Id);
    return Results.Created($"/api/files/{signFile.Id}", signFile);
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SignDashboardDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureSchemaUpdates(db);
    await SeedJobWorkers(db);
    await SeedProjectTypeTemplates(db);
}

app.Run();

static int? ParseNullableInt(string? value) =>
    int.TryParse(value, out var parsed) ? parsed : null;

static DateTime? ParseNullableDateTime(string? value) =>
    DateTime.TryParse(value, out var parsed) ? parsed : null;

static long ParseMaxUploadBytes(string? value)
{
    const long defaultMaxUploadBytes = 1024L * 1024L * 1024L;
    return long.TryParse(value, out var parsed) && parsed > 0
        ? parsed
        : defaultMaxUploadBytes;
}

static Task BroadcastDashboardChange(IHubContext<DashboardHub> hub, string eventName, int entityId) =>
    hub.Clients.All.SendAsync("dashboardChanged", new
    {
        eventName,
        entityId,
        changedAt = DateTime.UtcNow
    });

static void SyncCustomerFromPrimaryContact(Customer customer, Contact contact)
{
    customer.ContactName = $"{contact.FirstName} {contact.LastName}".Trim();
    customer.Email = contact.Email;
    customer.Phone = contact.Phone;
}

static string NormalizeActivityStatus(string? status)
{
    if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
    {
        return "Completed";
    }

    return "Open";
}

static string NormalizeJobOrderStatus(string? status)
{
    var normalized = (status ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "Artwork";
    }

    return string.Equals(normalized, "Design", StringComparison.OrdinalIgnoreCase)
        ? "Artwork"
        : normalized;
}

static string NormalizeSignFileCategory(string? category)
{
    var normalized = (category ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "General";
    }

    return normalized;
}

static async Task<string> BuildJobTitle(SignDashboardDbContext db, string? requestedTitle, int? quoteId, int? ticketId)
{
    var explicitTitle = (requestedTitle ?? string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(explicitTitle))
    {
        return explicitTitle;
    }

    if (quoteId is int selectedQuoteId)
    {
        var quoteContext = await db.Quotes
            .Where(quote => quote.Id == selectedQuoteId)
            .Select(quote => new
            {
                CustomerName = quote.Customer == null ? string.Empty : quote.Customer.CompanyName,
                OpportunityTitle = quote.Opportunity == null ? string.Empty : quote.Opportunity.Title,
                quote.ProjectType,
                quote.QuoteNumber
            })
            .FirstOrDefaultAsync();

        if (quoteContext is not null)
        {
            var parts = new[]
            {
                quoteContext.CustomerName,
                quoteContext.OpportunityTitle,
                quoteContext.ProjectType
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

            if (parts.Length > 0)
            {
                return string.Join(" / ", parts);
            }

            if (!string.IsNullOrWhiteSpace(quoteContext.QuoteNumber))
            {
                return quoteContext.QuoteNumber;
            }
        }
    }

    if (ticketId is int selectedTicketId)
    {
        var ticketContext = await db.Tickets
            .Where(ticket => ticket.Id == selectedTicketId)
            .Select(ticket => new
            {
                CustomerName = ticket.Customer == null ? string.Empty : ticket.Customer.CompanyName,
                ticket.Title
            })
            .FirstOrDefaultAsync();

        if (ticketContext is not null)
        {
            var parts = new[]
            {
                ticketContext.CustomerName,
                ticketContext.Title
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

            if (parts.Length > 0)
            {
                return string.Join(" / ", parts);
            }
        }
    }

    return "New Job";
}

static async Task<bool> EnsureAssignedJobTicket(SignDashboardDbContext db, JobOrder job)
{
    if (string.IsNullOrWhiteSpace(job.AssignedTo) || job.TicketId is not null)
    {
        return false;
    }

    var customerId = await ResolveJobCustomerId(db, job.QuoteId, job.TicketId);
    var ticket = new Ticket
    {
        Title = string.IsNullOrWhiteSpace(job.Title) ? job.JobNumber : job.Title,
        Description = $"Auto-created from job order {job.JobNumber}.",
        Status = "Design",
        Priority = "Normal",
        AssignedTo = job.AssignedTo.Trim(),
        DueDate = job.DueDate,
        SortOrder = await GetNextTicketSortOrder(db, "Design"),
        CustomerId = customerId
    };

    db.Tickets.Add(ticket);
    job.Ticket = ticket;
    return true;
}

static async Task<int?> ResolveJobCustomerId(SignDashboardDbContext db, int? quoteId, int? ticketId)
{
    if (ticketId is int selectedTicketId)
    {
        return await db.Tickets
            .Where(ticket => ticket.Id == selectedTicketId)
            .Select(ticket => (int?)ticket.CustomerId)
            .FirstOrDefaultAsync();
    }

    if (quoteId is int selectedQuoteId)
    {
        return await db.Quotes
            .Where(quote => quote.Id == selectedQuoteId)
            .Select(quote => (int?)quote.CustomerId)
            .FirstOrDefaultAsync();
    }

    return null;
}

static bool ShouldCreateProofSentFollowUp(ProofFile proof) =>
    string.Equals(proof.Status, "Sent", StringComparison.OrdinalIgnoreCase)
    || proof.SentAt is not null;

static string BuildProofSentActivityNotes(ProofFile proof, Ticket? ticket, JobOrder? job)
{
    var context = job is not null
        ? $"{job.JobNumber} / {job.Title}"
        : ticket is not null
            ? ticket.Title
            : $"Customer #{proof.CustomerId}";

    return $"Proof v{proof.Version:00} was sent for {context}. Check for customer feedback and keep the job moving.";
}

static string BuildProofRevisionActivityNotes(ProofFile proof)
{
    var note = $"Customer requested revisions for proof v{proof.Version:00}.";
    if (!string.IsNullOrWhiteSpace(proof.CustomerComment))
    {
        note += $" Comment: {proof.CustomerComment.Trim()}";
    }

    return note;
}

static async Task<Activity> CreateProofAutomationActivity(
    SignDashboardDbContext db,
    ProofFile proof,
    string subject,
    string notes,
    DateTime? dueAt,
    string assignedTo)
{
    var opportunityId = await ResolveProofOpportunityId(db, proof);
    var activity = new Activity
    {
        CustomerId = proof.CustomerId,
        OpportunityId = opportunityId,
        Type = "Follow-up",
        Subject = subject,
        Notes = notes,
        AssignedTo = assignedTo,
        Status = "Open",
        DueAt = dueAt,
        CreatedAt = DateTime.UtcNow
    };

    db.Activities.Add(activity);
    await db.SaveChangesAsync();
    return activity;
}

static async Task<int?> ResolveProofOpportunityId(SignDashboardDbContext db, ProofFile proof)
{
    if (proof.JobOrderId is int jobOrderId)
    {
        var opportunityId = await db.JobOrders
            .Where(job => job.Id == jobOrderId)
            .Select(job => job.Quote != null ? job.Quote.OpportunityId : null)
            .FirstOrDefaultAsync();

        if (opportunityId is not null)
        {
            return opportunityId;
        }
    }

    return null;
}

static async Task EnsureSchemaUpdates(SignDashboardDbContext db)
{
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Customers` ADD COLUMN IF NOT EXISTS `IsArchived` tinyint(1) NOT NULL DEFAULT 0");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Customers` ADD COLUMN IF NOT EXISTS `ArchivedAt` datetime(6) NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Tickets` ADD COLUMN IF NOT EXISTS `DueDate` datetime(6) NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Tickets` ADD COLUMN IF NOT EXISTS `SortOrder` int NOT NULL DEFAULT 0");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Quotes` ADD COLUMN IF NOT EXISTS `OpportunityId` int NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Quotes` ADD COLUMN IF NOT EXISTS `ProjectType` varchar(255) NOT NULL DEFAULT ''");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Opportunities` ADD COLUMN IF NOT EXISTS `ProjectType` varchar(255) NOT NULL DEFAULT ''");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `JobOrders` ADD COLUMN IF NOT EXISTS `DxfReady` tinyint(1) NOT NULL DEFAULT 0");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `JobOrders` ADD COLUMN IF NOT EXISTS `BendFileReady` tinyint(1) NOT NULL DEFAULT 0");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `JobOrders` ADD COLUMN IF NOT EXISTS `BacksReady` tinyint(1) NOT NULL DEFAULT 0");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `JobOrders` ADD COLUMN IF NOT EXISTS `FacesReady` tinyint(1) NOT NULL DEFAULT 0");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `JobOrders` ADD COLUMN IF NOT EXISTS `LedLayoutReady` tinyint(1) NOT NULL DEFAULT 0");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `SignFiles` ADD COLUMN IF NOT EXISTS `Category` varchar(255) NOT NULL DEFAULT 'General'");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `SignFiles` ADD COLUMN IF NOT EXISTS `Notes` longtext NOT NULL");
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `AppSettings` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `Key` varchar(255) NOT NULL,
            `Value` longtext NOT NULL,
            `UpdatedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_AppSettings` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `ArtworkFiles` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `OriginalFileName` longtext NOT NULL,
            `StoredFileName` longtext NOT NULL,
            `RelativePath` longtext NOT NULL,
            `ContentType` longtext NOT NULL,
            `SizeBytes` bigint NOT NULL,
            `Version` int NOT NULL,
            `Status` longtext NOT NULL,
            `UploadedBy` longtext NOT NULL,
            `Notes` longtext NOT NULL,
            `CustomerId` int NOT NULL,
            `TicketId` int NULL,
            `JobOrderId` int NULL,
            `UploadedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_ArtworkFiles` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `ProofFiles` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `OriginalFileName` longtext NOT NULL,
            `StoredFileName` longtext NOT NULL,
            `RelativePath` longtext NOT NULL,
            `ContentType` longtext NOT NULL,
            `SizeBytes` bigint NOT NULL,
            `Version` int NOT NULL,
            `Status` longtext NOT NULL,
            `SentTo` longtext NOT NULL,
            `ApprovedBy` longtext NOT NULL,
            `SentAt` datetime(6) NULL,
            `ApprovedAt` datetime(6) NULL,
            `Designer` longtext NOT NULL,
            `Notes` longtext NOT NULL,
            `ReviewToken` varchar(128) NOT NULL DEFAULT '',
            `ReviewTokenExpiresAt` datetime(6) NULL,
            `CustomerDecision` longtext NOT NULL,
            `CustomerComment` longtext NOT NULL,
            `CustomerRespondedAt` datetime(6) NULL,
            `CustomerIpAddress` longtext NOT NULL,
            `CustomerId` int NOT NULL,
            `TicketId` int NULL,
            `JobOrderId` int NULL,
            `UploadedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_ProofFiles` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ProofFiles` ADD COLUMN IF NOT EXISTS `ReviewToken` varchar(128) NOT NULL DEFAULT ''");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ProofFiles` ADD COLUMN IF NOT EXISTS `ReviewTokenExpiresAt` datetime(6) NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ProofFiles` ADD COLUMN IF NOT EXISTS `CustomerDecision` longtext NOT NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ProofFiles` ADD COLUMN IF NOT EXISTS `CustomerComment` longtext NOT NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ProofFiles` ADD COLUMN IF NOT EXISTS `CustomerRespondedAt` datetime(6) NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `ProofFiles` ADD COLUMN IF NOT EXISTS `CustomerIpAddress` longtext NOT NULL");
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `StorageAgents` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `AgentName` varchar(255) NOT NULL,
            `CustomerFilesRoot` longtext NOT NULL,
            `Version` longtext NOT NULL,
            `Status` longtext NOT NULL,
            `LastSeenAt` datetime(6) NOT NULL,
            `CreatedAt` datetime(6) NOT NULL,
            `UpdatedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_StorageAgents` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `StorageJobs` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `JobType` longtext NOT NULL,
            `Status` varchar(64) NOT NULL,
            `PayloadJson` longtext NOT NULL,
            `AssignedAgentName` longtext NOT NULL,
            `Attempts` int NOT NULL,
            `LastError` longtext NOT NULL,
            `CustomerId` int NULL,
            `CreatedAt` datetime(6) NOT NULL,
            `StartedAt` datetime(6) NULL,
            `CompletedAt` datetime(6) NULL,
            `UpdatedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_StorageJobs` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `Contacts` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `CustomerId` int NOT NULL,
            `FirstName` varchar(255) NOT NULL,
            `LastName` varchar(255) NOT NULL,
            `Title` longtext NOT NULL,
            `Email` longtext NOT NULL,
            `Phone` longtext NOT NULL,
            `IsPrimaryContact` tinyint(1) NOT NULL,
            `Notes` longtext NOT NULL,
            `CreatedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_Contacts` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `Leads` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `CompanyName` varchar(255) NOT NULL,
            `ContactName` varchar(255) NOT NULL,
            `Email` longtext NOT NULL,
            `Phone` longtext NOT NULL,
            `Source` varchar(255) NOT NULL,
            `Status` varchar(64) NOT NULL,
            `EstimatedValue` decimal(12,2) NULL,
            `Notes` longtext NOT NULL,
            `CreatedAt` datetime(6) NOT NULL,
            `UpdatedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_Leads` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `Opportunities` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `CustomerId` int NOT NULL,
            `Title` varchar(255) NOT NULL,
            `Stage` varchar(64) NOT NULL,
            `EstimatedValue` decimal(12,2) NOT NULL,
            `Probability` int NOT NULL,
            `Source` varchar(255) NOT NULL,
            `ExpectedCloseDate` datetime(6) NULL,
            `Notes` longtext NOT NULL,
            `CreatedAt` datetime(6) NOT NULL,
            `UpdatedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_Opportunities` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `Activities` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `CustomerId` int NOT NULL,
            `OpportunityId` int NULL,
            `Type` varchar(64) NOT NULL,
            `Subject` varchar(255) NOT NULL,
            `Notes` longtext NOT NULL,
            `AssignedTo` varchar(255) NOT NULL,
            `Status` varchar(64) NOT NULL,
            `DueAt` datetime(6) NULL,
            `CompletedAt` datetime(6) NULL,
            `CreatedAt` datetime(6) NOT NULL,
            CONSTRAINT `PK_Activities` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `ProjectTypeTemplates` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `ProjectType` varchar(255) NOT NULL,
            `Description` longtext NOT NULL,
            `SortOrder` int NOT NULL,
            CONSTRAINT `PK_ProjectTypeTemplates` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `ProjectTypeTemplateLines` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `ProjectTypeTemplateId` int NOT NULL,
            `Description` varchar(255) NOT NULL,
            `DefaultQuantity` int NOT NULL,
            `DefaultUnitPrice` decimal(12,2) NOT NULL,
            `SortOrder` int NOT NULL,
            CONSTRAINT `PK_ProjectTypeTemplateLines` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS `JobWorkers` (
            `Id` int NOT NULL AUTO_INCREMENT,
            `Name` varchar(255) NOT NULL,
            `Role` varchar(255) NOT NULL,
            `IsActive` tinyint(1) NOT NULL,
            `SortOrder` int NOT NULL,
            CONSTRAINT `PK_JobWorkers` PRIMARY KEY (`Id`)
        ) CHARACTER SET=utf8mb4;
        """);
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS `IX_StorageAgents_AgentName` ON `StorageAgents` (`AgentName`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_StorageJobs_Status_CreatedAt` ON `StorageJobs` (`Status`, `CreatedAt`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_Customers_IsArchived_CompanyName` ON `Customers` (`IsArchived`, `CompanyName`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_Contacts_CustomerId_LastName_FirstName` ON `Contacts` (`CustomerId`, `LastName`, `FirstName`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_Leads_Status_CreatedAt` ON `Leads` (`Status`, `CreatedAt`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_Opportunities_CustomerId_Stage` ON `Opportunities` (`CustomerId`, `Stage`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_Activities_CustomerId_CreatedAt` ON `Activities` (`CustomerId`, `CreatedAt`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_Activities_OpportunityId` ON `Activities` (`OpportunityId`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_JobWorkers_IsActive_SortOrder` ON `JobWorkers` (`IsActive`, `SortOrder`)");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS `IX_ProjectTypeTemplates_ProjectType` ON `ProjectTypeTemplates` (`ProjectType`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_ProjectTypeTemplateLines_ProjectTypeTemplateId_SortOrder` ON `ProjectTypeTemplateLines` (`ProjectTypeTemplateId`, `SortOrder`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_Quotes_OpportunityId` ON `Quotes` (`OpportunityId`)");
}

static async Task SeedJobWorkers(SignDashboardDbContext db)
{
    var catalog = new[]
    {
        new { Name = "Aiden Cruz", Role = "Design", SortOrder = 1 },
        new { Name = "Maya Chen", Role = "Proofing", SortOrder = 2 },
        new { Name = "Leo Brooks", Role = "Production", SortOrder = 3 },
        new { Name = "Sofia Patel", Role = "Install", SortOrder = 4 }
    };

    var existingKeys = await db.JobWorkers
        .Select(worker => $"{worker.Name}|{worker.Role}")
        .ToListAsync();
    var existingKeySet = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    var missingWorkers = catalog
        .Where(worker => !existingKeySet.Contains($"{worker.Name}|{worker.Role}"))
        .Select(worker => new JobWorker
        {
            Name = worker.Name,
            Role = worker.Role,
            IsActive = true,
            SortOrder = worker.SortOrder
        })
        .ToList();

    if (missingWorkers.Count > 0)
    {
        db.JobWorkers.AddRange(missingWorkers);
        await db.SaveChangesAsync();
    }
}

static async Task SeedProjectTypeTemplates(SignDashboardDbContext db)
{
    var catalog = new[]
    {
        new
        {
            ProjectType = "Channel Letters",
            Description = "Illuminated or non-illuminated storefront letter sets.",
            SortOrder = 1,
            Lines = new[]
            {
                new { Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 250m, SortOrder = 1 },
                new { Description = "Shop drawings", DefaultQuantity = 1, DefaultUnitPrice = 180m, SortOrder = 2 },
                new { Description = "Channel letter fabrication", DefaultQuantity = 1, DefaultUnitPrice = 2400m, SortOrder = 3 },
                new { Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 900m, SortOrder = 4 }
            }
        },
        new
        {
            ProjectType = "Cabinet Sign",
            Description = "Face changes and new cabinet sign builds.",
            SortOrder = 2,
            Lines = new[]
            {
                new { Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 200m, SortOrder = 1 },
                new { Description = "Cabinet fabrication", DefaultQuantity = 1, DefaultUnitPrice = 2100m, SortOrder = 2 },
                new { Description = "Face / vinyl graphics", DefaultQuantity = 1, DefaultUnitPrice = 650m, SortOrder = 3 },
                new { Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 850m, SortOrder = 4 }
            }
        },
        new
        {
            ProjectType = "Window Graphics",
            Description = "Perforated, frosted, and promotional window films.",
            SortOrder = 3,
            Lines = new[]
            {
                new { Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 150m, SortOrder = 1 },
                new { Description = "Print / production", DefaultQuantity = 1, DefaultUnitPrice = 480m, SortOrder = 2 },
                new { Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 320m, SortOrder = 3 }
            }
        },
        new
        {
            ProjectType = "Vehicle Wrap",
            Description = "Partial and full commercial vehicle graphics.",
            SortOrder = 4,
            Lines = new[]
            {
                new { Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 350m, SortOrder = 1 },
                new { Description = "Print / laminate", DefaultQuantity = 1, DefaultUnitPrice = 1900m, SortOrder = 2 },
                new { Description = "Vehicle prep and installation", DefaultQuantity = 1, DefaultUnitPrice = 1200m, SortOrder = 3 }
            }
        },
        new
        {
            ProjectType = "Wayfinding Package",
            Description = "Interior directories, ADA signs, and room IDs.",
            SortOrder = 5,
            Lines = new[]
            {
                new { Description = "Site survey", DefaultQuantity = 1, DefaultUnitPrice = 220m, SortOrder = 1 },
                new { Description = "Design / schedule set", DefaultQuantity = 1, DefaultUnitPrice = 360m, SortOrder = 2 },
                new { Description = "Fabrication", DefaultQuantity = 1, DefaultUnitPrice = 1600m, SortOrder = 3 },
                new { Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 740m, SortOrder = 4 }
            }
        },
        new
        {
            ProjectType = "Monument Sign",
            Description = "Ground signs with routed faces, push-through letters, or masonry structures.",
            SortOrder = 6,
            Lines = new[]
            {
                new { Description = "Site survey and engineering coordination", DefaultQuantity = 1, DefaultUnitPrice = 450m, SortOrder = 1 },
                new { Description = "Design / permit set", DefaultQuantity = 1, DefaultUnitPrice = 600m, SortOrder = 2 },
                new { Description = "Fabrication", DefaultQuantity = 1, DefaultUnitPrice = 4200m, SortOrder = 3 },
                new { Description = "Installation / equipment", DefaultQuantity = 1, DefaultUnitPrice = 1800m, SortOrder = 4 }
            }
        },
        new
        {
            ProjectType = "Pylon Sign",
            Description = "Multi-tenant pylons, face changes, and illuminated roadside signs.",
            SortOrder = 7,
            Lines = new[]
            {
                new { Description = "Design / layout", DefaultQuantity = 1, DefaultUnitPrice = 325m, SortOrder = 1 },
                new { Description = "Face fabrication", DefaultQuantity = 1, DefaultUnitPrice = 2600m, SortOrder = 2 },
                new { Description = "Electrical / lighting service", DefaultQuantity = 1, DefaultUnitPrice = 850m, SortOrder = 3 },
                new { Description = "Install / crane or lift", DefaultQuantity = 1, DefaultUnitPrice = 1500m, SortOrder = 4 }
            }
        },
        new
        {
            ProjectType = "ADA Signs",
            Description = "Braille, tactile, and code-compliant interior signage packages.",
            SortOrder = 8,
            Lines = new[]
            {
                new { Description = "Survey / sign schedule review", DefaultQuantity = 1, DefaultUnitPrice = 180m, SortOrder = 1 },
                new { Description = "Artwork and compliance layout", DefaultQuantity = 1, DefaultUnitPrice = 240m, SortOrder = 2 },
                new { Description = "Fabrication", DefaultQuantity = 1, DefaultUnitPrice = 1100m, SortOrder = 3 },
                new { Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 520m, SortOrder = 4 }
            }
        },
        new
        {
            ProjectType = "Lobby Signage",
            Description = "Interior brand walls, reception logos, and dimensional lobby pieces.",
            SortOrder = 9,
            Lines = new[]
            {
                new { Description = "Design / presentation", DefaultQuantity = 1, DefaultUnitPrice = 220m, SortOrder = 1 },
                new { Description = "Fabrication", DefaultQuantity = 1, DefaultUnitPrice = 1700m, SortOrder = 2 },
                new { Description = "Hardware / mounting", DefaultQuantity = 1, DefaultUnitPrice = 180m, SortOrder = 3 },
                new { Description = "Installation", DefaultQuantity = 1, DefaultUnitPrice = 480m, SortOrder = 4 }
            }
        },
        new
        {
            ProjectType = "Banners",
            Description = "Promotional banners, pole banners, and event signage.",
            SortOrder = 10,
            Lines = new[]
            {
                new { Description = "Layout / setup", DefaultQuantity = 1, DefaultUnitPrice = 95m, SortOrder = 1 },
                new { Description = "Print production", DefaultQuantity = 1, DefaultUnitPrice = 260m, SortOrder = 2 },
                new { Description = "Finishing / hems / grommets", DefaultQuantity = 1, DefaultUnitPrice = 80m, SortOrder = 3 }
            }
        }
    };

    var existingTemplates = await db.ProjectTypeTemplates.ToDictionaryAsync(item => item.ProjectType, item => item);
    var missingTemplates = catalog
        .Where(template => !existingTemplates.ContainsKey(template.ProjectType))
        .Select(template => new ProjectTypeTemplate
        {
            ProjectType = template.ProjectType,
            Description = template.Description,
            SortOrder = template.SortOrder
        })
        .ToList();

    if (missingTemplates.Count > 0)
    {
        db.ProjectTypeTemplates.AddRange(missingTemplates);
        await db.SaveChangesAsync();
        existingTemplates = await db.ProjectTypeTemplates.ToDictionaryAsync(item => item.ProjectType, item => item);
    }

    var existingLineKeys = (await db.ProjectTypeTemplateLines
        .Select(line => new { line.ProjectTypeTemplateId, line.Description, line.SortOrder })
        .ToListAsync())
        .Select(line => $"{line.ProjectTypeTemplateId}|{line.SortOrder}|{line.Description}")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var missingLines = new List<ProjectTypeTemplateLine>();
    foreach (var template in catalog)
    {
        if (!existingTemplates.TryGetValue(template.ProjectType, out var existingTemplate))
        {
            continue;
        }

        foreach (var line in template.Lines)
        {
            var key = $"{existingTemplate.Id}|{line.SortOrder}|{line.Description}";
            if (existingLineKeys.Contains(key))
            {
                continue;
            }

            missingLines.Add(new ProjectTypeTemplateLine
            {
                ProjectTypeTemplateId = existingTemplate.Id,
                Description = line.Description,
                DefaultQuantity = line.DefaultQuantity,
                DefaultUnitPrice = line.DefaultUnitPrice,
                SortOrder = line.SortOrder
            });
        }
    }

    if (missingLines.Count > 0)
    {
        db.ProjectTypeTemplateLines.AddRange(missingLines);
    }

    await db.SaveChangesAsync();
}

static async Task<int> GetNextTicketSortOrder(SignDashboardDbContext db, string status)
{
    var maxSortOrder = await db.Tickets
        .Where(ticket => ticket.Status == status)
        .Select(ticket => (int?)ticket.SortOrder)
        .MaxAsync();

    return (maxSortOrder ?? 0) + 100;
}

static async Task CreateCustomerStorage(SignDashboardDbContext db, Customer customer)
{
    if (await GetStorageMode(db) == "LocalAgent")
    {
        EnqueueCustomerFolderJob(db, customer);
        await db.SaveChangesAsync();
        return;
    }

    await CreateCustomerFolders(db, customer);
}

static void EnqueueCustomerFolderJob(SignDashboardDbContext db, Customer customer)
{
    var payload = new CustomerFolderJobPayload(
        GetCustomerFolderName(customer),
        GetStandardCustomerSubfolders());

    db.StorageJobs.Add(new StorageJob
    {
        JobType = "CreateCustomerFolders",
        Status = "Pending",
        PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        CustomerId = customer.Id,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    });
}

static void EnqueueFileStorageJob(SignDashboardDbContext db, string jobType, string relativePath, string originalFileName, string contentType, long sizeBytes, int customerId)
{
    var payload = new FileStorageJobPayload(relativePath, originalFileName, contentType, sizeBytes);
    db.StorageJobs.Add(new StorageJob
    {
        JobType = jobType,
        Status = "Pending",
        PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        CustomerId = customerId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    });
}

static async Task CreateCustomerFolders(SignDashboardDbContext db, Customer customer)
{
    var root = await GetCustomerFilesRoot(db);
    if (string.IsNullOrWhiteSpace(root))
    {
        return;
    }

    var customerFolderName = GetCustomerFolderName(customer);
    var customerRoot = Path.Combine(root, customerFolderName);

    Directory.CreateDirectory(customerRoot);
    foreach (var subfolder in GetStandardCustomerSubfolders())
    {
        Directory.CreateDirectory(Path.Combine(customerRoot, subfolder));
    }
}

static string GetCustomerFolderName(Customer customer) =>
    $"{customer.Id:0000}_{ToSafeFolderName(customer.CompanyName)}";

static string[] GetStandardCustomerSubfolders() =>
[
    "01_Admin",
    "02_Quotes",
    "03_Artwork",
    "04_Proofs",
    "05_Production",
    "06_Install",
    "07_Invoices",
    "08_Photos",
    "09_Archive"
];

static string ToSafeFolderName(string value)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    var cleaned = new string(value
        .Trim()
        .Select(character => invalidChars.Contains(character) ? '-' : character)
        .ToArray());

    cleaned = string.Join("-", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    return string.IsNullOrWhiteSpace(cleaned) ? "Customer" : cleaned;
}

static bool IsAllowedArtworkFile(string fileName)
{
    var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ai",
        ".eps",
        ".pdf",
        ".svg",
        ".psd",
        ".tif",
        ".tiff",
        ".png",
        ".jpg",
        ".jpeg",
        ".cdr"
    };

    return allowedExtensions.Contains(Path.GetExtension(fileName));
}

static bool IsDesignStatus(string status) =>
    status is "Design" or "In Review";

static bool IsAllowedProofFile(string fileName)
{
    var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".png",
        ".jpg",
        ".jpeg"
    };

    return allowedExtensions.Contains(Path.GetExtension(fileName));
}

static async Task<int> GetNextArtworkVersion(SignDashboardDbContext db, int? ticketId, int? jobOrderId, int customerId)
{
    var query = db.ArtworkFiles.AsQueryable();
    if (ticketId is not null)
    {
        query = query.Where(file => file.TicketId == ticketId);
    }
    else if (jobOrderId is not null)
    {
        query = query.Where(file => file.JobOrderId == jobOrderId);
    }
    else
    {
        query = query.Where(file => file.CustomerId == customerId && file.TicketId == null && file.JobOrderId == null);
    }

    var latest = await query.Select(file => (int?)file.Version).MaxAsync();
    return (latest ?? 0) + 1;
}

static async Task<int> GetNextProofVersion(SignDashboardDbContext db, int? ticketId, int? jobOrderId, int customerId)
{
    var query = db.ProofFiles.AsQueryable();
    if (ticketId is not null)
    {
        query = query.Where(file => file.TicketId == ticketId);
    }
    else if (jobOrderId is not null)
    {
        query = query.Where(file => file.JobOrderId == jobOrderId);
    }
    else
    {
        query = query.Where(file => file.CustomerId == customerId && file.TicketId == null && file.JobOrderId == null);
    }

    var latest = await query.Select(file => (int?)file.Version).MaxAsync();
    return (latest ?? 0) + 1;
}

static async Task<ProofFile?> CreateDraftProofFromArtwork(
    SignDashboardDbContext db,
    IWebHostEnvironment env,
    Customer customer,
    Ticket? ticket,
    JobOrder? job,
    ArtworkFile artwork,
    string artworkFullPath)
{
    var version = await GetNextProofVersion(db, artwork.TicketId, artwork.JobOrderId, artwork.CustomerId);
    var target = await PrepareProofTarget(db, env, customer, ticket, job, artwork.OriginalFileName, version);

    if (!string.Equals(artworkFullPath, target.FullPath, StringComparison.OrdinalIgnoreCase))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target.FullPath)!);
        File.Copy(artworkFullPath, target.FullPath, overwrite: false);
    }

    var notes = string.IsNullOrWhiteSpace(artwork.Notes)
        ? $"Auto-created from artwork v{artwork.Version:00}."
        : $"Auto-created from artwork v{artwork.Version:00}. {artwork.Notes.Trim()}";

    return new ProofFile
    {
        OriginalFileName = artwork.OriginalFileName,
        StoredFileName = Path.GetFileName(target.FullPath),
        RelativePath = target.RelativePath,
        ContentType = artwork.ContentType,
        SizeBytes = artwork.SizeBytes,
        Version = version,
        Status = "Draft",
        Designer = artwork.UploadedBy,
        Notes = notes,
        ReviewToken = CreateReviewToken(),
        ReviewTokenExpiresAt = DateTime.UtcNow.AddDays(30),
        CustomerId = artwork.CustomerId,
        TicketId = artwork.TicketId,
        JobOrderId = artwork.JobOrderId
    };
}

static async Task<ArtworkTarget> PrepareArtworkTarget(SignDashboardDbContext db, IWebHostEnvironment env, Customer customer, Ticket? ticket, JobOrder? job, string originalFileName, int version)
{
    var usesLocalAgent = await GetStorageMode(db) == "LocalAgent";
    var root = await GetWritableUploadRoot(db, env, usesLocalAgent, "Artwork");

    var customerFolderName = $"{customer.Id:0000}_{ToSafeFolderName(customer.CompanyName)}";
    var customerRoot = Path.Combine(root, customerFolderName);
    Directory.CreateDirectory(customerRoot);

    var jobFolderName = job is not null
        ? $"{job.JobNumber}_{ToSafeFolderName(job.Title)}"
        : ticket is not null
            ? $"TICKET-{ticket.Id:0000}_{ToSafeFolderName(ticket.Title)}"
            : "General-Artwork";

    var artworkRoot = Path.Combine(customerRoot, "Jobs", jobFolderName, "02_Artwork");
    Directory.CreateDirectory(artworkRoot);
    Directory.CreateDirectory(Path.Combine(customerRoot, "Jobs", jobFolderName, "03_Proofs"));
    Directory.CreateDirectory(Path.Combine(customerRoot, "Jobs", jobFolderName, "04_Print-Ready"));

    var extension = Path.GetExtension(originalFileName);
    var nameWithoutExtension = ToSafeFolderName(Path.GetFileNameWithoutExtension(originalFileName));
    var storedFileName = $"{nameWithoutExtension}_v{version:00}{extension}";
    var fullPath = Path.Combine(artworkRoot, storedFileName);
    var relativePath = Path.Combine(customerFolderName, "Jobs", jobFolderName, "02_Artwork", storedFileName);
    return new ArtworkTarget(fullPath, relativePath, usesLocalAgent);
}

static async Task<ArtworkTarget> PrepareProofTarget(SignDashboardDbContext db, IWebHostEnvironment env, Customer customer, Ticket? ticket, JobOrder? job, string originalFileName, int version)
{
    var usesLocalAgent = await GetStorageMode(db) == "LocalAgent";
    var root = await GetWritableUploadRoot(db, env, usesLocalAgent, "Proofs");

    var customerFolderName = $"{customer.Id:0000}_{ToSafeFolderName(customer.CompanyName)}";
    var customerRoot = Path.Combine(root, customerFolderName);
    Directory.CreateDirectory(customerRoot);

    var jobFolderName = job is not null
        ? $"{job.JobNumber}_{ToSafeFolderName(job.Title)}"
        : ticket is not null
            ? $"TICKET-{ticket.Id:0000}_{ToSafeFolderName(ticket.Title)}"
            : "General-Proofs";

    var proofRoot = Path.Combine(customerRoot, "Jobs", jobFolderName, "03_Proofs");
    Directory.CreateDirectory(proofRoot);

    var extension = Path.GetExtension(originalFileName);
    var nameWithoutExtension = ToSafeFolderName(Path.GetFileNameWithoutExtension(originalFileName));
    var storedFileName = $"{nameWithoutExtension}_proof_v{version:00}{extension}";
    var fullPath = Path.Combine(proofRoot, storedFileName);
    var relativePath = Path.Combine(customerFolderName, "Jobs", jobFolderName, "03_Proofs", storedFileName);
    return new ArtworkTarget(fullPath, relativePath, usesLocalAgent);
}

static async Task<string> GetWritableUploadRoot(SignDashboardDbContext db, IWebHostEnvironment env, bool usesLocalAgent, string fallbackFolder)
{
    if (usesLocalAgent)
    {
        return GetAgentStagingRoot(env);
    }

    var configuredRoot = await GetCustomerFilesRoot(db);
    return string.IsNullOrWhiteSpace(configuredRoot)
        ? Path.Combine(env.ContentRootPath, "Uploads", fallbackFolder)
        : configuredRoot;
}

static string GetAgentStagingRoot(IWebHostEnvironment env) =>
    Path.Combine(env.ContentRootPath, "Uploads", "AgentStaging");

static string GetAgentDownloadsRoot(IWebHostEnvironment env) =>
    Path.Combine(env.ContentRootPath, "AgentDownloads");

static async Task<string?> ResolveReadableUploadPath(SignDashboardDbContext db, IWebHostEnvironment env, string relativePath)
{
    var configuredRoot = await GetCustomerFilesRoot(db);
    if (!string.IsNullOrWhiteSpace(configuredRoot))
    {
        var directPath = ResolveContainedPath(configuredRoot, relativePath);
        if (directPath is not null && File.Exists(directPath))
        {
            return directPath;
        }
    }

    var stagedPath = ResolveContainedPath(GetAgentStagingRoot(env), relativePath);
    return stagedPath is not null && File.Exists(stagedPath) ? stagedPath : null;
}

static string? ResolveContainedPath(string root, string relativePath)
{
    var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
    var rootPath = Path.GetFullPath(root);
    return fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
}

static string CreateReviewToken()
{
    var bytes = RandomNumberGenerator.GetBytes(32);
    return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

static string BuildProofReviewUrl(string token)
{
    var webUrl = Environment.GetEnvironmentVariable("PUBLIC_WEB_URL")
        ?? Environment.GetEnvironmentVariable("NEXT_PUBLIC_WEB_URL")
        ?? "http://localhost:3000";
    return $"{webUrl.TrimEnd('/')}/proofs/review/{token}";
}

static async Task<string> GetCustomerFilesRoot(SignDashboardDbContext db)
{
    var saved = await db.AppSettings
        .Where(setting => setting.Key == "CUSTOMER_FILES_ROOT")
        .Select(setting => setting.Value)
        .FirstOrDefaultAsync();

    return string.IsNullOrWhiteSpace(saved)
        ? Environment.GetEnvironmentVariable("CUSTOMER_FILES_ROOT") ?? string.Empty
        : saved;
}

static async Task<string> GetStorageMode(SignDashboardDbContext db)
{
    var saved = await db.AppSettings
        .Where(setting => setting.Key == "STORAGE_MODE")
        .Select(setting => setting.Value)
        .FirstOrDefaultAsync();

    return NormalizeStorageMode(string.IsNullOrWhiteSpace(saved)
        ? Environment.GetEnvironmentVariable("STORAGE_MODE") ?? "Direct"
        : saved);
}

static string NormalizeStorageMode(string? value) =>
    string.Equals(value, "LocalAgent", StringComparison.OrdinalIgnoreCase) ? "LocalAgent" : "Direct";

static async Task<string> GetLocalAgentToken(SignDashboardDbContext db)
{
    var saved = await db.AppSettings
        .Where(setting => setting.Key == "LOCAL_AGENT_TOKEN")
        .Select(setting => setting.Value)
        .FirstOrDefaultAsync();

    return string.IsNullOrWhiteSpace(saved)
        ? Environment.GetEnvironmentVariable("LOCAL_AGENT_TOKEN") ?? string.Empty
        : saved;
}

static async Task<bool> IsAuthorizedAgent(HttpRequest request, SignDashboardDbContext db)
{
    var configuredToken = await GetLocalAgentToken(db);
    if (string.IsNullOrWhiteSpace(configuredToken))
    {
        return false;
    }

    var providedToken = request.Headers["X-Agent-Token"].FirstOrDefault();
    return string.Equals(providedToken, configuredToken, StringComparison.Ordinal);
}

static async Task UpsertSetting(SignDashboardDbContext db, string key, string value)
{
    var setting = await db.AppSettings.FirstOrDefaultAsync(item => item.Key == key);
    if (setting is null)
    {
        db.AppSettings.Add(new AppSetting
        {
            Key = key,
            Value = value,
            UpdatedAt = DateTime.UtcNow
        });
        return;
    }

    setting.Value = value;
    setting.UpdatedAt = DateTime.UtcNow;
}

static void LoadEnvFiles()
{
    var candidateDirectories = new[]
    {
        Directory.GetCurrentDirectory(),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".."))
    };

    foreach (var directory in candidateDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var path = Path.Combine(directory, ".env");
        if (!File.Exists(path))
        {
            continue;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = line[..equalsIndex].Trim();
            var value = line[(equalsIndex + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}

record CustomerRequest(string CompanyName, string ContactName, string Email, string Phone, string Address, string Notes);
record ContactRequest(string FirstName, string LastName, string Title, string Email, string Phone, bool IsPrimaryContact, string Notes);
record LeadRequest(string CompanyName, string ContactName, string Email, string Phone, string Source, string Status, decimal? EstimatedValue, string Notes);
record OpportunityRequest(string Title, string ProjectType, string Stage, decimal EstimatedValue, int Probability, string Source, DateTime? ExpectedCloseDate, string Notes);
record ActivityRequest(string Type, string Subject, string Notes, string AssignedTo, string Status, DateTime? DueAt, int? OpportunityId);
record SettingsRequest(string? CustomerFilesRoot, string? StorageMode, string? LocalAgentToken);
record AgentHeartbeatRequest(string? AgentName, string? CustomerFilesRoot, string? Version);
record AgentJobCompleteRequest(string? AgentName);
record AgentJobFailRequest(string? AgentName, string? ErrorMessage);
record CustomerFolderJobPayload(string CustomerFolderName, string[] Subfolders);
record FileStorageJobPayload(string RelativePath, string OriginalFileName, string ContentType, long SizeBytes);
record TicketRequest(string Title, string Description, string Status, string Priority, string AssignedTo, DateTime? DueDate, int? CustomerId);
record TicketPositionRequest(string Status, int? BeforeTicketId);
record InventoryRequest(string Sku, string Name, string Category, int QuantityOnHand, int ReorderPoint, decimal UnitCost, string Supplier);
record QuoteLineItemRequest(string Description, int Quantity, decimal UnitPrice);
record QuoteRequest(int CustomerId, int? OpportunityId, string ProjectType, string Status, string Scope, decimal TaxRate, DateTime ValidUntil, List<QuoteLineItemRequest> LineItems);
record JobOrderRequest(string Title, string Status, string AssignedTo, string ProductionNotes, bool DxfReady, bool BendFileReady, bool BacksReady, bool FacesReady, bool LedLayoutReady, DateTime? DueDate, int? TicketId, int? QuoteId);
record JobFromTicketRequest(string Title, string AssignedTo, string ProductionNotes, DateTime? DueDate);
record ArtworkTarget(string FullPath, string RelativePath, bool UsesLocalAgent);
record ProofReviewRequest(string Decision, string Comment, string ApprovedBy);
