namespace HRMS.Application.Common;

public sealed record Error(string Code, string Description)
{
    public static readonly Error None = new(string.Empty, string.Empty);
    public static Error NotFound(string resource, object id) => new("NotFound", $"{resource} '{id}' was not found.");
    public static Error Conflict(string description) => new("Conflict", description);
    public static Error Validation(string description) => new("Validation", description);
}
