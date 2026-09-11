using System.Text.Json;
using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

public sealed record FileMetadata(string OwnerId, string FileName, long Length);

public sealed class FilesController(
    IWebHostEnvironment environment,
    HrmsDbContext database,
    AccessScope access,
    IConfiguration configuration) : ApiControllerBase
{
    private static readonly string[] AllowedExtensions =
    [
        ".pdf", ".png", ".jpg", ".jpeg", ".docx", ".xlsx", ".csv", ".txt"
    ];

    private string StorageRoot => Path.GetFullPath(
        configuration["FileStorage:RootPath"] ?? "App_Data/uploads",
        environment.ContentRootPath);

    [HttpPost]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 20_000_000)
        {
            return BadRequest();
        }

        string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            return BadRequest(new
            {
                title = "Unsupported file type."
            });
        }

        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(StorageRoot);
        string path = Path.Combine(StorageRoot, id);
        try
        {
            await using (FileStream stream = System.IO.File.Create(path))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var metadata = new FileMetadata(access.UserId, Path.GetFileName(file.FileName), file.Length);
            database.Add(new FeatureResource
            {
                Area = "files",
                Collection = "uploads",
                ExternalId = id,
                Payload = JsonSerializer.Serialize(metadata)
            });
            await database.SaveChangesAsync(cancellationToken);
            return CreatedAtAction(nameof(Download), new
            {
                id
            }, new
            {
                id,
                metadata.FileName,
                file.Length
            });
        }
        catch
        {
            System.IO.File.Delete(path);
            throw;
        }
    }

    [HttpGet("{id}")]
    public IActionResult Download(string id)
    {
        FeatureResource? row = Find(id);
        if (row is null)
        {
            return NotFound();
        }

        FileMetadata metadata = JsonSerializer.Deserialize<FileMetadata>(row.Payload)!;
        if (!access.IsHr && metadata.OwnerId != access.UserId)
        {
            return Forbid();
        }

        string path = Path.Combine(StorageRoot, id);
        Response.Headers.XContentTypeOptions = "nosniff";
        return System.IO.File.Exists(path)
            ? PhysicalFile(path, "application/octet-stream", metadata.FileName)
            : NotFound();
    }

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        FeatureResource? row = Find(id);
        if (row is null)
        {
            return NotFound();
        }

        FileMetadata metadata = JsonSerializer.Deserialize<FileMetadata>(row.Payload)!;
        if (!access.IsHr && metadata.OwnerId != access.UserId)
        {
            return Forbid();
        }

        if (database.Documents.Any(x => x.FileId == id))
        {
            return Conflict(new
            {
                title = "This file is attached to a document."
            });
        }

        database.Remove(row);
        database.SaveChanges();
        System.IO.File.Delete(Path.Combine(StorageRoot, id));
        return NoContent();
    }

    private FeatureResource? Find(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            return null;
        }

        return database.FeatureResources.FirstOrDefault(
            resource => resource.Area == "files" &&
                        resource.Collection == "uploads" &&
                        resource.ExternalId == id);
    }
}
