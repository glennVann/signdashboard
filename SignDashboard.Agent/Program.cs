using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

LoadEnvFiles();

var apiBaseUrl = GetSetting("SIGN_DASHBOARD_API_BASE_URL", "http://localhost:5206/api").TrimEnd('/');
var agentName = GetSetting("SIGN_DASHBOARD_AGENT_NAME", Environment.MachineName);
var agentToken = GetSetting("SIGN_DASHBOARD_AGENT_TOKEN", string.Empty);
var customerFilesRoot = GetSetting("SIGN_DASHBOARD_CUSTOMER_FILES_ROOT", @"C:\SignDashboardFiles\Customers");
var pollSeconds = int.TryParse(GetSetting("SIGN_DASHBOARD_AGENT_POLL_SECONDS", "15"), out var parsedPollSeconds)
    ? Math.Max(parsedPollSeconds, 5)
    : 15;

if (string.IsNullOrWhiteSpace(agentToken))
{
    Console.Error.WriteLine("SIGN_DASHBOARD_AGENT_TOKEN is required.");
    return 1;
}

Directory.CreateDirectory(customerFilesRoot);

using var client = new HttpClient();
client.DefaultRequestHeaders.Add("X-Agent-Token", agentToken);

Console.WriteLine($"Sign Dashboard local agent started as {agentName}.");
Console.WriteLine($"API: {apiBaseUrl}");
Console.WriteLine($"Customer files root: {customerFilesRoot}");

while (true)
{
    try
    {
        await SendHeartbeat(client, apiBaseUrl, agentName, customerFilesRoot);
        await ProcessNextJob(client, apiBaseUrl, agentName, customerFilesRoot);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"{DateTimeOffset.Now:u} Agent loop failed: {exception.Message}");
    }

    await Task.Delay(TimeSpan.FromSeconds(pollSeconds));
}

static async Task SendHeartbeat(HttpClient client, string apiBaseUrl, string agentName, string customerFilesRoot)
{
    var response = await client.PostAsJsonAsync($"{apiBaseUrl}/agent/heartbeat", new
    {
        agentName,
        customerFilesRoot,
        version = "0.1.0"
    });

    response.EnsureSuccessStatusCode();
}

static async Task ProcessNextJob(HttpClient client, string apiBaseUrl, string agentName, string customerFilesRoot)
{
    var response = await client.GetAsync($"{apiBaseUrl}/agent/jobs/next?agentName={Uri.EscapeDataString(agentName)}");
    if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
    {
        return;
    }

    response.EnsureSuccessStatusCode();
    var job = await response.Content.ReadFromJsonAsync<AgentJob>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (job is null)
    {
        return;
    }

    try
    {
        if (job.JobType == "CreateCustomerFolders")
        {
            CreateCustomerFolders(customerFilesRoot, job.PayloadJson);
        }
        else if (job.JobType is "StoreArtworkFile" or "StoreProofFile")
        {
            await StoreFile(client, apiBaseUrl, customerFilesRoot, job);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported job type: {job.JobType}");
        }

        await client.PostAsJsonAsync($"{apiBaseUrl}/agent/jobs/{job.Id}/complete", new { agentName });
        Console.WriteLine($"{DateTimeOffset.Now:u} Completed {job.JobType} #{job.Id}.");
    }
    catch (Exception exception)
    {
        await client.PostAsJsonAsync($"{apiBaseUrl}/agent/jobs/{job.Id}/fail", new
        {
            agentName,
            errorMessage = exception.Message
        });
        Console.Error.WriteLine($"{DateTimeOffset.Now:u} Failed {job.JobType} #{job.Id}: {exception.Message}");
    }
}

static async Task StoreFile(HttpClient client, string apiBaseUrl, string customerFilesRoot, AgentJob job)
{
    var payload = JsonSerializer.Deserialize<FileStorageJobPayload>(
        job.PayloadJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    if (payload is null || string.IsNullOrWhiteSpace(payload.RelativePath))
    {
        throw new InvalidOperationException("StoreFile payload is invalid.");
    }

    var destinationPath = ResolveContainedPath(customerFilesRoot, payload.RelativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? customerFilesRoot);

    await using var source = await client.GetStreamAsync($"{apiBaseUrl}/agent/jobs/{job.Id}/content");
    await using var destination = File.Create(destinationPath);
    await source.CopyToAsync(destination);
}

static void CreateCustomerFolders(string customerFilesRoot, string payloadJson)
{
    var payload = JsonSerializer.Deserialize<CustomerFolderJobPayload>(
        payloadJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    if (payload is null || string.IsNullOrWhiteSpace(payload.CustomerFolderName))
    {
        throw new InvalidOperationException("CreateCustomerFolders payload is invalid.");
    }

    var customerRoot = ResolveContainedPath(customerFilesRoot, payload.CustomerFolderName);
    Directory.CreateDirectory(customerRoot);
    foreach (var subfolder in payload.Subfolders)
    {
        Directory.CreateDirectory(Path.Combine(customerRoot, subfolder));
    }
}

static string ResolveContainedPath(string root, string relativePath)
{
    var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
    var rootPath = Path.GetFullPath(root);
    if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Resolved path is outside the configured root.");
    }

    return fullPath;
}

static string GetSetting(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;

static void LoadEnvFiles()
{
    foreach (var path in GetCandidateEnvPaths().Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!File.Exists(path))
        {
            continue;
        }

        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
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

static IEnumerable<string> GetCandidateEnvPaths()
{
    yield return Path.Combine(Directory.GetCurrentDirectory(), ".env");
    yield return Path.Combine(AppContext.BaseDirectory, ".env");
    yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"));
}

sealed record AgentJob(int Id, string JobType, string PayloadJson, int Attempts);
sealed record CustomerFolderJobPayload(string CustomerFolderName, string[] Subfolders);
sealed record FileStorageJobPayload(string RelativePath, string OriginalFileName, string ContentType, long SizeBytes);
