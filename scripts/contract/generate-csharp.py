#!/usr/bin/env python3
"""从契约快照生成 C# DTO 与 operation 定义（System.Text.Json）。

用法：
    python3 scripts/contract/generate-csharp.py --out-dir <目录> [--mapping]

产出两个文件（相对 out-dir）：
    src/client-windows/Pim.Client.Core/Contract/ContractModels.cs
    src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs

设计目标（工单「pim-windows 接口边界契约化」/ issue #14）：
  * 只生成 `scripts/contract/client-operations.json` 里声明的 operation 的 schema 闭包 ——
    没声明进清单的 schema 不会进仓；声明了契约里不存在的 METHOD/path 会让生成**直接失败**，
    而不是产出半张契约。
  * 生成规则全部机械化、无手工例外，因此「手写模型对不上契约」这种偏差在生成阶段就暴露。
  * 结果只依赖 `contract/openapi.json` 与 operation 清单的字节（无时间戳、无随机顺序、
    无环境依赖），可以在 CI 里离线重跑并与仓内产物逐字节比对。

工具选型（工单要求「离线可跑、结果确定」）：本生成器只用 python3 标准库。
同仓族的 pim-android 用的是同构的自写生成器（scripts/contract/generate-kotlin.py），
两仓保持同一套语义与同一套门禁形状，便于跨仓维护。
实测 NSwag（14.7.1）在本仓不可用的原因是它无法只生成「用到的 operation」：
`nswag openapi2csclient` 会把契约全部 566 个 schema / 314 个 operation 都展开
（2,953,861 字节 / 560 个 DTO 类 / 607 个方法，实测于 contract/openapi.json@88a9d300），
且会在客户端侧引入 Newtonsoft.Json 依赖、文件头写死工具链版本。

字段可空性规则（与 OpenAPI 3.0 语义一致，不做任何「为了编过」的放宽）：
  * `required` 且不属于「请求方向的 readOnly」→ C# `required` 成员：调用方必须显式给值，
    少写一个必填字段是**编译错误**（CS9035），序列化时一定上线。
  * 其余属性 → 可空引用类型（值类型保持非空，缺字段取默认值，与契约「可缺席」一致）。
  * 契约枚举生成的 C# enum **不在值类型白名单里**：非必填的枚举字段生成为可空
    （缺席时上线 `null`，而不是发一个「看起来像值」的默认成员名）。
  * `array.items` / `additionalProperties` 自身 `nullable: true` → 逐字映射成
    `List<T?>` / `Dictionary<string, T?>`（不把元素的空性抹掉）。
  * OpenAPI 3.0：`readOnly` 属性的 `required` 只对响应生效，`writeOnly` 只对请求生效。
    生成器按 schema 在客户端实际出现的方向判定；同一 schema 在两个方向需要不同的
    required 集合时**直接报错**（不能悄悄二选一）。
  * 方向化的读/写约束（`readOnly` 字段出现在请求方向、`writeOnly` 出现在响应方向）需要
    「一个 schema 两个方向类型」或 `JsonIgnore` 支持，生成器**拒绝产出**而不是假装没看见。

不做静默降级的形状（一律 fail-closed，宁可刷新时停下来问）：内联对象属性、`allOf`/`oneOf`/
`anyOf`/`not`、`properties` 与 `additionalProperties` 同时出现、具名对象根上的 typed
`additionalProperties`（允许任意额外键）、内联 `enum`、整数枚举
（契约没给成员名，臆造名字等于编造接口）、字符串枚举取值无法与 C# 成员名逐字相同
（`JsonStringEnumConverter` 按成员名上线，改名就是改线格式）、operation 请求体/响应体根
不是 `$ref`、以及 `in: query|header|cookie` 等本生成器尚未表达的 operation 参数。

输出固定 UTF-8 + LF（`newline="\n"`）：Windows 上刷新与 Linux 上 CI 复跑必须逐字节相同。

类型映射（C#）：
  string → string；string/date-time → System.DateTimeOffset；string/date → System.DateOnly
  integer int32 / 无 format → int；integer int64 → long
  number float → float；number double / 无 format → double
  boolean → bool；array → System.Collections.Generic.List<T>
  object + additionalProperties → System.Collections.Generic.Dictionary<string, T>
  无 type 的自由 object（如 `{"nullable": true}` 的 rawJson）→ System.Text.Json.JsonElement
  string enum → C# enum（[JsonConverter(JsonStringEnumConverter)]，wire 值即成员名）
  integer enum → int（契约没有给成员名，臆造名字等于编造接口）
  内联 object / allOf / oneOf / anyOf → 直接报错（尚未支持，不能产出半个类型）
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONTRACT = REPO_ROOT / "contract" / "openapi.json"
DEFAULT_OPERATIONS = REPO_ROOT / "scripts" / "contract" / "client-operations.json"

MODELS_RELATIVE_PATH = Path("src/client-windows/Pim.Client.Core/Contract/ContractModels.cs")
OPERATIONS_RELATIVE_PATH = Path("src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs")

NAMESPACE = "Pim.Client.Core.Contract"
CONTRACT_BASE_PATH = "/api/v1"
HTTP_METHODS = ("get", "post", "put", "delete", "patch", "head", "options", "trace")

JSON_PROPERTY = "System.Text.Json.Serialization.JsonPropertyName"
JSON_CONVERTER = "System.Text.Json.Serialization.JsonConverter"
JSON_STRING_ENUM = "System.Text.Json.Serialization.JsonStringEnumConverter"

CSHARP_KEYWORDS = {
    "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
    "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
    "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
    "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
    "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
    "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
    "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
    "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
}


class GenerationError(Exception):
    """生成前置条件不满足（契约/清单不一致等）——必须报错，不能产出半张契约。"""


# --------------------------------------------------------------------------- #
# 契约读取
# --------------------------------------------------------------------------- #
def load_contract(path: Path) -> dict:
    if not path.is_file():
        raise GenerationError(f"契约快照不存在：{path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:  # pragma: no cover - 防御性分支
        raise GenerationError(f"契约快照不是合法 JSON：{path}: {exc}") from exc


def load_operations(path: Path) -> list[dict]:
    if not path.is_file():
        raise GenerationError(f"operation 清单不存在：{path}")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise GenerationError(f"operation 清单不是合法 JSON：{path}: {exc}") from exc
    operations = data.get("operations")
    if not isinstance(operations, list) or not operations:
        raise GenerationError(f"operation 清单为空或形状不对：{path}")

    seen: set[tuple[str, str]] = set()
    seen_names: set[str] = set()
    for entry in operations:
        method = str(entry.get("method", "")).lower()
        op_path = str(entry.get("path", ""))
        client_name = str(entry.get("apiClient", ""))
        if method not in HTTP_METHODS or not op_path.startswith("/") or not client_name:
            raise GenerationError(f"operation 清单条目形状不对：{entry}")
        key = (method.upper(), op_path)
        if key in seen:
            raise GenerationError(f"operation 清单有重复条目：{key}")
        seen.add(key)
        if client_name in seen_names:
            raise GenerationError(f"operation 清单有重复的 apiClient 名：{client_name}")
        seen_names.add(client_name)
    return operations


def operation_schema(contract: dict, method: str, op_path: str) -> dict:
    paths = contract.get("paths", {})
    item = paths.get(op_path)
    if item is None:
        raise GenerationError(
            f"契约里没有路径 {op_path}（客户端清单声明了它）——"
            "需要新接口时请让后端在 pim-api 实现并刷新契约，不要在客户端造一个。"
        )
    op = item.get(method.lower())
    if op is None:
        raise GenerationError(
            f"契约里 {op_path} 没有 {method.upper()} 方法（客户端清单声明了它）——"
            f"该路径已有的方法：{sorted(k.upper() for k in item if k in HTTP_METHODS)}"
        )
    return op


# --------------------------------------------------------------------------- #
# schema 闭包与方向
# --------------------------------------------------------------------------- #
def iter_refs(node) -> list[str]:
    found: list[str] = []

    def walk(value):
        if isinstance(value, dict):
            ref = value.get("$ref")
            if isinstance(ref, str):
                found.append(ref.rsplit("/", 1)[-1])
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)

    walk(node)
    return found


def single_ref(schema, what: str) -> str | None:
    """请求体/响应体的根必须是 `$ref`；内联根形状会让边界拿不到具名类型（静默丢类型）。"""
    if schema is None:
        return None
    if isinstance(schema, dict) and isinstance(schema.get("$ref"), str):
        return schema["$ref"].rsplit("/", 1)[-1]
    raise GenerationError(
        f"{what} 的 schema 根不是 $ref（内联形状生成器尚未支持，不能悄悄产出没有类型的边界）："
        f"{json.dumps(schema, sort_keys=True)[:200]}"
    )


def reject_non_path_parameters(op: dict, op_path: str, path_item: dict | None = None) -> None:
    """只支持 path 参数（由路径模板表达）：query/header/cookie 参数会被生成器丢掉，
    丢掉的后果是「请求少带了契约要求的参数」，因此 fail-closed。

    注意 OpenAPI 允许参数写在 **Path Item** 级（对同一路径的所有方法生效），
    只检查 operation 级会漏掉它们。
    """
    parameters = list((path_item or {}).get("parameters") or []) + list(op.get("parameters") or [])
    for parameter in parameters:
        name = parameter.get("name")
        location = parameter.get("in")

        if location == "path":
            # path 参数由路径模板表达；但它**自带的 schema 约束**（enum/pattern/format/数值范围）
            # 或非 string 类型，生成的边界表达不了 —— 静默丢掉等于「契约有约束、C# 边界没有」，
            # 因此这里 fail-closed，而不是假装没看见。
            if "$ref" in parameter:
                raise GenerationError(
                    f"operation {op_path} 的 path 参数用了 $ref（{parameter['$ref']}），"
                    "生成器尚未解析 components.parameters，拒绝产出。"
                )
            if "content" in parameter:
                raise GenerationError(
                    f"operation {op_path} 的 path 参数 {name} 用 content 描述（而非 schema），"
                    "生成器尚未支持，拒绝产出。"
                )
            if name and f"{{{name}}}" not in op_path:
                raise GenerationError(
                    f"operation {op_path} 声明了 path 参数 {name}，但它不在路径模板里 —— 会被静默丢掉。"
                )
            schema = parameter.get("schema") or {}
            # 白名单式判定：path 参数的 schema 只允许「就是 string」这一种形状。
            # 任何其它写法（enum/pattern/format/数值范围/$ref/allOf/oneOf/anyOf/not/items…）
            # 都是生成器表达不了的约束 —— 静默丢掉等于「契约有约束、C# 边界没有」。
            allowed_keys = {"type", "description", "example", "title", "deprecated", "default", "nullable"}
            unknown = sorted(key for key in schema if key not in allowed_keys and not key.startswith("x-"))
            if unknown:
                raise GenerationError(
                    f"operation {op_path} 的 path 参数 {name} 的 schema 带生成器无法表达的键"
                    f"（{', '.join(unknown)}）：需要扩展显式方法签名（例如生成枚举/校验），不能静默丢掉。"
                )
            if schema.get("$ref") is not None:
                raise GenerationError(
                    f"operation {op_path} 的 path 参数 {name} 的 schema 用了 $ref，"
                    "生成器尚未解析参数级 schema 引用，拒绝产出。"
                )
            if schema.get("nullable") is True:
                raise GenerationError(
                    f"operation {op_path} 的 path 参数 {name} 声明为 nullable，"
                    "路径参数不可能是 null —— 契约形状可疑，拒绝产出。"
                )
            if schema.get("type") not in (None, "string"):
                raise GenerationError(
                    f"operation {op_path} 的 path 参数 {name} 是 {schema.get('type')} 类型："
                    "生成的边界只接受 string 路由值，需要先决定类型映射。"
                )
            continue

        raise GenerationError(
            f"operation {op_path} 带 {location} 参数（{name}），"
            "生成器目前只支持由路径模板表达的 path 参数 —— 需要先扩展显式方法签名。"
        )


def json_request_schema(op: dict):
    body = op.get("requestBody") or {}
    media = (body.get("content") or {}).get("application/json")
    return media.get("schema") if media else None


def success_response_schemas(op: dict) -> list[dict]:
    result = []
    for status, response in (op.get("responses") or {}).items():
        if not str(status).startswith("2"):
            continue
        media = (response.get("content") or {}).get("application/json")
        if media is None or media.get("schema") is None:
            raise GenerationError(f"operation 的 {status} 响应不是 application/json（生成器只支持 JSON 边界）")
        single_ref(media["schema"], f"{status} 响应")
        result.append(media["schema"])
    if not result:
        raise GenerationError("operation 没有任何 2xx 响应")
    return result


def collect_closure(schemas: dict, roots: set[str]) -> set[str]:
    closure: set[str] = set()
    stack = list(roots)
    while stack:
        name = stack.pop()
        if name in closure:
            continue
        schema = schemas.get(name)
        if schema is None:
            raise GenerationError(f"契约 components.schemas 里没有 {name}")
        closure.add(name)
        stack.extend(child for child in iter_refs(schema) if child not in closure)
    return closure


def schema_directions(contract: dict, operations: list[dict]) -> tuple[dict[str, set[str]], dict[str, dict]]:
    """返回 (schema → 出现方向集合, "METHOD path" → 元信息)。"""
    schemas = contract.get("components", {}).get("schemas", {})
    request_roots: set[str] = set()
    response_roots: set[str] = set()
    meta: dict[str, dict] = {}

    for entry in operations:
        method = str(entry["method"]).upper()
        op_path = str(entry["path"])
        op = operation_schema(contract, method, op_path)
        reject_non_path_parameters(op, op_path, contract.get("paths", {}).get(op_path))

        request_schema = json_request_schema(op)
        if request_schema is not None:
            single_ref(request_schema, f"{method} {op_path} 请求体")
            request_roots.update(iter_refs(request_schema))

        response_schemas = success_response_schemas(op)
        for schema in response_schemas:
            response_roots.update(iter_refs(schema))

        meta[f"{method} {op_path}"] = {
            "operation": op,
            "requestSchema": request_schema,
            "responseSchemas": response_schemas,
        }

    request_closure = collect_closure(schemas, request_roots)
    response_closure = collect_closure(schemas, response_roots)

    directions: dict[str, set[str]] = {}
    for name in sorted(request_closure | response_closure):
        found = set()
        if name in request_closure:
            found.add("request")
        if name in response_closure:
            found.add("response")
        directions[name] = found
    return directions, meta


# --------------------------------------------------------------------------- #
# C# 标识符与类型映射
# --------------------------------------------------------------------------- #
def split_words(raw: str) -> list[str]:
    return [part for part in re.split(r"[^0-9A-Za-z]+", raw) if part]


def pascal_case(raw: str, what: str) -> str:
    words = split_words(raw)
    if not words:
        raise GenerationError(f"{what} 无法转成 C# 标识符：{raw!r}")
    name = "".join(word[:1].upper() + word[1:] for word in words)
    if name[0].isdigit():
        name = "_" + name
    return "@" + name if name in CSHARP_KEYWORDS else name


def enum_member_name(value: str, enum_name: str) -> str:
    """枚举成员名必须与契约里的 wire 值**逐字相同**。

    `JsonStringEnumConverter`（无命名策略）上线时写的是**成员名**，所以任何
    「PascalCase 化」都会静默改掉线格式（例如契约值 `low` 变成上线 `Low`）。
    net8.0 没有 `[JsonStringEnumMemberName]`，因此这里只能要求成员名 == wire 值；
    不满足就拒绝产出，而不是产出一个线格式不对的枚举。
    """
    if not value:
        raise GenerationError(f"契约枚举 {enum_name} 有空取值，无法生成")
    name = value
    if not (name[0].isalpha() or name[0] == "_") or not all(ch.isalnum() or ch == "_" for ch in name):
        raise GenerationError(
            f"契约枚举 {enum_name} 的取值 {value!r} 不能直接作为 C# 成员名"
            "（net8.0 的 JsonStringEnumConverter 按成员名上线，改名就是改线格式）："
            "需要后端把取值改成合法标识符，或改用自定义 converter。"
        )
    return "@" + name if name in CSHARP_KEYWORDS else name


def scalar_type(schema: dict) -> str:
    kind = schema.get("type")
    fmt = schema.get("format")
    if kind == "string":
        if fmt == "date-time":
            return "System.DateTimeOffset"
        if fmt == "date":
            return "System.DateOnly"
        if fmt in ("binary", "byte"):
            return "byte[]"
        return "string"
    if kind == "integer":
        return "long" if fmt == "int64" else "int"
    if kind == "number":
        return "float" if fmt == "float" else "double"
    if kind == "boolean":
        return "bool"
    raise GenerationError(f"无法映射的标量类型：{json.dumps(schema, sort_keys=True)}")


def csharp_type(schema: dict) -> str:
    """把 schema（属性或根 schema）映射成 C# 类型（不含可空标记）。"""
    if not isinstance(schema, dict):
        raise GenerationError(f"schema 形状不对：{schema!r}")

    ref = schema.get("$ref")
    if isinstance(ref, str):
        return ref.rsplit("/", 1)[-1]

    for combinator in ("allOf", "oneOf", "anyOf", "not"):
        if combinator in schema:
            raise GenerationError(
                f"契约里出现 {combinator}，生成器尚未支持（不能产出半个类型）："
                f"{json.dumps(schema, sort_keys=True)[:200]}"
            )

    if "enum" in schema:
        raise GenerationError(
            "契约里出现**内联** enum（属性/元素级），生成器尚未支持（需要先决定命名规则）："
            f"{json.dumps(schema, sort_keys=True)[:200]}"
        )

    kind = schema.get("type")
    if kind == "array":
        items = schema.get("items")
        if not isinstance(items, dict):
            raise GenerationError(f"array 没有 items：{json.dumps(schema, sort_keys=True)}")
        return f"System.Collections.Generic.List<{element_type(items)}>"
    if kind == "object":
        additional = schema.get("additionalProperties")
        if isinstance(additional, dict):
            if "properties" in schema:
                # 同时有具名字段与自由键：只映射成字典会**静默丢掉** properties。
                raise GenerationError(
                    "契约里同一个对象同时有 properties 与 additionalProperties，生成器尚未支持"
                    f"（二选一会静默丢字段）：{json.dumps(schema, sort_keys=True)[:200]}"
                )
            return f"System.Collections.Generic.Dictionary<string, {element_type(additional)}>"
        if "properties" in schema:
            raise GenerationError(
                "契约里出现内联对象属性，生成器尚未支持（需要先决定命名规则）："
                f"{json.dumps(schema, sort_keys=True)[:200]}"
            )
        return "System.Text.Json.JsonElement"
    if kind is None:
        # 契约里的「自由 object」写法：`{"nullable": true}`（untyped / 任意 JSON，例如 rawJson）。
        if not schema.get("properties") and "enum" not in schema:
            return "System.Text.Json.JsonElement"
        raise GenerationError(f"无法映射的无类型 schema：{json.dumps(schema, sort_keys=True)[:200]}")
    return scalar_type(schema)


