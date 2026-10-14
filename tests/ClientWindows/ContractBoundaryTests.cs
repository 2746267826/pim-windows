using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 接口边界契约化（工单：pim-windows 接口边界契约化 / issue #14）的结构性守卫。
///
/// 这些用例回答的是工单的核心诉求「客户端开发时不要违背 API 的设定乱做」：
/// <list type="number">
///   <item><description>HTTP 边界不得再暴露「裸字符串 endpoint + object body」的公共面 ——
///     那是 AI 随手编一个路径就能编译通过的入口；</description></item>
///   <item><description>公共 HTTP 面的每个方法都必须在本仓的 operation 清单里声明，
///     而清单里的每条 operation 又由生成器对照契约逐个校验存在性 ——
///     于是「编造一个契约里没有的端点」在这条链上无处落脚；</description></item>
///   <item><description>边界代码里不得再出现硬编码的接口路径字面量：路径只能来自生成物，
///     生成物又由 CI 逐字节对照契约快照复核。</description></item>
/// </list>
///
/// 注意：本文件里的断言全部是**结构性**证据（源码形态 + 生成物哈希），
/// 它证明的是「边界形状受约束」，不能替代真实 HTTP 交互的行为证据 ——
/// 行为证据在 <see cref="ContractBoundaryBehaviorTests"/>。
/// </summary>
public class ContractBoundaryTests
{
    /// <summary>operation 清单：客户端实际使用的接口，生成器/门禁都读它。</summary>
    private const string OperationsRelativePath = "scripts/contract/client-operations.json";

    /// <summary>生成物：契约 DTO 与 operation 定义（均由 scripts/contract/generate-csharp.py 产出）。</summary>
    private static readonly string[] GeneratedArtifacts =
    {
        "src/client-windows/Pim.Client.Core/Contract/ContractModels.cs",
        "src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs",
    };

    [Fact]
    public void ApiClient_ExposesNoRawEndpointOrObjectBodySurface()
    {
        var offenders = typeof(ApiClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters().Select(p => (method, parameter: p)))
            .Where(x => IsRawEndpointParameter(x.parameter) || IsUnconstrainedBody(x.parameter))
            .Select(x => $"{x.method.Name}({x.parameter.ParameterType.Name} {x.parameter.Name})")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "ApiClient 不得再暴露「裸字符串 endpoint / object body」的公共 HTTP 面（这些入口让编造的路径与字段在编译期无从拦截）："
                + string.Join(", ", offenders));
    }

    [Fact]
    public void ApiClient_PublicHttpSurfaceMatchesDeclaredOperations()
    {
        var declared = DeclaredOperationClientMethods();
        var actual = typeof(ApiClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType))
            .Select(m => m.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(declared, actual);
    }

    [Fact]
    public void ApiClient_ContainsNoHardcodedContractPathLiterals()
    {
        var source = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.Core", "Services", "ApiClient.cs"));

        // 只允许基础地址前缀；任何接口路径都必须来自生成物（ContractOperations）。
        var allowed = new[] { "/api/v1/" };
        var literals = StringLiterals(source)
            .Where(literal => literal.StartsWith("/", StringComparison.Ordinal))
            .Where(literal => !allowed.Contains(literal, StringComparer.Ordinal))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            literals.Count == 0,
            "ApiClient.cs 里不得出现硬编码接口路径字面量（路径必须来自契约生成物）：" + string.Join(", ", literals));
    }

    [Fact]
    public void GeneratedArtifacts_ExistAndEmbedTheContractSnapshotHash()
    {
        var snapshotHash = Sha256(RepoPath("contract", "openapi.json"));

        foreach (var relative in GeneratedArtifacts)
        {
            var path = RepoPath(relative.Split('/'));
            var content = File.ReadAllText(path);

            Assert.Contains("GENERATED FILE", content);
            Assert.Contains("请勿手工修改", content);
            Assert.Contains(snapshotHash, content);
        }
    }

    // ---- helpers ------------------------------------------------------------------

    private static bool IsRawEndpointParameter(ParameterInfo parameter)
    {
        if (!typeof(Task).IsAssignableFrom(parameter.Member is MethodInfo m ? m.ReturnType : typeof(void)))
        {
            return false;
        }

        if (parameter.ParameterType != typeof(string))
        {
            return false;
        }

        return parameter.Name is "endpoint" or "path" or "url" or "uri";
    }

    private static bool IsUnconstrainedBody(ParameterInfo parameter)
        => parameter.ParameterType == typeof(object);

    private static List<string> DeclaredOperationClientMethods()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPath(OperationsRelativePath.Split('/'))));
        return document.RootElement.GetProperty("operations")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("apiClient").GetString()!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> StringLiterals(string source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != '"')
            {
                continue;
            }

            var j = i + 1;
            while (j < source.Length && source[j] != '"')
            {
                j += source[j] == '\\' ? 2 : 1;
            }

            if (j >= source.Length)
            {
                yield break;
            }

            yield return source[(i + 1)..j];
            i = j;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}

