using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using Minio;
using Minio.DataModel.Args;

namespace TeslaCamViewer.Services;

/// <summary>
/// Streams a MinIO object with HTTP byte-range support so HTML5 video can seek
/// without buffering the entire file in memory.
/// </summary>
/// <remarks>
/// The MinIO .NET SDK's GetObjectAsync throws <c>PartialContentException</c> on
/// HTTP 206, so ranged reads go through a presigned GET and HttpClient instead.
/// </remarks>
public sealed class MinioRangeStreamResult : IResult
{
    private const int PresignExpirySeconds = 300;
    private readonly IMinioClient _minio;
    private readonly HttpClient _http;
    private readonly string _bucket;
    private readonly string _objectName;
    private readonly long _knownSize;

    public MinioRangeStreamResult(
        IMinioClient minio,
        HttpClient http,
        string bucket,
        string objectName,
        long knownSize)
    {
        _minio = minio;
        _http = http;
        _bucket = bucket;
        _objectName = objectName;
        _knownSize = knownSize;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.ContentType = "video/mp4";
        response.Headers.AcceptRanges = "bytes";
        response.Headers.CacheControl = "public, max-age=3600";

        long totalLength = _knownSize;
        if (totalLength <= 0)
        {
            var stat = await _minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(_bucket)
                .WithObject(_objectName), httpContext.RequestAborted);
            totalLength = stat.Size;
        }

        if (totalLength <= 0)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!TryResolveRange(httpContext.Request, totalLength, out var start, out var end, out var isRange))
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers.ContentRange = $"bytes */{totalLength}";
            return;
        }

        var length = end - start + 1;
        response.ContentLength = length;

        if (isRange)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
        }

        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            return;
        }

        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var presignedUrl = await _minio.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(_bucket)
            .WithObject(_objectName)
            .WithExpiry(PresignExpirySeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, presignedUrl);
        if (isRange)
        {
            request.Headers.Range = new RangeHeaderValue(start, end);
        }

        try
        {
            using var upstream = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                httpContext.RequestAborted);

            if (upstream.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.PartialContent)
            {
                if (!response.HasStarted)
                {
                    response.StatusCode = (int)upstream.StatusCode;
                }

                return;
            }

            await using var stream = await upstream.Content.ReadAsStreamAsync(httpContext.RequestAborted);
            await stream.CopyToAsync(response.Body, 64 * 1024, httpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // Client seeked or closed the connection; this is expected.
        }
        catch (Exception) when (httpContext.Response.HasStarted)
        {
            // Response already committed (e.g. 206); abort rather than rewrite status.
        }
    }

    private static bool TryResolveRange(HttpRequest request, long totalLength, out long start, out long end, out bool isRange)
    {
        start = 0;
        end = totalLength - 1;
        isRange = false;

        var ranges = request.GetTypedHeaders().Range?.Ranges;
        if (ranges is null || ranges.Count == 0)
        {
            return true;
        }

        isRange = true;
        var range = ranges.First();

        if (range.From is null && range.To is not null)
        {
            var suffix = range.To.Value;
            if (suffix <= 0)
            {
                return false;
            }

            start = Math.Max(0, totalLength - suffix);
            end = totalLength - 1;
            return true;
        }

        start = range.From ?? 0;
        end = range.To ?? (totalLength - 1);

        if (start < 0 || start >= totalLength || end < start)
        {
            return false;
        }

        end = Math.Min(end, totalLength - 1);
        return true;
    }
}