def element_type(schema: dict) -> str:
    """数组元素 / 字典值：契约允许该元素为 null 时保留可空标记（不抹掉空性）。"""
    mapped = csharp_type(schema)
    if schema.get("nullable") and not mapped.endswith("?"):
        return f"{mapped}?"
    return mapped


def is_value_type(csharp: str) -> bool:
    if csharp.endswith("?"):
        return True
    if csharp in ("string", "byte[]") or csharp.startswith("System.Collections.Generic."):
        return False
    return csharp.split("<")[0] in (
        "int", "long", "double", "float", "bool",
        "System.DateTimeOffset", "System.DateOnly", "System.Text.Json.JsonElement",
    )


def reject_direction_only_property(schema_name: str, name: str, prop: dict, directions: set[str]) -> None:
    """readOnly 字段出现在请求方向（或 writeOnly 出现在响应方向）需要方向化类型才能表达。

    生成器不这么做，也**不允许**静默忽略：那样会让「客户端不该发这个字段」这条契约约束
    变成纸面注释（字段照发不误）。
    """
    if prop.get("readOnly") and "request" in directions:
        raise GenerationError(
            f"schema {schema_name} 的字段 {name} 是 readOnly，但它所在的 schema 也用于请求方向："
            "需要「一个 schema 两个方向类型」或 JsonIgnore 支持，生成器拒绝产出。"
        )
    if prop.get("writeOnly") and "response" in directions:
        raise GenerationError(
            f"schema {schema_name} 的字段 {name} 是 writeOnly，但它所在的 schema 也用于响应方向："
            "需要方向化类型或 JsonIgnore 支持，生成器拒绝产出。"
        )


