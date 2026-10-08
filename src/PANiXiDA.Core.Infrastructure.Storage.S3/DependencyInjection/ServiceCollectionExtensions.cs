using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PANiXiDA.Core.Application.Storage;
using PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.DependencyInjection;

/// <summary>
/// Registers the S3 implementation of the application file storage contract.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers file storage using the S3Storage section and standard AWS SDK configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration containing S3Storage and optional AWS sections.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddS3FileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<S3StorageOptions>, S3StorageOptionsValidator>();
        services.AddOptions<S3StorageOptions>()
            .Bind(configuration.GetRequiredSection(S3StorageOptions.SectionName))
            .ValidateOnStart();
        services.AddDefaultAWSOptions(provider =>
        {
            var awsOptions = configuration.GetAWSOptions();
            var options = provider.GetRequiredService<IOptions<S3StorageOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.AccessKey))
            {
                awsOptions.Credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
            }

            return awsOptions;
        });
        services.AddAWSService<IAmazonS3>();
        services.AddSingleton<ITransferUtility>(provider =>
            new TransferUtility(provider.GetRequiredService<IAmazonS3>()));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IFileStorage, S3FileStorage>();

        return services;
    }
}
