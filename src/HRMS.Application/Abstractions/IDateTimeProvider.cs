namespace HRMS.Application.Abstractions;

public interface IDateTimeProvider
{
    DateTimeOffset UtcNow
    {
        get;
    }
}
