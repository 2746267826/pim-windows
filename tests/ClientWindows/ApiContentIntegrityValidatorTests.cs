using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ApiContentIntegrityValidatorTests
{
    private const string SourceSha =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SegmentSha =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void SegmentBytes_AreComparedWithContentHash_NotWholeSourceHash()
    {
        var expected = new ApiContentIdentityBinding(
            "attempt-1",
            "version-1",
            "lease-1",
            7,
            2,
            SourceSha,
            16000,
            8000);
        var response = new ApiContentResponseBinding(
            "attempt-1",
            "version-1",
            "lease-1",
            7,
            2,
            SourceSha,
            16000,
            8000,
            SegmentSha);

        Assert.Empty(ApiContentIntegrityValidator.ValidateBinding(expected, response));
        Assert.True(ApiContentIntegrityValidator.MatchesActualContentBytes(response, SegmentSha));
        Assert.False(ApiContentIntegrityValidator.MatchesActualContentBytes(response, SourceSha));
    }

    [Fact]
    public void Response_RejectsChangedIdentityOrSourceHash()
    {
        var expected = new ApiContentIdentityBinding(
            "attempt-1",
            "version-1",
            "lease-1",
            7,
            2,
            SourceSha,
            16000,
            8000);
        var response = new ApiContentResponseBinding(
            "attempt-1",
            "version-2",
            "lease-1",
            8,
            3,
            SourceSha[..63] + "c",
            16000,
            8000,
            SegmentSha);

        var errors = ApiContentIntegrityValidator.ValidateBinding(expected, response);

        Assert.Contains(LocalRuntimeReasonCodes.ContentBindingMismatch, errors);
        Assert.Contains(LocalRuntimeReasonCodes.ContentSourceHashMismatch, errors);
    }

    [Fact]
    public void Response_RejectsInvalidIdentityBoundsEvenWhenBothSidesMatch()
    {
        var expected = new ApiContentIdentityBinding(
            "attempt-1",
            "version-1",
            "lease-1",
            0,
            -1,
            SourceSha,
            -1,
            0);
        var response = new ApiContentResponseBinding(
            "attempt-1",
            "version-1",
            "lease-1",
            0,
            -1,
            SourceSha,
            -1,
            0,
            SegmentSha);

        var errors = ApiContentIntegrityValidator.ValidateBinding(expected, response);

        Assert.Contains(LocalRuntimeReasonCodes.ContentBindingMismatch, errors);
    }

    [Fact]
    public void Response_RejectsMissingOrMalformedContentHash()
    {
        var expected = new ApiContentIdentityBinding(
            "attempt-1",
            "version-1",
            "lease-1",
            7,
            2,
            SourceSha,
            16000,
            8000);
        var response = new ApiContentResponseBinding(
            "attempt-1",
            "version-1",
            "lease-1",
            7,
            2,
            SourceSha,
            16000,
            8000,
            "not-a-sha256");

        var errors = ApiContentIntegrityValidator.ValidateBinding(expected, response);

        Assert.Contains(LocalRuntimeReasonCodes.ContentDigestMismatch, errors);
        Assert.False(ApiContentIntegrityValidator.MatchesActualContentBytes(
            response,
            SegmentSha));
    }
}
