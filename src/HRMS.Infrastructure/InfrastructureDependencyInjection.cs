using HRMS.Application.Abstractions;
using HRMS.Application.Abstractions.Persistence;
using HRMS.Infrastructure.Persistence;
using HRMS.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace HRMS.Infrastructure;

public static class InfrastructureDependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryEmployeeRepository>();
        services.AddSingleton<IEmployeeRepository>(provider => provider.GetRequiredService<InMemoryEmployeeRepository>());
        services.AddSingleton<IUnitOfWork>(provider => provider.GetRequiredService<InMemoryEmployeeRepository>());
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        return services;
    }
}
