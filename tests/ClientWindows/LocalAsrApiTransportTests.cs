using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class LocalAsrApiTransportTests
{
    private const string ShaA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly LocalAccountScope Scope =
        new("https://api.example.test", Guid.Parse("11111111-1111-1111-1111-111111111111").ToString("D"));
    private static readonly Guid NodeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LeaseId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LeaseToken = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid AttemptId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid VersionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid TaskId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid RecordingId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    [Fact]
    public async Task ApiClient_SevenTypedOperationsUseGeneratedPathsAndRequiredHeaders()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/content", StringComparison.Ordinal)
                ? SegmentResponse(
                    "audio bytes"u8.ToArray(),
                    segmentIndex: 1,
                    sourceFileIndex: 0,
                    sourceOffsetSamples: 100,
                    samples: 200)
                : JsonResponse(path.Contains("/ack", StringComparison.Ordinal)
                    ? """
                      {"code":0,"message":"ok","timestamp":"2026-10-31T00:00:00Z","data":{"duplicate":false,"fencingToken":7,"leaseId":"33333333-3333-3333-3333-333333333333","status":"accepted"}}
                      """
                    : path.Contains("/claim", StringComparison.Ordinal)
                        ? ClaimResponseJson(Array.Empty<object>())
                        : path.Contains("/renew", StringComparison.Ordinal)
                            ? LeaseResponseJson(stopRequested: false)
                            : NodeResponseJson());
        });
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(Scope.ServerOrigin);
        api.SetAccessToken("test-token");

        await api.RegisterNativeAsrNodeAsync(new RegisterNoteAsrNodeRequest
        {
            NodeId = NodeId.ToString("D"),
            NodeIdExternal = "node-a",
            Enabled = true,
            ModelId = "model-a",
            ModelRevision = "revision-a",
            ModelVerified = true,
            Capabilities = ["local-asr"],
        });
        await api.HeartbeatNativeAsrNodeAsync(NodeId.ToString("D"), new NoteAsrNodeHeartbeatRequest
        {
            NodeId = NodeId.ToString("D"),
            ModelId = "model-a",
            ModelRevision = "revision-a",
            ModelVerified = true,
            Capabilities = ["local-asr"],
        });
        await api.WithdrawNativeAsrNodeAsync(NodeId.ToString("D"));
        await api.ClaimNativeAsrLeaseAsync(new ClaimNoteAsrTaskRequest
        {
            NodeId = NodeId.ToString("D"),
            IdempotencyKey = "claim-key",
            LeaseSeconds = 120,
        });
        await api.RenewNativeAsrLeaseAsync(LeaseId.ToString("D"), new NoteAsrLeaseHeartbeatRequest
        {
            LeaseId = LeaseId.ToString("D"),
            LeaseToken = LeaseToken.ToString("D"),
            FencingToken = 7,
        });
        await using (var content = await api.GetNativeAsrSegmentContentAsync(
            LeaseId.ToString("D"),
            1,
            LeaseToken.ToString("D"),
            7,
            1024))
        {
            Assert.NotNull(content);
            using var reader = new StreamReader(content.Content, Encoding.UTF8);
            Assert.Equal("audio bytes", await reader.ReadToEndAsync());
        }
        await api.AckNativeAsrAttemptAsync(AttemptId.ToString("D"), new NoteAsrAckRequest
        {
            LeaseId = LeaseId.ToString("D"),
            LeaseToken = LeaseToken.ToString("D"),
            FencingToken = 7,
            Kind = "segment",
            Result = "Succeeded",
            SegmentIndex = 1,
            ProviderHandle = ShaB,
            ResultText = "hello",
        });

        Assert.Equal(7, handler.Requests.Count);
        Assert.Equal(
            [
                "/api/v1/native-notes/asr-routing/nodes/register",
                $"/api/v1/native-notes/asr-routing/nodes/{NodeId:D}/heartbeat",
                $"/api/v1/native-notes/asr-routing/nodes/{NodeId:D}/withdraw",
                "/api/v1/native-notes/asr-routing/leases/claim",
                $"/api/v1/native-notes/asr-routing/leases/{LeaseId:D}/renew",
                $"/api/v1/native-notes/asr-routing/leases/{LeaseId:D}/segments/1/content",
                $"/api/v1/native-notes/asr-routing/leases/{AttemptId:D}/ack",
            ],
            handler.Requests.Select(request => request.Path).ToArray());
        Assert.Null(handler.Requests[2].Body);
        Assert.Equal(LeaseToken.ToString("D"), handler.Requests[5].Headers["X-Pim-Lease-Token"]);
        Assert.Equal("7", handler.Requests[5].Headers["X-Pim-Fencing-Token"]);
        Assert.Contains("\"providerHandle\"", handler.Requests[6].Body!);
        Assert.All(handler.Requests, request =>
            Assert.Equal("Bearer test-token", request.Authorization));
    }

    [Fact]
    public async Task ApiClient_SetBaseUrl_PreservesAuthorizationOnlyForSameCanonicalApiRoot()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """{"code":0,"message":"ok","timestamp":"2026-10-31T00:00:00Z","data":[]}"""));
        var api = new ApiClient(() => handler);

        api.SetBaseUrl("https://api.example.test");
        api.SetAccessToken("same-root");
        api.SetBaseUrl("https://api.example.test/");
        await api.GetEndpointStatusesAsync();

        api.SetBaseUrl("https://api.example.test/tenant");
        await api.GetEndpointStatusesAsync();

        api.SetAccessToken("cross-origin");
        api.SetBaseUrl("https://other.example.test");
        await api.GetEndpointStatusesAsync();

        Assert.Equal("Bearer same-root", handler.Requests[0].Authorization);
        Assert.Null(handler.Requests[1].Authorization);
        Assert.Null(handler.Requests[2].Authorization);
    }

    [Fact]
    public async Task SegmentResponse_RejectsMissingMultipleInvalidHeadersAndDeclaredLimit()
    {
        var missing = new RecordingHandler(_ => SegmentResponse(
            new byte[4], segmentIndex: 1, sourceFileIndex: 0, sourceOffsetSamples: 10, samples: 20,
            omitHeader: "X-Pim-Samples"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ApiClient(() => missing).GetNativeAsrSegmentContentAsync(
                LeaseId.ToString("D"), 1, LeaseToken.ToString("D"), 7, 1024));

        var multiple = new RecordingHandler(_ => SegmentResponse(
            new byte[4], segmentIndex: 1, sourceFileIndex: 0, sourceOffsetSamples: 10, samples: 20,
            duplicateHeader: "X-Pim-Segment-Index"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ApiClient(() => multiple).GetNativeAsrSegmentContentAsync(
                LeaseId.ToString("D"), 1, LeaseToken.ToString("D"), 7, 1024));

        var invalid = new RecordingHandler(_ => SegmentResponse(
            new byte[4], segmentIndex: 1, sourceFileIndex: 0, sourceOffsetSamples: 10, samples: 20,
            contentSha256: "not-a-digest"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ApiClient(() => invalid).GetNativeAsrSegmentContentAsync(
                LeaseId.ToString("D"), 1, LeaseToken.ToString("D"), 7, 1024));

        var tooLarge = new RecordingHandler(_ => SegmentResponse(
            new byte[32], segmentIndex: 1, sourceFileIndex: 0, sourceOffsetSamples: 10, samples: 20));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ApiClient(() => tooLarge).GetNativeAsrSegmentContentAsync(
                LeaseId.ToString("D"), 1, LeaseToken.ToString("D"), 7, 8));
    }

    [Fact]
    public async Task SegmentResponse_BoundsUnknownLengthStreamAndOwnsDisposal()
    {
        var owned = new TrackingStream(new byte[32]);
        var handler = new RecordingHandler(_ =>
        {
            var response = SegmentResponse(
                owned,
                segmentIndex: 1,
                sourceFileIndex: 0,
                sourceOffsetSamples: 10,
                samples: 20);
            response.Content.Headers.ContentLength = null;
            return response;
        });
        var api = new ApiClient(() => handler);
        var download = await api.GetNativeAsrSegmentContentAsync(
            LeaseId.ToString("D"), 1, LeaseToken.ToString("D"), 7, 8);
        Assert.NotNull(download);

        var buffer = new byte[32];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            while (await download.Content.ReadAsync(buffer) > 0)
            {
            }
        });
        await download.DisposeAsync();
        Assert.True(owned.Disposed);
    }

    [Fact]
    public async Task ApiClient_DisposesUnauthorizedResponseWhenRefreshCancels()
    {
        var owned = new TrackingStream(new byte[8]);
        var handler = new RecordingHandler(_ => StreamingResponse(
            owned,
            HttpStatusCode.Unauthorized,
            "application/json"));
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(Scope.ServerOrigin);
        api.SetAccessToken("old-token");
        api.RenewTokenAsync = () =>
            Task.FromException<bool>(new OperationCanceledException("refresh cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.AckNativeAsrAttemptAsync(AttemptId.ToString("D"), new NoteAsrAckRequest
            {
                LeaseId = LeaseId.ToString("D"),
                LeaseToken = LeaseToken.ToString("D"),
                FencingToken = 7,
                Kind = "segment",
                Result = "Succeeded",
                SegmentIndex = 0,
                ProviderHandle = ShaB,
                ResultText = "hello",
            }));

        Assert.True(owned.Disposed);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApiClient_DisposesResponseWhenRequestTimingSubscriberThrows()
    {
        var owned = new TrackingStream(new byte[8]);
        var handler = new RecordingHandler(_ => StreamingResponse(
            owned,
            HttpStatusCode.OK,
            "application/json"));
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(Scope.ServerOrigin);
        api.RequestTiming += (_, _) =>
            throw new InvalidOperationException("timing subscriber failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.GetEndpointStatusesAsync());

        Assert.True(owned.Disposed);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Transport_ClaimKeepsSparseIndexesAndGeneratedContentRoute()
    {
        var handler = new RecordingHandler(request => JsonResponse(
            ClaimResponseJson([SegmentDto(index: 2, expectedSegments: 3)])));
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(Scope.ServerOrigin);
        var transport = new LocalAsrApiTransport(
            api,
            () => Scope,
            (_, _, _) => Task.FromResult(true),
            requestTimeout: TimeSpan.FromSeconds(2),
            maxSegmentBytes: 1024);

        var result = await transport.ClaimAsync(ClaimRequest());

        Assert.Equal(LocalAsrClaimStatus.Claimed, result.Status);
        Assert.NotNull(result.Task);
        Assert.Equal(3, result.Task.ExpectedSegments);
        var segment = Assert.Single(result.Task.Segments);
        Assert.Equal(2, segment.SegmentIndex);
        Assert.Equal(LeaseId.ToString("D"), segment.LeaseId);
        Assert.Equal(AttemptId.ToString("D"), segment.AttemptId);
    }

    [Fact]
    public async Task Transport_RejectsWrongOwnerCrossServerAndContentEndpointMismatch()
    {
        using var scopeState = new ScopeState(Scope);

        var wrongOwner = "99999999-9999-9999-9999-999999999999";
        var handler = new RecordingHandler(_ => JsonResponse(
            ClaimResponseJson([SegmentDto(index: 0, expectedSegments: 1)], ownerId: wrongOwner)));
        var transport = Transport(handler, scopeState);
        var result = await transport.ClaimAsync(ClaimRequest());
        Assert.Equal(LocalAsrClaimStatus.Rejected, result.Status);
        Assert.Equal("lease_owner_mismatch", result.ReasonCode);

        var crossServer = new RecordingHandler(_ => JsonResponse(
            ClaimResponseJson(
                [SegmentDto(index: 0, expectedSegments: 1)],
                leaseOrigin: "https://evil.example.test")));
        var crossResult = await Transport(crossServer, scopeState).ClaimAsync(ClaimRequest());
        Assert.Equal(LocalAsrClaimStatus.Rejected, crossResult.Status);
        Assert.Equal("lease_cross_server", crossResult.ReasonCode);

        var mismatch = new RecordingHandler(_ => JsonResponse(
            ClaimResponseJson([SegmentDto(index: 0, expectedSegments: 1, contentEndpoint: "/api/v1/evil")])));
        var mismatchResult = await Transport(mismatch, scopeState).ClaimAsync(ClaimRequest());
        Assert.Equal(LocalAsrClaimStatus.Rejected, mismatchResult.Status);
        Assert.Equal("claim_content_endpoint_mismatch", mismatchResult.ReasonCode);
    }

    [Fact]
    public async Task Transport_OpenSegmentValidatesAllResponseHeadersAndReturnsBoundedOwnedStream()
    {
        var bytes = "wav"u8.ToArray();
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? SegmentResponse(
                    bytes,
                    segmentIndex: 2,
                    sourceFileIndex: 1,
                    sourceOffsetSamples: 400,
                    samples: 800)
                : JsonResponse(ClaimResponseJson([SegmentDto(
                    index: 2,
                    expectedSegments: 3,
                    sourceFileIndex: 1,
                    sourceOffsetSamples: 400,
                    samples: 800)])));
        var transport = Transport(handler, new ScopeState(Scope));
        var claim = await transport.ClaimAsync(ClaimRequest());
        Assert.NotNull(claim.Task);

        var content = await transport.OpenSegmentContentAsync(
            claim.Task.Lease,
            Assert.Single(claim.Task.Segments));
        try
        {
            using var copy = new MemoryStream();
            await content.Content.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
            Assert.Equal(Scope.ServerBaseAddress, content.ApiOrigin);
            Assert.Equal(Scope.OwnerIdentity, content.OwnerIdentity);
            Assert.Equal(NodeId.ToString("D"), content.NodeId);
            Assert.Equal(ShaA, content.Binding.ContentSha256);
        }
        finally
        {
            await content.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task Transport_SegmentContentOriginPreservesCanonicalPathBase()
    {
        var scoped = new LocalAccountScope(
            Scope.ServerOrigin + "/tenant-a",
            Scope.OwnerIdentity);
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? SegmentResponse(
                    "wav"u8.ToArray(),
                    segmentIndex: 0,
                    sourceFileIndex: 0,
                    sourceOffsetSamples: 10,
                    samples: 20)
                : JsonResponse(ClaimResponseJson(
                    [SegmentDto(index: 0, expectedSegments: 1)],
                    leaseOrigin: scoped.ServerBaseAddress)));
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(scoped.ServerBaseAddress);
        var transport = new LocalAsrApiTransport(
            api,
            () => scoped,
            (_, _, _) => Task.FromResult(true),
            requestTimeout: TimeSpan.FromSeconds(2),
            maxSegmentBytes: 1024);
        var claim = await transport.ClaimAsync(new LocalAsrClaimRequest(
            scoped,
            NodeId.ToString("D"),
            "claim-key",
            "runtime-a",
            "model-a",
            "revision-a",
            ModelManifest().Sha256));
        Assert.NotNull(claim.Task);

        var content = await transport.OpenSegmentContentAsync(
            claim.Task.Lease,
            Assert.Single(claim.Task.Segments));
        try
        {
            Assert.Equal(scoped.ServerBaseAddress, content.ApiOrigin);
        }
        finally
        {
            await content.Content.DisposeAsync();
        }
    }

    [Fact]
    public async Task Transport_AccountSwitchWhileRequestIsInFlightDiscardsResponse()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(_ => gate.Task);
        using var scopeState = new ScopeState(Scope);
        var transport = Transport(handler, scopeState);

        var pending = transport.ClaimAsync(ClaimRequest());
        await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scopeState.Current = new LocalAccountScope(
            "https://api-2.example.test",
            Scope.OwnerIdentity);
        gate.SetResult(JsonResponse(ClaimResponseJson([SegmentDto(index: 0, expectedSegments: 1)])));

        var result = await pending;
        Assert.Equal(LocalAsrClaimStatus.Deferred, result.Status);
        Assert.Contains("scope_changed", result.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_RegistrationUsesVerifiedManifestsAndRejectsUnverifiedModelBeforeNetwork()
    {
        var handler = new RecordingHandler(_ => JsonResponse(NodeResponseJson()));
        var transport = Transport(handler, new ScopeState(Scope));
        var node = await transport.RegisterAsync(
            NodeId.ToString("D"),
            "node-a",
            RuntimeManifest(),
            ModelManifest());
        Assert.NotNull(node);
        Assert.Single(handler.Requests);
        Assert.Contains("\"capabilities\":[\"local-asr\"]", handler.Requests[0].Body!);
        Assert.Contains("\"modelVerified\":true", handler.Requests[0].Body!);

        var badModel = ModelManifest() with { Sha256 = "" };
        var before = handler.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.RegisterAsync(
            NodeId.ToString("D"),
            "node-a",
            RuntimeManifest(),
            badModel));
        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task Transport_RegistrationRequiresFreshInstalledDigestVerificationBeforeNetwork()
    {
        var handler = new RecordingHandler(_ => JsonResponse(NodeResponseJson()));
        var transport = new LocalAsrApiTransport(
            new ApiClient(() => handler),
            () => Scope,
            (_, _, _) => Task.FromResult(false),
            requestTimeout: TimeSpan.FromSeconds(2),
            maxSegmentBytes: 1024);

        var error = await Assert.ThrowsAsync<LocalAsrTransportValidationException>(() =>
            transport.RegisterAsync(
                NodeId.ToString("D"),
                "node-a",
                RuntimeManifest(),
                ModelManifest()));

        Assert.Equal("runtime_or_model_install_unverified", error.ReasonCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Transport_CompleteClaimKeepsSourceSamplesUnknownAndRejectsMissingOrigin()
    {
        var complete = new RecordingHandler(_ => JsonResponse(
            ClaimResponseJson([SegmentDto(index: 0, expectedSegments: 2)])));
        var completeResult = await Transport(complete, new ScopeState(Scope))
            .ClaimAsync(ClaimRequest());
        Assert.Equal(LocalAsrClaimStatus.Claimed, completeResult.Status);
        Assert.NotNull(completeResult.Task);
        Assert.Equal(2, completeResult.Task.ExpectedSegments);
        Assert.Null(completeResult.Task.SourceSamples);

        var missingOrigin = new RecordingHandler(_ => JsonResponse(
            ClaimResponseJson(
                [SegmentDto(index: 0, expectedSegments: 1)],
                leaseOrigin: "")));
        var rejected = await Transport(missingOrigin, new ScopeState(Scope))
            .ClaimAsync(ClaimRequest());
        Assert.Equal(LocalAsrClaimStatus.Rejected, rejected.Status);
        Assert.Equal("lease_server_origin_invalid", rejected.ReasonCode);
    }

    [Fact]
    public async Task Transport_RenewRejectsChangedOwnerOrLeaseToken()
    {
        var wrongOwnerCalls = 0;
        var wrongOwnerHandler = new RecordingHandler(_ =>
            Interlocked.Increment(ref wrongOwnerCalls) == 1
                ? JsonResponse(ClaimResponseJson(
                    [SegmentDto(index: 0, expectedSegments: 1)]))
                : JsonResponse(LeaseResponseJson(
                    stopRequested: false,
                    ownerId: "99999999-9999-9999-9999-999999999999")));
        var wrongOwnerTransport = Transport(wrongOwnerHandler, new ScopeState(Scope));
        var claim = await wrongOwnerTransport.ClaimAsync(ClaimRequest());
        Assert.NotNull(claim.Task);
        var wrongOwnerResult = await wrongOwnerTransport.RenewAsync(claim.Task.Lease);
        Assert.Equal(LocalAsrRenewStatus.Rejected, wrongOwnerResult.Status);
        Assert.Equal("lease_owner_mismatch", wrongOwnerResult.ReasonCode);

        var changedTokenCalls = 0;
        var tokenHandler = new RecordingHandler(_ =>
            Interlocked.Increment(ref changedTokenCalls) == 1
                ? JsonResponse(ClaimResponseJson(
                    [SegmentDto(index: 0, expectedSegments: 1)]))
                : JsonResponse(LeaseResponseJson(
                    stopRequested: false,
                    leaseToken: Guid.Parse("99999999-9999-9999-9999-999999999999"))));
        var changedTokenTransport = Transport(tokenHandler, new ScopeState(Scope));
        var tokenClaim = await changedTokenTransport.ClaimAsync(ClaimRequest());
        Assert.NotNull(tokenClaim.Task);
        var changedToken = await changedTokenTransport.RenewAsync(tokenClaim.Task.Lease);
        Assert.Equal(LocalAsrRenewStatus.Rejected, changedToken.Status);
        Assert.Equal("lease_identity_changed", changedToken.ReasonCode);
    }

    [Fact]
    public async Task ApiClient_AllAsrOperationsRejectDeploymentSwitchDuringAuthRetryBeforeSecondSend()
    {
        for (var operation = 0; operation < 7; operation++)
        {
            var oldHandler = new RecordingHandler(_ =>
                StatusResponse(HttpStatusCode.Unauthorized));
            var newHandler = new RecordingHandler(_ => JsonResponse(NodeResponseJson()));
            var handlerCreations = 0;
            var api = new ApiClient(() =>
            {
                handlerCreations++;
                return handlerCreations <= 2 ? oldHandler : newHandler;
            });
            api.SetBaseUrl(Scope.ServerOrigin);
            api.SetAccessToken("old-owner-token");
            api.RenewTokenAsync = () =>
            {
                api.SetBaseUrl(Scope.ServerOrigin + "/tenant-b");
                api.SetAccessToken("new-owner-token");
                return Task.FromResult(true);
            };

            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                InvokeAsrOperationAsync(api, operation));
            Assert.Contains("deployment changed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(oldHandler.Requests);
            Assert.Empty(newHandler.Requests);
        }
    }

    private static LocalAsrApiTransport Transport(
        RecordingHandler handler,
        ScopeState scopeState,
        LocalAsrInstalledArtifactVerifier? verifier = null)
    {
        var api = new ApiClient(() => handler);
        api.SetBaseUrl(Scope.ServerOrigin);
        return new LocalAsrApiTransport(
            api,
            () => scopeState.Current,
            verifier ?? ((_, _, _) => Task.FromResult(true)),
            requestTimeout: TimeSpan.FromSeconds(2),
            maxSegmentBytes: 1024);
    }

    private static LocalAsrClaimRequest ClaimRequest() =>
        new(
            Scope,
            NodeId.ToString("D"),
            "claim-key",
            "runtime-a",
            "model-a",
            "revision-a",
            ModelManifest().Sha256);

    private static Task InvokeAsrOperationAsync(ApiClient api, int operation)
        => operation switch
        {
            0 => api.RegisterNativeAsrNodeAsync(new RegisterNoteAsrNodeRequest
            {
                NodeId = NodeId.ToString("D"),
                NodeIdExternal = "node-a",
                Enabled = true,
                ModelId = "model-a",
                ModelRevision = "revision-a",
                ModelVerified = true,
                Capabilities = ["local-asr"],
            }),
            1 => api.HeartbeatNativeAsrNodeAsync(
                NodeId.ToString("D"),
                new NoteAsrNodeHeartbeatRequest
                {
                    NodeId = NodeId.ToString("D"),
                    ModelId = "model-a",
                    ModelRevision = "revision-a",
                    ModelVerified = true,
                    Capabilities = ["local-asr"],
                }),
            2 => api.WithdrawNativeAsrNodeAsync(NodeId.ToString("D")),
            3 => api.ClaimNativeAsrLeaseAsync(new ClaimNoteAsrTaskRequest
            {
                NodeId = NodeId.ToString("D"),
                IdempotencyKey = "claim-key",
                LeaseSeconds = 120,
            }),
            4 => api.RenewNativeAsrLeaseAsync(
                LeaseId.ToString("D"),
                new NoteAsrLeaseHeartbeatRequest
                {
                    LeaseId = LeaseId.ToString("D"),
                    LeaseToken = LeaseToken.ToString("D"),
                    FencingToken = 7,
                }),
            5 => GetSegmentAsync(api),
            6 => api.AckNativeAsrAttemptAsync(
                AttemptId.ToString("D"),
                new NoteAsrAckRequest
                {
                    LeaseId = LeaseId.ToString("D"),
                    LeaseToken = LeaseToken.ToString("D"),
                    FencingToken = 7,
                    Kind = "segment",
                    Result = "Succeeded",
                    SegmentIndex = 0,
                    ProviderHandle = ShaB,
                    ResultText = "hello",
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static async Task GetSegmentAsync(ApiClient api)
    {
        await using var download = await api.GetNativeAsrSegmentContentAsync(
            LeaseId.ToString("D"),
            0,
            LeaseToken.ToString("D"),
            7,
            1024);
        Assert.NotNull(download);
    }

    private static object SegmentDto(
        int index,
        int expectedSegments,
        int sourceFileIndex = 0,
        long sourceOffsetSamples = 10,
        long samples = 20,
        string? contentEndpoint = null) =>
        new
        {
            index,
            recordingId = RecordingId.ToString("D"),
            recordingSegment = index,
            chunkIndex = index,
            sourceFileIndex,
            sourceSha256 = ShaB,
            sourceOffsetSamples,
            samples,
            contentEndpoint = contentEndpoint
                ?? ContentRoute(index),
            expectedSegments,
        };

    private static string ClaimResponseJson(
        IReadOnlyList<object> segments,
        string ownerId = "11111111-1111-1111-1111-111111111111",
        string? leaseOrigin = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expectedSegments = segments.Count == 0
            ? 3
            : segments.Max(segment => (int)segment.GetType().GetProperty("expectedSegments")!.GetValue(segment)!);
        return JsonSerializer.Serialize(new
        {
            code = 0,
            message = "ok",
            timestamp = now,
            data = new
            {
                leaseId = LeaseId.ToString("D"),
                status = "claimed",
                lease = new
                {
                    leaseId = LeaseId.ToString("D"),
                    leaseToken = LeaseToken.ToString("D"),
                    attemptId = AttemptId.ToString("D"),
                    fencingToken = 7,
                    expiresAt = now.AddMinutes(5),
                    route = "local",
                    ownerId,
                    serverNow = now,
                    serverOrigin = leaseOrigin ?? Scope.ServerOrigin,
                    stopRequested = false,
                },
                execution = new
                {
                    attemptId = AttemptId.ToString("D"),
                    configRevision = Guid.NewGuid().ToString("D"),
                    consentVersion = "v1",
                    expectedSegments,
                    inputSha256 = ShaA,
                    language = "en",
                    localModelId = "model-a",
                    localModelRevision = "revision-a",
                    noteId = Guid.NewGuid().ToString("D"),
                    ownerId,
                    route = "local",
                    segments,
                    serverNow = now,
                    serverOrigin = leaseOrigin ?? Scope.ServerOrigin,
                    taskId = TaskId.ToString("D"),
                    versionId = VersionId.ToString("D"),
                },
            },
        });
    }

    private static string ContentRoute(int segmentIndex) =>
        ContractOperation.GetNativeAsrSegmentContent.PathTemplate
            .Replace("{leaseId}", Uri.EscapeDataString(LeaseId.ToString("D")), StringComparison.Ordinal)
            .Replace(
                "{segmentIndex}",
                Uri.EscapeDataString(
                    segmentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                StringComparison.Ordinal);

    private static string LeaseResponseJson(
        bool stopRequested,
        string ownerId = "11111111-1111-1111-1111-111111111111",
        Guid? leaseToken = null)
    {
        var now = DateTimeOffset.UtcNow;
        return JsonSerializer.Serialize(new
        {
            code = 0,
            message = "ok",
            timestamp = now,
            data = new
            {
                leaseId = LeaseId.ToString("D"),
                leaseToken = (leaseToken ?? LeaseToken).ToString("D"),
                attemptId = AttemptId.ToString("D"),
                fencingToken = 7,
                expiresAt = now.AddMinutes(5),
                route = "local",
                ownerId,
                serverNow = now,
                serverOrigin = Scope.ServerOrigin,
                stopRequested,
            },
        });
    }

    private static string NodeResponseJson() =>
        JsonSerializer.Serialize(new
        {
            code = 0,
            message = "ok",
            timestamp = DateTimeOffset.UtcNow,
            data = new
            {
                capabilities = new[] { "local-asr" },
                claimable = true,
                createdAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                enabled = true,
                lastHeartbeatAt = DateTimeOffset.UtcNow,
                modelId = "model-a",
                modelRevision = "revision-a",
                modelVerified = true,
                nodeId = NodeId.ToString("D"),
                nodeIdExternal = "node-a",
                resourceSnapshotJson = "{}",
                runtimeId = "runtime-a",
                runtimeVersion = "runtime-revision",
                sampledAt = DateTimeOffset.UtcNow,
            },
        });

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage StatusResponse(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage StreamingResponse(
        Stream stream,
        HttpStatusCode status,
        string mediaType) =>
        new(status)
        {
            Content = new StreamContent(stream)
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue(mediaType),
                },
            },
        };

    private static HttpResponseMessage SegmentResponse(
        byte[] bytes,
        int segmentIndex,
        int sourceFileIndex,
        long sourceOffsetSamples,
        long samples,
        string? omitHeader = null,
        string? duplicateHeader = null,
        string contentSha256 = ShaA) =>
        SegmentResponse(
            new MemoryStream(bytes),
            segmentIndex,
            sourceFileIndex,
            sourceOffsetSamples,
            samples,
            omitHeader,
            duplicateHeader,
            contentSha256,
            bytes.LongLength);

    private static HttpResponseMessage SegmentResponse(
        Stream stream,
        int segmentIndex,
        int sourceFileIndex,
        long sourceOffsetSamples,
        long samples,
        string? omitHeader = null,
        string? duplicateHeader = null,
        string contentSha256 = ShaA,
        long? contentLength = null)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        if (contentLength is { } length)
            content.Headers.ContentLength = length;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Content-SHA256"] = contentSha256,
            ["X-Pim-Attempt-Id"] = AttemptId.ToString("D"),
            ["X-Pim-Fencing-Token"] = "7",
            ["X-Pim-Lease-Id"] = LeaseId.ToString("D"),
            ["X-Pim-Node-Id"] = NodeId.ToString("D"),
            ["X-Pim-Owner-Id"] = Scope.OwnerIdentity,
            ["X-Pim-Samples"] = samples.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Pim-Segment-Index"] = segmentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Pim-Source-File-Index"] = sourceFileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Pim-Source-Offset-Samples"] = sourceOffsetSamples.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Pim-Source-SHA256"] = ShaB,
            ["X-Pim-Version-Id"] = VersionId.ToString("D"),
        };
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, omitHeader, StringComparison.Ordinal))
                continue;
            if (string.Equals(name, duplicateHeader, StringComparison.Ordinal))
                response.Headers.TryAddWithoutValidation(name, new[] { value, value });
            else
                response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private static LocalRuntimeManifest RuntimeManifest() =>
        new(
            "runtime-a",
            "Runtime A",
            "helper.exe",
            "https://example.invalid/runtime.exe",
            "MIT",
            "https://example.invalid/LICENSE",
            "runtime-revision",
            Sha("runtime"),
            7,
            LocalRuntimeArtifactKind.File,
            "helper.exe",
            [new LocalRuntimeFileManifest("helper.exe", 7, Sha("runtime"))],
            new LocalRuntimeExecutionProfile(
                ["--input", "{inputPath}"],
                TimeSpan.FromSeconds(30),
                LocalRuntimeExecutionProfile.PlainText));

    private static LocalModelManifest ModelManifest() =>
        new(
            "model-a",
            "Model A",
            "runtime-a",
            "MIT",
            "https://example.invalid/LICENSE",
            "https://example.invalid/model.bin",
            "revision-a",
            Sha("model"),
            5,
            "model.bin",
            new LocalResourceRequirements(CpuCores: 1, MemoryBytes: 1024));

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class ScopeState : IDisposable
    {
        private readonly CancellationTokenSource _disposed = new();

        public ScopeState(LocalAccountScope current)
        {
            Current = current;
        }

        public LocalAccountScope? Current { get; set; }

        public void Dispose() => _disposed.Dispose();
    }

    private sealed class TrackingStream : MemoryStream
    {
        public TrackingStream(byte[] bytes)
            : base(bytes)
        {
        }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _respondAsync;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respondAsync)
        {
            _respondAsync = respondAsync;
            _respond = _ => throw new InvalidOperationException();
        }

        public List<RecordedRequest> Requests { get; } = [];

        public TaskCompletionSource FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                body,
                request.Headers.Authorization?.ToString(),
                request.Headers
                    .Where(header => header.Key.StartsWith("X-Pim-", StringComparison.Ordinal))
                    .ToDictionary(
                        header => header.Key,
                        header => string.Join(",", header.Value),
                        StringComparer.OrdinalIgnoreCase)));
            FirstRequest.TrySetResult();
            return _respondAsync is null
                ? _respond(request)
                : await _respondAsync(request);
        }
    }

    private sealed record RecordedRequest(
        string Path,
        string Query,
        string? Body,
        string? Authorization,
        IReadOnlyDictionary<string, string> Headers);
}
