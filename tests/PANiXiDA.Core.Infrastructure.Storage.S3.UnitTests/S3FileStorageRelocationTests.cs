using System.Collections.ObjectModel;
using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using NSubstitute;
using PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.UnitTests;

public sealed class S3FileStorageRelocationTests
{
    private readonly IAmazonS3 _client = Substitute.For<IAmazonS3>();
    private readonly ITransferUtility _transfer = Substitute.For<ITransferUtility>();

    [Theory(DisplayName = "Copy pins the source version and reports the destination version")]
    [InlineData("copied-version", 0L)]
    [InlineData(null, 4L)]
    [InlineData("boundary-version", 5L * 1024 * 1024 * 1024)]
    public async Task CopyAsync_Should_PinSourceAndReturnDestination_When_CopySucceeds(string? versionId, long size)
    {
        var storage = CreateStorage();
        _client.CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CopyObjectResponse { VersionId = versionId });

        var result = await storage.CopyAsync(
            "personal/source", "opaque/version+id==", "avatar/target", "text/plain", size,
            new Dictionary<string, string> { ["relocation-attempt-id"] = "attempt-1" },
            TestContext.Current.CancellationToken);

        result.VersionId.ShouldBe(versionId);
        await _client.Received(1).CopyObjectAsync(Arg.Is<CopyObjectRequest>(request =>
            request.SourceBucket == "files" && request.DestinationBucket == "files"
            && request.SourceKey == "development/personal/source"
            && request.SourceVersionId == "opaque/version+id=="
            && request.DestinationKey == "development/avatar/target"
            && request.ContentType == "text/plain" && request.MetadataDirective == S3MetadataDirective.REPLACE
            && request.Metadata["relocation-attempt-id"] == "attempt-1"), TestContext.Current.CancellationToken);
        await _client.DidNotReceive().DeleteObjectAsync(
            Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetObjectAsync(
            Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetObjectMetadataAsync(
            Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Invalid copy parameters fail before storage requests")]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("version")]
    [InlineData("type")]
    [InlineData("size")]
    [InlineData("too-large")]
    [InlineData("metadata")]
    [InlineData("same-key")]
    [InlineData("prefixed-metadata")]
    [InlineData("duplicate-metadata")]
    public async Task CopyAsync_Should_AvoidRequests_When_ArgumentsAreInvalid(string kind)
    {
        var storage = CreateStorage();
        IReadOnlyDictionary<string, string> metadata = kind switch
        {
            "metadata" => null!,
            "prefixed-metadata" => new Dictionary<string, string> { ["x-amz-meta-operation"] = "attempt" },
            "duplicate-metadata" => new Dictionary<string, string>
            {
                ["Operation"] = "one",
                ["operation"] = "two"
            },
            _ => ReadOnlyDictionary<string, string>.Empty
        };

        await Should.ThrowAsync<ArgumentException>(() => storage.CopyAsync(
            kind == "source" ? "../source" : "source",
            kind == "version" ? "" : "version-1",
            kind == "destination" ? "../target" : kind == "same-key" ? "source" : "target",
            kind == "type" ? "invalid" : "text/plain",
            kind == "size" ? -1 : kind == "too-large" ? long.MaxValue : 4,
            metadata,
            TestContext.Current.CancellationToken));

        await _client.DidNotReceive().CopyObjectAsync(
            Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Copy metadata rejects malformed HTTP headers before storage requests")]
    public async Task CopyAsync_Should_AvoidRequests_When_MetadataHeaderIsMalformed()
    {
        var storage = CreateStorage();

        await Should.ThrowAsync<FormatException>(() => storage.CopyAsync(
            "source", "version-1", "target", "text/plain", 4,
            new Dictionary<string, string> { ["operation"] = "one\r\ntwo" },
            TestContext.Current.CancellationToken));

        await _client.DidNotReceive().CopyObjectAsync(
            Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Deleting a version specifies its exact identifier without deleting the whole key")]
    public async Task DeleteAsync_Should_TargetOnlyVersion_When_VersionIsSpecified()
    {
        var storage = CreateStorage();
        _client.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteObjectResponse());

        await storage.DeleteAsync("file", "opaque/version+id==", TestContext.Current.CancellationToken);

        await _client.Received(1).DeleteObjectAsync(Arg.Is<DeleteObjectRequest>(request =>
            request.BucketName == "files" && request.Key == "development/file"
            && request.VersionId == "opaque/version+id=="), TestContext.Current.CancellationToken);
    }

    [Theory(DisplayName = "Version deletion rejects missing versions and invalid keys")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DeleteAsync_Should_AvoidRequests_When_VersionIsMissing(string? versionId)
    {
        var storage = CreateStorage();

        await Should.ThrowAsync<ArgumentException>(() =>
            storage.DeleteAsync("file", versionId!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() =>
            storage.DeleteAsync("../file", "version-1", TestContext.Current.CancellationToken));

        await _client.DidNotReceive().DeleteObjectAsync(
            Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Copy and version deletion preserve storage failures")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RelocationOperations_Should_PropagateFailure_When_StorageFails(bool copy)
    {
        var storage = CreateStorage();
        var failure = new AmazonS3Exception("Access denied") { StatusCode = HttpStatusCode.Forbidden };
        _client.CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<CopyObjectResponse>(failure));
        _client.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DeleteObjectResponse>(failure));

        var thrown = await Should.ThrowAsync<AmazonS3Exception>(() => copy
            ? storage.CopyAsync("source", "version-1", "target", "text/plain", 4,
                ReadOnlyDictionary<string, string>.Empty, TestContext.Current.CancellationToken)
            : storage.DeleteAsync("file", "version-1", TestContext.Current.CancellationToken));

        thrown.ShouldBeSameAs(failure);
    }

    [Theory(DisplayName = "Cancelled relocation requests do not contact storage")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RelocationOperations_Should_AvoidRequests_When_Cancelled(bool copy)
    {
        var storage = CreateStorage();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => copy
            ? storage.CopyAsync("source", "version-1", "target", "text/plain", 4,
                ReadOnlyDictionary<string, string>.Empty, cancellation.Token)
            : storage.DeleteAsync("file", "version-1", cancellation.Token));

        await _client.DidNotReceive().CopyObjectAsync(
            Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().DeleteObjectAsync(
            Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    private S3FileStorage CreateStorage()
    {
        _client.Config.Returns(new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 });
        _transfer.S3Client.Returns(_client);
        return new S3FileStorage(_transfer, Options.Create(new S3StorageOptions
        {
            BucketName = "files",
            KeyPrefix = "development"
        }));
    }
}
