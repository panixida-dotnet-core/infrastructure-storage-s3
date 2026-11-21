using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal.Util;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.Util;
using Microsoft.Extensions.Options;
using PANiXiDA.Core.Application.Storage;
using PANiXiDA.Core.Application.Storage.Models;
using PANiXiDA.Core.Infrastructure.Storage.S3.Configuration;

namespace PANiXiDA.Core.Infrastructure.Storage.S3;

internal sealed class S3FileStorage(
    ITransferUtility transferUtility,
    IOptions<S3StorageOptions> options) : IFileStorage
{
    private const string NoSuchBucketErrorCode = "NoSuchBucket";
    private const string MetadataHeaderPrefix = "x-amz-meta-";
    private const long MaximumCopyPartSizeBytes = 5L * 1024 * 1024 * 1024;
    private const long MinimumCopyPartSizeBytes = 64L * 1024 * 1024;
    private const int MaximumCopyParts = 10_000;

    private readonly S3StorageOptions _options = options.Value;

    public Task UploadAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var request = CreateUploadRequest(key, content, contentType, ReadOnlyDictionary<string, string>.Empty);
        return transferUtility.UploadWithResponseAsync(request, cancellationToken);
    }

    public async Task<FileUploadResult> UploadAsync(
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var request = CreateUploadRequest(key, content, contentType, metadata);
        var response = await transferUtility.UploadWithResponseAsync(request, cancellationToken);
        return new FileUploadResult(response.VersionId);
    }

    public Task<Stream> DownloadAsync(string key, CancellationToken cancellationToken)
    {
        return DownloadVersionAsync(key, versionId: null, cancellationToken);
    }

    public Task<Stream> DownloadAsync(string key, string versionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        return DownloadVersionAsync(key, versionId, cancellationToken);
    }

    public Task<FileMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken)
    {
        return GetVersionMetadataAsync(key, versionId: null, cancellationToken);
    }

    public Task<FileMetadata?> GetMetadataAsync(string key, string versionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        return GetVersionMetadataAsync(key, versionId, cancellationToken);
    }

    public async IAsyncEnumerable<FileVersion> GetVersionsAsync(
        string key,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var objectKey = BuildObjectKey(key);
        var request = new ListVersionsRequest
        {
            BucketName = _options.BucketName,
            Prefix = objectKey
        };

        bool hasMorePages;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await transferUtility.S3Client.ListVersionsAsync(request, cancellationToken);
            var versions = response.Versions ?? [];
            for (var index = 0; index < versions.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = versions[index];
                if (!string.Equals(version.Key, objectKey, StringComparison.Ordinal) || version.IsDeleteMarker == true)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(version.VersionId))
                {
                    throw new InvalidDataException("S3 returned a version without an identifier.");
                }
                var lastModified = version.LastModified
                    ?? throw new InvalidDataException("S3 returned a version without a modification time.");

                yield return new FileVersion(
                    version.VersionId,
                    new DateTimeOffset(DateTime.SpecifyKind(lastModified, DateTimeKind.Utc)));
            }

            hasMorePages = response.IsTruncated == true;
            if (hasMorePages)
            {
                var nextKeyMarker = response.NextKeyMarker
                    ?? throw new InvalidDataException("S3 did not return a key for the next version page.");
                if ((nextKeyMarker, response.NextVersionIdMarker) == (request.KeyMarker, request.VersionIdMarker))
                {
                    throw new InvalidDataException("S3 version pagination did not advance.");
                }

                request.KeyMarker = nextKeyMarker;
                request.VersionIdMarker = response.NextVersionIdMarker;
            }
        } while (hasMorePages);
    }

    public async Task<FileCopyResult> CopyAsync(
        string sourceKey,
        string sourceVersionId,
        string destinationKey,
        string contentType,
        long size,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersionId);
        ValidateContentType(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size > MaximumCopyPartSizeBytes * MaximumCopyParts)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "The size exceeds S3 multipart copy limits.");
        }
        ArgumentNullException.ThrowIfNull(metadata);
        var sourceObjectKey = BuildObjectKey(sourceKey);
        var destinationObjectKey = BuildObjectKey(destinationKey);
        if (string.Equals(sourceObjectKey, destinationObjectKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The destination key must differ from the source key.", nameof(destinationKey));
        }

        var request = new CopyObjectRequest
        {
            SourceBucket = _options.BucketName,
            SourceKey = sourceObjectKey,
            SourceVersionId = sourceVersionId,
            DestinationBucket = _options.BucketName,
            DestinationKey = destinationObjectKey,
            ContentType = contentType,
            MetadataDirective = S3MetadataDirective.REPLACE
        };
        AddMetadata(request.Metadata, metadata);
        cancellationToken.ThrowIfCancellationRequested();
        if (size > MaximumCopyPartSizeBytes)
        {
            return await CopyMultipartAsync(request, size, metadata, cancellationToken);
        }

        var response = await transferUtility.S3Client.CopyObjectAsync(request, cancellationToken);

        return new FileCopyResult(response.VersionId);
    }

    public Task DeleteAsync(
        string key,
        CancellationToken cancellationToken)
    {
        return transferUtility.S3Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key)
        }, cancellationToken);
    }

    public Task DeleteAsync(string key, string versionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var objectKey = BuildObjectKey(key);
        cancellationToken.ThrowIfCancellationRequested();

        return transferUtility.S3Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            VersionId = versionId
        }, cancellationToken);
    }

    public async Task<PresignedUploadUrl> GetPresignedUploadUrlAsync(
        string key,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        ValidateContentType(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        cancellationToken.ThrowIfCancellationRequested();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key),
            Verb = HttpVerb.PUT,
            Protocol = GetPresignedUrlProtocol(),
            ContentType = contentType
        };
        request.Headers.ContentLength = size;
        var expiresAt = GetPresignedUrlExpiration(request);
        request.Expires = expiresAt.UtcDateTime;

        var url = await transferUtility.S3Client.GetPreSignedURLAsync(request).WaitAsync(cancellationToken);
        var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            [HeaderKeys.ContentTypeHeader] = contentType
        });

        return new PresignedUploadUrl(url, expiresAt, headers);
    }

    public Task<PresignedDownloadUrl> GetPresignedDownloadUrlAsync(
        string key,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        return CreateDownloadUrlAsync(key, versionId: null, fileName, contentType, cancellationToken);
    }

    public Task<PresignedDownloadUrl> GetPresignedDownloadUrlAsync(
        string key,
        string versionId,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        return CreateDownloadUrlAsync(key, versionId, fileName, contentType, cancellationToken);
    }

    private static void AddMetadata(
        MetadataCollection destination,
        IReadOnlyDictionary<string, string> metadata)
    {
        using var headers = new HttpRequestMessage();
        foreach (var entry in metadata)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);
            ArgumentNullException.ThrowIfNull(entry.Value);
            if (entry.Key.StartsWith(MetadataHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Metadata names must omit the x-amz-meta- prefix.", nameof(metadata));
            }
            if (destination[entry.Key] is not null)
            {
                throw new ArgumentException("Metadata names must be unique ignoring case.", nameof(metadata));
            }

            headers.Headers.Add(MetadataHeaderPrefix + entry.Key, entry.Value);
            destination.Add(entry.Key, entry.Value);
        }
    }

    private async Task<FileCopyResult> CopyMultipartAsync(
        CopyObjectRequest request,
        long size,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var initiation = new InitiateMultipartUploadRequest
        {
            BucketName = request.DestinationBucket,
            Key = request.DestinationKey,
            ContentType = request.ContentType
        };
        AddMetadata(initiation.Metadata, metadata);
        var initiated = await transferUtility.S3Client.InitiateMultipartUploadAsync(initiation, cancellationToken);
        if (string.IsNullOrWhiteSpace(initiated.UploadId))
        {
            throw new InvalidDataException("S3 did not return a multipart upload identifier.");
        }

        try
        {
            var parts = await CopyPartsAsync(request, initiated.UploadId, size, cancellationToken);
            var completed = await transferUtility.S3Client.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = request.DestinationBucket,
                    Key = request.DestinationKey,
                    UploadId = initiated.UploadId,
                    PartETags = parts
                }, cancellationToken);

            return new FileCopyResult(completed.VersionId);
        }
        catch (Exception copyException)
        {
            using (var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    await transferUtility.S3Client.AbortMultipartUploadAsync(
                        new AbortMultipartUploadRequest
                        {
                            BucketName = request.DestinationBucket,
                            Key = request.DestinationKey,
                            UploadId = initiated.UploadId
                        }, cleanupCancellation.Token);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Multipart copying and aborting its upload both failed.", copyException, cleanupException);
                }
            }

            return await Task.FromException<FileCopyResult>(copyException);
        }
    }

    private async Task<List<PartETag>> CopyPartsAsync(
        CopyObjectRequest request,
        string uploadId,
        long size,
        CancellationToken cancellationToken)
    {
        var partSize = Math.Max(MinimumCopyPartSizeBytes, ((size - 1) / MaximumCopyParts) + 1);
        var parts = new List<PartETag>();
        for (long offset = 0; offset < size; offset += partSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partNumber = parts.Count + 1;
            var copied = await transferUtility.S3Client.CopyPartAsync(
                new CopyPartRequest
                {
                    SourceBucket = request.SourceBucket,
                    SourceKey = request.SourceKey,
                    SourceVersionId = request.SourceVersionId,
                    DestinationBucket = request.DestinationBucket,
                    DestinationKey = request.DestinationKey,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    FirstByte = offset,
                    LastByte = Math.Min(offset + partSize, size) - 1
                }, cancellationToken);
            if (string.IsNullOrWhiteSpace(copied.ETag))
            {
                throw new InvalidDataException("S3 did not return a copied part ETag.");
            }

            parts.Add(new PartETag(partNumber, copied.ETag));
        }

        return parts;
    }

    private TransferUtilityUploadRequest CreateUploadRequest(
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata)
    {
        ValidateContentType(contentType);
        if (content is null || !content.CanRead)
        {
            throw new ArgumentException("A readable content stream is required.", nameof(content));
        }
        ArgumentNullException.ThrowIfNull(metadata);

        var request = new TransferUtilityUploadRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key),
            InputStream = new NonDisposingWrapperStream(content),
            ContentType = contentType,
            AutoCloseStream = false,
            AutoResetStreamPosition = false
        };
        AddMetadata(request.Metadata, metadata);

        return request;
    }

    private async Task<Stream> DownloadVersionAsync(
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await OpenDownloadStreamAsync(key, versionId, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode != NoSuchBucketErrorCode)
        {
            throw new FileNotFoundException("The file does not exist in storage.", key, exception);
        }
    }

    private async Task<FileMetadata?> GetVersionMetadataAsync(
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var request = new GetObjectMetadataRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key),
            VersionId = versionId
        };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await transferUtility.S3Client.GetObjectMetadataAsync(request, cancellationToken);
            IReadOnlyDictionary<string, string> metadata = response.Metadata.Count == 0
                ? ReadOnlyDictionary<string, string>.Empty
                : response.Metadata.Keys.ToDictionary(
                    name => name[MetadataHeaderPrefix.Length..],
                    name => response.Metadata[name],
                    StringComparer.OrdinalIgnoreCase);

            return new FileMetadata(
                response.Headers.ContentType, response.ContentLength, metadata, response.VersionId);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<PresignedDownloadUrl> CreateDownloadUrlAsync(
        string key,
        string? versionId,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ValidateContentType(contentType);
        cancellationToken.ThrowIfCancellationRequested();
        var disposition = new ContentDispositionHeaderValue(DispositionTypeNames.Attachment)
        {
            FileNameStar = fileName
        };
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key),
            VersionId = versionId,
            Verb = HttpVerb.GET,
            Protocol = GetPresignedUrlProtocol(),
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = contentType,
                ContentDisposition = disposition.ToString()
            }
        };
        var expiresAt = GetPresignedUrlExpiration(request);
        request.Expires = expiresAt.UtcDateTime;

        var url = await transferUtility.S3Client.GetPreSignedURLAsync(request).WaitAsync(cancellationToken);

        return new PresignedDownloadUrl(url, expiresAt);
    }

    private async Task<Stream> OpenDownloadStreamAsync(
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await transferUtility.OpenStreamWithResponseAsync(new TransferUtilityOpenStreamRequest
            {
                BucketName = _options.BucketName,
                Key = BuildObjectKey(key),
                VersionId = versionId,
                MultipartDownloadType = MultipartDownloadType.RANGE,
                PartSize = _options.DownloadPartSizeBytes,
                MaxInMemoryParts = _options.MaxInMemoryDownloadParts
            }, cancellationToken);

            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var response = await transferUtility.S3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _options.BucketName,
                Key = BuildObjectKey(key),
                VersionId = versionId
            }, cancellationToken);

            return response.ResponseStream;
        }
    }

    private string BuildObjectKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.StartsWith('/') || key.Contains('\\') || key.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException(
                "Keys must be relative and must not contain backslashes or dot segments.", nameof(key));
        }

        return _options.KeyPrefix.Length == 0 ? key : $"{_options.KeyPrefix}/{key}";
    }

    private static void ValidateContentType(string contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out _))
        {
            throw new ArgumentException("A valid content type is required.", nameof(contentType));
        }
    }

    private DateTimeOffset GetPresignedUrlExpiration(GetPreSignedUrlRequest request)
    {
        var endpoint = transferUtility.S3Client.Config.DetermineServiceOperationEndpoint(
            new ServiceOperationEndpointParameters(request));
        return CorrectClockSkew.GetCorrectedUtcNowForEndpoint(endpoint.URL).Add(_options.PresignedUrlLifetime);
    }

    private Protocol GetPresignedUrlProtocol()
    {
        if (Uri.TryCreate(transferUtility.S3Client.Config.ServiceURL, UriKind.Absolute, out var endpoint))
        {
            return endpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
        }

        return transferUtility.S3Client.Config.UseHttp ? Protocol.HTTP : Protocol.HTTPS;
    }
}
