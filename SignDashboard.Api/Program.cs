using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
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
    await db.Customers.OrderBy(customer => customer.CompanyName).ToListAsync());

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
        Status = "Scheduled"
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
    await db.Quotes.Include(quote => quote.Customer)
        .Include(quote => quote.LineItems)
        .OrderByDescending(quote => quote.CreatedAt)
        .ToListAsync());

api.MapPost("/quotes", async (QuoteRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var subtotal = request.LineItems.Sum(item => item.Quantity * item.UnitPrice);
    var tax = Math.Round(subtotal * request.TaxRate, 2);
    var quote = new Quote
    {
        QuoteNumber = $"Q-{DateTime.UtcNow:yyyyMMddHHmmss}",
        CustomerId = request.CustomerId,
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
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "quote.created", quote.Id);
    return Results.Created($"/api/quotes/{quote.Id}", quote);
});

api.MapGet("/job-orders", async (SignDashboardDbContext db) =>
    await db.JobOrders.Include(job => job.Ticket)
        .Include(job => job.Quote)
        .OrderByDescending(job => job.CreatedAt)
        .ToListAsync());

api.MapPost("/job-orders", async (JobOrderRequest request, SignDashboardDbContext db, IHubContext<DashboardHub> hub) =>
{
    var job = new JobOrder
    {
        JobNumber = $"JOB-{DateTime.UtcNow:yyyyMMddHHmmss}",
        TicketId = request.TicketId,
        QuoteId = request.QuoteId,
        Title = request.Title,
        Status = request.Status,
        AssignedTo = request.AssignedTo,
        ProductionNotes = request.ProductionNotes,
        DueDate = request.DueDate
    };

    db.JobOrders.Add(job);
    await db.SaveChangesAsync();
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

    job.TicketId = request.TicketId;
    job.QuoteId = request.QuoteId;
    job.Title = request.Title;
    job.Status = request.Status;
    job.AssignedTo = request.AssignedTo;
    job.ProductionNotes = request.ProductionNotes;
    job.DueDate = request.DueDate;
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "job.updated", job.Id);
    return Results.Ok(job);
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
    await db.SaveChangesAsync();
    if (target.UsesLocalAgent)
    {
        EnqueueFileStorageJob(db, "StoreArtworkFile", target.RelativePath, artwork.OriginalFileName, artwork.ContentType, artwork.SizeBytes, customer.Id);
        await db.SaveChangesAsync();
    }
    await BroadcastDashboardChange(hub, "artwork.uploaded", artwork.Id);
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
    if (target.UsesLocalAgent)
    {
        EnqueueFileStorageJob(db, "StoreProofFile", target.RelativePath, proof.OriginalFileName, proof.ContentType, proof.SizeBytes, customer.Id);
        await db.SaveChangesAsync();
    }
    await BroadcastDashboardChange(hub, "proof.uploaded", proof.Id);
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
    await BroadcastDashboardChange(hub, "proof.review_submitted", proof.Id);
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
        CustomerId = ParseNullableInt(form["customerId"]),
        TicketId = ParseNullableInt(form["ticketId"]),
        JobOrderId = ParseNullableInt(form["jobOrderId"])
    };

    db.SignFiles.Add(signFile);
    await db.SaveChangesAsync();
    await BroadcastDashboardChange(hub, "file.uploaded", signFile.Id);
    return Results.Created($"/api/files/{signFile.Id}", signFile);
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SignDashboardDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureSchemaUpdates(db);
}

app.Run();

static int? ParseNullableInt(string? value) =>
    int.TryParse(value, out var parsed) ? parsed : null;

static DateTime? ParseNullableDateTime(string? value) =>
    DateTime.TryParse(value, out var parsed) ? parsed : null;

static Task BroadcastDashboardChange(IHubContext<DashboardHub> hub, string eventName, int entityId) =>
    hub.Clients.All.SendAsync("dashboardChanged", new
    {
        eventName,
        entityId,
        changedAt = DateTime.UtcNow
    });

static async Task EnsureSchemaUpdates(SignDashboardDbContext db)
{
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Tickets` ADD COLUMN IF NOT EXISTS `DueDate` datetime(6) NULL");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE `Tickets` ADD COLUMN IF NOT EXISTS `SortOrder` int NOT NULL DEFAULT 0");
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
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS `IX_StorageAgents_AgentName` ON `StorageAgents` (`AgentName`)");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS `IX_StorageJobs_Status_CreatedAt` ON `StorageJobs` (`Status`, `CreatedAt`)");
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
record QuoteRequest(int CustomerId, string Status, string Scope, decimal TaxRate, DateTime ValidUntil, List<QuoteLineItemRequest> LineItems);
record JobOrderRequest(string Title, string Status, string AssignedTo, string ProductionNotes, DateTime? DueDate, int? TicketId, int? QuoteId);
record JobFromTicketRequest(string Title, string AssignedTo, string ProductionNotes, DateTime? DueDate);
record ArtworkTarget(string FullPath, string RelativePath, bool UsesLocalAgent);
record ProofReviewRequest(string Decision, string Comment, string ApprovedBy);