def property_is_required(schema: dict, name: str, prop: dict, directions: set[str]) -> bool:
    required = name in (schema.get("required") or [])
    if not required:
        return False
    if prop.get("readOnly") and "request" in directions:
        # OpenAPI 3.0：readOnly 属性的 required 只对响应生效。
        return False
    if prop.get("writeOnly") and "response" in directions:
        return False
    return True


def required_names(schema: dict, directions: set[str]) -> list[str]:
    names = []
    for name, prop in sorted((schema.get("properties") or {}).items()):
        if property_is_required(schema, name, prop, directions):
            names.append(name)
    return names


def render_property(
    schema_name: str, schema: dict, contract_name: str, prop: dict, directions: set[str]
) -> list[str]:
    reject_direction_only_property(schema_name, contract_name, prop, directions)
    required = property_is_required(schema, contract_name, prop, directions)
    base_type = csharp_type(prop)
    nullable = bool(prop.get("nullable"))

    if required:
        type_name = f"{base_type}?" if nullable else base_type
        declaration = f"public required {type_name} {pascal_case(contract_name, '属性名')} {{ get; set; }}"
    elif nullable or not is_value_type(base_type):
        # 契约允许缺席：引用类型必须可空，值类型保持非空（缺席取默认值）——不额外放宽。
        type_name = f"{base_type}?" if (nullable or not is_value_type(base_type)) else base_type
        declaration = f"public {type_name} {pascal_case(contract_name, '属性名')} {{ get; set; }}"
    else:
        declaration = f"public {base_type} {pascal_case(contract_name, '属性名')} {{ get; set; }}"

    lines = [f"    /// <summary>契约字段 <c>{contract_name}</c>。</summary>"]
    if prop.get("readOnly") and contract_name in (schema.get("required") or []):
        lines.append("    // readOnly：契约 required 只对响应生效，请求方向不发该字段。")
    lines.append(f'    [{JSON_PROPERTY}("{contract_name}")]')
    lines.append(f"    {declaration}")
    return lines


