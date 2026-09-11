namespace HRMS.Api.Services;

public sealed record UserAccount(
    string Id,
    string Email,
    string Username,
    string Password,
    string Role,
    string Name,
    string Designation,
    string Department,
    string? EmployeeId = null,
    string? ReportsTo = null);

public sealed record LeaveRequest(
    string Id,
    string EmployeeId,
    string EmployeeName,
    string LeaveType,
    DateOnly StartDate,
    DateOnly EndDate,
    int DurationDays,
    string Reason,
    string Status,
    DateOnly AppliedOn,
    string? RejectReason = null);

public sealed record AttendanceRecord(
    string Id,
    string EmployeeId,
    string EmployeeName,
    DateOnly Date,
    TimeOnly? CheckIn,
    TimeOnly? CheckOut,
    string Status,
    string WorkingHours);

public sealed record HelpTicket(
    string Id,
    string EmployeeId,
    string EmployeeName,
    string Category,
    string Subject,
    string Description,
    DateOnly Date,
    string Status,
    string Priority,
    string ResponseNote = "");

public sealed record EmployeeDocument(
    string Id,
    string EmployeeId,
    string Employee,
    string Title,
    string FileName,
    string Category,
    string Size,
    DateOnly Uploaded,
    string Status,
    string? FileId = null);

public sealed record NotificationItem(
    string Id,
    string Audience,
    string? RecipientId,
    string Category,
    string Title,
    string Message,
    bool Unread,
    string TargetPath,
    DateTimeOffset CreatedAt);

public sealed record Payslip(
    string Id,
    string EmployeeId,
    string EmployeeName,
    int Month,
    int Year,
    decimal Basic,
    decimal Allowances,
    decimal Deductions,
    decimal NetPay,
    string Status);

public sealed class HrmsStore
{
    public HrmsStore(HrmsDbContext database)
    {
        Database = database;
    }

    public HrmsDbContext Database { get; }

    public static string NewId(string prefix)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        return $"{prefix}-{suffix}";
    }

    public void Notify(
        string audience,
        string? recipientId,
        string category,
        string title,
        string message,
        string path)
    {
        var notification = new NotificationItem(
            NewId("NOTIF"),
            audience,
            recipientId,
            category,
            title,
            message,
            true,
            path,
            DateTimeOffset.UtcNow);
        Database.Notifications.Add(notification);
    }
}
