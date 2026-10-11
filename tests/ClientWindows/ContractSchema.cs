using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 · REQ-COM-02 / AC-COM-02（F-03）：从权威快照 <c>contract/openapi.json</c>
/// 读出 operation 的请求体 schema 与 method/path 定义，供各测试工程共用。
///
/// 本文件被**三个测试工程**编译（见各自 csproj 的 <c>Compile Include</c>）：
///   - <c>tests/Pim.Client.Tests</c>（net8.0，Linux CI）：核心 operation；
///   - <c>tests/Pim.Client.App.Tests</c>（net8.0-windows）：托盘更新检查 operation；
///   - <c>src/client-shell-windows/Pim.Shell.Tests</c>（net8.0 以 Link 方式编译）：
///     壳的健康检查 operation。
/// 每个程序集各自编译一份（internal），互不依赖。
///
/// 判定口径（Grok 独立 review I-1 修正后的版本，round4 起沿用）：
///   - 契约 <c>required</c> 里的字段**必须出现**；
///   - 实际出现的字段必须属于契约 <c>properties</c> 允许集合（<c>additionalProperties: false</c>）；
///   - 契约里的**可选字段允许不发送**；
///   - 值的 JSON 类型 / nullable / enum 按契约核对；
///   - 只有 fixture 明确填满全部可选字段时，才允许断言“集合相等”。
///
/// round5 增加 <see cref="ContractOperation.ForOperation"/>：`/health`、
/// `/api/client/shell/latest` 这类 **没有 requestBody** 的 operation 也要能被真实
/// 请求（method + 路径）对照契约 —— 之前的 <see cref="ContractOperation.ForPath"/>
/// 直接读 `requestBody`，对 GET operation 会 KeyNotFound，无法用于这 5 个缺口的覆盖。
/// </summary>
internal static class ContractJson
{
    public static HashSet<string> ObjectNames(string json, string? arrayProperty = null, int index = 0)
    {
        using var doc = JsonDocument.Parse(json);
        var target = doc.RootElement;
        if (arrayProperty is not null)
        {
            target = target.GetProperty(arrayProperty)[index];
        }

        Assert.Equal(JsonValueKind.Object, target.ValueKind);
        return target.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// 契约里的一个 operation：method + 路径 + （可选的）请求体 schema。
/// 带上 openapi 的 required / properties / type / enum / nullable 语义。
/// </summary>
internal sealed class ContractOperation
{
    private static readonly JsonDocumentOptions ParseOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    private ContractOperation(JsonElement schema, JsonElement root, bool hasRequestBody, string path, string method)
    {
        _schema = schema;
        _root = root;
        Path = path;
        Method = method;
        HasRequestBody = hasRequestBody;
    }

    private readonly JsonElement _schema;
    private readonly JsonElement _root;

    /// <summary>契约里的路径字面量（例如 <c>/api/v1/auth/login</c>）。</summary>
    public string Path { get; }

    /// <summary>小写 HTTP method（例如 <c>post</c>）。</summary>
    public string Method { get; }

    /// <summary>该 operation 是否声明了 `application/json` 请求体。</summary>
    public bool HasRequestBody { get; }

    public IReadOnlySet<string> PropertyNames => Properties.Keys.ToHashSet(StringComparer.Ordinal);

    public IReadOnlySet<string> Required => _schema.ValueKind == JsonValueKind.Object
        && _schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private Dictionary<string, JsonElement> Properties
    {
        get
        {
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (_schema.ValueKind == JsonValueKind.Object && _schema.TryGetProperty("properties", out var props))
            {
                foreach (var p in props.EnumerateObject())
                {
                    result[p.Name] = p.Value;
                }
            }

            return result;
        }
    }

    public ContractProperty Property(string name)
    {
        Assert.True(Properties.TryGetValue(name, out var value), $"契约 schema 里没有属性 {name}");
        return new ContractProperty(name, value, _root);
    }

    /// <summary>
    /// 从契约里取出一条 operation（method + 路径），**不要求**它有请求体。
    /// 路径或 method 不在契约里时抛错 —— 生产代码拼错路径/写错 method 时
    /// 这条断言就是机械门禁。
    /// </summary>
    public static ContractOperation ForOperation(string path, string method)
    {
        var root = LoadRoot();
        var paths = root.GetProperty("paths");
        if (!paths.TryGetProperty(path, out var item))
        {
            throw new InvalidOperationException(
                $"契约里没有这个 path：{path}（契约共 {paths.EnumerateObject().Count()} 条 path；"
                + "生产代码新增/改动了端点时必须先刷新契约快照，不允许手写契约）。");
        }

        var normalizedMethod = method.ToLowerInvariant();
        if (!item.TryGetProperty(normalizedMethod, out var operation))
        {
            var declared = string.Join(", ", item.EnumerateObject().Select(p => p.Name));
            throw new InvalidOperationException($"契约的 {path} 没有 {normalizedMethod} method（该 path 声明了：{declared}）");
        }

        var hasRequestBody = false;
        var schema = default(JsonElement);
        if (operation.TryGetProperty("requestBody", out var requestBody)
            && requestBody.TryGetProperty("content", out var content)
            && content.TryGetProperty("application/json", out var json))
        {
            hasRequestBody = true;
            schema = Deref(json.GetProperty("schema"), root);
        }

        return new ContractOperation(schema, root, hasRequestBody, path, normalizedMethod);
    }

    /// <summary>有请求体的 operation：返回其请求体 schema（旧行为，F-01/F-02 沿用）。</summary>
    public static ContractOperation ForPath(string path, string method)
    {
        var operation = ForOperation(path, method);
        Assert.True(
            operation.HasRequestBody,
            $"契约的 {method.ToUpperInvariant()} {path} 没有 application/json 请求体，不能用 ForPath 取 schema");
        return operation;
    }

    /// <summary>契约 required 必须全部出现在真实请求体里。</summary>
    public void AssertRequiredPresent(string json)
    {
        var actual = ContractJson.ObjectNames(json);
        var missing = Required.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            $"契约要求必填但客户端没发送：=[{string.Join(", ", missing)}]；实际字段=[{string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))}]");
    }

    /// <summary>实际字段必须全部在契约 properties 允许集合内（additionalProperties:false）。</summary>
    public void AssertActualFieldsAllowed(IReadOnlySet<string> actual)
        => AssertActualFieldsAllowed(actual, PropertyNames);

    public void AssertValueConforms(string pointer, JsonElement value)
        => AssertConforms(pointer, _schema, value, _root);

    private static void AssertActualFieldsAllowed(IReadOnlySet<string> actual, IReadOnlySet<string> allowed)
    {
        var extra = actual.Except(allowed).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(
            extra.Length == 0,
            $"请求体出现契约未声明的字段（additionalProperties:false）：=[{string.Join(", ", extra)}]；"
            + $"契约允许=[{string.Join(", ", allowed.OrderBy(x => x, StringComparer.Ordinal))}]");
    }

    internal static void AssertConforms(string pointer, JsonElement schema, JsonElement value, JsonElement root)
    {
        schema = Deref(schema, root);
        var nullable = schema.TryGetProperty("nullable", out var n) && n.ValueKind == JsonValueKind.True;

        if (value.ValueKind == JsonValueKind.Null)
        {
            Assert.True(nullable, $"{pointer}: 契约未声明 nullable，却发送了 null");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            var allowed = enumValues.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.True(
                value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString()),
                $"{pointer}: 值 {value.GetRawText()} 不在契约枚举 [{string.Join(", ", allowed)}] 内");
            return;
        }

        if (schema.TryGetProperty("type", out var typeElement))
        {
            var expected = typeElement.GetString();
            var matches = expected switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => value.ValueKind == JsonValueKind.Array,
                "object" => value.ValueKind == JsonValueKind.Object,
                _ => true,
            };
            Assert.True(matches, $"{pointer}: 契约类型是 {expected}，实际是 {value.ValueKind}");
        }

        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props))
        {
            var allowed = props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var actual in value.EnumerateObject())
            {
                Assert.True(allowed.Contains(actual.Name), $"{pointer}: 出现契约未声明的属性 {actual.Name}");
                AssertConforms($"{pointer}/{actual.Name}", props.GetProperty(actual.Name), actual.Value, root);
            }
        }

        if (value.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("additionalProperties", out var additional)
            && additional.ValueKind == JsonValueKind.Object)
        {
            foreach (var actual in value.EnumerateObject())
            {
                AssertConforms($"{pointer}/{actual.Name}", additional, actual.Value, root);
            }
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                AssertConforms($"{pointer}/{index}", items, item, root);
                index++;
            }
        }
    }

    private static JsonElement Deref(JsonElement schema, JsonElement root)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!.Split('/').Last();
            return root.GetProperty("components").GetProperty("schemas").GetProperty(name);
        }

        return schema;
    }

    private static JsonElement LoadRoot()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ContractPath()), ParseOptions);
        return doc.RootElement.Clone();
    }

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "contract", "openapi.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"从 {AppContext.BaseDirectory} 向上找不到 contract/openapi.json；契约快照是这些断言的前提。");
    }
}

