using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PANiXiDA.Core.Infrastructure.Storage.S3.DependencyInjection;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.IntegrationTests.Infrastructure;

public sealed class SeaweedFsContainerFixture : IAsyncLifetime
{
    public const string BucketName = "storage-tests";
    private const string AccessKey = "integration-access";
    private const string SecretKey = "integration-secret";
    private readonly IContainer _container = new ContainerBuilder("chrislusf/seaweedfs:4.48")
        .WithCommand("mini", "-dir=/data", "-s3.autoCreateBucket=false")
        .WithPortBinding(8333, true)
        .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
        .WithEnvironment("S3_BUCKET", BucketName)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S3 Endpoint:", strategy =>
            strategy.WithTimeout(TimeSpan.FromMinutes(1))))
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var services = CreateServices("readiness");
        var response = await services.GetRequiredService<IAmazonS3>().ListBucketsAsync(TestContext.Current.CancellationToken);
        response.Buckets.ShouldContain(bucket => bucket.BucketName == BucketName);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public ServiceProvider CreateServices(string prefix)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AWS:ServiceURL"] = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8333)}",
            ["AWS:AuthenticationRegion"] = "us-east-1",
            ["AWS:ForcePathStyle"] = "true",
            ["S3Storage:BucketName"] = BucketName,
            ["S3Storage:AccessKey"] = AccessKey,
            ["S3Storage:SecretKey"] = SecretKey,
            ["S3Storage:KeyPrefix"] = prefix,
            ["S3Storage:PresignedUrlLifetime"] = "00:05:00"
        }).Build();

        return new ServiceCollection().AddS3FileStorage(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
