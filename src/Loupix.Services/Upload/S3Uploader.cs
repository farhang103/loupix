using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;

namespace Loupix.Services.Upload;

/// <summary>Uploads objects to S3-compatible storage (AWS S3, Cloudflare R2, MinIO, …) with a SigV4-signed PUT.</summary>
public sealed class S3Uploader(HttpClient http, TimeProvider time)
{
    private const string Service = "s3";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    public S3Uploader(HttpClient http) : this(http, TimeProvider.System) { }

    /// <summary>
    /// PUTs <paramref name="content"/> at <c>cfg.KeyPrefix + key</c> and returns its public URL:
    /// <c>PublicUrlBase/key</c> when configured, otherwise the object URL (path or virtual-hosted style).
    /// </summary>
    /// <exception cref="S3UploadException">The service answered with a non-2xx status.</exception>
    public async Task<Uri> UploadAsync(S3Config cfg, string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        string objectKey = BuildObjectKey(cfg, key);
        Uri objectUri = ObjectUri(cfg, objectKey);
        string host = HostHeader(objectUri);
        DateTimeOffset now = _time.GetUtcNow();
        string amzDate = AwsSigV4.FormatAmzDate(now);
        string payloadHash = AwsSigV4.Sha256Hex(content);

        var signed = new List<KeyValuePair<string, string>>
        {
            new("host", host),
            new("x-amz-content-sha256", payloadHash),
            new("x-amz-date", amzDate),
        };
        if (!string.IsNullOrEmpty(cfg.Acl)) signed.Add(new("x-amz-acl", cfg.Acl));

        string authorization = AwsSigV4.Sign("PUT", objectUri.AbsolutePath, "", signed, payloadHash,
            cfg.AccessKeyId, cfg.SecretAccessKey, cfg.Region, Service, now);

        using var request = new HttpRequestMessage(HttpMethod.Put, objectUri);
        request.Headers.Host = host;
        foreach (var (name, value) in signed)
        {
            if (name != "host") request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (code, message) = ParseError(body);
            throw new S3UploadException(response.StatusCode, code, message, body);
        }

        return string.IsNullOrWhiteSpace(cfg.PublicUrlBase)
            ? objectUri
            : new Uri(cfg.PublicUrlBase.TrimEnd('/') + AwsSigV4.CanonicalS3Path(objectKey));
    }

    internal static string BuildObjectKey(S3Config cfg, string key)
    {
        string full = ((cfg.KeyPrefix ?? "") + key).TrimStart('/');
        if (full.Length == 0) throw new ArgumentException("The object key is empty.", nameof(key));
        // System.Uri collapses "." and ".." segments, which would make the signed path differ from the sent one.
        if (full.Split('/').Any(seg => seg is "." or ".."))
            throw new ArgumentException($"The object key '{full}' contains a '.' or '..' path segment.", nameof(key));
        return full;
    }

    /// <summary>The object's URL; the path is encoded once per segment so it is also the SigV4 canonical URI.</summary>
    internal static Uri ObjectUri(S3Config cfg, string objectKey)
    {
        var endpoint = new Uri(cfg.Endpoint.Trim().TrimEnd('/'), UriKind.Absolute);
        string basePath = endpoint.AbsolutePath.TrimEnd('/'); // usually empty; non-empty behind a path-prefix proxy
        string encodedKey = AwsSigV4.CanonicalS3Path(objectKey);
        var builder = new UriBuilder(endpoint.Scheme, endpoint.Host, endpoint.Port);
        if (cfg.PathStyle)
        {
            return new Uri(builder.Uri.GetLeftPart(UriPartial.Authority) + basePath + "/" + AwsSigV4.UriEncode(cfg.Bucket) + encodedKey);
        }
        builder.Host = cfg.Bucket + "." + endpoint.Host;
        return new Uri(builder.Uri.GetLeftPart(UriPartial.Authority) + basePath + encodedKey);
    }

    /// <summary>The Host header exactly as it is sent and signed: IDN-encoded host, port only when non-default.</summary>
    internal static string HostHeader(Uri uri)
    {
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    internal static (string? Code, string? Message) ParseError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            var root = XDocument.Parse(body).Root;
            if (root is null) return (null, null);
            string? code = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value;
            string? message = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
            return (code, message);
        }
        catch (XmlException)
        {
            return (null, null);
        }
    }
}

/// <summary>An S3-compatible service rejected an upload.</summary>
public sealed class S3UploadException : Exception
{
    public HttpStatusCode StatusCode { get; }

    /// <summary>S3 error code from the XML body, e.g. <c>AccessDenied</c> or <c>NoSuchBucket</c>; null if the body had none.</summary>
    public string? ErrorCode { get; }

    /// <summary>S3 error message from the XML body; null if the body had none.</summary>
    public string? ErrorMessage { get; }

    /// <summary>The raw response body.</summary>
    public string? ResponseBody { get; }

    public S3UploadException(HttpStatusCode statusCode, string? errorCode, string? errorMessage, string? responseBody = null)
        : base(BuildMessage(statusCode, errorCode, errorMessage))
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ResponseBody = responseBody;
    }

    private static string BuildMessage(HttpStatusCode status, string? code, string? message)
    {
        string head = $"S3 upload failed with HTTP {(int)status} ({status})";
        if (code is null && message is null) return head + ".";
        return $"{head}: {code ?? "Error"}{(message is null ? "" : " – " + message)}";
    }
}
