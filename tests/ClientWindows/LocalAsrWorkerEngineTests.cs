using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Models.LocalRuntime;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class LocalAsrWorkerEngineTests : IDisposable
{
    private static readonly LocalAccountScope Scope =
        new("https://api.example.test", "account-a");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-asr-worker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AutomaticRun_ClaimsValidatesExecutesAndAcknowledgesEverySegment()
    {
        var bytes = new[]
        {
            "segment-zero"u8.ToArray(),
            "segment-one"u8.ToArray(),
        };
        var transport = new FakeTransport(CreateClaim(bytes), bytes);
        var adapter = new FakeRuntimeAdapter("transcript");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(
            Scope,
            LocalAsrWorkTrigger.Automatic,
            "claim-key");

        Assert.Equal(LocalAsrWorkerRunStatus.Completed, result.Status);
        Assert.True(result.TerminalAcknowledged);
        Assert.Equal(2, result.Segments.Count);
        Assert.All(result.Segments, segment =>
        {
            Assert.True(segment.Succeeded);
            Assert.True(segment.Acknowledged);
        });
        Assert.Equal(2, adapter.CallCount);
        var acks = transport.GetAcks();
        Assert.Equal(3, acks.Count);
        Assert.Equal(LocalAsrAckKind.SegmentSucceeded, acks[0].Kind);
        Assert.Equal(LocalAsrAckKind.SegmentSucceeded, acks[1].Kind);
        Assert.Equal(LocalAsrAckKind.AttemptCompleted, acks[2].Kind);
        Assert.Equal("transcript", acks[0].Text);
        Assert.Equal(2, transport.ContentOpenCount);
        var persisted = await CreateStateStore().LoadAsync(Scope, "node-a");
        Assert.NotNull(persisted);
        Assert.Equal(LocalAsrPersistedStage.Completed, persisted.Stage);
    }

    [Fact]
    public async Task ResourceBackoff_BlocksAutomaticButManualStillRunsTheResourceGate()
    {
        var transport = new FakeTransport(
            new LocalAsrClaimResult(LocalAsrClaimStatus.NoWork, null, "empty"));
        var adapter = new FakeRuntimeAdapter("unused");
        var resources = new MutableResourceProvider(ResourceSnapshot.Unknown(DateTimeOffset.UtcNow));
        var backoff = new RuntimeBackoffStore(
            Path.Combine(_root, "backoff"),
            new RuntimeBackoffPolicy(
                MaxAutoAttempts: 2,
                BaseDelaySeconds: 30,
                MaxDelaySeconds: 60));
        var engine = CreateEngine(transport, adapter, () => resources.Snapshot, backoff);

        var first = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);
        Assert.Equal(LocalAsrWorkerRunStatus.ResourceBlocked, first.Status);
        Assert.Equal(0, transport.ClaimCount);
        Assert.Equal(1, resources.ReadCount);

        var second = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);
        Assert.Equal(LocalAsrWorkerRunStatus.BackoffDeferred, second.Status);
        Assert.Equal(1, resources.ReadCount);
        Assert.Equal(0, transport.ClaimCount);

        var manualBlocked = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Manual);
        Assert.Equal(LocalAsrWorkerRunStatus.ResourceBlocked, manualBlocked.Status);
        Assert.Equal(2, resources.ReadCount);
        Assert.Equal(0, transport.ClaimCount);

        resources.Snapshot = GoodSnapshot()();
        var manual = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Manual);
        Assert.Equal(LocalAsrWorkerRunStatus.NoWork, manual.Status);
        Assert.Equal(3, resources.ReadCount);
        Assert.Equal(1, transport.ClaimCount);
    }

    [Fact]
    public async Task ResourceDecision_IsFrozenPerClaimKeyAndResourceFailureNeverClaims()
    {
        var transport = new FakeTransport(
            new LocalAsrClaimResult(LocalAsrClaimStatus.NoWork, null, "empty"));
        var adapter = new FakeRuntimeAdapter("unused");
        var resources = new MutableResourceProvider(ResourceSnapshot.Unknown(DateTimeOffset.UtcNow));
        var outbox = new LocalAsrResourceDecisionOutbox(Path.Combine(_root, "resource-outbox"));
        var decisionAdapter = new LocalAsrClaimDecisionAdapter(transport, outbox);
        var engine = CreateEngine(
            transport,
            adapter,
            () => resources.Snapshot,
            claimDecisionAdapter: decisionAdapter);

        var first = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic, "frozen-key");
        var firstPending = await outbox.FindPendingAsync(Scope, "node-a", "frozen-key");
        resources.Snapshot = GoodSnapshot()();
        var replay = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic, "frozen-key");
        var replayPending = await outbox.FindPendingAsync(Scope, "node-a", "frozen-key");

        Assert.Equal(LocalAsrWorkerRunStatus.ResourceBlocked, first.Status);
        Assert.Equal(LocalAsrWorkerRunStatus.ResourceBlocked, replay.Status);
        Assert.NotNull(firstPending);
        Assert.NotNull(replayPending);
        Assert.False(firstPending.Decision.Passed);
        Assert.False(replayPending.Decision.Passed);
        Assert.Equal(firstPending.Decision.Reasons, replayPending.Decision.Reasons);
        Assert.True(replayPending.Decision.IsAutomatic);
        Assert.Equal(0, transport.ClaimCount);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task ScopeSwitchAfterSampling_CancelsBeforeAnyClaimIsSent()
    {
        var transport = new FakeTransport(
            new LocalAsrClaimResult(LocalAsrClaimStatus.NoWork, null, "empty"));
        var adapter = new FakeRuntimeAdapter("unused");
        var guard = new LocalRuntimeAccountGuard();
        var snapshotStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource<ResourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegatingSnapshotProvider(async _ =>
        {
            snapshotStarted.TrySetResult(true);
            return await releaseSnapshot.Task.ConfigureAwait(false);
        });
        var engine = new LocalAsrWorkerEngine(
            transport,
            adapter,
            guard,
            provider,
            new RuntimeBackoffStore(Path.Combine(_root, "switch-backoff")),
            CreateStateStore(),
            new LocalAsrWorkerOptions(
                "node-a", "Node A", CreateRuntimeManifest(), CreateModelManifest(),
                Path.Combine(_root, "staging")));

        var run = engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Manual, "switch-key");
        await snapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await engine.SwitchScopeAsync(new LocalAccountScope("https://other.example.test", "account-b"));
        releaseSnapshot.TrySetResult(GoodSnapshot()());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LocalAsrWorkerRunStatus.ScopeChanged, result.Status);
        Assert.Equal(0, transport.ClaimCount);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task ContentDigestMismatch_FailsBeforeExecutionAndSendsFailedAcks()
    {
        var bytes = new[] { "actual-segment-bytes"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(bytes), bytes)
        {
            ContentSha256Override = new string('a', 64),
        };
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.Failed, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.ContentDigestMismatch, result.ReasonCode);
        Assert.Equal(0, adapter.CallCount);
        Assert.True(result.TerminalAcknowledged);
        var acks = transport.GetAcks();
        Assert.Equal(2, acks.Count);
        Assert.Equal(LocalAsrAckKind.SegmentFailed, acks[0].Kind);
        Assert.Equal(LocalAsrAckKind.AttemptFailed, acks[1].Kind);
    }

    [Fact]
    public async Task InputManifestSourceAndActualContentDigests_AreKeptDistinct()
    {
        var bytes = "actual-binary-segment"u8.ToArray();
        var claim = CreateClaim(new[] { bytes });
        claim = claim with
        {
            Segments = new[]
            {
                claim.Segments[0] with { SourceSha256 = new string('d', 64) },
            },
        };
        var segment = Assert.Single(claim.Segments);

        Assert.Equal(new string('c', 64), claim.InputManifestSha256);
        Assert.NotEqual(claim.InputManifestSha256, segment.SourceSha256);
        Assert.NotEqual(segment.SourceSha256, Sha256(bytes));

        var result = await CreateEngine(
                new FakeTransport(claim, new[] { bytes }),
                new FakeRuntimeAdapter("digest-separated"),
                GoodSnapshot())
            .ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task SparsePendingSegments_ExecuteOnlyTheClaimedSubset()
    {
        var bytes = new[]
        {
            "segment-one"u8.ToArray(),
            "segment-two"u8.ToArray(),
        };
        var claim = CreateClaim(bytes);
        claim = claim with
        {
            ExpectedSegments = 3,
            Segments = new[]
            {
                claim.Segments[0] with { SegmentIndex = 1 },
                claim.Segments[1] with { SegmentIndex = 2 },
            },
        };
        var transport = new FakeTransport(claim, bytes);
        var adapter = new FakeRuntimeAdapter("sparse");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.Completed, result.Status);
        Assert.Equal(new[] { 1, 2 }, result.Segments.Select(segment => segment.SegmentIndex));
        Assert.Equal(2, adapter.CallCount);
        Assert.Equal(
            new[] { 1, 2, -1 },
            transport.GetAcks().Select(ack => ack.SegmentIndex ?? -1));
    }

    [Fact]
    public async Task EmptyPendingSegments_FailClosedWithoutRuntimeOrAck()
    {
        var claim = CreateClaim(new[] { "unused"u8.ToArray() }) with
        {
            Segments = Array.Empty<LocalAsrInputSegment>(),
        };
        var transport = new FakeTransport(claim, Array.Empty<byte[]>());
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.NoWork, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.NoPendingSegments, result.ReasonCode);
        Assert.False(result.TerminalAcknowledged);
        Assert.Equal(0, adapter.CallCount);
        Assert.Equal(0, transport.ContentOpenCount);
        Assert.Empty(transport.GetAcks());
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 1)]
    public async Task InvalidSparseSegmentIndex_IsRejected(int expectedSegments, int segmentIndex)
    {
        var bytes = new[] { "audio"u8.ToArray() };
        var claim = CreateClaim(bytes);
        claim = claim with
        {
            ExpectedSegments = expectedSegments,
            Segments = new[]
            {
                claim.Segments[0] with { SegmentIndex = segmentIndex },
            },
        };
        var transport = new FakeTransport(claim, bytes);
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.InvalidClaim, result.Status);
        Assert.Equal(0, transport.ContentOpenCount);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task DuplicateSparseSegmentIndex_IsRejectedBeforeContentRead()
    {
        var bytes = new[] { "audio"u8.ToArray() };
        var claim = CreateClaim(bytes);
        claim = claim with
        {
            ExpectedSegments = 3,
            Segments = new[]
            {
                claim.Segments[0] with { SegmentIndex = 1 },
                claim.Segments[0] with { SegmentIndex = 1 },
            },
        };
        var transport = new FakeTransport(claim, new[] { bytes[0], bytes[0] });
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.InvalidClaim, result.Status);
        Assert.Equal(0, transport.ContentOpenCount);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task OversizedClaim_IsRejectedBeforeContentRead()
    {
        var bytes = new[] { "12345"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(bytes), bytes);
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(
            transport,
            adapter,
            GoodSnapshot(),
            maxSegmentContentBytes: 4,
            maxTotalContentBytes: 4);

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.InvalidClaim, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.ClaimInvalid, result.ReasonCode);
        Assert.Equal(0, transport.ContentOpenCount);
        Assert.Equal(0, adapter.CallCount);
        Assert.True(result.TerminalAcknowledged);
        Assert.Equal(
            LocalAsrAckKind.AttemptFailed,
            Assert.Single(transport.GetAcks()).Kind);
    }

    [Fact]
    public async Task ActualContentBeyondBound_FailsWithoutRuntimeEvenWhenClaimOmitsSize()
    {
        var bytes = "12345"u8.ToArray();
        var claim = CreateClaim(new[] { bytes });
        claim = claim with
        {
            Segments = new[]
            {
                claim.Segments[0] with { ExpectedContentBytes = null },
            },
        };
        var transport = new FakeTransport(claim, new[] { bytes });
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(
            transport,
            adapter,
            GoodSnapshot(),
            maxSegmentContentBytes: 4,
            maxTotalContentBytes: 4);

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.Failed, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.InputInvalid, result.ReasonCode);
        Assert.Equal(0, adapter.CallCount);
        Assert.True(result.TerminalAcknowledged);
    }

    [Fact]
    public async Task RenewStop_CancelsTheRunWithoutFalseSuccessAck()
    {
        var audio = new[] { "audio"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(audio), audio)
        {
            RenewHandler = (_, _) => Task.FromResult(new LocalAsrRenewResult(
                LocalAsrRenewStatus.StopRequested,
                StopRequested: true,
                ReasonCode: "server-stop")),
        };
        var adapter = new FakeRuntimeAdapter(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var engine = CreateEngine(
            transport,
            adapter,
            GoodSnapshot(),
            renewInterval: TimeSpan.FromMilliseconds(20));

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(LocalAsrWorkerRunStatus.Stopped, result.Status);
        Assert.True(result.TerminalAcknowledged);
        var terminal = Assert.Single(transport.GetAcks());
        Assert.Equal(LocalAsrAckKind.AttemptFailed, terminal.Kind);
        Assert.Equal(LocalRuntimeReasonCodes.WorkerStopped, terminal.ErrorCode);
        var persisted = await CreateStateStore().LoadAsync(Scope, "node-a");
        Assert.NotNull(persisted);
        Assert.Equal(LocalAsrPersistedStage.ExecutionUnknown, persisted.Stage);
    }

    [Fact]
    public async Task LeaseExpiry_CancelsTheRun()
    {
        var now = DateTimeOffset.UtcNow;
        var audio = new[] { "audio"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(
            audio,
            leaseExpiresAtUtc: now.AddMilliseconds(500)), audio)
        {
            RenewHandler = (_, _) => new TaskCompletionSource<LocalAsrRenewResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task,
        };
        var adapter = new FakeRuntimeAdapter(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var engine = CreateEngine(
            transport,
            adapter,
            GoodSnapshot(),
            renewInterval: TimeSpan.FromMilliseconds(50));

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(LocalAsrWorkerRunStatus.LeaseExpired, result.Status);
        Assert.False(result.TerminalAcknowledged);
        Assert.Empty(transport.GetAcks());
    }

    [Fact]
    public async Task SameOwnerDifferentOrigin_CancelsRunAndWithdrawsOldScope()
    {
        var audio = new[] { "audio"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(audio), audio);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeRuntimeAdapter(async (_, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var engine = CreateEngine(transport, adapter, GoodSnapshot());
        var newScope = new LocalAccountScope("https://api-2.example.test", "account-a");

        var run = engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);
        await started.Task;
        var switched = await engine.SwitchScopeAsync(newScope);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(LocalAsrWorkerRunStatus.ScopeChanged, result.Status);
        Assert.True(switched.AccountSwitch.ActiveRunCancelled);
        Assert.True(switched.AccountSwitch.CapabilityRevoked);
        Assert.NotNull(switched.Withdraw);
        Assert.Equal(LocalAsrWithdrawStatus.Withdrawn, switched.Withdraw.Status);
        var withdraw = Assert.Single(transport.GetWithdrawals());
        Assert.True(withdraw.Scope.Matches(Scope));
        Assert.Empty(transport.GetAcks());
    }

    [Fact]
    public async Task SameHostDifferentBasePath_CancelsRunAndWithdrawsOldScope()
    {
        var audio = new[] { "audio"u8.ToArray() };
        var scopeA = new LocalAccountScope("https://api.example.test/tenant-a", "account-a");
        var scopeB = new LocalAccountScope("https://api.example.test/tenant-b", "account-a");
        var claim = CreateClaim(audio);
        var transport = new FakeTransport(claim, audio)
        {
            ContentOriginOverride = scopeA.ServerBaseAddress,
        };
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeRuntimeAdapter(async (_, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var run = engine.ProcessOnceAsync(scopeA, LocalAsrWorkTrigger.Automatic);
        await started.Task;
        var switched = await engine.SwitchScopeAsync(scopeB);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(LocalAsrWorkerRunStatus.ScopeChanged, result.Status);
        Assert.True(switched.AccountSwitch.ActiveRunCancelled);
        Assert.True(switched.AccountSwitch.CapabilityRevoked);
        Assert.True(Assert.Single(transport.GetWithdrawals()).Scope.Matches(scopeA));
        Assert.Empty(transport.GetAcks());
    }

    [Fact]
    public async Task SameHostDifferentBasePath_UsesIndependentBackoffAndCheckpoint()
    {
        var scopeA = new LocalAccountScope("https://api.example.test/tenant-a", "account-a");
        var scopeB = new LocalAccountScope("https://api.example.test/tenant-b", "account-a");
        var backoff = new RuntimeBackoffStore(
            Path.Combine(_root, "backoff-shared"),
            new RuntimeBackoffPolicy(
                MaxAutoAttempts: 2,
                BaseDelaySeconds: 30,
                MaxDelaySeconds: 60));
        var unknown = new MutableResourceProvider(
            ResourceSnapshot.Unknown(DateTimeOffset.UtcNow));
        var engineA = CreateEngine(
            new FakeTransport(new LocalAsrClaimResult(LocalAsrClaimStatus.NoWork, null)),
            new FakeRuntimeAdapter("unused"),
            () => unknown.Snapshot,
            backoff);
        var engineB = CreateEngine(
            new FakeTransport(new LocalAsrClaimResult(LocalAsrClaimStatus.NoWork, null)),
            new FakeRuntimeAdapter("unused"),
            () => unknown.Snapshot,
            backoff);

        var firstA = await engineA.ProcessOnceAsync(scopeA, LocalAsrWorkTrigger.Automatic);
        var firstB = await engineB.ProcessOnceAsync(scopeB, LocalAsrWorkTrigger.Automatic);
        var secondA = await engineA.ProcessOnceAsync(scopeA, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.ResourceBlocked, firstA.Status);
        Assert.Equal(LocalAsrWorkerRunStatus.ResourceBlocked, firstB.Status);
        Assert.Equal(LocalAsrWorkerRunStatus.BackoffDeferred, secondA.Status);

        var stateStore = CreateStateStore();
        await stateStore.SaveAsync(new LocalAsrWorkerPersistedState(
            scopeA.ServerBaseAddress,
            scopeA.OwnerIdentity,
            "node-a",
            "attempt-a",
            "lease-a",
            7,
            LocalAsrPersistedStage.Running,
            null,
            null,
            DateTimeOffset.UtcNow));

        Assert.NotNull(await stateStore.LoadAsync(scopeA, "node-a"));
        Assert.Null(await stateStore.LoadAsync(scopeB, "node-a"));
        Assert.Equal(
            LocalAsrRestartRecoveryStatus.NoPriorState,
            (await stateStore.RecoverAfterRestartAsync(scopeB, "node-a")).Status);
    }

    [Fact]
    public async Task Restart_ConvertsRunningLeaseToUnknownWithoutClaimingOrExecuting()
    {
        var now = DateTimeOffset.UtcNow;
        var stateStore = CreateStateStore();
        await stateStore.SaveAsync(new LocalAsrWorkerPersistedState(
            Scope.ServerBaseAddress,
            Scope.OwnerIdentity,
            "node-a",
            "attempt-old",
            "lease-old",
            7,
            LocalAsrPersistedStage.Running,
            null,
            null,
            now));
        var transport = new FakeTransport(
            new LocalAsrClaimResult(LocalAsrClaimStatus.NoWork, null));
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(
            transport,
            adapter,
            GoodSnapshot(),
            stateStoreOverride: stateStore);

        var recovery = await engine.RecoverAfterRestartAsync(Scope);

        Assert.Equal(LocalAsrRestartRecoveryStatus.ExecutionUnknown, recovery.Status);
        Assert.Equal("attempt-old", recovery.AttemptId);
        Assert.Equal(0, transport.ClaimCount);
        Assert.Equal(0, adapter.CallCount);
        var persisted = await stateStore.LoadAsync(Scope, "node-a");
        Assert.NotNull(persisted);
        Assert.Equal(LocalAsrPersistedStage.ExecutionUnknown, persisted.Stage);
        Assert.Equal(LocalRuntimeReasonCodes.RestartUnknown, persisted.ReasonCode);
    }

    [Fact]
    public async Task RenewWithChangedLeaseIdentity_AbortsAsUnknownAndNeverUpgradesFence()
    {
        var audio = new[] { "audio"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(audio), audio)
        {
            RenewHandler = (lease, _) => Task.FromResult(new LocalAsrRenewResult(
                LocalAsrRenewStatus.Renewed,
                lease with
                {
                    LeaseToken = lease.LeaseToken + "-upgraded",
                    FencingToken = lease.FencingToken + 1,
                    ExpiresAtUtc = lease.ExpiresAtUtc.AddMinutes(5),
                    ServerNowUtc = DateTimeOffset.UtcNow,
                })),
        };
        var adapter = new FakeRuntimeAdapter(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var engine = CreateEngine(
            transport,
            adapter,
            GoodSnapshot(),
            renewInterval: TimeSpan.FromMilliseconds(20));

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(LocalAsrWorkerRunStatus.ExecutionUnknown, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.RenewFailed, result.ReasonCode);
        Assert.False(result.TerminalAcknowledged);
        Assert.Empty(transport.GetAcks());
    }

    [Fact]
    public async Task HungRenew_AbortsAtLeaseDeadlineInsteadOfRunningPastExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var audio = new[] { "audio"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(
            audio,
            leaseExpiresAtUtc: now.AddMilliseconds(500)), audio)
        {
            RenewHandler = (_, _) => new TaskCompletionSource<LocalAsrRenewResult>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task,
        };
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeRuntimeAdapter(async (_, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        var engine = CreateEngine(
            transport,
            adapter,
            GoodSnapshot(),
            renewInterval: TimeSpan.FromMilliseconds(50));

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(LocalAsrWorkerRunStatus.LeaseExpired, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.LeaseExpired, result.ReasonCode);
        Assert.False(result.TerminalAcknowledged);
    }

    [Fact]
    public async Task ContentResponseOriginMismatch_FailsBeforeExecutingTheRuntime()
    {
        var bytes = new[] { "audio"u8.ToArray() };
        var transport = new FakeTransport(CreateClaim(bytes), bytes)
        {
            ContentOriginOverride = "https://evil.example.test",
        };
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(Scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.Failed, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.ContentBindingMismatch, result.ReasonCode);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task SameHostOtherTenantContent_IsRejectedBeforeExecutingTheRuntime()
    {
        var bytes = new[] { "audio"u8.ToArray() };
        var scope = new LocalAccountScope("https://api.example.test/tenant-a", "account-a");
        var transport = new FakeTransport(CreateClaim(bytes), bytes)
        {
            ContentOriginOverride = "https://api.example.test/tenant-b",
        };
        var adapter = new FakeRuntimeAdapter("must-not-run");
        var engine = CreateEngine(transport, adapter, GoodSnapshot());

        var result = await engine.ProcessOnceAsync(scope, LocalAsrWorkTrigger.Automatic);

        Assert.Equal(LocalAsrWorkerRunStatus.Failed, result.Status);
        Assert.Equal(LocalRuntimeReasonCodes.ContentBindingMismatch, result.ReasonCode);
        Assert.Equal(0, adapter.CallCount);
        Assert.Equal(0, transport.GetAcks().Count(ack =>
            ack.Kind == LocalAsrAckKind.SegmentSucceeded));
    }

    private LocalAsrWorkerEngine CreateEngine(
        FakeTransport transport,
        FakeRuntimeAdapter adapter,
        Func<ResourceSnapshot> snapshot,
        RuntimeBackoffStore? backoff = null,
        TimeSpan? renewInterval = null,
        long maxSegmentContentBytes = 1024 * 1024,
        long maxTotalContentBytes = 1024 * 1024,
        LocalAsrWorkerStateStore? stateStoreOverride = null,
        LocalAsrClaimDecisionAdapter? claimDecisionAdapter = null) =>
        new(
            transport,
            adapter,
            new LocalRuntimeAccountGuard(),
            new MockResourceSnapshotProvider(snapshot),
            backoff ?? new RuntimeBackoffStore(Path.Combine(_root, "backoff")),
            stateStoreOverride ?? CreateStateStore(),
            new LocalAsrWorkerOptions(
                "node-a",
                "Node A",
                CreateRuntimeManifest(),
                CreateModelManifest(),
                Path.Combine(_root, "staging"),
                MaxSegmentCount: 8,
                MaxSegmentContentBytes: maxSegmentContentBytes,
                MaxTotalContentBytes: maxTotalContentBytes,
                RenewInterval: renewInterval ?? TimeSpan.FromMinutes(5),
                AckTimeout: TimeSpan.FromSeconds(1),
                WithdrawTimeout: TimeSpan.FromSeconds(1)),
            claimDecisionAdapter: claimDecisionAdapter);

    private LocalAsrWorkerStateStore CreateStateStore() =>
        new(Path.Combine(_root, "state"));

    private static LocalAsrClaimedTask CreateClaim(
        IReadOnlyList<byte[]> segmentBytes,
        DateTimeOffset? leaseExpiresAtUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        var segments = new List<LocalAsrInputSegment>();
        long sourceOffset = 0;
        const long samplesPerByte = 16;
        foreach (var (bytes, index) in segmentBytes.Select((bytes, index) => (bytes, index)))
        {
            var samples = bytes.LongLength * samplesPerByte;
            segments.Add(new LocalAsrInputSegment(
                index,
                sourceOffset,
                samples,
                ExpectedContentBytes: bytes.LongLength,
                SourceSha256: Sha256(bytes)));
            sourceOffset = checked(sourceOffset + samples);
        }

        var lease = new LocalAsrLease(
            "lease-a",
            "opaque-lease-token",
            7,
            "attempt-a",
            "version-a",
            now,
            leaseExpiresAtUtc ?? now.AddMinutes(5),
            now);
        return new LocalAsrClaimedTask(
            "attempt-a",
            "task-a",
            "version-a",
            "en",
            "runtime-a",
            "model-a",
            "revision-a",
            Sha256("model"u8.ToArray()),
            new string('c', 64),
            sourceOffset,
            segmentBytes.Count,
            segments,
            lease);
    }

    private static LocalRuntimeManifest CreateRuntimeManifest() =>
        new(
            "runtime-a",
            "Runtime A",
            "helper.exe",
            "https://example.invalid/runtime.exe",
            "MIT",
            "https://example.invalid/LICENSE",
            "runtime-revision",
            Sha256("runtime"u8.ToArray()),
            7,
            LocalRuntimeArtifactKind.File,
            "helper.exe",
            new[]
            {
                new LocalRuntimeFileManifest(
                    "helper.exe",
                    7,
                    Sha256("runtime"u8.ToArray())),
            },
            new LocalRuntimeExecutionProfile(
                new[] { "--input", "{inputPath}" },
                TimeSpan.FromSeconds(30),
                LocalRuntimeExecutionProfile.PlainText));

    private static LocalModelManifest CreateModelManifest() =>
        new(
            "model-a",
            "Model A",
            "runtime-a",
            "MIT",
            "https://example.invalid/LICENSE",
            "https://example.invalid/model.bin",
            "revision-a",
            Sha256("model"u8.ToArray()),
            5,
            "model.bin",
            new LocalResourceRequirements(CpuCores: 1, MemoryBytes: 1024));

    private static Func<ResourceSnapshot> GoodSnapshot() =>
        () => new ResourceSnapshot(
            DateTimeOffset.UtcNow,
            LogicalProcessorCount: 8,
            CpuLoadPercent: 10,
            AvailableMemoryBytes: 1024L * 1024 * 1024,
            VramAvailableBytes: null,
            IsOnAcPower: true,
            BatteryPercent: 100);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class MutableResourceProvider
    {
        public MutableResourceProvider(ResourceSnapshot snapshot)
        {
            SnapshotValue = snapshot;
        }

        private ResourceSnapshot SnapshotValue { get; set; }

        public int ReadCount { get; private set; }

        public ResourceSnapshot Snapshot
        {
            get
            {
                ReadCount++;
                return SnapshotValue;
            }
            set => SnapshotValue = value;
        }
    }

    private sealed class DelegatingSnapshotProvider(
        Func<CancellationToken, Task<ResourceSnapshot>> handler) : IResourceSnapshotProvider
    {
        public Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            handler(cancellationToken);
    }

    private sealed class FakeRuntimeAdapter : ILocalSpeechRuntimeAdapter
    {
        private readonly Func<LocalSpeechExecutionRequest, CancellationToken, Task<LocalSpeechExecutionResult>> _handler;

        public FakeRuntimeAdapter(string text)
            : this((_, _) => Task.FromResult(Success(text)))
        {
        }

        public FakeRuntimeAdapter(
            Func<LocalSpeechExecutionRequest, CancellationToken, Task<LocalSpeechExecutionResult>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public Task<LocalSpeechExecutionResult> ExecuteAsync(
            LocalRuntimeManifest runtimeManifest,
            LocalModelManifest modelManifest,
            LocalSpeechExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _handler(request, cancellationToken);
        }

        private static LocalSpeechExecutionResult Success(string text) =>
            new(
                true,
                null,
                null,
                text,
                new[]
                {
                    new LocalSpeechTranscriptSegment(
                        TimeSpan.Zero,
                        TimeSpan.FromMilliseconds(500),
                        text),
                },
                null);
    }

    private sealed class FakeTransport : ILocalAsrNodeTransport
    {
        private readonly LocalAsrClaimedTask _task;
        private readonly object _sync = new();
        private readonly Dictionary<int, byte[]> _content;
        private readonly List<LocalAsrAckRequest> _acks = new();
        private readonly List<LocalAsrWithdrawRequest> _withdrawals = new();
        private readonly List<LocalAsrSegmentContent> _contentStreams = new();
        private readonly Dictionary<int, string> _contentSha256 = new();

        public FakeTransport(
            LocalAsrClaimedTask task,
            IReadOnlyList<byte[]> content)
        {
            _task = task;
            Assert.Equal(task.Segments.Count, content.Count);
            _content = task.Segments
                .Select((segment, index) => (segment.SegmentIndex, content[index]))
                .GroupBy(item => item.SegmentIndex)
                .ToDictionary(group => group.Key, group => group.First().Item2);
            _contentSha256 = _content.ToDictionary(
                item => item.Key,
                item => Sha256(item.Value));
        }

        public FakeTransport(LocalAsrClaimResult noWork)
        {
            NoWork = noWork;
            _task = null!;
            _content = new Dictionary<int, byte[]>();
        }

        public LocalAsrClaimResult? NoWork { get; }

        public string? ContentOriginOverride { get; set; }

        public string? ContentSha256Override { get; set; }

        public Func<LocalAsrLease, CancellationToken, Task<LocalAsrRenewResult>>? RenewHandler { get; set; }

        public int ClaimCount { get; private set; }

        public int ContentOpenCount { get; private set; }

        public Task<LocalAsrClaimResult> ClaimAsync(
            LocalAsrClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            ClaimCount++;
            return Task.FromResult(
                NoWork ?? new LocalAsrClaimResult(LocalAsrClaimStatus.Claimed, _task));
        }

        public Task<LocalAsrRenewResult> RenewAsync(
            LocalAsrLease lease,
            CancellationToken cancellationToken = default) =>
            RenewHandler?.Invoke(lease, cancellationToken)
            ?? Task.FromResult(new LocalAsrRenewResult(
                LocalAsrRenewStatus.Renewed,
                lease with
                {
                    ExpiresAtUtc = lease.ExpiresAtUtc.AddMinutes(5),
                    ServerNowUtc = DateTimeOffset.UtcNow,
                }));

        public Task<LocalAsrSegmentContent> OpenSegmentContentAsync(
            LocalAsrLease lease,
            LocalAsrInputSegment segment,
            CancellationToken cancellationToken = default)
        {
            ContentOpenCount++;
            var binding = new ApiContentResponseBinding(
                lease.AttemptId,
                lease.VersionId,
                lease.LeaseId,
                lease.FencingToken,
                segment.SegmentIndex,
                segment.SourceSha256 ?? string.Empty,
                segment.SourceOffsetSamples,
                segment.Samples,
                ContentSha256Override ?? _contentSha256[segment.SegmentIndex]);
            _contentStreams.Add(new LocalAsrSegmentContent(
                new MemoryStream(
                    _content[segment.SegmentIndex],
                    writable: false),
                ContentOriginOverride ?? "https://api.example.test",
                "account-a",
                "node-a",
                binding));
            return Task.FromResult(_contentStreams[^1]);
        }

        public Task<LocalAsrAckResult> AcknowledgeAsync(
            LocalAsrLease lease,
            LocalAsrAckRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _acks.Add(request);
            }

            return Task.FromResult(new LocalAsrAckResult(LocalAsrAckStatus.Accepted));
        }

        public Task<LocalAsrWithdrawResult> WithdrawAsync(
            LocalAsrWithdrawRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _withdrawals.Add(request);
            }

            return Task.FromResult(new LocalAsrWithdrawResult(LocalAsrWithdrawStatus.Withdrawn));
        }

        public IReadOnlyList<LocalAsrAckRequest> GetAcks()
        {
            lock (_sync)
            {
                return _acks.ToArray();
            }
        }

        public IReadOnlyList<LocalAsrWithdrawRequest> GetWithdrawals()
        {
            lock (_sync)
            {
                return _withdrawals.ToArray();
            }
        }
    }
}
