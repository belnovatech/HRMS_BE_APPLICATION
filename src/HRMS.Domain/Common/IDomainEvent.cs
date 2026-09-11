namespace HRMS.Domain.Common;

public interface IDomainEvent
{
    DateTimeOffset OccurredAtUtc
    {
        get;
    }
}
