using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Options;
using NSubstitute;
using PANiXiDA.Core.Application.Storage.Models;
using PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

namespace PANiXiDA.Core.Infrastructure.Storage.S3.UnitTests;

public sealed class S3FileStorageVersionTests
{
    private readonly IAmazonS3 _client = Substitute.For<IAmazonS3>();
    private readonly ITransferUtility _transfer = Substitute.For<ITransferUtility>();

    [Theory(DisplayName = "Metadata uploads return the exact upload version without inspecting the current object")]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("opaque/version+id==")]
    public async Task UploadAsync_Should_ReturnUploadVersion_When_MetadataIsProvided(string? versionId)
    {
        var storage = CreateStorage();
        using var content = new MemoryStream("prefix-content"u8.ToArray());
        content.Position = 7;
        var values = new Dictionary<string, string> { ["operation-id"] = "attempt-1", ["empty"] = "" };
        _transfer.UploadWithResponseAsync(Arg.Any<TransferUtilityUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<TransferUtilityUploadRequest>().InputStream.Dispose();
                return new TransferUtilityUploadResponse { VersionId = versionId };
            });

        var result = await storage.UploadAsync(
            "file.txt", content, "text/plain", values, TestContext.Current.CancellationToken);

        result.VersionId.ShouldBe(versionId);
        content.CanRead.ShouldBeTrue();
        content.Position.ShouldBe(7);
        await _transfer.Received(1).UploadWithResponseAsync(
            Arg.Is<TransferUtilityUploadRequest>(request =>
                request.BucketName == "files" && request.Key == "development/file.txt"
                && request.ContentType == "text/plain"
                && request.Metadata["operation-id"] == "attempt-1" && request.Metadata["empty"] == ""
                && !request.AutoCloseStream && !request.AutoResetStreamPosition),
            TestContext.Current.CancellationToken);
        await _client.DidNotReceive().GetObjectMetadataAsync(
            Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Metadata uploads reject absent or ambiguous metadata")]
    [InlineData("null")]
    [InlineData("empty-name")]
    [InlineData("null-value")]
    [InlineData("prefixed-name")]
    [InlineData("duplicate-name")]
    public async Task UploadAsync_Should_RejectMetadata_When_MetadataIsInvalid(string condition)
    {
        var storage = CreateStorage();
        using var content = new MemoryStream([1]);
        IReadOnlyDictionary<string, string>? values = condition switch
        {
            "null" => null,
            "empty-name" => new Dictionary<string, string> { [""] = "value" },
            "null-value" => new Dictionary<string, string> { ["id"] = null! },
            "prefixed-name" => new Dictionary<string, string> { ["X-Amz-Meta-id"] = "value" },
            _ => new Dictionary<string, string> { ["id"] = "first", ["ID"] = "second" }
        };

        await Should.ThrowAsync<ArgumentException>(() =>
            storage.UploadAsync("file", content, "text/plain", values!, TestContext.Current.CancellationToken));

        await _transfer.DidNotReceive().UploadWithResponseAsync(
            Arg.Any<TransferUtilityUploadRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Metadata uploads reject malformed HTTP header names and values")]
    [InlineData("bad name", "value")]
    [InlineData("id", "first\r\nInjected: value")]
    public async Task UploadAsync_Should_RejectHeaderInjection_When_MetadataContainsInvalidHeaders(
        string name, string value)
    {
        var storage = CreateStorage();
        using var content = new MemoryStream([1]);
        var metadata = new Dictionary<string, string> { [name] = value };

        await Should.ThrowAsync<FormatException>(() =>
            storage.UploadAsync("file", content, "text/plain", metadata, TestContext.Current.CancellationToken));

        await _transfer.DidNotReceive().UploadWithResponseAsync(
            Arg.Any<TransferUtilityUploadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Metadata upload failures preserve the storage error")]
    public async Task UploadAsync_Should_PropagateFailure_When_StorageRejectsUpload()
    {
        var storage = CreateStorage();
        using var content = new MemoryStream([1]);
        var failure = new IOException("Storage unavailable.");
        _transfer.UploadWithResponseAsync(Arg.Any<TransferUtilityUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TransferUtilityUploadResponse>(failure));

        var thrown = await Should.ThrowAsync<IOException>(() => storage.UploadAsync(
            "file", content, "text/plain", new Dictionary<string, string>(), TestContext.Current.CancellationToken));

        thrown.ShouldBeSameAs(failure);
    }

    [Theory(DisplayName = "Metadata reads expose caller metadata without provider header prefixes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMetadataAsync_Should_ReturnCallerMetadata_When_ObjectHasMetadata(bool versioned)
    {
        var storage = CreateStorage();
        var response = new GetObjectMetadataResponse { ContentLength = 4, VersionId = "version-1" };
        response.Headers.ContentType = "text/plain";
        response.Metadata.Add("Operation-ID", "attempt-1");
        _client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = versioned
            ? await storage.GetMetadataAsync("file", "version-1", TestContext.Current.CancellationToken)
            : await storage.GetMetadataAsync("file", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ContentType.ShouldBe("text/plain");
        result.Size.ShouldBe(4);
        result.VersionId.ShouldBe("version-1");
        result.Metadata.Keys.ShouldHaveSingleItem().ShouldBe("Operation-ID");
        result.Metadata["operation-id"].ShouldBe("attempt-1");
        await _client.Received(1).GetObjectMetadataAsync(
            Arg.Is<GetObjectMetadataRequest>(request =>
                request.BucketName == "files" && request.Key == "development/file"
                && request.VersionId == (versioned ? "version-1" : null)),
            TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "A missing requested version never falls back to the current metadata")]
    public async Task GetMetadataAsync_Should_ReturnNull_When_RequestedVersionIsMissing()
    {
        var storage = CreateStorage();
        var failure = new AmazonS3Exception("Missing version")
        {
            StatusCode = HttpStatusCode.NotFound,
            ErrorCode = "NoSuchVersion"
        };
        _client.GetObjectMetadataAsync(Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetObjectMetadataResponse>(failure));

        var result = await storage.GetMetadataAsync("file", "version-1", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _client.Received(1).GetObjectMetadataAsync(
            Arg.Is<GetObjectMetadataRequest>(request => request.VersionId == "version-1"),
            TestContext.Current.CancellationToken);
        await _client.DidNotReceive().GetObjectMetadataAsync(
            Arg.Is<GetObjectMetadataRequest>(request => request.VersionId == null), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Version downloads remain pinned when ranged requests fall back to regular GET")]
    [InlineData(false, "opaque/version+id==")]
    [InlineData(true, "opaque/version+id==")]
    [InlineData(true, "null")]
    public async Task DownloadAsync_Should_PinAllRequests_When_SpecificVersionIsRequested(
        bool fallback, string versionId)
    {
        var storage = CreateStorage();
        await using var content = new MemoryStream([1, 2, 3]);
        var ranged = fallback
            ? Task.FromException<TransferUtilityOpenStreamResponse>(new AmazonS3Exception("Range rejected")
            {
                StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable
            })
            : Task.FromResult(new TransferUtilityOpenStreamResponse { ResponseStream = content });
        _transfer.OpenStreamWithResponseAsync(Arg.Any<TransferUtilityOpenStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(ranged);
        _client.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectResponse { ResponseStream = content });

        var result = await storage.DownloadAsync("file", versionId, TestContext.Current.CancellationToken);

        result.ShouldBeSameAs(content);
        await _transfer.Received(1).OpenStreamWithResponseAsync(
            Arg.Is<TransferUtilityOpenStreamRequest>(request =>
                request.Key == "development/file" && request.VersionId == versionId
                && request.MultipartDownloadType == MultipartDownloadType.RANGE),
            TestContext.Current.CancellationToken);
        if (fallback)
        {
            await _client.Received(1).GetObjectAsync(
                Arg.Is<GetObjectRequest>(request =>
                    request.Key == "development/file" && request.VersionId == versionId),
                TestContext.Current.CancellationToken);
        }
        else
        {
            await _client.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact(DisplayName = "A missing requested version raises FileNotFoundException without reading current content")]
    public async Task DownloadAsync_Should_ReportMissingVersion_When_StorageReturnsNoSuchVersion()
    {
        var storage = CreateStorage();
        var failure = new AmazonS3Exception("Missing version")
        {
            StatusCode = HttpStatusCode.NotFound,
            ErrorCode = "NoSuchVersion"
        };
        _transfer.OpenStreamWithResponseAsync(Arg.Any<TransferUtilityOpenStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TransferUtilityOpenStreamResponse>(failure));

        var thrown = await Should.ThrowAsync<FileNotFoundException>(() =>
            storage.DownloadAsync("file", "version-1", TestContext.Current.CancellationToken));

        thrown.InnerException.ShouldBeSameAs(failure);
        await _client.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Version download URLs sign the exact requested version")]
    public async Task GetPresignedDownloadUrlAsync_Should_PinVersion_When_SpecificVersionIsRequested()
    {
        var storage = CreateStorage();
        _client.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://signed.example");

        var result = await storage.GetPresignedDownloadUrlAsync(
            "file", "opaque/version+id==", "file.txt", "text/plain", TestContext.Current.CancellationToken);

        result.Url.ShouldBe("https://signed.example");
        await _client.Received(1).GetPreSignedURLAsync(Arg.Is<GetPreSignedUrlRequest>(request =>
            request.Key == "development/file" && request.VersionId == "opaque/version+id=="
            && request.Verb == HttpVerb.GET && request.ResponseHeaderOverrides.ContentType == "text/plain"));
    }

    [Theory(DisplayName = "Version-specific operations require a nonempty version identifier")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task VersionOperations_Should_RejectVersion_When_IdentifierIsMissing(string? versionId)
    {
        var storage = CreateStorage();

        await Should.ThrowAsync<ArgumentException>(() =>
            storage.DownloadAsync("file", versionId!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() =>
            storage.GetMetadataAsync("file", versionId!, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() =>
            storage.GetPresignedDownloadUrlAsync("file", versionId!, "file.txt", "text/plain",
                TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "Version enumeration follows both markers and excludes other keys and deletion markers")]
    public async Task GetVersionsAsync_Should_FilterExactKeyAndFollowPages_When_ResponseIsTruncated()
    {
        var storage = CreateStorage();
        var timestamp = DateTime.SpecifyKind(new DateTime(2026, 11, 19, 12, 0, 0), DateTimeKind.Utc);
        var pages = new Queue<ListVersionsResponse>(
        [
            new()
            {
                IsTruncated = true,
                NextKeyMarker = "development/file",
                NextVersionIdMarker = "first",
                Versions =
                [
                    Version("development/file", "first", timestamp),
                    new() { Key = "development/file", IsDeleteMarker = true },
                    Version("development/file-neighbor", "other", timestamp),
                    Version("development/File", "case-sensitive", timestamp)
                ]
            },
            new()
            {
                IsTruncated = true,
                NextKeyMarker = "development/file",
                NextVersionIdMarker = "second",
                Versions = [Version("development/file", "second", timestamp)]
            },
            new()
            {
                IsTruncated = false,
                Versions = [Version("development/file", "null", timestamp)]
            }
        ]);
        var markers = new List<(string? Key, string? Version)>();
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ListVersionsRequest>();
                request.BucketName.ShouldBe("files");
                request.Prefix.ShouldBe("development/file");
                markers.Add((request.KeyMarker, request.VersionIdMarker));
                return pages.Dequeue();
            });

        var versions = await CollectAsync(storage.GetVersionsAsync("file", TestContext.Current.CancellationToken));

        versions.Select(version => version.VersionId).ShouldBe(["first", "second", "null"]);
        versions.ShouldAllBe(version => version.LastModified == new DateTimeOffset(timestamp));
        markers.ShouldBe(new (string? Key, string? Version)[]
        {
            (null, null), ("development/file", "first"), ("development/file", "second")
        });
    }

    [Fact(DisplayName = "Absent keys return no versions even when the SDK omits its collection")]
    public async Task GetVersionsAsync_Should_ReturnEmpty_When_KeyHasNoVersions()
    {
        var storage = CreateStorage();
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListVersionsResponse());

        var versions = await CollectAsync(storage.GetVersionsAsync("missing", TestContext.Current.CancellationToken));

        versions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Stopping version enumeration does not fetch another provider page")]
    public async Task GetVersionsAsync_Should_StopFetching_When_ConsumerStops()
    {
        var storage = CreateStorage();
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListVersionsResponse
            {
                IsTruncated = true,
                NextKeyMarker = "development/file",
                NextVersionIdMarker = "first",
                Versions = [Version("development/file", "first", DateTime.UtcNow)]
            });

        await using var versions = storage.GetVersionsAsync("file", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator();
        (await versions.MoveNextAsync()).ShouldBeTrue();

        await _client.Received(1).ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Malformed version pagination fails instead of looping forever")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetVersionsAsync_Should_RejectPagination_When_MarkersDoNotAdvance(bool missingMarker)
    {
        var storage = CreateStorage();
        var first = new ListVersionsResponse
        {
            IsTruncated = true,
            NextKeyMarker = missingMarker ? null : "development/file",
            NextVersionIdMarker = "first",
            Versions = []
        };
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>()).Returns(first);

        await Should.ThrowAsync<InvalidDataException>(() =>
            CollectAsync(storage.GetVersionsAsync("file", TestContext.Current.CancellationToken)));
    }

    [Theory(DisplayName = "Incomplete version entries fail instead of inventing identifiers or timestamps")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetVersionsAsync_Should_RejectVersion_When_ProviderMetadataIsIncomplete(bool missingIdentifier)
    {
        var storage = CreateStorage();
        var version = Version("development/file", "first", DateTime.UtcNow);
        if (missingIdentifier)
        {
            version.VersionId = null;
        }
        else
        {
            version.LastModified = null;
        }
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListVersionsResponse { Versions = [version] });

        await Should.ThrowAsync<InvalidDataException>(() =>
            CollectAsync(storage.GetVersionsAsync("file", TestContext.Current.CancellationToken)));
    }

    [Theory(DisplayName = "Version enumeration honors cancellation before requests and between yielded entries")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetVersionsAsync_Should_Stop_When_CancellationIsRequested(bool beforeStart)
    {
        var storage = CreateStorage();
        using var cancellation = new CancellationTokenSource();
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListVersionsResponse
            {
                Versions =
                [
                    Version("development/file", "first", DateTime.UtcNow),
                    Version("development/file", "second", DateTime.UtcNow)
                ]
            });
        if (beforeStart)
        {
            await cancellation.CancelAsync();
        }
        await using var versions = storage.GetVersionsAsync("file", cancellation.Token).GetAsyncEnumerator();
        if (!beforeStart)
        {
            (await versions.MoveNextAsync()).ShouldBeTrue();
            await cancellation.CancelAsync();
        }

        await Should.ThrowAsync<OperationCanceledException>(() => versions.MoveNextAsync().AsTask());

        await _client.Received(beforeStart ? 0 : 1).ListVersionsAsync(
            Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Version listing preserves provider failures")]
    public async Task GetVersionsAsync_Should_PropagateFailure_When_StorageIsUnavailable()
    {
        var storage = CreateStorage();
        var failure = new AmazonS3Exception("Access denied") { StatusCode = HttpStatusCode.Forbidden };
        _client.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ListVersionsResponse>(failure));

        var thrown = await Should.ThrowAsync<AmazonS3Exception>(() =>
            CollectAsync(storage.GetVersionsAsync("file", TestContext.Current.CancellationToken)));

        thrown.ShouldBeSameAs(failure);
    }

    [Fact(DisplayName = "Version enumeration validates keys before starting provider requests")]
    public void GetVersionsAsync_Should_RejectKey_When_KeyEscapesPrefix()
    {
        var storage = CreateStorage();

        Should.Throw<ArgumentException>(() =>
            storage.GetVersionsAsync("../file", TestContext.Current.CancellationToken));
    }

    private static S3ObjectVersion Version(string key, string versionId, DateTime modified)
    {
        return new S3ObjectVersion { Key = key, VersionId = versionId, LastModified = modified };
    }

    private static async Task<List<FileVersion>> CollectAsync(IAsyncEnumerable<FileVersion> versions)
    {
        var result = new List<FileVersion>();
        await foreach (var version in versions)
        {
            result.Add(version);
        }
        return result;
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