def render_enum(name: str, schema: dict) -> list[str]:
    values = schema.get("enum") or []
    kind = schema.get("type")
    if kind != "string":
        joined = ", ".join(str(value) for value in values)
        raise GenerationError(
            f"契约枚举 {name} 是整数枚举（取值：{joined}），契约没有给出成员名 —— "
            "臆造名字等于编造接口，生成器拒绝产出（需要后端把它改成字符串枚举或给出成员名）。"
        )

    lines = [
        "/// <summary>",
        f"/// 契约枚举 <c>{name}</c>：wire 值即成员名，写错取值编译期就不通过。",
        "/// </summary>",
        f"[{JSON_CONVERTER}(typeof({JSON_STRING_ENUM}))]",
        f"public enum {name}",
        "{",
    ]
    seen: dict[str, str] = {}
    for value in values:
        member = enum_member_name(str(value), name)
        if member in seen:
            raise GenerationError(f"{name} 的枚举值归一化后重名：{seen[member]!r} / {value!r}")
        seen[member] = str(value)
        lines.append(f"    {member},")
    lines.append("}")
    return lines


def reject_root_additional_properties(name: str, schema: dict) -> None:
    """根级（具名 schema）的 typed `additionalProperties` 表示「允许任意额外键」。

    C# 的一个类表达不了这个语义（要么丢约束、要么丢具名字段），因此拒绝产出。
    `additionalProperties: false`（禁止额外键）与类的形状一致，放行。
    """
    additional = schema.get("additionalProperties")
    if isinstance(additional, dict) or additional is True:
        raise GenerationError(
            f"schema {name} 是具名对象但带 additionalProperties（允许任意额外键），"
            "C# 类无法同时表达具名字段与该约束，生成器拒绝产出。"
        )


