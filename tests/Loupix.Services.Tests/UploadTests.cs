using System.Net;
using System.Net.Http;
using System.Text;
using Loupix.Services.Upload;

namespace Loupix.Services.Tests;

public class AwsSigV4Tests
{
    /// <summary>aws-sig-v4-test-suite "get-vanilla".</summary>
    [Fact]
    public void Test_suite_get_vanilla()
    {
        var time = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);
        var headers = new Dictionary<string, string>
        {
            ["Host"] = "example.amazonaws.com",
            ["X-Amz-Date"] = "20150830T123600Z",
        };

        var (canonicalHeaders, signedHeaders) = AwsSigV4.CanonicalizeHeaders(headers);
        string canonicalRequest = AwsSigV4.CanonicalRequest("GET", "/", "", canonicalHeaders, signedHeaders, AwsSigV4.Sha256Hex(""));
        Assert.Equal(
            "GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            canonicalRequest);

        string scope = AwsSigV4.CredentialScope("20150830", "us-east-1", "service");
        string stringToSign = AwsSigV4.StringToSign(AwsSigV4.FormatAmzDate(time), scope, canonicalRequest);
        Assert.Equal(
            "AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n" +
            "bb579772317eb040ac9ed261061d46c1f17a8133879d6129b6e1c25292927e63",
            stringToSign);

        byte[] key = AwsSigV4.SigningKey("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20150830", "us-east-1", "service");
        string signature = AwsSigV4.Signature(key, stringToSign);
        Assert.Equal("5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", signature);

        const string expectedAuth =
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, " +
            "SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31";
        Assert.Equal(expectedAuth, AwsSigV4.AuthorizationHeader("AKIDEXAMPLE", scope, signedHeaders, signature));
        Assert.Equal(expectedAuth, AwsSigV4.Sign("GET", "/", "", headers, AwsSigV4.EmptyPayloadHash,
            "AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "us-east-1", "service", time));
    }

    /// <summary>S3 docs, "Signature Calculations for the Authorization Header", Example: GET Object.</summary>
    [Fact]
    public void S3_documentation_get_object()
    {
        var headers = new Dictionary<string, string>
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["Range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = AwsSigV4.EmptyPayloadHash,
            ["x-amz-date"] = "20130524T000000Z",
        };
        var (canonicalHeaders, signedHeaders) = AwsSigV4.CanonicalizeHeaders(headers);
        string canonicalRequest = AwsSigV4.CanonicalRequest("GET", AwsSigV4.CanonicalS3Path("/test.txt"), "",
            canonicalHeaders, signedHeaders, AwsSigV4.EmptyPayloadHash);
        Assert.Equal(
            "GET\n/test.txt\n\nhost:examplebucket.s3.amazonaws.com\nrange:bytes=0-9\n" +
            "x-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n" +
            "x-amz-date:20130524T000000Z\n\nhost;range;x-amz-content-sha256;x-amz-date\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            canonicalRequest);

        string scope = AwsSigV4.CredentialScope("20130524", "us-east-1", "s3");
        string stringToSign = AwsSigV4.StringToSign("20130524T000000Z", scope, canonicalRequest);
        Assert.Equal(
            "AWS4-HMAC-SHA256\n20130524T000000Z\n20130524/us-east-1/s3/aws4_request\n" +
            "7344ae5b7ee6c3e7e6b0fe0640412a37625d1fbfff95c48bbb2dc43964946972",
            stringToSign);

        string signature = AwsSigV4.Signature(
            AwsSigV4.SigningKey("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "20130524", "us-east-1", "s3"), stringToSign);
        Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", signature);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            AwsSigV4.Sign("GET", "/test.txt", "", headers, AwsSigV4.EmptyPayloadHash, "AKIAIOSFODNN7EXAMPLE",
                "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3",
                new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>S3 docs, Example: PUT Object (key "test$file.text", so it also checks once-per-segment path encoding).</summary>
    [Fact]
    public void S3_documentation_put_object()
    {
        byte[] body = Encoding.UTF8.GetBytes("Welcome to Amazon S3.");
        string payloadHash = AwsSigV4.Sha256Hex(body);
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payloadHash);

        var headers = new Dictionary<string, string>
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["Date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["x-amz-date"] = "20130524T000000Z",
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
            ["x-amz-content-sha256"] = payloadHash,
        };
        string canonicalUri = AwsSigV4.CanonicalS3Path("/test$file.text");
        Assert.Equal("/test%24file.text", canonicalUri);

        var (canonicalHeaders, signedHeaders) = AwsSigV4.CanonicalizeHeaders(headers);
        Assert.Equal("date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class", signedHeaders);
        string canonicalRequest = AwsSigV4.CanonicalRequest("PUT", canonicalUri, "", canonicalHeaders, signedHeaders, payloadHash);
        string scope = AwsSigV4.CredentialScope("20130524", "us-east-1", "s3");
        string stringToSign = AwsSigV4.StringToSign("20130524T000000Z", scope, canonicalRequest);
        Assert.EndsWith("9e0e90d9c76de8fa5b200d8c849cd5b8dc7a3be3951ddb7f6a76b4158342019d", stringToSign);

        string signature = AwsSigV4.Signature(
            AwsSigV4.SigningKey("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "20130524", "us-east-1", "s3"), stringToSign);
        Assert.Equal("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", signature);
    }

    /// <summary>IAM docs, "Examples of how to derive a signing key for Signature Version 4".</summary>
    [Fact]
    public void Signing_key_derivation_example()
    {
        byte[] key = AwsSigV4.SigningKey("wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", "20120215", "us-east-1", "iam");
        Assert.Equal("f4780e2d9f65fa895f9c67b32ce1baf0b0d8a43505a000a1a9e090d414db404d", Convert.ToHexStringLower(key));
    }

    [Theory]
    [InlineData("AZaz09-_.~", "AZaz09-_.~")]
    [InlineData("a b", "a%20b")]
    [InlineData("a+b=c&d", "a%2Bb%3Dc%26d")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("é", "%C3%A9")]
    [InlineData("😀", "%F0%9F%98%80")]
    [InlineData("*", "%2A")]
    public void Uri_encoding_is_rfc3986(string input, string expected)
    {
        Assert.Equal(expected, AwsSigV4.UriEncode(input));
    }

    [Fact]
    public void S3_path_encodes_each_segment_once()
    {
        Assert.Equal("/shots/2026%2011/a%2Bb%20%281%29.png", AwsSigV4.CanonicalS3Path("shots/2026 11/a+b (1).png"));
        // Already-encoded text is encoded again rather than passed through: S3 keys are literal.
        Assert.Equal("/a%2520b", AwsSigV4.CanonicalS3Path("/a%20b"));
    }

    [Fact]
    public void Query_string_is_sorted_and_encoded()
    {
        var q = new[] { KeyValuePair.Create("prefix", "a b"), KeyValuePair.Create("list-type", "2"), KeyValuePair.Create("acl", "") };
        Assert.Equal("acl=&list-type=2&prefix=a%20b", AwsSigV4.CanonicalQueryString(q));
    }

    [Fact]
    public void Header_values_are_trimmed_and_spaces_collapsed()
    {
        var (canonical, signed) = AwsSigV4.CanonicalizeHeaders(
        [
            KeyValuePair.Create("My-Header1", "    a   b   c  "),
            KeyValuePair.Create("Host", "example.amazonaws.com"),
            KeyValuePair.Create("my-header1", "d"),
        ]);
        Assert.Equal("host:example.amazonaws.com\nmy-header1:a b c,d\n", canonical);
        Assert.Equal("host;my-header1", signed);
    }
}

public class S3UploaderTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 5, 12, 34, 56, TimeSpan.Zero);

    private static S3Config Config(bool pathStyle = true, string? publicBase = null, string? acl = null, string endpoint = "https://s3.us-east-1.amazonaws.com") =>
        new(endpoint, "us-east-1", "my-bucket", "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            PathStyle: pathStyle, PublicUrlBase: publicBase, KeyPrefix: "shots/", Acl: acl);

    [Fact]
    public async Task Puts_signed_object_and_returns_path_style_url()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var uploader = new S3Uploader(new HttpClient(handler), new FixedTime(FixedNow));
        byte[] content = Encoding.UTF8.GetBytes("png bytes");

        Uri url = await uploader.UploadAsync(Config(acl: "public-read"), "Screen Shot 1.png", content, "image/png");

        Assert.Equal("https://s3.us-east-1.amazonaws.com/my-bucket/shots/Screen%20Shot%201.png", url.AbsoluteUri);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal(url.AbsoluteUri, req.Uri.AbsoluteUri);
        Assert.Equal("/my-bucket/shots/Screen%20Shot%201.png", req.Uri.AbsolutePath);
        Assert.Equal(content, req.Body);
        Assert.Equal("image/png", req.ContentType);
        Assert.Equal("s3.us-east-1.amazonaws.com", req.Headers["Host"]);
        Assert.Equal("20261105T123456Z", req.Headers["x-amz-date"]);
        Assert.Equal(AwsSigV4.Sha256Hex(content), req.Headers["x-amz-content-sha256"]);
        Assert.Equal("public-read", req.Headers["x-amz-acl"]);

        // Recompute the signature independently from what was actually sent.
        string expected = AwsSigV4.Sign("PUT", req.Uri.AbsolutePath, "",
        [
            KeyValuePair.Create("host", req.Headers["Host"]),
            KeyValuePair.Create("x-amz-acl", req.Headers["x-amz-acl"]),
            KeyValuePair.Create("x-amz-content-sha256", AwsSigV4.Sha256Hex(req.Body)),
            KeyValuePair.Create("x-amz-date", req.Headers["x-amz-date"]),
        ], AwsSigV4.Sha256Hex(req.Body), "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", FixedNow);
        Assert.Equal(expected, req.Headers["Authorization"]);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20261105/us-east-1/s3/aws4_request, " +
                          "SignedHeaders=host;x-amz-acl;x-amz-content-sha256;x-amz-date, Signature=", expected);
    }

    [Fact]
    public async Task Virtual_hosted_style_and_no_acl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var uploader = new S3Uploader(new HttpClient(handler), new FixedTime(FixedNow));

        Uri url = await uploader.UploadAsync(Config(pathStyle: false), "a$b.png", [1, 2, 3], "image/png");

        Assert.Equal("https://my-bucket.s3.us-east-1.amazonaws.com/shots/a%24b.png", url.AbsoluteUri);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("my-bucket.s3.us-east-1.amazonaws.com", req.Headers["Host"]);
        Assert.False(req.Headers.ContainsKey("x-amz-acl"));
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date,", req.Headers["Authorization"]);
    }

    [Fact]
    public async Task Minio_endpoint_with_port_and_public_base()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var uploader = new S3Uploader(new HttpClient(handler), new FixedTime(FixedNow));

        Uri url = await uploader.UploadAsync(Config(publicBase: "https://cdn.example.com/", endpoint: "http://localhost:9000/"),
            "x y.png", [1], "image/png");

        Assert.Equal("https://cdn.example.com/shots/x%20y.png", url.AbsoluteUri);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:9000/my-bucket/shots/x%20y.png", req.Uri.AbsoluteUri);
        Assert.Equal("localhost:9000", req.Headers["Host"]);
    }

    [Fact]
    public async Task Error_response_throws_with_s3_code_and_message()
    {
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Error><Code>SignatureDoesNotMatch</Code><Message>The request signature we calculated does not match the signature you provided.</Message><RequestId>4442587FB7D0A2F9</RequestId></Error>
            """;
        var handler = new FakeHandler(HttpStatusCode.Forbidden, body);
        var uploader = new S3Uploader(new HttpClient(handler), new FixedTime(FixedNow));

        var ex = await Assert.ThrowsAsync<S3UploadException>(() => uploader.UploadAsync(Config(), "a.png", [1], "image/png"));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Equal("SignatureDoesNotMatch", ex.ErrorCode);
        Assert.Equal("The request signature we calculated does not match the signature you provided.", ex.ErrorMessage);
        Assert.Contains("403", ex.Message);
        Assert.Contains("SignatureDoesNotMatch", ex.Message);
    }

    [Fact]
    public async Task Non_xml_error_body_still_throws()
    {
        var handler = new FakeHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html");
        var uploader = new S3Uploader(new HttpClient(handler), new FixedTime(FixedNow));

        var ex = await Assert.ThrowsAsync<S3UploadException>(() => uploader.UploadAsync(Config(), "a.png", [1], "image/png"));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Null(ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../a.png")]
    [InlineData("a/./b.png")]
    public async Task Rejects_keys_that_cannot_be_signed_faithfully(string key)
    {
        var uploader = new S3Uploader(new HttpClient(new FakeHandler(HttpStatusCode.OK, "")));
        var cfg = Config() with { KeyPrefix = "" };
        await Assert.ThrowsAsync<ArgumentException>(() => uploader.UploadAsync(cfg, key, [1], "image/png"));
    }

    [Fact]
    public void Config_ToString_hides_secret()
    {
        Assert.DoesNotContain("wJalrXUtnFEMI", Config().ToString());
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, byte[] Body, string? ContentType);

    private sealed class FakeHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, headers, body, request.Content?.Headers.ContentType?.ToString()));
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/xml") };
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
