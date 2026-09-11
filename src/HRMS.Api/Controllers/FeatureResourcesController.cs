using System.Text.Json;
using System.Text.Json.Nodes;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

[ApiController]
[Route("api/{area:regex(^(organization|recruitment|roles|settings|biometric|holidays|announcements|requests)$)}")]
[Authorize(Roles = "hr")]
public sealed class FeatureResourcesController(HrmsDbContext database) : ControllerBase
{
    [HttpGet("{collection}")]
    public async Task<IActionResult> List(
        string area,
        string collection,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        ResourceRules.Collection(area, collection);
        FeatureResource[] resources = await database.FeatureResources
            .AsNoTracking()
            .Where(resource => resource.Area == area && resource.Collection == collection)
            .ToArrayAsync(cancellationToken);
        IEnumerable<JsonNode?> values = resources
            .OrderBy(resource => resource.CreatedAtUtc)
            .Select(ToResponse);

        if (!string.IsNullOrWhiteSpace(status))
        {
            values = values.Where(body => string.Equals(
                body?["status"]?.GetValue<string>(),
                status,
                StringComparison.OrdinalIgnoreCase));
        }

        return Ok(values);
    }

    [HttpGet("{collection}/{id}")]
    public async Task<IActionResult> Get(
        string area,
        string collection,
        string id,
        CancellationToken cancellationToken)
    {
        FeatureResource? row = await Find(area, collection, id, cancellationToken);
        return row is null ? NotFound() : Ok(ToResponse(row));
    }

    [HttpPost("{collection}")]
    public async Task<IActionResult> Create(
        string area,
        string collection,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        JsonObject body = RequireObject(payload);
        string externalId = ReadId(body) ?? NewResourceId(collection);
        bool resourceExists = await database.FeatureResources.AnyAsync(
            resource => resource.Area == area &&
                        resource.Collection == collection &&
                        resource.ExternalId == externalId,
            cancellationToken);
        if (resourceExists)
        {
            return Conflict(new ProblemDetails { Title = "A resource with this id already exists." });
        }

        body["id"] = externalId;
        ResourceRules.Validate(area, collection, body);
        var row = new FeatureResource
        {
            Area = area,
            Collection = collection,
            ExternalId = externalId,
            Payload = body.ToJsonString()
        };

        database.FeatureResources.Add(row);
        await database.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new
        {
            area,
            collection,
            id = externalId
        }, ToResponse(row));
    }

    [HttpPut("{collection}/{id}")]
    public async Task<IActionResult> Replace(
        string area,
        string collection,
        string id,
        [FromBody] JsonElement payload,
        CancellationToken cancellationToken)
    {
        FeatureResource? row = await Find(area, collection, id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        JsonObject body = RequireObject(payload);
        body["id"] = id;
        ResourceRules.Validate(area, collection, body);
        row.Payload = body.ToJsonString();
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(row));
    }

    [HttpPatch("{collection}/{id}")]
    public async Task<IActionResult> Patch(
        string area,
        string collection,
        string id,
        [FromBody] JsonElement patch,
        CancellationToken cancellationToken)
    {
        FeatureResource? row = await Find(area, collection, id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        JsonObject body = JsonNode.Parse(row.Payload)!.AsObject();
        foreach ((string key, JsonNode? value) in RequireObject(patch))
        {
            if (!key.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                body[key] = value?.DeepClone();
            }
        }

        ResourceRules.Validate(area, collection, body);
        row.Payload = body.ToJsonString();
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(row));
    }

    [HttpDelete("{collection}/{id}")]
    public async Task<IActionResult> Delete(
        string area,
        string collection,
        string id,
        CancellationToken cancellationToken)
    {
        FeatureResource? row = await Find(area, collection, id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        database.FeatureResources.Remove(row);
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{collection}/{id}/status")]
    public Task<IActionResult> Status(
        string area,
        string collection,
        string id,
        [FromBody] JsonElement patch,
        CancellationToken cancellationToken)
    {
        return Patch(area, collection, id, patch, cancellationToken);
    }

    private Task<FeatureResource?> Find(
        string area,
        string collection,
        string id,
        CancellationToken cancellationToken)
    {
        return database.FeatureResources.FirstOrDefaultAsync(
            resource => resource.Area == area &&
                        resource.Collection == collection &&
                        resource.ExternalId == id,
            cancellationToken);
    }

    private static JsonObject RequireObject(JsonElement payload)
    {
        return JsonNode.Parse(payload.GetRawText()) as JsonObject
            ?? throw new BadHttpRequestException("A JSON object is required.");
    }

    private static string? ReadId(JsonObject body) => body["id"]?.ToString();

    private static string NewResourceId(string collection)
    {
        string prefix = collection.Length >= 3
            ? collection[..3].ToUpperInvariant()
            : collection.ToUpperInvariant();
        string suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        return $"{prefix}-{suffix}";
    }

    private static JsonNode? ToResponse(FeatureResource row) => JsonNode.Parse(row.Payload);
}
