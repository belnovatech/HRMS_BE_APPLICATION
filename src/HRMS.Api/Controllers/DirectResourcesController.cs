using System.Text.Json;
using System.Text.Json.Nodes;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRMS.Api.Controllers;

[ApiController]
public sealed class DirectResourcesController(HrmsDbContext database, AccessScope access) : ControllerBase
{
    [HttpGet("api/roles")]
    [HttpGet("api/holidays")]
    [HttpGet("api/announcements")]
    [HttpGet("api/requests")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        string area = Area();
        if (area == "roles" && !access.IsHr)
        {
            return Forbid();
        }

        FeatureResource[] resources = await database.FeatureResources
            .AsNoTracking()
            .Where(resource => resource.Area == area && resource.Collection == "items")
            .ToArrayAsync(cancellationToken);
        IEnumerable<JsonNode?> response = resources
            .OrderByDescending(resource => resource.CreatedAtUtc)
            .Select(resource => JsonNode.Parse(resource.Payload))
            .Where(body => area != "requests" ||
                           access.IsHr ||
                           body?["employeeId"]?.ToString() == access.UserId);

        return Ok(response);
    }

    [HttpPost("api/roles")]
    [HttpPost("api/holidays")]
    [HttpPost("api/announcements")]
    [HttpPost("api/requests")]
    public async Task<IActionResult> Create([FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        if (!access.IsHr && Area() != "requests")
        {
            return Forbid();
        }

        JsonObject body = Parse(payload);
        if (Area() == "requests")
        {
            body["employeeId"] = access.UserId;
            body["status"] = "Pending";
        }
        string area = Area();
        string id = body["id"]?.ToString() ??
                    $"{area[..3].ToUpperInvariant()}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        body["id"] = id;
        ResourceRules.Validate(area, "items", body);
        var row = new FeatureResource
        {
            Area = area,
            Collection = "items",
            ExternalId = id,
            Payload = body.ToJsonString()
        };

        database.Add(row);
        await database.SaveChangesAsync(cancellationToken);
        return Created($"{Request.Path}/{id}", body);
    }

    [HttpPatch("api/roles/{id}")]
    [HttpPatch("api/holidays/{id}")]
    [HttpPatch("api/announcements/{id}")]
    [HttpPatch("api/requests/{id}")]
    public async Task<IActionResult> Patch(string id, [FromBody] JsonElement patch, CancellationToken cancellationToken)
    {
        if (!access.IsHr)
        {
            return Forbid();
        }

        string area = Area();
        FeatureResource? row = await FindResource(area, id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        JsonObject body = JsonNode.Parse(row.Payload)!.AsObject();
        foreach ((string key, JsonNode? value) in Parse(patch))
        {
            if (key != "id")
            {
                body[key] = value?.DeepClone();
            }
        }

        ResourceRules.Validate(area, "items", body);
        row.Payload = body.ToJsonString();
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        return Ok(body);
    }

    [HttpDelete("api/roles/{id}")]
    [HttpDelete("api/holidays/{id}")]
    [HttpDelete("api/announcements/{id}")]
    [HttpDelete("api/requests/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        if (!access.IsHr)
        {
            return Forbid();
        }

        string area = Area();
        FeatureResource? row = await FindResource(area, id, cancellationToken);
        if (row is null)
        {
            return NotFound();
        }

        database.Remove(row);
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private string Area()
    {
        return Request.Path.Value!.Trim('/').Split('/')[1];
    }

    private Task<FeatureResource?> FindResource(
        string area,
        string id,
        CancellationToken cancellationToken)
    {
        return database.FeatureResources.FirstOrDefaultAsync(
            resource => resource.Area == area &&
                        resource.Collection == "items" &&
                        resource.ExternalId == id,
            cancellationToken);
    }

    private static JsonObject Parse(JsonElement value)
    {
        return JsonNode.Parse(value.GetRawText()) as JsonObject
            ?? throw new BadHttpRequestException("A JSON object is required.");
    }
}
