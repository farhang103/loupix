namespace Loupix.Services.Upload;

/// <summary>
/// Settings for an S3-compatible bucket.
/// </summary>
/// <param name="Endpoint">Service endpoint without the bucket, e.g. <c>https://s3.us-east-1.amazonaws.com</c>,
/// <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c> or a MinIO URL such as <c>http://localhost:9000</c>.</param>
/// <param name="Region">Signing region, e.g. <c>us-east-1</c>; Cloudflare R2 uses <c>auto</c>.</param>
/// <param name="PathStyle">true: <c>https://endpoint/bucket/key</c>; false (virtual-hosted): <c>https://bucket.endpoint/key</c>.</param>
/// <param name="PublicUrlBase">If set, the returned URL is this base + "/" + key (e.g. a CDN or r2.dev domain).</param>
/// <param name="KeyPrefix">Prepended verbatim to every key; include a trailing '/' to use it as a folder.</param>
/// <param name="Acl">Sent as <c>x-amz-acl</c> when set, e.g. <c>public-read</c>. Leave null for R2 and buckets with ACLs disabled.</param>
public sealed record S3Config(
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKeyId,
    string SecretAccessKey,
    bool PathStyle = true,
    string? PublicUrlBase = null,
    string KeyPrefix = "",
    string? Acl = null)
{
    /// <summary>Keeps the secret out of logs and debugger displays.</summary>
    public override string ToString() =>
        $"S3Config {{ Endpoint = {Endpoint}, Region = {Region}, Bucket = {Bucket}, AccessKeyId = {AccessKeyId}, PathStyle = {PathStyle}, PublicUrlBase = {PublicUrlBase}, KeyPrefix = {KeyPrefix}, Acl = {Acl} }}";
}