def render_class(name: str, schema: dict, directions: set[str]) -> list[str]:
    reject_root_additional_properties(name, schema)
    lines = [f"public sealed class {name}", "{"]
    seen: dict[str, str] = {}
    for contract_name in sorted(schema.get("properties") or {}):
        prop = (schema["properties"])[contract_name]
        identifier = pascal_case(contract_name, "属性名")
        if identifier in seen:
            raise GenerationError(
                f"{name} 的字段归一化后重名：{seen[identifier]!r} / {contract_name!r}（契约里大小写不同的字段会撞到同一个 C# 属性）"
            )
        seen[identifier] = contract_name
        lines.extend(render_property(name, schema, contract_name, prop, directions))
    lines.append("}")
    return lines


def render_schema(name: str, schema: dict, directions: set[str]) -> list[str]:
    # 组合关键字必须先拦下来：schema 同时带 properties 和 allOf 时，静默忽略 allOf
    # 会生成一个「看起来对、实际少了半个契约」的类型。
    for combinator in ("allOf", "oneOf", "anyOf", "not"):
        if combinator in schema:
            raise GenerationError(
                f"schema {name} 使用了 {combinator}，生成器尚未支持（不能产出半个类型）："
                f"{json.dumps(schema, sort_keys=True)[:200]}"
            )
    kind = schema.get("type")
    if "enum" in schema:
        return render_enum(name, schema)
    if kind == "object" and "properties" in schema:
        return render_class(name, schema, directions)
    if kind == "object":
        raise GenerationError(
            f"schema {name} 是自由 object，无法生成具名 C# 类型：{json.dumps(schema, sort_keys=True)[:200]}"
        )
    raise GenerationError(f"schema {name} 的形状不受支持：{json.dumps(schema, sort_keys=True)[:200]}")