/// <summary>
/// 接口边界契约化的**行为**证据：真实装配 <see cref="ApiClient"/>（桩传输层拦截请求），
/// 逐个 operation 断言「实际发出的 HTTP 方法 + 路径 + JSON 字段」与 <c>contract/openapi.json</c> 一致。
///
/// 与 <see cref="ContractBoundaryTests"/> 的分工：那边证明「边界的形状受约束」（源码/反射/哈希），
/// 这边证明「边界真的按契约收发」——请求体字段集合与契约 schema 的属性集合**逐个相等**，
/// 契约 required 的字段一个不少，契约里没有的字段一个不多。
/// </summary>
public class ContractBoundaryBehaviorTests
{
    /// <summary>契约里 required 的字段一个都不能少。</summary>
    [Fact]
    public async Task EveryOperation_SendsContractMethodPathAndFieldSet()
    {
        var contract = JsonDocument.Parse(File.ReadAllText(RepoPath("contract", "openapi.json")));
        var cases = OperationCases();

        // 13 个 operation 全部覆盖：少一个就说明边界与清单脱节。
        var declared = DeclaredOperations();
        Assert.Equal(
            declared.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            cases.Select(c => $"{c.Method} {c.Path}").OrderBy(x => x, StringComparer.Ordinal).ToList());

        foreach (var testCase in cases)
        {
            var handler = new RecordingHandler(testCase.ResponseBody);
            var api = new ApiClient(() => handler);

            await testCase.Invoke(api, testCase.Request);

            var sent = Assert.Single(handler.Requests);
            Assert.Equal(testCase.Method, sent.Method);
            // 带路由参数的 operation：契约里是模板，实际请求是填好并转义后的路径。
            Assert.Equal(testCase.Path.Replace("{deviceId}", "PC-01"), sent.Path);

            if (testCase.Request is null)
            {
                Assert.Null(sent.Body);
                continue;
            }

            var (schemaProperties, required) = RequestSchema(contract, testCase.Method, testCase.Path);
            var sentFields = JsonDocument.Parse(sent.Body!)
                .RootElement.EnumerateObject()
                .Select(p => p.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(schemaProperties.OrderBy(x => x, StringComparer.Ordinal).ToList(), sentFields);
            Assert.Empty(required.Except(sentFields));
        }
    }

    [Fact]
    public async Task RouteParameter_IsEscapedIntoTheContractPath()
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);

