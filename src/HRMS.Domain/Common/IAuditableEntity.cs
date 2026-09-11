namespace HRMS.Domain.Common;

public interface IAuditableEntity
{
    DateTimeOffset CreatedAtUtc
    {
        get; set;
    }
    Guid? CreatedBy
    {
        get; set;
    }
    DateTimeOffset? UpdatedAtUtc
    {
        get; set;
    }
    Guid? UpdatedBy
    {
        get; set;
    }
}