def direction_label(directions: set[str]) -> str:
    if directions == {"request", "response"}:
        return "request+response"
    return "request" if "request" in directions else "response"


# --------------------------------------------------------------------------- #
# 生成
# --------------------------------------------------------------------------- #
def generated_header(contract_sha: str) -> list[str]:
    return [
        "// ---------------------------------------------------------------------------",
        "// 本文件由工具生成，请勿手工修改 / GENERATED FILE — DO NOT EDIT BY HAND.",
        "//",
        f"//   契约快照      : contract/openapi.json (sha256 {contract_sha})",
        "//   operation 清单: scripts/contract/client-operations.json",
        "//   生成器        : scripts/contract/generate-csharp.py",
        "//   重新生成      : bash scripts/ci/generate-contract-csharp.sh",
        "//   一键刷新      : bash scripts/ci/refresh-contract.sh（快照 + 生成物同批更新）",
        "//   一致性门禁    : bash scripts/ci/check-contract-csharp.sh（CI 逐字节比对，改了就红）",
        "//",
        "// 只覆盖 operation 清单声明的 operation 的 schema 闭包；需要新接口时先让后端在",
        "// pim-api 实现并刷新契约，再往清单里加一条 —— 不要在客户端造类型。",
        "// ---------------------------------------------------------------------------",
        f"namespace {NAMESPACE};",
        "",
    ]


def generate_models(contract: dict, operations: list[dict], contract_sha: str) -> str:
    schemas = contract.get("components", {}).get("schemas", {})
    directions, _ = schema_directions(contract, operations)

    # 同一 schema 在两个方向需要不同的 required 集合时无法用「一个 C# 类型」表达 —— 报错而不是二选一。
    for name in sorted(directions):
        schema = schemas[name]
        dirs = directions[name]
        if dirs == {"request", "response"}:
            as_request = required_names(schema, {"request"})
            as_response = required_names(schema, {"response"})
            if as_request != as_response:
                raise GenerationError(
                    f"schema {name} 同时用于请求与响应，但两个方向需要的 required 集合不同"
                    f"（请求 {as_request} / 响应 {as_response}）：需要为两个方向分别命名才能生成。"
                )

    body: list[str] = []
    for name in sorted(directions):
        schema = schemas[name]
        body.append(
            f"/// <summary>契约 <c>components.schemas.{name}</c>（{direction_label(directions[name])}）。</summary>"
        )
        body.extend(render_schema(name, schema, directions[name]))
        body.append("")

    return "\n".join(generated_header(contract_sha) + body).rstrip("\n") + "\n"


