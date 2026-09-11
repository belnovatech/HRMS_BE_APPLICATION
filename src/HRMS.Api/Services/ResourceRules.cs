using System.Text.Json.Nodes;

namespace HRMS.Api.Services;

public static class ResourceRules
{
    private static readonly Dictionary<string, string[]> Collections = new()
    {
        ["organization"] = ["departments", "branches", "designations"],
        ["recruitment"] = ["candidates", "jobs"],
        ["biometric"] = ["devices", "events"],
        ["settings"] =
        [
            "company", "branches", "departments", "designations", "shifts", "leave",
            "payroll", "tax", "notifications", "email", "security", "audit"
        ],
        ["roles"] = ["items"],
        ["holidays"] = ["items"],
        ["announcements"] = ["items"],
        ["requests"] = ["items"]
    };

    public static void Collection(string area, string collection)
    {
        if (!Collections.TryGetValue(area, out string[]? names) || !names.Contains(collection))
        {
            throw new BadHttpRequestException("Unknown resource collection.", 404);
        }
    }
    public static void Validate(string area, string collection, JsonObject body)
    {
        Collection(area, collection);
        if (body.ToJsonString().Length > 65536)
        {
            throw new BadHttpRequestException("Resource payload is too large.");
        }

        string? required = area switch
        {
            "organization" or "roles" or "biometric" => "name",
            "recruitment" => collection == "jobs" ? "title" : "name",
            "holidays" => "name",
            "announcements" => "title",
            "requests" => "type",
            _ => null
        };
        if (required is not null && !HasValidRequiredText(body, required))
        {
            throw new BadHttpRequestException($"{required} is required and must be at most 250 characters.");
        }

        if (body["email"] is { } email && !IsValidEmail(email.ToString()))
        {
            throw new BadHttpRequestException("Invalid email.");
        }

        if (area == "holidays" && !DateOnly.TryParse(body["date"]?.ToString(), out _))
        {
            throw new BadHttpRequestException("A holiday date is required.");
        }

        string? candidateStage = body["stage"]?.ToString();
        if (area == "recruitment" &&
            collection == "candidates" &&
            candidateStage is not null &&
            candidateStage is not ("Applied" or "Screening" or "Interview" or "Offer" or "Hired" or "Rejected"))
        {
            throw new BadHttpRequestException("Invalid candidate stage.");
        }

        if (area == "biometric" && collection == "devices")
        {
            // Connection state is written only after a successful gateway sync.
            body.Remove("lastSync");
            body["status"] = "Not synced";
        }
    }

    private static bool HasValidRequiredText(JsonObject body, string propertyName)
    {
        return body[propertyName] is JsonValue value &&
               value.TryGetValue(out string? text) &&
               !string.IsNullOrWhiteSpace(text) &&
               text.Length <= 250;
    }

    private static bool IsValidEmail(string email)
    {
        return System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
               address.Address == email;
    }
}
