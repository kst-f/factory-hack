using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

/// <summary>
/// Orchestrates the repair planning workflow:
/// fault mapping → Cosmos queries → LLM invocation → work order creation.
/// </summary>
// Primary constructor — parameters become implicit fields (like Python's __init__)
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";

    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return the response as valid JSON matching the WorkOrder schema.
        
        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: "corrective" | "preventive" | "emergency"
        - priority: "critical" | "high" | "medium" | "low"
        - status, assignedTo (technician id or null), notes
        - estimatedDuration: integer (minutes, e.g. 60 not "60 minutes")
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes (integer), requiredSkills, safetyNotes }]
        
        IMPORTANT: All duration fields must be integers representing minutes (e.g. 90), not strings.
        
        Rules:
        - Assign the most qualified available technician
        - Include only relevant parts; empty array if none needed
        - Tasks must be ordered and actionable
        Return ONLY valid JSON, no markdown fences, no commentary.
        """;

    // AllowReadingFromString handles LLMs returning numbers as strings ("60" instead of 60)
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Registers (or updates) the agent definition in Azure AI Foundry.
    /// Call once at startup before invoking PlanAndCreateWorkOrderAsync.
    /// </summary>
    public async Task EnsureAgentVersionAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Registering agent '{AgentName}' with model '{Model}'",
            AgentName, modelDeploymentName);

        var definition = new PromptAgentDefinition(model: modelDeploymentName)
        {
            Instructions = AgentInstructions
        };

        await projectClient.Agents.CreateAgentVersionAsync(
            AgentName,
            new AgentVersionCreationOptions(definition),
            ct);

        logger.LogInformation("Agent '{AgentName}' registered successfully", AgentName);
    }

    /// <summary>
    /// Full workflow: map fault → query Cosmos → invoke LLM → save work order.
    /// </summary>
    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(
        DiagnosedFault fault,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Planning repair for machine {MachineId}, fault type: {FaultType}, severity: {Severity}",
            fault.MachineId, fault.FaultType, fault.Severity);

        // 1. Look up required skills and part numbers from hardcoded mappings
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredPartNumbers = faultMapping.GetRequiredParts(fault.FaultType);

        logger.LogInformation("Fault '{FaultType}' requires skills: [{Skills}], parts: [{Parts}]",
            fault.FaultType,
            string.Join(", ", requiredSkills),
            string.Join(", ", requiredPartNumbers));

        // 2. Query Cosmos DB for matching technicians and parts
        var technicians = await cosmosDb.GetAvailableTechniciansAsync(requiredSkills, ct);
        var parts = await cosmosDb.GetPartsByNumbersAsync(requiredPartNumbers, ct);

        // 3. Build the prompt with all context for the LLM
        var prompt = BuildPrompt(fault, technicians, parts, requiredSkills);

        // 4. Invoke the LLM agent
        logger.LogInformation("Invoking agent '{AgentName}'...", AgentName);
        var agent = projectClient.GetAIAgent(name: AgentName);
        var response = await agent.RunAsync(prompt, thread: null, options: null, ct);
        var rawJson = response.Text ?? "";

        logger.LogInformation("Agent responded with {Length} characters", rawJson.Length);

        // 5. Parse the LLM response into a WorkOrder
        var workOrder = ParseWorkOrder(rawJson, fault);

        // 6. Save to Cosmos DB
        var saved = await cosmosDb.CreateWorkOrderAsync(workOrder, ct);

        logger.LogInformation("Work order {WorkOrderNumber} created and saved for machine {MachineId}",
            saved.WorkOrderNumber, saved.MachineId);

        return saved;
    }

    private static string BuildPrompt(
        DiagnosedFault fault,
        List<Technician> technicians,
        List<Part> parts,
        IReadOnlyList<string> requiredSkills)
    {
        var techSummary = technicians.Count > 0
            ? string.Join("\n", technicians.Select(t =>
                $"  - {t.Id}: {t.Name}, role: {t.Role}, skills: [{string.Join(", ", t.Skills)}], shift: {t.ShiftSchedule}"))
            : "  (no matching technicians available)";

        var partsSummary = parts.Count > 0
            ? string.Join("\n", parts.Select(p =>
                $"  - {p.PartNumber}: {p.Name}, in stock: {p.QuantityInStock}, cost: ${p.UnitCost}"))
            : "  (no parts required or none in inventory)";

        return $"""
            Create a repair work order for the following diagnosed fault:

            Fault Details:
            - Machine ID: {fault.MachineId}
            - Fault Type: {fault.FaultType}
            - Severity: {fault.Severity}
            - Description: {fault.Description}
            - Timestamp: {fault.Timestamp}

            Required Skills: [{string.Join(", ", requiredSkills)}]

            Available Technicians:
            {techSummary}

            Available Parts:
            {partsSummary}

            Generate a complete work order as JSON.
            """;
    }

    private WorkOrder ParseWorkOrder(string rawJson, DiagnosedFault fault)
    {
        // Strip markdown code fences if the LLM wraps the JSON
        var json = rawJson.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0)
                json = json[(firstNewline + 1)..];
            if (json.EndsWith("```"))
                json = json[..^3];
            json = json.Trim();
        }

        WorkOrder? workOrder;
        try
        {
            workOrder = JsonSerializer.Deserialize<WorkOrder>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse LLM response as WorkOrder. Raw: {Raw}", rawJson);
            throw new InvalidOperationException("LLM returned invalid JSON for work order", ex);
        }

        if (workOrder is null)
            throw new InvalidOperationException("LLM returned null work order");

        // Apply defaults for any fields the LLM may have omitted
        // ??= means "assign if null" (like Python's: x = x or default_value)
        workOrder.MachineId ??= fault.MachineId;
        workOrder.Status ??= "pending";
        workOrder.Priority ??= "medium";
        workOrder.Type ??= "corrective";

        if (string.IsNullOrEmpty(workOrder.WorkOrderNumber))
            workOrder.WorkOrderNumber = $"WO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6]}";

        if (string.IsNullOrEmpty(workOrder.Id))
            workOrder.Id = workOrder.WorkOrderNumber;

        if (string.IsNullOrEmpty(workOrder.CreatedDate))
            workOrder.CreatedDate = DateTime.UtcNow.ToString("o");

        return workOrder;
    }
}