        await api.GetEndpointCollectionQualityAsync("PC 01/../etc");

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api/v1/endpoints/PC%2001%2F..%2Fetc/collection-quality", sent.Path);
    }

    [Fact]
    public void RouteValues_MustMatchTheContractTemplateExactly()
    {
        var operation = ContractOperation.GetEndpointCollectionQuality;

        Assert.Equal("/api/v1/endpoints/{deviceId}/collection-quality", operation.PathTemplate);
        Assert.Equal(new[] { "deviceId" }, operation.RouteParameters);

        // 写错名字 / 少传 / 多传都必须报错，而不是拼出一个看起来对的路径。
        Assert.Throws<ArgumentException>(() => operation.Resolve(("deviceID", "d1")));
        Assert.Throws<ArgumentException>(() => operation.Resolve());
        Assert.Throws<ArgumentException>(() => operation.Resolve(("deviceId", "d1"), ("other", "x")));
        Assert.Throws<ArgumentException>(() => operation.Resolve(("deviceId", "d1"), ("deviceId", "d2")));
        Assert.Equal("endpoints/d1/collection-quality", operation.Resolve(("deviceId", "d1")));
    }

    [Fact]
    public async Task SiteEvents_DoNotSendFieldsAbsentFromTheContract()
    {
        // 本机桥接类型 SiteEventDto 带一个契约里没有的 at；上线前必须被挡在边界之外。
        var handler = new RecordingHandler(IntEnvelopeBody);
        var api = new ApiClient(() => handler);

        await api.UploadSiteEventsAsync(new SiteEventsUploadRequest
        {
            DeviceId = "PC-01",
            Events = new List<SiteEventUploadDto> { new() { Kind = "focus", Host = "github.com" } },
        });

        var body = Assert.Single(handler.Requests).Body!;
        Assert.DoesNotContain("\"at\"", body);
        Assert.Contains("\"kind\"", body);
        Assert.Contains("\"host\"", body);
    }

    [Fact]
    public async Task KeystatsUpload_UsesTheContractFieldNamesForThatPath()
    {
        // pim-api#419 之前这两条路径的 peak 字段拼写不一致（samples 用 peakKPS/peakCPS）；
        // 现在已统一为 camelCase，两条路径都必须是 peakKps/peakCps。
        // 客户端不自己发明大小写 —— 生成物照搬契约，这里逐字核对发出去的 body。
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);

        await api.UploadKeystatsSnapshotAsync(SnapshotRequest());
        await api.UploadKeystatsSampleAsync(SampleRequest());

        Assert.Equal(2, handler.Requests.Count);
        var legacyBody = handler.Requests[0].Body!;
        var sampleBody = handler.Requests[1].Body!;

        Assert.Contains("\"peakKps\"", legacyBody);
        Assert.Contains("\"peakCps\"", legacyBody);
        Assert.DoesNotContain("\"peakKPS\"", legacyBody);
        Assert.DoesNotContain("\"peakCPS\"", legacyBody);
        Assert.DoesNotContain("formattedMouseDistance", legacyBody);

        Assert.Contains("\"peakKps\"", sampleBody);
        Assert.Contains("\"peakCps\"", sampleBody);
        Assert.DoesNotContain("\"peakKPS\"", sampleBody);
        Assert.Contains("formattedMouseDistance", sampleBody);
    }

    /// <summary>
    /// 契约里被生成的每个字符串枚举：每个取值都必须与生成的 C# 枚举**逐字往返**。
    ///
    /// 这条挡的是「成员名 ≠ wire 值」这类静默线格式变化：`JsonStringEnumConverter` 上线写的是
    /// 成员名，任何 PascalCase 化（例如契约值 `low` → 成员 `Low`）都会改掉线上字节。
    /// </summary>
    [Fact]
    public void EveryGeneratedEnumRoundTripsEveryContractValue()
    {
        var assembly = typeof(ApiClient).Assembly;
        using var contract = JsonDocument.Parse(File.ReadAllText(RepoPath("contract", "openapi.json")));

        var checkedEnums = 0;
        var checkedValues = 0;
        foreach (var schema in contract.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("enum", out var enumValues)) continue;
            if (schema.Value.TryGetProperty("type", out var typeNode) && typeNode.GetString() != "string") continue;

            var generated = assembly.GetType($"Pim.Client.Core.Contract.{schema.Name}");
            if (generated is null || !generated.IsEnum) continue; // 不在本次 operation 闭包里 → 没有生成

            checkedEnums++;
            foreach (var value in enumValues.EnumerateArray())
            {
                var wire = value.GetString()!;
                var parsed = JsonSerializer.Deserialize($"\"{wire}\"", generated);
                Assert.NotNull(parsed);
                // 上线必须是与契约取值逐字相同的 JSON 字符串（不是数字、不是大小写变形）。
                Assert.Equal(JsonSerializer.Serialize(wire), JsonSerializer.Serialize(parsed, generated));
                Assert.Equal(wire, parsed!.ToString());
                checkedValues++;
            }
        }

        Assert.True(checkedEnums > 0 && checkedValues > 0, "没有任何被生成的字符串枚举参与校验");
    }

    /// <summary>
    /// 事件里的 `rawJson` 是无类型 JSON：嵌套对象的键名必须与改造前一致（Web 默认 camelCase）。
    /// 改造前它由 `PostAsJsonAsync`（Web 选项）序列化；现在提前用同一套选项固化，
    /// 换成默认 options 会把 `sessionId` 变成 `SessionId` —— 那是未声明的线格式变化。
    /// </summary>
    [Fact]
    public void TrackerEventRawJson_KeepsCamelCaseForNestedPageVisit()
    {
        var logger = new TrackerLogger(1);
        var statePath = Path.Combine(Path.GetTempPath(), "pim-rawjson-state-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

        using var tracker = new NativeTrackerService(
            new ApiClient(() => new RecordingHandler()),
            new TrackerConfig { Enabled = true },
            windowResolver: new AlwaysNullWindowResolver(),
            idleDetector: new AlwaysZeroIdleDetector(),
            bridge: new BrowserBridgeService(15699, logger),
            logger: logger,
            stateManager: new TrackerStateManager(statePath));

        var session = new TrackerSession
        {
            Id = 42,
            AppName = "chrome",
            ExePath = @"C:\chrome.exe",
            StartedAt = start,
            EndedAt = start.AddSeconds(60),
            IsIdle = false,
            PageVisits = new List<TrackerPageVisit>
            {
                new()
                {
                    Id = 7,
                    SessionId = 42,
                    Url = "https://github.com/",
                    Domain = "github.com",
                    StartedAt = start,
                    EndedAt = start.AddSeconds(30),
                    DurationSecs = 30,
                },
            },
        };

        var events = tracker.SessionToEvents(session, start.AddSeconds(60));
        var pageEvent = events.Single(e => e.EventType == "web-page");
        var json = JsonSerializer.Serialize(pageEvent.RawJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // 外层与嵌套都必须是 camelCase（嵌套 pageVisit 是 TrackerPageVisit 的内部形状）。
        Assert.Contains("\"sessionId\":42", json);
        Assert.Contains("\"pageVisit\":{", json);
        Assert.Contains("\"id\":7", json);
        Assert.Contains("\"startedAt\"", json);
        Assert.DoesNotContain("\"SessionId\"", json);
        Assert.DoesNotContain("\"StartedAt\"", json);
    }

    /// <summary>
    /// 匿名 operation（契约 `security: null`）不得被本地令牌状态牵连：令牌临期时也不触发续期、
    /// 续期失败也不影响它发出（首轮 review 的 Important）。
    /// </summary>
    [Fact]
    public async Task AnonymousShellLatest_IsNotBlockedByTokenRenewal()
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);
        var renewals = 0;
        api.RenewTokenAsync = () =>
        {
            renewals++;
            return Task.FromResult(false); // 续期会失败：若走了续期这条路，请求就发不出去
        };
        api.IsTokenRenewalDue = () => true;

        await api.GetClientShellLatestAsync();

        Assert.Equal(0, renewals);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("/api/client/shell/latest", sent.Path);
    }

    /// <summary>对照组：业务 operation（契约 `bearerAuth`）在令牌临期时**仍然**先续期再发。</summary>
    [Fact]
    public async Task BusinessOperation_StillRenewsBeforeSendingWhenDue()
    {
        var handler = new RecordingHandler(IntEnvelopeBody);
        var api = new ApiClient(() => handler);
        var renewals = 0;
        api.RenewTokenAsync = () =>
        {
            renewals++;
            return Task.FromResult(true);
        };
        api.IsTokenRenewalDue = () => true;

        await api.UploadTrackerEventsAsync(new TrackerEventsUploadRequest
        {
            DeviceId = "PC-01",
            Events = new List<TrackerEventDto>(),
        });

        Assert.Equal(1, renewals);
        Assert.Single(handler.Requests);
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>契约 required 的字段在编译期就必须给全（漏一个 = CS9035）。</summary>
    private static KeystatsSampleUploadRequest SampleRequest() => new()
    {
        PimDeviceId = "PC-01",
        SampledAt = "2026-10-14T00:00:00Z",
        Date = "2026-10-14",
        KeyPresses = 1,
        LeftClicks = 1,
        RightClicks = 1,
        MiddleClicks = 1,
        SideBackClicks = 0,
        SideForwardClicks = 0,
        MouseDistance = 1,
        ScrollDistance = 1,
        PeakKps = 1,
        PeakCps = 1,
    };

    /// <summary>同上；注意这两条路径的 peak 字段现在都是小写 peakKps/peakCps。</summary>
    private static KeystatsUploadRequest SnapshotRequest() => new()
    {
        DeviceId = "PC-01",
        Date = "2026-10-14",
        KeyPresses = 1,
        LeftClicks = 1,
        RightClicks = 1,
        MiddleClicks = 1,
        SideBackClicks = 0,
        SideForwardClicks = 0,
        MouseDistance = 1,
        ScrollDistance = 1,
        PeakKps = 1,
        PeakCps = 1,
    };

    /// <summary>契约要求的信封必填字段（code/message/timestamp）；data 在多数信封里可空。</summary>
    private const string EnvelopeBody =
        "{\"code\":0,\"message\":\"\",\"timestamp\":\"2026-10-14T00:00:00Z\"}";

    /// <summary>
    /// <c>Int32ApiResponse</c> 的 <c>data</c> 在契约里是**非可空整数**（服务端 200 路径总是带回真实计数），
    /// 所以桩响应必须带上它，否则严格反序列化会判缺字段。
    /// </summary>
    private const string IntEnvelopeBody =
        "{\"code\":0,\"data\":0,\"message\":\"\",\"timestamp\":\"2026-10-14T00:00:00Z\"}";

    private sealed record OperationCase(
        string Method,
        string Path,
        object? Request,
        Func<ApiClient, object?, Task> Invoke,
        string ResponseBody = EnvelopeBody);

    private static List<OperationCase> OperationCases() => new()
    {
        new("POST", "/api/v1/auth/login",
            new LoginRequest { Username = "u", Password = "p" },
            (api, req) => api.LoginAsync((LoginRequest)req!)),
        new("POST", "/api/v1/auth/register",
            new RegisterRequest { Username = "u", Email = "u@example.com", Password = "password1", DisplayName = "U" },
            (api, req) => api.RegisterAsync((RegisterRequest)req!)),
        new("POST", "/api/v1/auth/refresh",
            new RefreshRequest { RefreshToken = "r" },
            (api, req) => api.RefreshTokenAsync((RefreshRequest)req!)),
        new("POST", "/api/v1/daemon/heartbeat",
            new DaemonHeartbeatRequest
            {
                DeviceId = "PC-01",
                DaemonKind = "windows",
                Version = "1.0.0",
                ServerUrl = "http://127.0.0.1:5858",
                ActivityWatchState = DaemonSourceState.Available,
                KeyStatsState = DaemonSourceState.Unknown,
                CollectionPaused = false,
                StatusJson = "{}",
            },
            (api, req) => api.ReportDaemonHeartbeatAsync((DaemonHeartbeatRequest)req!)),
        new("POST", "/api/v1/daemon/planned-offline",
            new PlannedOfflineRequest { DeviceId = "PC-01", DaemonKind = "windows", Reason = "suspend" },
            (api, req) => api.ReportPlannedOfflineAsync((PlannedOfflineRequest)req!)),
        new("GET", "/api/v1/endpoints", null,
            (api, _) => api.GetEndpointStatusesAsync()),
        new("GET", "/api/v1/endpoints/{deviceId}/collection-quality", null,
            (api, _) => api.GetEndpointCollectionQualityAsync("PC-01")),
        new("POST", "/api/v1/endpoints/{deviceId}/notification-actions",
            new EndpointNotificationActionRequest { Action = "open", RiskLevel = "low" },
            (api, req) => api.SendEndpointNotificationActionAsync("PC-01", (EndpointNotificationActionRequest)req!)),
        new("POST", "/api/v1/pc/tracker/upload",
            new TrackerEventsUploadRequest
            {
                DeviceId = "PC-01",
                Events = new List<TrackerEventDto>
                {
                    new()
                    {
                        Timestamp = "2026-10-14T00:00:00Z",
                        Duration = 1,
                        EventType = "window",
                        IsIdle = false,
                        IsMediaActive = false,
                        PageVisitCount = 0,
                        PageVisitDuration = 0,
                        Date = "2026-10-14",
                    },
                },
            },
            (api, req) => api.UploadTrackerEventsAsync((TrackerEventsUploadRequest)req!),
            IntEnvelopeBody),
        new("POST", "/api/v1/pc/browser-tt/upload",
            new SiteEventsUploadRequest
            {
                DeviceId = "PC-01",
                Events = new List<SiteEventUploadDto> { new() { Kind = "focus", Host = "github.com" } },
            },
            (api, req) => api.UploadSiteEventsAsync((SiteEventsUploadRequest)req!),
            IntEnvelopeBody),
        new("POST", "/api/v1/pc/tracker/health",
            new TrackerHealthRequest
            {
                DeviceId = "PC-01",
                Status = "running",
                UptimeSeconds = 1,
                HookActive = true,
                PollCount = 1,
                SessionsCreated = 1,
                EventsUploaded = 1,
                UploadFailures = 0,
                BrowserConnected = false,
            },
            (api, req) => api.ReportTrackerHealthAsync((TrackerHealthRequest)req!)),
        new("POST", "/api/v1/pc/keystats/samples",
            SampleRequest(),
            (api, req) => api.UploadKeystatsSampleAsync((KeystatsSampleUploadRequest)req!)),
        new("POST", "/api/v1/pc/keystats/upload",
            SnapshotRequest(),
            (api, req) => api.UploadKeystatsSnapshotAsync((KeystatsUploadRequest)req!)),
        // 契约基址 /api/v1 之外的 host 根路径：守护程序「检查更新」用它。
        new("GET", "/api/client/shell/latest", null,
            (api, _) => api.GetClientShellLatestAsync()),
    };

    private static List<string> DeclaredOperations()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPath("scripts", "contract", "client-operations.json")));
        return document.RootElement.GetProperty("operations")
            .EnumerateArray()
            .Select(entry => $"{entry.GetProperty("method").GetString()} {entry.GetProperty("path").GetString()}")
            .ToList();
    }

    /// <summary>从契约里取某个 operation 的请求体 schema：属性名集合 + required 集合。</summary>
    private static (List<string> Properties, List<string> Required) RequestSchema(
        JsonDocument contract, string method, string path)
    {
        var schema = contract.RootElement
            .GetProperty("paths").GetProperty(path).GetProperty(method.ToLowerInvariant())
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema");

        var name = schema.GetProperty("$ref").GetString()!.Split('/')[^1];
        var schemaNode = contract.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);

        var properties = schemaNode.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        var required = schemaNode.TryGetProperty("required", out var requiredNode)
            ? requiredNode.EnumerateArray().Select(x => x.GetString()!).ToList()
            : new List<string>();
        return (properties, required);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }


    private sealed class AlwaysNullWindowResolver : IWindowResolver
    {
        public TrackerWindowInfo? GetForegroundWindowInfo() => null;
    }

    private sealed class AlwaysZeroIdleDetector : IIdleDetector
    {
        public TimeSpan GetIdleDuration() => TimeSpan.Zero;
        public bool IsScreenOff() => false;
    }

    private sealed class RecordingHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly string _responseBody;

        public RecordingHandler(string responseBody = EnvelopeBody) => _responseBody = responseBody;

        public List<(string Method, string Path, string? Body)> Requests { get; } = new();

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));

            // 契约给信封标了必填字段（code/message/timestamp），所以桩响应必须带上它们 ——
            // 这才是服务端真实会发出的形状。个别 operation 的 data 非可空，由调用方传入专用 body。
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    _responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