def relative_to_base(full_path: str) -> str:
    """把契约里的绝对路径换算成相对 BaseAddress（`<serverUrl>/api/v1/`）的相对路径。

    BaseAddress 固定以 `/api/v1/` 结尾（见 ApiClient），因此：
      * `/api/v1/pc/tracker/health`      → `pc/tracker/health`（同一层）
      * `/api/client/shell/latest`       → `../client/shell/latest`（退回 /api/ 一层）
      * `/health`                        → `../../health`（退回 host 根两层）
    这样 `<serverUrl>/子路径/api/v1/` 这类部署也能落到 `<serverUrl>/子路径/...`，
    与改造前「先去掉 /api/v1 再拼」的语义一致。
    """
    base = CONTRACT_BASE_PATH.strip("/").split("/")
    target = full_path.lstrip("/").split("/")
    common = 0
    while common < len(base) and common < len(target) and base[common] == target[common]:
        common += 1
    ups = len(base) - common
    rest = "/".join(target[common:])
    if ups == 0:
        return rest or "./"
    return "../" * ups + rest


def render_operation_entry(entry: dict) -> list[str]:
    client_name = str(entry["apiClient"])
    if not client_name.endswith("Async"):
        raise GenerationError(f"operation 清单里的 apiClient 名必须以 Async 结尾（收到 {client_name}）")
    method = str(entry["method"]).upper()
    full_path = str(entry["path"])
    if not full_path.startswith("/"):
        raise GenerationError(f"operation 清单里的路径必须是绝对路径：{full_path}")
    relative = relative_to_base(full_path)
    field_name = client_name[: -len("Async")]
    return [
        f'    public static readonly ContractOperation {field_name} =',
        f'        new("{method}", "{full_path}", "{relative}");',
    ]


def generate_operations(contract: dict, operations: list[dict], contract_sha: str) -> str:
    # 逐个校验清单里的 operation 在契约里存在（失败即拒绝产出）。
    for entry in operations:
        operation_schema(contract, str(entry["method"]), str(entry["path"]))

    body: list[str] = [
        "/// <summary>",
        "/// 契约 operation 定义：客户端允许调用的全部接口。",
        "///",
        "/// 路径只能从这里取 —— 构造函数是私有的，因此**无法在别处编造一个路径**；",
        "/// 而本文件由 <c>scripts/contract/generate-csharp.py</c> 从契约快照生成、",
        "/// 并由 <c>scripts/ci/check-contract-csharp.sh</c> 在 CI 里逐字节复核。",
        "/// 需要契约里没有的接口时：不要在这里加，按 AGENTS.md「接口契约与门禁」把需求交给后端。",
        "/// </summary>",
        "public sealed class ContractOperation",
        "{",
        "    private ContractOperation(string httpMethod, string pathTemplate, string relativePath)",
        "    {",
        "        HttpMethod = httpMethod;",
        "        PathTemplate = pathTemplate;",
        "        RelativePath = relativePath;",
        "        RouteParameters = ParseRouteParameters(pathTemplate);",
        "    }",
        "",
        "    /// <summary>HTTP 方法（契约里的方法，全大写）。</summary>",
        "    public string HttpMethod { get; }",
        "",
        "    /// <summary>契约里的完整路径（含 <c>/api/v1</c> 前缀）。</summary>",
        "    public string PathTemplate { get; }",
        "",
        "    /// <summary>相对 <c>/api/v1/</c> 基址的路径（实际请求用的就是它）。</summary>",
        "    public string RelativePath { get; }",
        "",
        "    /// <summary>路径模板里的占位符（按出现顺序）。</summary>",
        "    public System.Collections.Generic.IReadOnlyList<string> RouteParameters { get; }",
        "",
        "    /// <summary>",
        "    /// 用路由值填充路径模板。",
        "    /// 占位符与传入的名字必须**完全一致**（多传、少传、写错名字都会抛异常），",
        "    /// 值按 RFC 3986 做转义 —— 避免把未转义的 deviceId 拼进 URL。",
        "    /// </summary>",
        "    public string Resolve(params (string Name, string Value)[] routeValues)",
        "    {",
        "        var supplied = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);",
        "        foreach (var (name, value) in routeValues ?? System.Array.Empty<(string, string)>())",
        "        {",
        "            if (!supplied.TryAdd(name, value))",
        "            {",
        "                throw new System.ArgumentException($\"路由值重复：{name}\", nameof(routeValues));",
        "            }",
        "        }",
        "",
        "        if (supplied.Count != RouteParameters.Count)",
        "        {",
        "            throw new System.ArgumentException(",
        "                $\"路由值数量不符：契约路径 {PathTemplate} 需要 [{string.Join(\", \", RouteParameters)}]，\" +",
        "                $\"收到 [{string.Join(\", \", supplied.Keys)}]\",",
        "                nameof(routeValues));",
        "        }",
        "",
        "        var resolved = RelativePath;",
        "        foreach (var parameter in RouteParameters)",
        "        {",
        "            if (!supplied.TryGetValue(parameter, out var value))",
        "            {",
        "                throw new System.ArgumentException(",
        "                    $\"缺少路由值 {parameter}（契约路径 {PathTemplate}）\", nameof(routeValues));",
        "            }",
        "",
        "            resolved = resolved.Replace(\"{\" + parameter + \"}\", System.Uri.EscapeDataString(value), System.StringComparison.Ordinal);",
        "        }",
        "",
        "        return resolved;",
        "    }",
        "",
        "    private static System.Collections.Generic.IReadOnlyList<string> ParseRouteParameters(string pathTemplate)",
        "    {",
        "        var parameters = new System.Collections.Generic.List<string>();",
        "        var index = 0;",
        "        while (true)",
        "        {",
        "            var start = pathTemplate.IndexOf('{', index);",
        "            if (start < 0)",
        "            {",
        "                break;",
        "            }",
        "",
        "            var end = pathTemplate.IndexOf('}', start + 1);",
        "            if (end < 0)",
        "            {",
        "                throw new System.ArgumentException($\"契约路径模板的占位符没有闭合：{pathTemplate}\", nameof(pathTemplate));",
        "            }",
        "",
        "            parameters.Add(pathTemplate[(start + 1)..end]);",
        "            index = end + 1;",
        "        }",
        "",
        "        return parameters;",
        "    }",
        "",
        "    public override string ToString() => $\"{HttpMethod} {PathTemplate}\";",
        "",
    ]
    for entry in operations:
        body.extend(render_operation_entry(entry))
    body.append("}")

    return "\n".join(generated_header(contract_sha) + body).rstrip("\n") + "\n"


