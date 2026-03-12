using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;

// ── Read environment variables ──────────────────────────────────────────────
var projectEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_AI_PROJECT_ENDPOINT is not set");

var modelDeploymentName = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("MODEL_DEPLOYMENT_NAME is not set");

var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")
    ?? throw new InvalidOperationException("COSMOS_ENDPOINT is not set");

var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY")
    ?? throw new InvalidOperationException("COSMOS_KEY is not set");

var cosmosDatabase = Environment.GetEnvironmentVariable("COSMOS_DATABASE_NAME")
    ?? throw new InvalidOperationException("COSMOS_DATABASE_NAME is not set");

// ── Set up DI container with logging ────────────────────────────────────────
var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

services.AddSingleton(new CosmosDbOptions
{
    Endpoint = cosmosEndpoint,
    Key = cosmosKey,
    DatabaseName = cosmosDatabase
});

services.AddSingleton<CosmosDbService>();
services.AddSingleton<IFaultMappingService, FaultMappingService>();

// AIProjectClient uses DefaultAzureCredential (run "az login" first)
services.AddSingleton(_ => new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential()));

services.AddSingleton(sp => new RepairPlannerAgent(
    sp.GetRequiredService<AIProjectClient>(),
    sp.GetRequiredService<CosmosDbService>(),
    sp.GetRequiredService<IFaultMappingService>(),
    modelDeploymentName,
    sp.GetRequiredService<ILogger<RepairPlannerAgent>>()));

// await using — like Python's "async with", disposes resources when done
await using var provider = services.BuildServiceProvider();

var logger = provider.GetRequiredService<ILogger<Program>>();
var agent = provider.GetRequiredService<RepairPlannerAgent>();

// ── Register the agent in Azure AI Foundry ──────────────────────────────────
logger.LogInformation("Registering RepairPlannerAgent...");
await agent.EnsureAgentVersionAsync();

// ── Sample diagnosed fault (simulates output from Challenge 1) ──────────────
var sampleFault = new DiagnosedFault
{
    MachineId = "machine-001",
    FaultType = "curing_temperature_excessive",
    Severity = "high",
    Description = "Mold temperature consistently exceeding 185°C upper threshold. "
                + "Heating element degradation suspected. Temperature oscillation pattern "
                + "indicates possible PID controller issue.",
    Timestamp = DateTime.UtcNow.ToString("o")
};

logger.LogInformation("Processing fault: {FaultType} on {MachineId}",
    sampleFault.FaultType, sampleFault.MachineId);

// ── Run the full repair planning workflow ────────────────────────────────────
var workOrder = await agent.PlanAndCreateWorkOrderAsync(sampleFault);

// ── Print the result ─────────────────────────────────────────────────────────
var prettyJson = JsonSerializer.Serialize(workOrder, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
});

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════");
Console.WriteLine("  REPAIR WORK ORDER CREATED");
Console.WriteLine("═══════════════════════════════════════════════════");
Console.WriteLine(prettyJson);
Console.WriteLine("═══════════════════════════════════════════════════");

