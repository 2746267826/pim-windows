using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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

    private static readonly string[] AllowedBaseAddressLiterals =
    {
        "/",
        "/api/v1",
        "/api/v1/",
    };

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
        var literals = FindHardcodedContractPathLiterals(source);

        Assert.True(
            literals.Count == 0,
            "ApiClient.cs 里不得出现硬编码接口路径字面量（路径必须来自契约生成物）：" + string.Join(", ", literals));
    }

    [Fact]
    public void HardcodedPathGuard_RejectsARealOperationLiteral()
    {
        const string source =
            "var path = \"/api/v1/native-notes/asr-routing/leases/claim\";";

        Assert.Contains(
            "/api/v1/native-notes/asr-routing/leases/claim",
            FindHardcodedContractPathLiterals(source));
    }

    [Fact]
    public void GeneratedArtifacts_ExistAndEmbedTheContractSnapshotHash()
    {
        var snapshotHash = CanonicalSha256(RepoPath("contract", "openapi.json"));

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

    private static List<string> FindHardcodedContractPathLiterals(string source)
    {
        // Base-address construction is not an operation path. Only these exact
        // literals are exempt; a concrete contract path must still come from
        // ContractOperations.
        return StringLiterals(source)
            .Where(literal => literal.StartsWith("/", StringComparison.Ordinal))
            .Where(literal => !AllowedBaseAddressLiterals.Contains(literal, StringComparer.Ordinal))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The generator hashes the contract after normalizing CRLF to LF.
    /// Normalize only line endings here so Windows working trees verify the same
    /// canonical bytes without touching generated files.
    /// </summary>
    private static string CanonicalSha256(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var normalized = new byte[bytes.Length];
        var length = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\r'
                && index + 1 < bytes.Length
                && bytes[index + 1] == (byte)'\n')
            {
                continue;
            }

            normalized[length++] = bytes[index];
        }

        return Convert.ToHexString(SHA256.HashData(normalized.AsSpan(0, length))).ToLowerInvariant();
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

        // 清单里的 operation 全部覆盖：少一个就说明边界与清单脱节。
        var declared = DeclaredOperations();
        Assert.Equal(
            declared.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            cases.Select(c => $"{c.Method} {c.Path}").OrderBy(x => x, StringComparer.Ordinal).ToList());

        foreach (var testCase in cases)
        {
            var handler = new RecordingHandler(
                testCase.ResponseBody,
                testCase.ResponseFactory);
            var api = new ApiClient(() => handler);

            await testCase.Invoke(api, testCase.Request);

            var sent = Assert.Single(handler.Requests);
            Assert.Equal(testCase.Method, sent.Method);
            // 带路由参数的 operation：契约里是模板，实际请求是填好并转义后的路径。
            Assert.Equal(
                testCase.ExpectedPath
                    ?? testCase.Path.Replace("{deviceId}", "PC-01"),
                sent.Path);
            Assert.Equal(testCase.ExpectedQuery ?? string.Empty, sent.Query);

            if (testCase.BinaryBody)
            {
                Assert.Equal("0123456789abcdef", sent.Body);
                continue;
            }

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
    public void QueryParameters_AreRequiredTypedAndEscaped()
    {
        var taskSuggestions = ContractOperation.GetNoteTaskSuggestions;
        var taskStatus = Assert.Single(
            taskSuggestions.QueryParameters, parameter => parameter.Name == "status");
        Assert.Equal("enum", taskStatus.Kind);
        Assert.Equal(new[] { "Draft", "Confirmed", "Dismissed" }, taskStatus.AllowedValues);
        Assert.Equal("?status=Draft", taskSuggestions.ResolveQuery(("status", "Draft")));
        Assert.Equal("?status=Confirmed", taskSuggestions.ResolveQuery(("status", "Confirmed")));
        Assert.Equal("?status=Dismissed", taskSuggestions.ResolveQuery(("status", "Dismissed")));
        Assert.Throws<ArgumentException>(
            () => taskSuggestions.ResolveQuery(("status", "draft")));
        Assert.Throws<ArgumentException>(
            () => taskSuggestions.ResolveQuery(("status", "Confirmed&admin=true")));

        var artifacts = ContractOperation.GetNoteDerivedArtifacts;
        Assert.Equal(
            new[] { new ContractQueryParameter("noteId", "uuid", true) },
            artifacts.QueryParameters);
        Assert.Throws<ArgumentException>(() => artifacts.ResolveQuery());
        Assert.Throws<ArgumentException>(() => artifacts.ResolveQuery(("noteId", "not-a-uuid")));
        Assert.Throws<ArgumentException>(() => artifacts.ResolveQuery(("other", "x")));
        Assert.Throws<ArgumentException>(() => artifacts.ResolveQuery(
            ("noteId", "11111111-1111-1111-1111-111111111111"),
            ("noteId", "22222222-2222-2222-2222-222222222222")));
        Assert.Equal(
            "?noteId=11111111-1111-1111-1111-111111111111",
            artifacts.ResolveQuery(("noteId", "11111111-1111-1111-1111-111111111111")));

        var sources = ContractOperation.GetNoteDerivedSources;
        Assert.Equal(
            "?offset=0&limit=20",
            sources.ResolveQuery(("limit", "20"), ("offset", "0")));
        Assert.Equal(string.Empty, sources.ResolveQuery());
        Assert.Throws<ArgumentException>(() => sources.ResolveQuery(("offset", "-1")));
        Assert.Equal(
            "?sha256=a%20b%2F%3F%26",
            ContractOperation.UploadNoteMaterialPart.ResolveQuery(("sha256", "a b/?&")));

        var events = ContractOperation.GetCalendarEvents;
        Assert.Equal(
            "?start=2026-11-01T08%3A30%3A00.0000000%2B00%3A00&end=2026-11-02T08%3A30%3A00.0000000%2B00%3A00&page=1&pageSize=50",
            events.ResolveQuery(
                ("start", "2026-11-01T08:30:00.0000000+00:00"),
                ("end", "2026-11-02T08:30:00.0000000+00:00"),
                ("page", "1"),
                ("pageSize", "50")));
        Assert.Throws<ArgumentException>(() => events.ResolveQuery(("start", "tomorrow")));
        Assert.Throws<ArgumentException>(() => ContractOperation.GetTasks.ResolveQuery(("inbox", "yes")));
    }

    [Fact]
    public async Task CalendarAndTaskOperationsSendContractQueriesAndWireStatuses()
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);
        var from = DateTimeOffset.Parse("2026-11-01T08:30:00Z");

        await api.GetCalendarsAsync(kind: "PERSONAL");
        await api.GetCalendarEventsAsync(
            start: from,
            end: from.AddDays(1),
            search: "规划 & 会议",
            calendarId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            page: 2,
            pageSize: 40);
        await api.GetTaskBooksAsync();
        await api.GetTasksAsync(
            inbox: true,
            search: "Windows",
            calendarId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            status: "NEEDS-ACTION",
            priority: 3,
            plannedFrom: from,
            page: 1,
            pageSize: 25);

        var operations = handler.Requests;
        Assert.Equal(4, operations.Count);
        Assert.Equal("/api/v1/calendar/calendars", operations[0].Path);
        Assert.Equal("?kind=PERSONAL", operations[0].Query);
        Assert.Equal(
            "?start=2026-11-01T08%3A30%3A00.0000000%2B00%3A00&end=2026-11-02T08%3A30%3A00.0000000%2B00%3A00&search=%E8%A7%84%E5%88%92%20%26%20%E4%BC%9A%E8%AE%AE&calendarId=11111111-1111-1111-1111-111111111111&page=2&pageSize=40",
            operations[1].Query);
        Assert.Equal("/api/v1/calendar/task-books", operations[2].Path);
        Assert.Contains("inbox=true", operations[3].Query, StringComparison.Ordinal);
        Assert.Contains("plannedFrom=2026-11-01T08%3A30%3A00.0000000%2B00%3A00", operations[3].Query, StringComparison.Ordinal);
        Assert.Contains("status=NEEDS-ACTION", operations[3].Query, StringComparison.Ordinal);

        Assert.Equal(
            "\"NEEDS-ACTION\"",
            JsonSerializer.Serialize(CreateTaskRequestStatus.NEEDS_ACTION));
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
        string ResponseBody = EnvelopeBody,
        Func<HttpResponseMessage>? ResponseFactory = null,
        string? ExpectedPath = null,
        string? ExpectedQuery = null,
        bool BinaryBody = false);

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
        new("GET", "/api/v1/calendar/calendars", null,
            (api, _) => api.GetCalendarsAsync(kind: "PERSONAL"),
            ExpectedQuery: "?kind=PERSONAL"),
        new("GET", "/api/v1/calendar/events", null,
            (api, _) => api.GetCalendarEventsAsync(
                start: DateTimeOffset.Parse("2026-11-01T08:30:00Z"),
                end: DateTimeOffset.Parse("2026-11-02T08:30:00Z"),
                search: "规划 & 会议",
                calendarId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                page: 2,
                pageSize: 40),
            ExpectedQuery: "?start=2026-11-01T08%3A30%3A00.0000000%2B00%3A00&end=2026-11-02T08%3A30%3A00.0000000%2B00%3A00&search=%E8%A7%84%E5%88%92%20%26%20%E4%BC%9A%E8%AE%AE&calendarId=11111111-1111-1111-1111-111111111111&page=2&pageSize=40"),
        new("GET", "/api/v1/calendar/task-books", null,
            (api, _) => api.GetTaskBooksAsync()),
        new("GET", "/api/v1/calendar/tasks", null,
            (api, _) => api.GetTasksAsync(
                inbox: true,
                search: "Windows",
                calendarId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                status: "NEEDS-ACTION",
                priority: 3,
                plannedFrom: DateTimeOffset.Parse("2026-11-01T08:30:00Z"),
                page: 1,
                pageSize: 25),
            ExpectedQuery: "?inbox=true&search=Windows&calendarId=11111111-1111-1111-1111-111111111111&status=NEEDS-ACTION&priority=3&plannedFrom=2026-11-01T08%3A30%3A00.0000000%2B00%3A00&page=1&pageSize=25"),
        new("POST", "/api/v1/calendar/events",
            new CreateEventRequest
            {
                CalendarId = "11111111-1111-1111-1111-111111111111",
                DtStart = DateTimeOffset.Parse("2026-11-01T09:00:00Z"),
                DtEnd = DateTimeOffset.Parse("2026-11-01T10:00:00Z"),
                IsAllDay = false,
                Title = "设计评审",
            },
            (api, req) => api.CreateCalendarEventAsync((CreateEventRequest)req!)),
        new("POST", "/api/v1/calendar/tasks",
            new CreateTaskRequest
            {
                Priority = 3,
                Status = CreateTaskRequestStatus.NEEDS_ACTION,
                Title = "回归检查",
            },
            (api, req) => api.CreateTaskAsync((CreateTaskRequest)req!)),
        new("PUT", "/api/v1/calendar/tasks/{id}",
            new UpdateTaskRequest
            {
                Status = UpdateTaskRequestStatus.COMPLETED,
                PercentComplete = 100,
            },
            (api, req) => api.UpdateTaskAsync(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                (UpdateTaskRequest)req!),
            ExpectedPath: "/api/v1/calendar/tasks/33333333-3333-3333-3333-333333333333"),
        new("POST", "/api/v1/endpoints/{deviceId}/heartbeat",
            new EndpointHeartbeatRequest
            {
                Platform = "windows",
                AppVersion = "1.0.0",
                UploadStatus = "Healthy",
                CollectionCacheCount = 0,
            },
            (api, req) => api.ReportEndpointHeartbeatAsync("PC-01", (EndpointHeartbeatRequest)req!)),
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
        new("POST", "/api/v1/native-notes/asr-routing/nodes/register",
            new RegisterNoteAsrNodeRequest
            {
                Capabilities = new List<string> { "local-asr" },
                Enabled = true,
                ModelId = "model-a",
                ModelRevision = "revision-a",
                ModelVerified = true,
                NodeId = "22222222-2222-2222-2222-222222222222",
                NodeIdExternal = "PC-01",
                ResourceSnapshotJson = "{}",
                RuntimeId = "runtime-a",
                RuntimeVersion = "runtime-r1",
                SampledAt = DateTimeOffset.Parse("2026-10-14T00:00:00Z"),
            },
            (api, req) => api.RegisterNativeAsrNodeAsync((RegisterNoteAsrNodeRequest)req!)),
        new("POST", "/api/v1/native-notes/asr-routing/nodes/{nodeId}/heartbeat",
            new NoteAsrNodeHeartbeatRequest
            {
                Capabilities = new List<string> { "local-asr" },
                ModelId = "model-a",
                ModelRevision = "revision-a",
                ModelVerified = true,
                NodeId = "22222222-2222-2222-2222-222222222222",
                ResourceSnapshotJson = "{}",
                RuntimeId = "runtime-a",
                RuntimeVersion = "runtime-r1",
                SampledAt = DateTimeOffset.Parse("2026-10-14T00:00:00Z"),
            },
            (api, req) => api.HeartbeatNativeAsrNodeAsync(
                "22222222-2222-2222-2222-222222222222",
                (NoteAsrNodeHeartbeatRequest)req!),
            ExpectedPath: "/api/v1/native-notes/asr-routing/nodes/22222222-2222-2222-2222-222222222222/heartbeat"),
        new("POST", "/api/v1/native-notes/asr-routing/nodes/{nodeId}/withdraw",
            null,
            (api, _) => api.WithdrawNativeAsrNodeAsync(
                "22222222-2222-2222-2222-222222222222"),
            ExpectedPath: "/api/v1/native-notes/asr-routing/nodes/22222222-2222-2222-2222-222222222222/withdraw"),
        new("POST", "/api/v1/native-notes/asr-routing/leases/claim",
            new ClaimNoteAsrTaskRequest
            {
                IdempotencyKey = "claim-key",
                LeaseSeconds = 120,
                NodeId = "22222222-2222-2222-2222-222222222222",
            },
            (api, req) => api.ClaimNativeAsrLeaseAsync((ClaimNoteAsrTaskRequest)req!)),
        new("POST", "/api/v1/native-notes/asr-routing/leases/{leaseId}/renew",
            new NoteAsrLeaseHeartbeatRequest
            {
                FencingToken = 7,
                LeaseId = "33333333-3333-3333-3333-333333333333",
                LeaseToken = "44444444-4444-4444-4444-444444444444",
            },
            (api, req) => api.RenewNativeAsrLeaseAsync(
                "33333333-3333-3333-3333-333333333333",
                (NoteAsrLeaseHeartbeatRequest)req!),
            ExpectedPath: "/api/v1/native-notes/asr-routing/leases/33333333-3333-3333-3333-333333333333/renew"),
        new("GET", "/api/v1/native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content",
            null,
            async (api, _) =>
            {
                await using var response = await api.GetNativeAsrSegmentContentAsync(
                    "33333333-3333-3333-3333-333333333333",
                    2,
                    "44444444-4444-4444-4444-444444444444",
                    7,
                    1024);
                Assert.NotNull(response);
            },
            ResponseFactory: SuccessfulSegmentResponse,
            ExpectedPath: "/api/v1/native-notes/asr-routing/leases/33333333-3333-3333-3333-333333333333/segments/2/content"),
        new("POST", "/api/v1/native-notes/asr-routing/leases/{attemptId}/ack",
            new NoteAsrAckRequest
            {
                CoverageJson = "{}",
                ErrorCode = string.Empty,
                ErrorMessage = string.Empty,
                FencingToken = 7,
                Kind = "segment",
                LeaseId = "33333333-3333-3333-3333-333333333333",
                LeaseToken = "44444444-4444-4444-4444-444444444444",
                ProviderHandle =
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Result = "Succeeded",
                ResultText = "hello",
                SegmentIndex = 2,
            },
            (api, req) => api.AckNativeAsrAttemptAsync(
                "55555555-5555-5555-5555-555555555555",
                (NoteAsrAckRequest)req!),
            ExpectedPath: "/api/v1/native-notes/asr-routing/leases/55555555-5555-5555-5555-555555555555/ack"),
        new("POST", "/api/v1/native-notes/versions",
            new BeginNoteMaterialUploadRequest
            {
                BaseVersionId = null,
                InputJson = "{}",
                ManifestJson = "{}",
                NoteId = "11111111-1111-1111-1111-111111111111",
                StorageConsent = true,
                VersionId = "22222222-2222-2222-2222-222222222222",
            },
            (api, req) => api.BeginNoteMaterialUploadAsync((BeginNoteMaterialUploadRequest)req!)),
        new("GET", "/api/v1/native-notes/{noteId}/versions", null,
            (api, _) => api.GetNoteMaterialVersionsAsync(
                Guid.Parse("11111111-1111-1111-1111-111111111111")),
            ExpectedPath: "/api/v1/native-notes/11111111-1111-1111-1111-111111111111/versions"),
        new("GET", "/api/v1/native-notes/versions/{versionId}", null,
            (api, _) => api.GetNoteMaterialVersionAsync(
                Guid.Parse("22222222-2222-2222-2222-222222222222")),
            ExpectedPath: "/api/v1/native-notes/versions/22222222-2222-2222-2222-222222222222"),
        new("GET", "/api/v1/native-notes/versions/{versionId}/files/{fileIndex}", null,
            async (api, _) =>
            {
                await using var response = await api.DownloadNoteMaterialFileAsync(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    1,
                    maxContentBytes: 1024);
                Assert.NotNull(response);
            },
            ResponseFactory: SuccessfulNoteMaterialResponse,
            ExpectedPath: "/api/v1/native-notes/versions/22222222-2222-2222-2222-222222222222/files/1"),
        new("POST", "/api/v1/native-notes/versions/{versionId}/complete", null,
            (api, _) => api.CompleteNoteMaterialUploadAsync(
                Guid.Parse("22222222-2222-2222-2222-222222222222")),
            ExpectedPath: "/api/v1/native-notes/versions/22222222-2222-2222-2222-222222222222/complete"),
        new("PUT", "/api/v1/native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}", null,
            async (api, _) =>
            {
                await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("0123456789abcdef"));
                await api.UploadNoteMaterialPartAsync(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    1,
                    2,
                    new string('a', 64),
                    stream);
            },
            ExpectedPath: "/api/v1/native-notes/versions/22222222-2222-2222-2222-222222222222/files/1/parts/2",
            ExpectedQuery: "?sha256=" + new string('a', 64),
            BinaryBody: true),
        new("DELETE", "/api/v1/native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}", null,
            (api, _) => api.DeleteNoteMaterialPartAsync(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                1,
                2),
            ExpectedPath: "/api/v1/native-notes/versions/22222222-2222-2222-2222-222222222222/files/1/parts/2"),
        new("POST", "/api/v1/native-notes/versions/{versionId}/promote",
            new PromoteNoteMaterialVersionRequest
            {
                ExpectedCurrentVersionId = "33333333-3333-3333-3333-333333333333",
            },
            (api, req) => api.PromoteNoteMaterialVersionAsync(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                (PromoteNoteMaterialVersionRequest)req!),
            ExpectedPath: "/api/v1/native-notes/versions/22222222-2222-2222-2222-222222222222/promote"),
        new("GET", "/api/v1/native-notes/asr-tasks/sources", null,
            (api, _) => api.GetNoteAsrSourcesAsync(),
            ExpectedQuery: "?offset=0&limit=20"),
        new("POST", "/api/v1/native-notes/asr-tasks",
            new StartNoteAsrRequest
            {
                ExpectedUserId = "11111111-1111-1111-1111-111111111111",
                TaskId = "44444444-4444-4444-4444-444444444444",
                VersionId = "22222222-2222-2222-2222-222222222222",
            },
            (api, req) => api.StartNoteAsrTaskAsync((StartNoteAsrRequest)req!)),
        new("GET", "/api/v1/native-notes/asr-tasks", null,
            (api, _) => api.GetNoteAsrTasksAsync()),
        new("GET", "/api/v1/native-notes/asr-tasks/{taskId}", null,
            (api, _) => api.GetNoteAsrTaskAsync(
                Guid.Parse("44444444-4444-4444-4444-444444444444")),
            ExpectedPath: "/api/v1/native-notes/asr-tasks/44444444-4444-4444-4444-444444444444"),
        new("GET", "/api/v1/native-notes/asr-tasks/{taskId}/segments", null,
            (api, _) => api.GetNoteAsrTaskSegmentsAsync(
                Guid.Parse("44444444-4444-4444-4444-444444444444")),
            ExpectedPath: "/api/v1/native-notes/asr-tasks/44444444-4444-4444-4444-444444444444/segments",
            ExpectedQuery: "?offset=0&limit=10"),
        new("GET", "/api/v1/native-notes/derived-tasks/sources", null,
            (api, _) => api.GetNoteDerivedSourcesAsync(),
            ExpectedQuery: "?offset=0&limit=20"),
        new("GET", "/api/v1/native-notes/derived-tasks", null,
            (api, _) => api.GetNoteDerivedTasksAsync(
                Guid.Parse("11111111-1111-1111-1111-111111111111")),
            ExpectedQuery: "?noteId=11111111-1111-1111-1111-111111111111"),
        new("POST", "/api/v1/native-notes/derived-tasks",
            new StartNoteDerivedTaskRequest
            {
                Consent = new NoteDerivedGenerationConsent
                {
                    Confirmed = true,
                    Endpoint = "local",
                    ModelId = "model",
                    ModelRevision = "revision",
                },
                ExpectedUserId = "11111111-1111-1111-1111-111111111111",
                TaskId = "66666666-6666-6666-6666-666666666666",
                VersionId = "22222222-2222-2222-2222-222222222222",
            },
            (api, req) => api.StartNoteDerivedTaskAsync((StartNoteDerivedTaskRequest)req!)),
        new("GET", "/api/v1/native-notes/derived-tasks/{taskId}", null,
            (api, _) => api.GetNoteDerivedTaskAsync(
                Guid.Parse("66666666-6666-6666-6666-666666666666")),
            ExpectedPath: "/api/v1/native-notes/derived-tasks/66666666-6666-6666-6666-666666666666"),
        new("GET", "/api/v1/native-notes/derived-tasks/{taskId}/steps", null,
            (api, _) => api.GetNoteDerivedTaskStepsAsync(
                Guid.Parse("66666666-6666-6666-6666-666666666666")),
            ExpectedPath: "/api/v1/native-notes/derived-tasks/66666666-6666-6666-6666-666666666666/steps",
            ExpectedQuery: "?offset=0&limit=10"),
        new("POST", "/api/v1/native-notes/derived-tasks/{taskId}/retry",
            new RetryNoteDerivedTaskRequest
            {
                AcknowledgePossibleDuplicateCharge = true,
                Consent = new NoteDerivedGenerationConsent
                {
                    Confirmed = true,
                    Endpoint = "local",
                    ModelId = "model",
                    ModelRevision = "revision",
                },
                ExpectedRevision = "revision",
                ExpectedUserId = "11111111-1111-1111-1111-111111111111",
                RetryId = "77777777-7777-7777-7777-777777777777",
                StepKeys = new List<string> { "RecordSynthesis" },
            },
            (api, req) => api.RetryNoteDerivedTaskAsync(
                Guid.Parse("66666666-6666-6666-6666-666666666666"),
                (RetryNoteDerivedTaskRequest)req!),
            ExpectedPath: "/api/v1/native-notes/derived-tasks/66666666-6666-6666-6666-666666666666/retry"),
        new("POST", "/api/v1/native-notes/derived-tasks/{taskId}/stop", null,
            (api, _) => api.StopNoteDerivedTaskAsync(
                Guid.Parse("66666666-6666-6666-6666-666666666666")),
            ExpectedPath: "/api/v1/native-notes/derived-tasks/66666666-6666-6666-6666-666666666666/stop"),
        new("GET", "/api/v1/native-notes/derived-artifacts", null,
            (api, _) => api.GetNoteDerivedArtifactsAsync(
                Guid.Parse("11111111-1111-1111-1111-111111111111")),
            ExpectedQuery: "?noteId=11111111-1111-1111-1111-111111111111"),
        new("GET", "/api/v1/native-notes/derived-artifacts/{artifactId}", null,
            (api, _) => api.GetNoteDerivedArtifactAsync(
                Guid.Parse("88888888-8888-8888-8888-888888888888")),
            ExpectedPath: "/api/v1/native-notes/derived-artifacts/88888888-8888-8888-8888-888888888888"),
        new("GET", "/api/v1/native-notes/derived-artifacts/{artifactId}/versions/{versionId}", null,
            (api, _) => api.GetNoteDerivedArtifactVersionAsync(
                Guid.Parse("88888888-8888-8888-8888-888888888888"),
                Guid.Parse("99999999-9999-9999-9999-999999999999")),
            ExpectedPath: "/api/v1/native-notes/derived-artifacts/88888888-8888-8888-8888-888888888888/versions/99999999-9999-9999-9999-999999999999"),
        new("POST", "/api/v1/native-notes/derived-artifacts/{artifactId}/manual-revisions",
            new SaveNoteDerivedManualRevisionRequest
            {
                ContentMarkdown = "# 修订",
                ExpectedHeadRevision = "revision",
                ExpectedHeadVersionId = "99999999-9999-9999-9999-999999999999",
                RevisionId = "77777777-7777-7777-7777-777777777777",
            },
            (api, req) => api.SaveNoteDerivedManualRevisionAsync(
                Guid.Parse("88888888-8888-8888-8888-888888888888"),
                (SaveNoteDerivedManualRevisionRequest)req!),
            ExpectedPath: "/api/v1/native-notes/derived-artifacts/88888888-8888-8888-8888-888888888888/manual-revisions"),
        new("POST", "/api/v1/native-notes/derived-artifacts/{artifactId}/select",
            new SelectNoteDerivedVersionRequest
            {
                ExpectedHeadRevision = "revision",
                VersionId = "99999999-9999-9999-9999-999999999999",
            },
            (api, req) => api.SelectNoteDerivedArtifactVersionAsync(
                Guid.Parse("88888888-8888-8888-8888-888888888888"),
                (SelectNoteDerivedVersionRequest)req!),
            ExpectedPath: "/api/v1/native-notes/derived-artifacts/88888888-8888-8888-8888-888888888888/select"),
        new("GET", "/api/v1/native-notes/derived-tasks/destinations", null,
            (api, _) => api.GetNoteDerivedDestinationsAsync()),
        new("POST", "/api/v1/native-notes/task-suggestions/extractions",
            new StartNoteTaskSuggestionExtractionRequest
            {
                AcknowledgePossibleCharge = true,
                ArtifactId = "88888888-8888-8888-8888-888888888888",
                Consent = new NoteDerivedGenerationConsent
                {
                    Confirmed = true,
                    Endpoint = "https://text.example.test",
                    ModelId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    ModelRevision = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                },
                ExpectedContentSha256 = new string('a', 64),
                ExpectedUserId = "11111111-1111-1111-1111-111111111111",
                NoteId = "11111111-1111-1111-1111-111111111111",
                RequestId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
                VersionId = "99999999-9999-9999-9999-999999999999",
            },
            (api, req) => api.StartNoteTaskSuggestionExtractionAsync(
                (StartNoteTaskSuggestionExtractionRequest)req!)),
        new("GET", "/api/v1/native-notes/task-suggestions/extractions/{id}", null,
            (api, _) => api.GetNoteTaskSuggestionExtractionAsync(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            ExpectedPath: "/api/v1/native-notes/task-suggestions/extractions/cccccccc-cccc-cccc-cccc-cccccccccccc"),
        new("GET", "/api/v1/native-notes/task-suggestions", null,
            (api, _) => api.GetNoteTaskSuggestionsAsync(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                NoteTaskSuggestionStatusDto.Draft),
            ExpectedQuery: "?noteId=11111111-1111-1111-1111-111111111111&status=Draft"),
        new("GET", "/api/v1/native-notes/task-suggestions/{id}", null,
            (api, _) => api.GetNoteTaskSuggestionAsync(
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
            ExpectedPath: "/api/v1/native-notes/task-suggestions/dddddddd-dddd-dddd-dddd-dddddddddddd"),
        new("PUT", "/api/v1/native-notes/task-suggestions/{id}",
            new UpdateNoteTaskSuggestionRequest
            {
                Description = "准备议题",
                Due = DateTimeOffset.Parse("2026-11-03T10:00:00Z"),
                ExpectedRevision = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                Priority = 3,
                Title = "整理评审材料",
            },
            (api, req) => api.UpdateNoteTaskSuggestionAsync(
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                (UpdateNoteTaskSuggestionRequest)req!),
            ExpectedPath: "/api/v1/native-notes/task-suggestions/dddddddd-dddd-dddd-dddd-dddddddddddd"),
        new("POST", "/api/v1/native-notes/task-suggestions/{id}/confirm",
            new ConfirmNoteTaskSuggestionRequest
            {
                ExpectedRevision = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                RequestId = "ffffffff-ffff-ffff-ffff-ffffffffffff",
            },
            (api, req) => api.ConfirmNoteTaskSuggestionAsync(
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                (ConfirmNoteTaskSuggestionRequest)req!),
            ExpectedPath: "/api/v1/native-notes/task-suggestions/dddddddd-dddd-dddd-dddd-dddddddddddd/confirm"),
        new("POST", "/api/v1/native-notes/task-suggestions/{id}/dismiss",
            new DismissNoteTaskSuggestionRequest
            {
                ExpectedRevision = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
            },
            (api, req) => api.DismissNoteTaskSuggestionAsync(
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                (DismissNoteTaskSuggestionRequest)req!),
            ExpectedPath: "/api/v1/native-notes/task-suggestions/dddddddd-dddd-dddd-dddd-dddddddddddd/dismiss"),
    };

    private static HttpResponseMessage SuccessfulSegmentResponse()
    {
        var content = new ByteArrayContent("RIFFwav"u8.ToArray());
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = content,
        };
        response.Headers.TryAddWithoutValidation(
            "X-Content-SHA256",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        response.Headers.TryAddWithoutValidation(
            "X-Pim-Attempt-Id",
            "55555555-5555-5555-5555-555555555555");
        response.Headers.TryAddWithoutValidation("X-Pim-Fencing-Token", "7");
        response.Headers.TryAddWithoutValidation(
            "X-Pim-Lease-Id",
            "33333333-3333-3333-3333-333333333333");
        response.Headers.TryAddWithoutValidation(
            "X-Pim-Node-Id",
            "22222222-2222-2222-2222-222222222222");
        response.Headers.TryAddWithoutValidation(
            "X-Pim-Owner-Id",
            "11111111-1111-1111-1111-111111111111");
        response.Headers.TryAddWithoutValidation("X-Pim-Samples", "20");
        response.Headers.TryAddWithoutValidation("X-Pim-Segment-Index", "2");
        response.Headers.TryAddWithoutValidation("X-Pim-Source-File-Index", "0");
        response.Headers.TryAddWithoutValidation("X-Pim-Source-Offset-Samples", "10");
        response.Headers.TryAddWithoutValidation(
            "X-Pim-Source-SHA256",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        response.Headers.TryAddWithoutValidation(
            "X-Pim-Version-Id",
            "66666666-6666-6666-6666-666666666666");
        return response;
    }

    private static HttpResponseMessage SuccessfulNoteMaterialResponse()
    {
        var payload = "material"u8.ToArray();
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentDisposition =
            new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileName = "note.json",
            };
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = content,
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "private, no-store");
        response.Headers.TryAddWithoutValidation(
            "X-Content-SHA256",
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
        return response;
    }

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
        private readonly Func<System.Net.Http.HttpResponseMessage>? _responseFactory;

        public RecordingHandler(
            string responseBody = EnvelopeBody,
            Func<System.Net.Http.HttpResponseMessage>? responseFactory = null)
        {
            _responseBody = responseBody;
            _responseFactory = responseFactory;
        }

        public List<(string Method, string Path, string Query, string? Body)> Requests { get; } = new();

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add((
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                body));

            if (_responseFactory is not null)
            {
                return _responseFactory();
            }

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
