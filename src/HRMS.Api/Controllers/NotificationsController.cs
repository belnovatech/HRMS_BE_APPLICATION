using HRMS.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HRMS.Api.Controllers;

public sealed record CreateNotificationRequest(
    string Audience,
    string? RecipientId,
    string Category,
    string Title,
    string Message,
    string? TargetPath);

public sealed class NotificationsController(HrmsStore store, AccessScope access) : ApiControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        string[] readNotificationIds = store.Database.FeatureResources
            .Where(resource => resource.Area == "notificationReads" &&
                               resource.Collection == access.UserId)
            .Select(resource => resource.ExternalId)
            .ToArray();
        IEnumerable<NotificationItem> notifications = VisibleNotifications()
            .AsEnumerable()
            .OrderByDescending(notification => notification.CreatedAt)
            .Select(notification => notification with
            {
                Unread = !readNotificationIds.Contains(notification.Id)
            });

        return Ok(notifications);
    }

    [HttpPost]
    public IActionResult Create(CreateNotificationRequest request)
    {
        if (!access.IsHr)
        {
            return Forbid();
        }

        if (request.Audience is not ("All" or "HR" or "Manager" or "Employee") || string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest();
        }

        store.Notify(
            request.Audience,
            request.RecipientId,
            request.Category,
            request.Title,
            request.Message,
            request.TargetPath ?? "");
        store.Database.SaveChanges();
        return Accepted();
    }
    [HttpPatch("{id}/read")]
    public IActionResult Read(string id)
    {
        if (!VisibleNotifications().Any(notification => notification.Id == id))
        {
            return NotFound();
        }

        Mark(id);
        store.Database.SaveChanges();
        return NoContent();
    }
    [HttpPatch("read-all")]
    public IActionResult ReadAll()
    {
        foreach (string id in VisibleNotifications().Select(notification => notification.Id).ToArray())
        {
            Mark(id);
        }

        store.Database.SaveChanges();
        return NoContent();
    }

    private IQueryable<NotificationItem> VisibleNotifications()
    {
        return store.Database.Notifications.Where(notification =>
            notification.RecipientId == access.UserId ||
            (notification.RecipientId == null &&
             (notification.Audience == "All" ||
              (access.IsHr && notification.Audience == "HR") ||
              (access.IsManager && notification.Audience == "Manager") ||
              (!access.IsHr && !access.IsManager && notification.Audience == "Employee"))));
    }

    private void Mark(string id)
    {
        bool isAlreadyRead = store.Database.FeatureResources.Any(
            resource => resource.Area == "notificationReads" &&
                        resource.Collection == access.UserId &&
                        resource.ExternalId == id);
        if (!isAlreadyRead)
        {
            store.Database.Add(new FeatureResource
            {
                Area = "notificationReads",
                Collection = access.UserId,
                ExternalId = id,
                Payload = "{}"
            });
        }
    }
}
