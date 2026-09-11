using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

public sealed record CreateTicketRequest(
    string EmployeeId,
    string? EmployeeName,
    string Category,
    string Subject,
    string Description,
    string? Priority);

public sealed record UpdateTicketRequest(string Status, string? ResponseNote);

public sealed class SupportController(HrmsStore store, AccessScope access) : ApiControllerBase
{
    [HttpGet("tickets")]
    public IActionResult List([FromQuery] string? employeeId)
    {
        string[] visibleEmployeeIds = access.VisibleEmployeeIds();
        IQueryable<HelpTicket> tickets = store.Database.Tickets
            .Where(ticket => access.IsHr || visibleEmployeeIds.Contains(ticket.EmployeeId))
            .Where(ticket => employeeId == null || ticket.EmployeeId == employeeId);

        return Ok(tickets);
    }

    [HttpPost("tickets")]
    public IActionResult Create(CreateTicketRequest request)
    {
        if (!access.CanWriteSelf(request.EmployeeId))
        {
            return Forbid();
        }

        UserAccount? account = store.Database.Users.Find(request.EmployeeId);
        if (account is null)
        {
            return BadRequest(new
            {
                title = "Unknown employee."
            });
        }

        string name = account.Name;
        var ticket = new HelpTicket(
            HrmsStore.NewId("TKT"),
            request.EmployeeId,
            name,
            request.Category,
            request.Subject,
            request.Description,
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Open",
            request.Priority ?? "Normal");

        store.Database.Tickets.Add(ticket);
        store.Notify(
            "HR",
            null,
            "Support",
            "New Support Ticket Received",
            $"{name} opened ticket '{request.Subject}'.",
            "/hr/help");
        store.Database.SaveChanges();
        return Created($"/api/support/tickets/{ticket.Id}", ticket);
    }

    [HttpPatch("tickets/{id}")]
    public IActionResult Update(string id, UpdateTicketRequest request)
    {
        HelpTicket? ticket = store.Database.Tickets.Find(id);
        if (ticket is null)
        {
            return NotFound();
        }

        if (!access.IsHr)
        {
            return Forbid();
        }

        if (request.Status is not ("Open" or "In Progress" or "Resolved" or "Closed"))
        {
            return BadRequest();
        }

        HelpTicket updated = ticket with
        {
            Status = request.Status,
            ResponseNote = request.ResponseNote ?? ""
        };
        store.Database.Entry(ticket).CurrentValues.SetValues(updated);
        store.Notify(
            "Employee",
            ticket.EmployeeId,
            "Support",
            $"Support Ticket {request.Status}",
            $"Ticket '{ticket.Subject}' is now {request.Status}.",
            "/employee/help");
        store.Database.SaveChanges();
        return Ok(updated);
    }
}
