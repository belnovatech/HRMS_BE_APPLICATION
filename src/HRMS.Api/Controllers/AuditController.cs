using System.Text.Json;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace HRMS.Api.Controllers;
[Authorize(Roles = "hr")]
public sealed class AuditController(HrmsDbContext database) : ApiControllerBase
{
    [HttpGet]
    public IActionResult List([FromQuery] int limit = 100) => Ok(database.FeatureResources
        .Where(x => x.Area == "audit" && x.Collection == "events").AsEnumerable().OrderByDescending(x => x.CreatedAtUtc)
        .Take(Math.Clamp(limit, 1, 500)).Select(x => JsonSerializer.Deserialize<JsonElement>(x.Payload)));
}
