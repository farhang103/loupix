using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Loupix.Services.Upload;

/// <summary>
/// AWS Signature Version 4 (header-based), split into the steps the AWS documentation describes so each one
/// can be checked against the published examples. All hashes are lower-case hex.
/// </summary>
public static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>SHA-256 of an empty payload.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary><c>20130524T000000Z</c>, the <c>x-amz-date</c> format.</summary>
    public static string FormatAmzDate(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>20130524</c>, the date used in the credential scope.</summary>
    public static string FormatDateStamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// RFC 3986 percent-encoding as SigV4 requires: unreserved characters (A–Z a–z 0–9 - _ . ~) stay as they are,
    /// everything else becomes %XX of its UTF-8 bytes with upper-case hex. '/' is kept when <paramref name="encodeSlash"/> is false.
    /// </summary>
    public static string UriEncode(string value, bool encodeSlash = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length * 3 / 2);
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (IsUnreserved(ch) || (ch == '/' && !encodeSlash))
            {
                sb.Append(ch);
                continue;
            }
            int len;
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                len = Encoding.UTF8.GetBytes(value.AsSpan(i, 2), buf);
                i++;
            }
            else
            {
                len = Encoding.UTF8.GetBytes(value.AsSpan(i, 1), buf);
            }
            for (int k = 0; k < len; k++) sb.Append('%').Append(buf[k].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static bool IsUnreserved(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~';

    /// <summary>
    /// Canonical URI for S3: each '/'-separated segment of the (unencoded) path encoded exactly once,
    /// e.g. <c>/test$file.text</c> → <c>/test%24file.text</c>. S3 does not normalise or double-encode paths.
    /// </summary>
    public static string CanonicalS3Path(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0) return "/";
        var encoded = string.Join('/', path.Split('/').Select(seg => UriEncode(seg)));
        return encoded.StartsWith('/') ? encoded : "/" + encoded;
    }

    /// <summary>Query parameters encoded and sorted by encoded name, then encoded value. Empty input gives "".</summary>
    public static string CanonicalQueryString(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return string.Join('&', parameters
            .Select(p => (Key: UriEncode(p.Key), Value: UriEncode(p.Value ?? "")))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value));
    }

    /// <summary>
    /// Canonical headers ("name:value\n" per header: names lower-cased and sorted, values trimmed with inner runs
    /// of spaces collapsed, repeated names joined with ',') and the matching signed-headers list ("a;b;c").
    /// </summary>
    public static (string CanonicalHeaders, string SignedHeaders) CanonicalizeHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var grouped = headers
            .GroupBy(h => h.Key.Trim().ToLowerInvariant())
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Value: string.Join(',', g.Select(h => NormalizeHeaderValue(h.Value)))))
            .ToList();

        var canonical = new StringBuilder();
        foreach (var (name, value) in grouped) canonical.Append(name).Append(':').Append(value).Append('\n');
        return (canonical.ToString(), string.Join(';', grouped.Select(g => g.Name)));
    }

    private static string NormalizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        bool space = false;
        foreach (char c in value.Trim())
        {
            if (c == ' ')
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// <c>Method\nCanonicalURI\nCanonicalQuery\nCanonicalHeaders\nSignedHeaders\nPayloadHash</c>.
    /// <paramref name="canonicalHeaders"/> already ends in '\n', which yields the blank line AWS expects.
    /// </summary>
    public static string CanonicalRequest(string method, string canonicalUri, string canonicalQueryString,
        string canonicalHeaders, string signedHeaders, string payloadHash) =>
        $"{method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

    /// <summary><c>20130524/us-east-1/s3/aws4_request</c></summary>
    public static string CredentialScope(string dateStamp, string region, string service) =>
        $"{dateStamp}/{region}/{service}/aws4_request";

    /// <summary><c>AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{hex(sha256(canonicalRequest))}</c></summary>
    public static string StringToSign(string amzDate, string credentialScope, string canonicalRequest) =>
        $"{Algorithm}\n{amzDate}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

    /// <summary>HMAC chain: "AWS4"+secret → date → region → service → "aws4_request".</summary>
    public static byte[] SigningKey(string secretAccessKey, string dateStamp, string region, string service)
    {
        byte[] kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        byte[] kRegion = Hmac(kDate, region);
        byte[] kService = Hmac(kRegion, service);
        return Hmac(kService, "aws4_request");
    }

    /// <summary>hex(HMAC-SHA256(signingKey, stringToSign))</summary>
    public static string Signature(byte[] signingKey, string stringToSign) =>
        Convert.ToHexStringLower(Hmac(signingKey, stringToSign));

    /// <summary>The <c>Authorization</c> header value.</summary>
    public static string AuthorizationHeader(string accessKeyId, string credentialScope, string signedHeaders, string signature) =>
        $"{Algorithm} Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

    /// <summary>
    /// All steps at once. <paramref name="headers"/> must include every header to sign, including
    /// <c>host</c> and <c>x-amz-date</c> (which must equal <see cref="FormatAmzDate"/> of <paramref name="timestamp"/>).
    /// Returns the <c>Authorization</c> header value.
    /// </summary>
    public static string Sign(string method, string canonicalUri, string canonicalQueryString,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash,
        string accessKeyId, string secretAccessKey, string region, string service, DateTimeOffset timestamp)
    {
        var (canonicalHeaders, signedHeaders) = CanonicalizeHeaders(headers);
        string request = CanonicalRequest(method, canonicalUri, canonicalQueryString, canonicalHeaders, signedHeaders, payloadHash);
        string dateStamp = FormatDateStamp(timestamp);
        string scope = CredentialScope(dateStamp, region, service);
        string toSign = StringToSign(FormatAmzDate(timestamp), scope, request);
        string signature = Signature(SigningKey(secretAccessKey, dateStamp, region, service), toSign);
        return AuthorizationHeader(accessKeyId, scope, signedHeaders, signature);
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
