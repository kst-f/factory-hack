using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

public sealed class CosmosDbService : IDisposable
{
    private readonly CosmosClient _client;
    private readonly Database _database;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        _logger = logger;
        _client = new CosmosClient(options.Endpoint, options.Key);
        _database = _client.GetDatabase(options.DatabaseName);
    }

    /// <summary>
    /// Queries available technicians whose skills overlap with the required skills.
    /// </summary>
    public async Task<List<Technician>> GetAvailableTechniciansAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken ct = default)
    {
        var container = _database.GetContainer("Technicians");
        var results = new List<Technician>();

        // Query all available technicians, then filter by skill overlap in memory.
        // Cosmos DB doesn't support ARRAY_INTERSECT natively in a parameterized way,
        // so we fetch available techs and filter client-side.
        var query = new QueryDefinition("SELECT * FROM c WHERE c.available = true");

        _logger.LogInformation("Querying available technicians for skills: {Skills}",
            string.Join(", ", requiredSkills));

        using var iterator = container.GetItemQueryIterator<Technician>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }

        // Filter to technicians that have at least one matching skill
        var requiredSet = new HashSet<string>(requiredSkills, StringComparer.OrdinalIgnoreCase);
        var matched = results
            .Where(t => t.Skills.Any(s => requiredSet.Contains(s)))
            .ToList();

        _logger.LogInformation("Found {Total} available technicians, {Matched} with matching skills",
            results.Count, matched.Count);

        return matched;
    }

    /// <summary>
    /// Fetches parts from inventory by their part numbers.
    /// </summary>
    public async Task<List<Part>> GetPartsByNumbersAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken ct = default)
    {
        if (partNumbers.Count == 0)
            return [];

        var container = _database.GetContainer("PartsInventory");
        var results = new List<Part>();

        // Build an IN clause with parameters for safety
        var paramNames = partNumbers.Select((_, i) => $"@p{i}").ToList();
        var queryText = $"SELECT * FROM c WHERE c.partNumber IN ({string.Join(", ", paramNames)})";

        var query = new QueryDefinition(queryText);
        for (int i = 0; i < partNumbers.Count; i++)
        {
            query = query.WithParameter($"@p{i}", partNumbers[i]);
        }

        _logger.LogInformation("Querying parts inventory for: {PartNumbers}",
            string.Join(", ", partNumbers));

        using var iterator = container.GetItemQueryIterator<Part>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            results.AddRange(response);
        }

        _logger.LogInformation("Found {Count} parts in inventory", results.Count);
        return results;
    }

    /// <summary>
    /// Creates a work order in the WorkOrders container.
    /// Partition key is the work order's status field.
    /// </summary>
    public async Task<WorkOrder> CreateWorkOrderAsync(
        WorkOrder workOrder,
        CancellationToken ct = default)
    {
        var container = _database.GetContainer("WorkOrders");

        // Ensure id is set
        if (string.IsNullOrEmpty(workOrder.Id))
        {
            // ?? means "if null/empty, use this instead" (like Python's "or")
            workOrder.Id = workOrder.WorkOrderNumber ?? Guid.NewGuid().ToString();
        }

        _logger.LogInformation("Creating work order {Id} with status '{Status}'",
            workOrder.Id, workOrder.Status);

        var response = await container.CreateItemAsync(
            workOrder,
            new PartitionKey(workOrder.Status),
            cancellationToken: ct);

        _logger.LogInformation("Work order {Id} created (RU charge: {RU})",
            workOrder.Id, response.RequestCharge);

        return response.Resource;
    }

    public void Dispose() => _client.Dispose();
}
