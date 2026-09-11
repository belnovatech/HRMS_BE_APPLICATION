using HRMS.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    protected IActionResult FromFailure(Result result) => result.Error.Code switch
    {
        "NotFound" => NotFound(ToProblem(result.Error, StatusCodes.Status404NotFound)),
        "Conflict" => Conflict(ToProblem(result.Error, StatusCodes.Status409Conflict)),
        _ => BadRequest(ToProblem(result.Error, StatusCodes.Status400BadRequest))
    };

    private static ProblemDetails ToProblem(Error error, int status) => new()
    {
        Status = status,
        Title = error.Code,
        Detail = error.Description
    };
}
