using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

public sealed record CreateDocumentRequest(
    string EmployeeId,
    string? EmployeeName,
    string Title,
    string FileName,
    string? Category,
    string? Size,
    string? FileId);

public sealed record DocumentStatusRequest(string Status);

public sealed class DocumentsController(HrmsStore store, AccessScope access) : ApiControllerBase
{
    [HttpGet]
    public IActionResult List([FromQuery] string? employeeId, [FromQuery] string? status)
    {
        string[] visibleEmployeeIds = access.VisibleEmployeeIds();
        IQueryable<EmployeeDocument> documents = store.Database.Documents
            .Where(document => access.IsHr || visibleEmployeeIds.Contains(document.EmployeeId))
            .Where(document => employeeId == null || document.EmployeeId == employeeId)
            .Where(document => status == null || document.Status == status)
            .OrderByDescending(document => document.Uploaded);

        return Ok(documents);
    }

    [HttpPost]
    public IActionResult Create(CreateDocumentRequest request)
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
        FeatureResource? file = store.Database.FeatureResources.FirstOrDefault(
            resource => resource.Area == "files" &&
                        resource.Collection == "uploads" &&
                        resource.ExternalId == request.FileId);
        if (file is null || string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new
            {
                title = "Upload a file and provide a document title."
            });
        }

        FileMetadata metadata = System.Text.Json.JsonSerializer.Deserialize<FileMetadata>(file.Payload)!;
        if (metadata.OwnerId != access.UserId && !access.IsHr)
        {
            return Forbid();
        }

        var document = new EmployeeDocument(
            HrmsStore.NewId("DOC"),
            request.EmployeeId,
            name,
            request.Title,
            metadata.FileName,
            request.Category ?? "General",
            metadata.Length.ToString(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Pending",
            request.FileId);

        store.Database.Documents.Add(document);
        store.Notify(
            "HR",
            null,
            "Documents",
            "Document Uploaded for Verification",
            $"{name} uploaded {request.Title}.",
            "/hr/documents");
        store.Database.SaveChanges();
        return Created($"/api/documents/{document.Id}", document);
    }

    [HttpPatch("{id}/status")]
    public IActionResult SetStatus(string id, DocumentStatusRequest request)
    {
        EmployeeDocument? document = store.Database.Documents.Find(id);
        if (document is null)
        {
            return NotFound();
        }

        if (!access.IsHr)
        {
            return Forbid();
        }

        if (request.Status is not ("Pending" or "Verified" or "Rejected"))
        {
            return BadRequest(new ProblemDetails { Title = "Status must be Pending, Verified, or Rejected." });
        }

        EmployeeDocument updated = document with
        {
            Status = request.Status
        };
        store.Database.Entry(document).CurrentValues.SetValues(updated);
        store.Notify(
            "Employee",
            document.EmployeeId,
            "Documents",
            $"Document {request.Status}",
            $"Your document '{document.Title}' has been {request.Status.ToLowerInvariant()}.",
            "/employee/documents");
        store.Database.SaveChanges();
        return Ok(updated);
    }
}
