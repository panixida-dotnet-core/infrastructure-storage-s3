using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
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
    IOptions<S3StorageOptions> options,
    TimeProvider timeProvider) : IFileStorage
{
    private readonly S3StorageOptions _options = options.Value;

    public Task UploadAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ValidateContentType(contentType);
        if (content is null || !content.CanRead)
        {
            throw new ArgumentException("A readable content stream is required.", nameof(content));
        }

        return transferUtility.UploadWithResponseAsync(new TransferUtilityUploadRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key),
            InputStream = new NonDisposingWrapperStream(content),
            ContentType = contentType,
            AutoCloseStream = false,
            AutoResetStreamPosition = false
        }, cancellationToken);
    }

    public async Task<Stream> DownloadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await transferUtility.S3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _options.BucketName,
                Key = BuildObjectKey(key)
            }, cancellationToken);

            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode != "NoSuchBucket")
        {
            throw new FileNotFoundException("The file does not exist in storage.", key, exception);
        }
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

    public async Task<PresignedUploadUrl> GetPresignedUploadUrlAsync(
        string key,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        ValidateContentType(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = timeProvider.GetUtcNow().Add(_options.PresignedUrlLifetime);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key),
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            Protocol = GetPresignedUrlProtocol(),
            ContentType = contentType
        };
        request.Headers.ContentLength = size;

        var url = await transferUtility.S3Client.GetPreSignedURLAsync(request).WaitAsync(cancellationToken);
        var headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            [HeaderKeys.ContentTypeHeader] = contentType
        });

        return new PresignedUploadUrl(url, expiresAt, headers);
    }

    public async Task<PresignedDownloadUrl> GetPresignedDownloadUrlAsync(
        string key,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ValidateContentType(contentType);
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = timeProvider.GetUtcNow().Add(_options.PresignedUrlLifetime);
        var disposition = new ContentDispositionHeaderValue(DispositionTypeNames.Attachment) { FileNameStar = fileName };
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.BucketName,
            Key = BuildObjectKey(key),
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
            Protocol = GetPresignedUrlProtocol(),
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = contentType,
                ContentDisposition = disposition.ToString()
            }
        };

        var url = await transferUtility.S3Client.GetPreSignedURLAsync(request).WaitAsync(cancellationToken);

        return new PresignedDownloadUrl(url, expiresAt);
    }

    private string BuildObjectKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.StartsWith('/') || key.Contains('\\') || key.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Keys must be relative and must not contain backslashes or dot segments.", nameof(key));
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

    private Protocol GetPresignedUrlProtocol()
    {
        if (Uri.TryCreate(transferUtility.S3Client.Config.ServiceURL, UriKind.Absolute, out var endpoint))
        {
            return endpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
        }

        return transferUtility.S3Client.Config.UseHttp ? Protocol.HTTP : Protocol.HTTPS;
    }
}
