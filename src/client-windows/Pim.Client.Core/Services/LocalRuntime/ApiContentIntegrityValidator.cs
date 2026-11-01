using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Integrity binding for an ASR segment response. SourceSha256 identifies the whole
/// source recording from the claim snapshot; it must never be compared with the
/// digest of the downloaded segment bytes. ContentSha256 is the digest of the exact
/// response body bytes and is the only value compared with the local byte hash.
/// </summary>
public sealed record ApiContentIdentityBinding(
    string AttemptId,
    string VersionId,
    string LeaseId,
    long FencingToken,
    int SegmentIndex,
    string SourceSha256,
    long SourceOffsetSamples,
    long Samples);

public sealed record ApiContentResponseBinding(
    string AttemptId,
    string VersionId,
    string LeaseId,
    long FencingToken,
    int SegmentIndex,
    string SourceSha256,
    long SourceOffsetSamples,
    long Samples,
    string ContentSha256);

public static class ApiContentIntegrityValidator
{
    public static IReadOnlyList<string> ValidateBinding(
        ApiContentIdentityBinding expected,
        ApiContentResponseBinding response)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(response);
        var errors = new List<string>();
        if (!HasValidBindingValues(
                expected.AttemptId,
                expected.VersionId,
                expected.LeaseId,
                expected.FencingToken,
                expected.SegmentIndex,
                expected.SourceSha256,
                expected.SourceOffsetSamples,
                expected.Samples)
            || !HasValidBindingValues(
                response.AttemptId,
                response.VersionId,
                response.LeaseId,
                response.FencingToken,
                response.SegmentIndex,
                response.SourceSha256,
                response.SourceOffsetSamples,
                response.Samples)
            || !string.Equals(expected.AttemptId, response.AttemptId, StringComparison.Ordinal)
            || !string.Equals(expected.VersionId, response.VersionId, StringComparison.Ordinal)
            || !string.Equals(expected.LeaseId, response.LeaseId, StringComparison.Ordinal)
            || expected.FencingToken != response.FencingToken
            || expected.SegmentIndex != response.SegmentIndex
            || expected.SourceOffsetSamples != response.SourceOffsetSamples
            || expected.Samples != response.Samples)
        {
            errors.Add(LocalRuntimeReasonCodes.ContentBindingMismatch);
        }

        if (!LocalModelManifest.IsValidSha256(expected.SourceSha256)
            || !LocalModelManifest.IsValidSha256(response.SourceSha256)
            || !string.Equals(
                expected.SourceSha256,
                response.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(LocalRuntimeReasonCodes.ContentSourceHashMismatch);
        }

        if (!LocalModelManifest.IsValidSha256(response.ContentSha256))
        {
            errors.Add(LocalRuntimeReasonCodes.ContentDigestMismatch);
        }

        return errors;
    }

    public static bool MatchesActualContentBytes(
        ApiContentResponseBinding response,
        string actualContentSha256)
    {
        ArgumentNullException.ThrowIfNull(response);
        return LocalModelManifest.IsValidSha256(response.ContentSha256)
               && LocalModelManifest.IsValidSha256(actualContentSha256)
               && string.Equals(
                   response.ContentSha256,
                   actualContentSha256,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidBindingValues(
        string attemptId,
        string versionId,
        string leaseId,
        long fencingToken,
        int segmentIndex,
        string sourceSha256,
        long sourceOffsetSamples,
        long samples) =>
        !string.IsNullOrWhiteSpace(attemptId)
        && !string.IsNullOrWhiteSpace(versionId)
        && !string.IsNullOrWhiteSpace(leaseId)
        && fencingToken > 0
        && segmentIndex >= 0
        && sourceOffsetSamples >= 0
        && samples > 0
        && LocalModelManifest.IsValidSha256(sourceSha256);
}
