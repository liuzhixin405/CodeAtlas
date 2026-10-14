namespace Microsoft.Extensions.DependencyInjection
{
    // 与真实 DI 扩展同名，fixture 不依赖 NuGet 包
    public interface IServiceCollection { }

    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection s) where TImplementation : TService => s;
        public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection s) where TImplementation : TService => s;
        public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection s) where TImplementation : TService => s;
    }
}

namespace Sample.Core
{
    using Microsoft.Extensions.DependencyInjection;

    public static class Registration
    {
        public static IServiceCollection AddSample(this IServiceCollection services, bool useReal)
        {
            services.AddScoped<IRepo, Repo>();
            if (useReal)
            {
                services.AddSingleton<IDispatcher, Dispatcher>();
            }
            return services;
        }
    }
}
