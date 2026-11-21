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

public sealed class S3FileStorageMultipartCopyTests
{
    private const long SingleCopyLimit = 5L * 1024 * 1024 * 1024;
    private const long MinimumPartSize = 64L * 1024 * 1024;
    private readonly IAmazonS3 _client = Substitute.For<IAmazonS3>();
    private readonly ITransferUtility _transfer = Substitute.For<ITransferUtility>();

    [Theory(DisplayName = "Multipart copy pins every source part and covers all bytes without HEAD or download")]
    [InlineData(SingleCopyLimit + 1, 81)]
    [InlineData(6L * 1024 * 1024 * 1024, 96)]
    [InlineData((MinimumPartSize * 10_000) + 1, 10_000)]
    public async Task CopyAsync_Should_CopyExactRanges_When_ObjectExceedsSingleCopyLimit(long size, int partCount)
    {
        var storage = CreateStorage();
        var ranges = new List<(long First, long Last)>();
        _client.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<CopyPartRequest>();
            request.SourceBucket.ShouldBe("files");
            request.SourceKey.ShouldBe("development/source");
            request.SourceVersionId.ShouldBe("opaque/version+id==");
            request.DestinationBucket.ShouldBe("files");
            request.DestinationKey.ShouldBe("development/target");
            request.UploadId.ShouldBe("upload-id");
            request.PartNumber.ShouldBe(ranges.Count + 1);
            ranges.Add((request.FirstByte!.Value, request.LastByte!.Value));
            return new CopyPartResponse { ETag = $"etag-{ranges.Count}" };
        });

        var result = await storage.CopyAsync(
            "source", "opaque/version+id==", "target", "text/plain", size,
            new Dictionary<string, string> { ["relocation-attempt-id"] = "attempt-1" },
            TestContext.Current.CancellationToken);

        result.VersionId.ShouldBe("destination-version");
        ranges.Count.ShouldBe(partCount);
        ranges[0].First.ShouldBe(0);
        ranges[^1].Last.ShouldBe(size - 1);
        ranges.Sum(range => range.Last - range.First + 1).ShouldBe(size);
        for (var index = 1; index < ranges.Count; index++)
        {
            ranges[index].First.ShouldBe(ranges[index - 1].Last + 1);
        }
        ranges.Take(ranges.Count - 1).ShouldAllBe(range => range.Last - range.First + 1 >= 5 * 1024 * 1024);
        ranges.ShouldAllBe(range => range.Last - range.First + 1 <= SingleCopyLimit);
        await _client.Received(1).InitiateMultipartUploadAsync(Arg.Is<InitiateMultipartUploadRequest>(request =>
            request.BucketName == "files" && request.Key == "development/target"
            && request.ContentType == "text/plain" && request.Metadata["relocation-attempt-id"] == "attempt-1"),
            TestContext.Current.CancellationToken);
        await _client.Received(1).CompleteMultipartUploadAsync(Arg.Is<CompleteMultipartUploadRequest>(request =>
            request.BucketName == "files" && request.Key == "development/target" && request.UploadId == "upload-id"
            && request.PartETags.Count == partCount
            && request.PartETags.Select(part => part.PartNumber).SequenceEqual(
                Enumerable.Range(1, partCount).Select(number => (int?)number))
            && request.PartETags.Select(part => part.ETag).SequenceEqual(
                Enumerable.Range(1, partCount).Select(number => $"etag-{number}"))),
            TestContext.Current.CancellationToken);
        await _client.DidNotReceive().GetObjectMetadataAsync(
            Arg.Any<GetObjectMetadataRequest>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
        await _client.DidNotReceive().AbortMultipartUploadAsync(
            Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Multipart copy aborts initiated uploads and preserves copy or cleanup errors")]
    [InlineData("initiation")]
    [InlineData("upload-id")]
    [InlineData("part")]
    [InlineData("etag")]
    [InlineData("completion")]
    [InlineData("abort")]
    public async Task CopyAsync_Should_CleanUpAndPreserveFailure_When_MultipartStageFails(string stage)
    {
        var storage = CreateStorage();
        var failure = new AmazonS3Exception("Copy failed") { StatusCode = HttpStatusCode.Forbidden };
        var abortFailure = new AmazonS3Exception("Abort failed") { StatusCode = HttpStatusCode.ServiceUnavailable };
        if (stage == "initiation")
        {
            _client.InitiateMultipartUploadAsync(
                Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<InitiateMultipartUploadResponse>(failure));
        }
        else if (stage == "upload-id")
        {
            _client.InitiateMultipartUploadAsync(
                Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>())
                .Returns(new InitiateMultipartUploadResponse());
        }
        else if (stage is "part" or "abort")
        {
            _client.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<CopyPartResponse>(failure));
        }
        else if (stage == "etag")
        {
            _client.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>())
                .Returns(new CopyPartResponse());
        }
        else
        {
            _client.CompleteMultipartUploadAsync(
                Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<CompleteMultipartUploadResponse>(failure));
        }
        if (stage == "abort")
        {
            _client.AbortMultipartUploadAsync(Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<AbortMultipartUploadResponse>(abortFailure));
        }

        var error = await Record.ExceptionAsync(() => storage.CopyAsync(
            "source", "source-version", "target", "text/plain", SingleCopyLimit + 1,
            ReadOnlyDictionary<string, string>.Empty, TestContext.Current.CancellationToken));

        error.ShouldNotBeNull();
        if (stage == "abort")
        {
            error.ShouldBeOfType<AggregateException>().InnerExceptions.ShouldBe([failure, abortFailure]);
        }
        else if (stage is "upload-id" or "etag")
        {
            error.ShouldBeOfType<InvalidDataException>();
        }
        else
        {
            error.ShouldBeSameAs(failure);
        }
        await _client.Received(stage is "initiation" or "upload-id" ? 0 : 1).AbortMultipartUploadAsync(
            Arg.Is<AbortMultipartUploadRequest>(request => request.BucketName == "files"
                && request.Key == "development/target" && request.UploadId == "upload-id"),
            Arg.Is<CancellationToken>(token => token.CanBeCanceled && !token.IsCancellationRequested));
    }

    [Fact(DisplayName = "Cancelling multipart copy still aborts with a separate cleanup token")]
    public async Task CopyAsync_Should_AbortWithIndependentToken_When_CancelledBetweenParts()
    {
        var storage = CreateStorage();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _client.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await cancellation.CancelAsync();
            return new CopyPartResponse { ETag = "first-part" };
        });

        await Should.ThrowAsync<OperationCanceledException>(() => storage.CopyAsync(
            "source", "source-version", "target", "text/plain", SingleCopyLimit + 1,
            ReadOnlyDictionary<string, string>.Empty, cancellation.Token));

        await _client.Received(1).AbortMultipartUploadAsync(Arg.Any<AbortMultipartUploadRequest>(),
            Arg.Is<CancellationToken>(token => token.CanBeCanceled && !token.IsCancellationRequested));
        await _client.DidNotReceive().CompleteMultipartUploadAsync(
            Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Multipart copy waits for provider responses and completes cleanup before reporting a failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_Should_AwaitProviderAndCleanup_When_ResponsesAreDelayed(bool failCompletion)
    {
        var storage = CreateStorage();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var initiation = new TaskCompletionSource<InitiateMultipartUploadResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var part = new TaskCompletionSource<CopyPartResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<CompleteMultipartUploadResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var abort = new TaskCompletionSource<AbortMultipartUploadResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var partStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(initiation.Task);
        _client.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            partStarted.TrySetResult();
            return part.Task;
        });
        _client.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                completionStarted.SetResult();
                return completion.Task;
            });
        _client.AbortMultipartUploadAsync(Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                abortStarted.SetResult();
                return abort.Task;
            });

        var pending = storage.CopyAsync(
            "source", "source-version", "target", "text/plain", SingleCopyLimit + 1,
            ReadOnlyDictionary<string, string>.Empty, timeout.Token);
        await _client.DidNotReceive().CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>());
        initiation.SetResult(new InitiateMultipartUploadResponse { UploadId = "upload-id" });
        await partStarted.Task.WaitAsync(timeout.Token);
        pending.IsCompleted.ShouldBeFalse();
        await _client.DidNotReceive().CompleteMultipartUploadAsync(
            Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>());
        part.SetResult(new CopyPartResponse { ETag = "etag" });
        await completionStarted.Task.WaitAsync(timeout.Token);
        pending.IsCompleted.ShouldBeFalse();

        if (failCompletion)
        {
            var failure = new AmazonS3Exception("Completion failed");
            completion.SetException(failure);
            await abortStarted.Task.WaitAsync(timeout.Token);
            pending.IsCompleted.ShouldBeFalse();
            abort.SetResult(new AbortMultipartUploadResponse());
            (await Should.ThrowAsync<AmazonS3Exception>(() => pending)).ShouldBeSameAs(failure);
        }
        else
        {
            completion.SetResult(new CompleteMultipartUploadResponse { VersionId = "destination-version" });
            (await pending.WaitAsync(timeout.Token)).VersionId.ShouldBe("destination-version");
            await _client.DidNotReceive().AbortMultipartUploadAsync(
                Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>());
        }
    }

    private S3FileStorage CreateStorage()
    {
        _client.Config.Returns(new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 });
        _transfer.S3Client.Returns(_client);
        _client.InitiateMultipartUploadAsync(Arg.Any<InitiateMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InitiateMultipartUploadResponse { UploadId = "upload-id" });
        _client.CopyPartAsync(Arg.Any<CopyPartRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CopyPartResponse { ETag = "etag" });
        _client.CompleteMultipartUploadAsync(Arg.Any<CompleteMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CompleteMultipartUploadResponse { VersionId = "destination-version" });
        _client.AbortMultipartUploadAsync(Arg.Any<AbortMultipartUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AbortMultipartUploadResponse());
        return new S3FileStorage(_transfer, Options.Create(new S3StorageOptions
        {
            BucketName = "files",
            KeyPrefix = "development"
        }));
    }
}