# --------------------------------------------------------------------------- #
# 清单输出（给人看的映射表）
# --------------------------------------------------------------------------- #
def ref_name(schema) -> str:
    if not isinstance(schema, dict):
        return "-"
    ref = schema.get("$ref")
    if isinstance(ref, str):
        return ref.rsplit("/", 1)[-1]
    kind = schema.get("type")
    if kind == "array":
        return f"List<{ref_name(schema.get('items'))}>"
    if kind == "object" and isinstance(schema.get("additionalProperties"), dict):
        return f"Dictionary<string, {ref_name(schema['additionalProperties'])}>"
    if kind is None:
        return "JsonElement"
    return f"{kind}({schema.get('format')})" if schema.get("format") else str(kind)


def print_mapping(contract: dict, operations: list[dict]) -> None:
    print("| ApiClient 方法 | METHOD path | 契约请求体 | 契约 2xx payload |")
    print("| --- | --- | --- | --- |")
    for entry in operations:
        method = str(entry["method"]).upper()
        op_path = str(entry["path"])
        op = operation_schema(contract, method, op_path)
        request_schema = json_request_schema(op)
        payload = ", ".join(dict.fromkeys(ref_name(s) for s in success_response_schemas(op)))
        print(
            f"| `{entry['apiClient']}` | `{method} {op_path}` | "
            f"`{ref_name(request_schema) if request_schema else '-'}` | `{payload}` |"
        )


# --------------------------------------------------------------------------- #
def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="从契约快照生成 C# DTO 与 operation 定义")
    parser.add_argument("--contract", type=Path, default=DEFAULT_CONTRACT)
    parser.add_argument("--operations", type=Path, default=DEFAULT_OPERATIONS)
    parser.add_argument("--out-dir", type=Path, help="生成目录（默认写回仓内）")
    parser.add_argument("--mapping", action="store_true", help="只打印 operation → 契约类型映射表")
    args = parser.parse_args(argv)

    try:
        contract = load_contract(args.contract)
        operations = load_operations(args.operations)
        if args.mapping:
            print_mapping(contract, operations)
            return 0
        contract_sha = hashlib.sha256(args.contract.read_bytes()).hexdigest()
        models = generate_models(contract, operations, contract_sha)
        ops = generate_operations(contract, operations, contract_sha)
    except GenerationError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    out_dir = args.out_dir or REPO_ROOT
    for relative, content in ((MODELS_RELATIVE_PATH, models), (OPERATIONS_RELATIVE_PATH, ops)):
        target = Path(out_dir) / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        # 固定 LF：Windows 上刷新与 Linux 上 CI 复跑必须逐字节相同。
        target.write_text(content, encoding="utf-8", newline="\n")
        print(f"生成 {relative}（{len(content.encode('utf-8'))} 字节）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
