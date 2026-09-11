using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;
namespace HRMS.Api.Controllers;
public sealed class TeamController(HrmsDbContext database, AccessScope access) : ApiControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        string[] ids = access.VisibleEmployeeIds();
        return Ok(database.Users.Where(x => ids.Contains(x.Id)).AsEnumerable().Select(AuthController.PublicUser));
    }
}