/// <summary>契约里的单个属性（可能是 $ref、数组、嵌套对象）。</summary>
internal sealed class ContractProperty
{
    private readonly string _name;
    private readonly JsonElement _schema;
    private readonly JsonElement _root;

    public ContractProperty(string name, JsonElement schema, JsonElement root)
    {
        _name = name;
        _schema = schema;
        _root = root;
    }

    public ContractProperty? Items
    {
        get
        {
            if (_schema.TryGetProperty("items", out var items))
            {
                return new ContractProperty($"{_name}[]", items, _root);
            }

            return null;
        }
    }

    public void AssertValueConforms(string pointer, JsonElement value)
        => ContractOperation.AssertConforms(pointer, _schema, value, _root);

    public void AssertActualFieldsAllowed(IReadOnlySet<string> actual)
    {
        var schema = Deref(_schema);
        var props = schema.GetProperty("properties");
        var allowed = props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var extra = actual.Except(allowed).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(
            extra.Length == 0,
            $"{_name}: 出现契约未声明的字段 =[{string.Join(", ", extra)}]；契约允许=[{string.Join(", ", allowed.OrderBy(x => x, StringComparer.Ordinal))}]");
    }

    private JsonElement Deref(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!.Split('/').Last();
            return _root.GetProperty("components").GetProperty("schemas").GetProperty(name);
        }

        return schema;
    }
}
