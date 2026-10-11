using System;
using System.Linq;
using System.Text;
using Pim.Client.Core.Security;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计 F-11 · 规格（<c>remediation/host-session/reviewed-spec.md</c>）§2–§4 的
/// **纯函数单元测试**：源比较、API 路径判定、wire 校验。
///
/// 这些用例今天是绿的，它们**不能单独**作为 F-11 的证据 —— 有效 RED 在
/// <see cref="HostSessionRedTests"/>（打在真实生产出口与“保持今天行为的接缝”上）。
///
/// 不覆盖（保持 NOT-VERIFIED，N-01）：WebView2 的真实导航、
/// <c>WebResourceRequested</c> 头注入、<c>NewWindowRequested</c>、<c>e.Source</c>、
/// <c>postMessage</c> 往返与 DPAPI 冷启动 —— 都需要 Windows runner。
/// </summary>
public sealed class WebViewCredentialBoundaryTests
{
    private const string ServerUrl = "https://pim.example.com";

    // ---- 源比较：合法来源 ---------------------------------------------------------

    [Theory]
    [InlineData("https://pim.example.com")]
    [InlineData("https://pim.example.com/")]
    [InlineData("https://pim.example.com/api/v1")]
    [InlineData("https://PIM.example.com")]
    public void OriginPolicy_AcceptsConfiguredOrigin(string url)
    {
        Assert.True(WebViewOriginPolicy.TryCreate(url, out var policy));
        Assert.True(policy.IsAllowedNavigation("https://pim.example.com/today"));
    }

    [Fact]
    public void OriginPolicy_NormalizesDefaultPorts()
    {
        Assert.True(WebViewOriginPolicy.TryCreate("https://pim.example.com:443", out var explicitPort));
        Assert.True(WebViewOriginPolicy.TryCreate("https://pim.example.com", out var implicitPort));
        Assert.Equal(implicitPort.Origin, explicitPort.Origin);
        Assert.True(explicitPort.IsSameOrigin("https://pim.example.com/today"));
    }

    [Fact]
    public void OriginPolicy_DistinguishesNonDefaultPort()
    {
        Assert.True(WebViewOriginPolicy.TryCreate("https://pim.example.com:8443", out var policy));
        Assert.True(policy.IsSameOrigin("https://pim.example.com:8443/api/v1/x"));
        Assert.False(policy.IsSameOrigin("https://pim.example.com/api/v1/x"));
    }

    [Fact]
    public void OriginPolicy_ComparesIdnHostAndKeepsIpv6BracketsInOrigin()
    {
        // 规格 §3.3：两边都取 IdnHost → Unicode 主机与对应 punycode 相等。
        Assert.True(WebViewOriginPolicy.TryCreate("https://例え.jp", out var unicode));
        Assert.True(unicode.IsSameOrigin("https://xn--r8jz45g.jp/today"));

        // 规格 §2：IPv6 的 apiOrigin 带方括号。
        Assert.True(WebViewOriginPolicy.TryCreate("http://[::1]:8080", out var ipv6));
        Assert.Equal("http://[::1]:8080", ipv6.Origin);
        Assert.True(ipv6.IsSameOrigin("http://[::1]:8080/api/v1/x"));

        // 规格 §3.5：尾部点保留，不与无点主机相等。
        Assert.True(WebViewOriginPolicy.TryCreate("https://pim.example.com.", out var trailingDot));
        Assert.False(trailingDot.IsSameOrigin("https://pim.example.com/today"));
    }

    // ---- 源比较：伪造来源必须全部拒绝 ---------------------------------------------

    [Theory]
    // userinfo 伪域：字符串里“夹着”合法域名，但真正的主机是 evil.com
    [InlineData("https://pim.example.com@evil.com/today")]
    [InlineData("https://evil.com#pim.example.com")]
    // 后缀伪域
    [InlineData("https://pim.example.com.evil.com/today")]
    [InlineData("https://evilpim.example.com/today")]
    // 非 http(s) 协议
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("blob:https://pim.example.com/abc")]
    [InlineData("ms-appx:///assets/index.html")]
    [InlineData("about:blank")]
    // 其它站外地址
    [InlineData("http://pim.example.com/today")]
    [InlineData("https://other.example.org/today")]
    [InlineData("//pim.example.com/today")]
    [InlineData("not a url")]
    [InlineData("https://pim.example.com\\@evil.com/today")]
    [InlineData("")]
    public void OriginPolicy_RejectsForgedOrForeignNavigations(string url)
    {
        Assert.True(WebViewOriginPolicy.TryCreate(ServerUrl, out var policy));
        Assert.False(policy.IsAllowedNavigation(url));
        Assert.False(policy.IsSameOrigin(url));
    }

    [Theory]
    [InlineData("https://pim.example.com@evil.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://pim.example.com")]
    [InlineData("https://pim.example.com/api/v1?token=leak")]
    [InlineData("")]
    [InlineData("   ")]
    public void OriginPolicy_RejectsBadConfigurationSources(string serverUrl)
    {
        Assert.False(WebViewOriginPolicy.TryCreate(serverUrl, out var policy));
        Assert.Null(policy);
    }

    // ---- Authorization 注入决策：只给允许的 origin + /api/ 路径 -------------------

    [Theory]
    [InlineData("https://pim.example.com/api/v1/pc/browser-tt/upload")]
    [InlineData("https://pim.example.com/api/")]
    public void ApiRequest_AllowedOnSameOriginApiPaths(string url)
    {
        Assert.True(WebViewOriginPolicy.TryCreate(ServerUrl, out var policy));
        Assert.True(policy.IsAllowedApiRequest(url));
        Assert.True(policy.TryBuildAuthorizationHeader(url, "tok-123", out var header));
        Assert.Equal("Bearer tok-123", header);
    }

    [Theory]
    // 同源但不是 /api/：静态资源、页面路由、第三方同源脚本都不给令牌
    [InlineData("https://pim.example.com/today")]
    [InlineData("https://pim.example.com/assets/app.js")]
    [InlineData("https://pim.example.com/apiary/x")]
    // 规格 §3.7：只有以 /api/ 开头才匹配；大小写与目录穿越按规范化后的路径判。
    [InlineData("https://pim.example.com/api")]
    [InlineData("https://pim.example.com/API/v1/x")]
    [InlineData("https://pim.example.com/api/../secret")]
    [InlineData("https://pim.example.com/api/%2e%2e/secret")]
    // 站外
    [InlineData("https://evil.com/api/v1/x")]
    [InlineData("https://pim.example.com.evil.com/api/v1/x")]
    // 伪协议
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("blob:https://pim.example.com/api/v1/x")]
    public void ApiRequest_RejectedForNonApiOrForeignTargets(string url)
    {
        Assert.True(WebViewOriginPolicy.TryCreate(ServerUrl, out var policy));
        Assert.False(policy.IsAllowedApiRequest(url));
        Assert.False(policy.TryBuildAuthorizationHeader(url, "tok-123", out var header));
        Assert.Null(header);
    }

    [Fact]
    public void ApiRequest_NoHeaderWhenThereIsNoToken()
    {
        Assert.True(WebViewOriginPolicy.TryCreate(ServerUrl, out var policy));
        Assert.False(policy.TryBuildAuthorizationHeader("https://pim.example.com/api/v1/x", null, out _));
        Assert.False(policy.TryBuildAuthorizationHeader("https://pim.example.com/api/v1/x", string.Empty, out _));
    }

    // ---- 宿主会话 wire：手写 JSON 夹具（不用被测序列化器生成期望值）--------------

    public static TheoryData<string> LegalFrames => new()
    {
        """{"v":1,"type":"page.ready","requestId":"req-ready-1","clientNonce":"nonce_12chars"}""",
        """{"v":1,"type":"page.refresh","requestId":"req-ref-1"}""",
        """{"v":1,"type":"page.logout","requestId":"req-logout-1"}""",
        """{"v":1,"type":"host.state","requestId":"req-ready-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"https://pim.example.com","authenticated":true}""",
        """{"v":1,"type":"host.refresh.result","requestId":"req-ref-1","ok":true}""",
        """{"v":1,"type":"host.refresh.result","requestId":"req-ref-1","ok":false,"reason":"transient"}""",
        """{"v":1,"type":"host.session.ended","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","reason":"logout","requestId":"req-logout-1"}""",
        """{"v":1,"type":"host.session.ended","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","reason":"expired"}""",
        """{"v":1,"type":"host.state","requestId":"req-ready-ipv6-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"http://[::1]:8080","authenticated":true}""",
        """{"v":1,"type":"host.state","requestId":"req-ready-ipv6-2","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"http://[::1]","authenticated":true}""",
        """{"v":1,"type":"host.state","requestId":"req-ready-ipv6-3","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"https://[2001:db8::1]","authenticated":true}""",
        """{"v":1,"type":"host.state","requestId":"req-ready-ipv6-4","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"https://[2001:db8::1]:8443","authenticated":true}""",
    };

    [Theory]
    [MemberData(nameof(LegalFrames))]
    public void Protocol_AcceptsTheSpecsLegalFrames(string json)
    {
        Assert.True(
            HostSessionProtocol.TryParse(Encoding.UTF8.GetBytes(json), out var message),
            $"规格里的合法帧被拒绝：{json}");
        Assert.NotNull(message);
        Assert.Empty(message!.ExtraFields);
    }

    public static TheoryData<string> IllegalFrames => new()
    {
        // 旧草稿 / 不存在的类型
        """{"v":1,"type":"session.hello","requestId":"req-1"}""",
        """{"v":1,"type":"page.ack","requestId":"req-1"}""",
        """{"v":1,"type":"page.unauthorized","requestId":"req-1"}""",
        """{"v":1,"type":"host.error"}""",
        // 版本
        """{"v":"1","type":"page.ready","requestId":"req-1","clientNonce":"nonce_12chars"}""",
        """{"v":2,"type":"page.ready","requestId":"req-1","clientNonce":"nonce_12chars"}""",
        // 缺必填
        """{"v":1,"type":"page.ready","requestId":"req-1"}""",
        """{"v":1,"type":"page.ready","clientNonce":"nonce_12chars"}""",
        """{"v":1,"type":"host.state","requestId":"req-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","authenticated":true}""",
        """{"v":1,"type":"host.session.ended","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc"}""",
        // 标识符格式
        """{"v":1,"type":"page.ready","requestId":"","clientNonce":"nonce_12chars"}""",
        """{"v":1,"type":"page.ready","requestId":"req 1","clientNonce":"nonce_12chars"}""",
        """{"v":1,"type":"page.ready","requestId":"req-1","clientNonce":"short"}""",
        """{"v":1,"type":"host.session.ended","handshakeId":"6F1E8C0A-1B2C-4D3E-8F90-123456789ABC","reason":"logout"}""",
        // 取值
        """{"v":1,"type":"host.state","requestId":"req-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"https://pim.example.com","authenticated":"yes"}""",
        """{"v":1,"type":"host.refresh.result","requestId":"req-1","ok":"yes"}""",
        """{"v":1,"type":"host.refresh.result","requestId":"req-1","ok":false,"reason":"whatever"}""",
        """{"v":1,"type":"host.session.ended","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","reason":"whatever"}""",
        """{"v":1,"type":"host.state","requestId":"req-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"https://pim.example.com/api/v1","authenticated":true}""",
        // IPv6 畸形 / userinfo / 路径
        """{"v":1,"type":"host.state","requestId":"req-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"http://::1:8080","authenticated":true}""",
        """{"v":1,"type":"host.state","requestId":"req-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"http://user:pass@[::1]:8080","authenticated":true}""",
        """{"v":1,"type":"host.state","requestId":"req-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"http://[::1]:8080/api","authenticated":true}""",
        // 非对象 / 非 JSON
        """["page.ready"]""",
        "\"page.ready\"",
        "42",
        "not json",
        "",
    };

    [Theory]
    [MemberData(nameof(IllegalFrames))]
    public void Protocol_RejectsFramesOutsideTheSpec(string json)
    {
        Assert.False(
            HostSessionProtocol.TryParse(Encoding.UTF8.GetBytes(json), out var message),
            $"规格外的帧被接受：{json}");
        Assert.Null(message);
    }

    [Fact]
    public void Protocol_RejectsOversizedFrames()
    {
        var filler = new string('x', HostSessionProtocol.MaxFrameBytes);
        var big = "{\"v\":1,\"type\":\"page.logout\",\"requestId\":\"req-1\",\"reason\":\"" + filler + "\"}";
        Assert.False(HostSessionProtocol.TryParse(Encoding.UTF8.GetBytes(big), out _));
    }

    [Fact]
    public void Protocol_TimeoutConstantsMatchTheSpec()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), HostSessionProtocol.ReadyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), HostSessionProtocol.RefreshTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), HostSessionProtocol.LogoutTimeout);
    }

    [Theory]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://[::1]", true)]
    [InlineData("https://[2001:db8::1]", true)]
    [InlineData("https://[2001:db8::1]:8443", true)]
    [InlineData("http://[::1]:80", false)]
    [InlineData("https://[2001:db8::1]:443", false)]
    [InlineData("http://::1:8080", false)]
    [InlineData("http://[::1]:8080/", false)]
    [InlineData("http://[::1]:8080/api", false)]
    [InlineData("http://user@[::1]:8080", false)]
    public void Protocol_IsOrigin_ValidatesIpv6Origins(string origin, bool expected)
    {
        Assert.Equal(expected, HostSessionProtocol.IsOrigin(origin));
    }

    [Theory]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://[::1]")]
    [InlineData("https://[2001:db8::1]")]
    [InlineData("https://[2001:db8::1]:8443")]
    [InlineData("https://pim.example.com")]
    [InlineData("https://pim.example.com:8443")]
    [InlineData("https://xn--r8jz45g.jp")]
    public void Protocol_IsOrigin_AcceptsDocumentOriginFromOriginPolicy(string serverUrl)
    {
        Assert.True(WebViewOriginPolicy.TryCreate(serverUrl, out var policy));
        Assert.True(HostSessionProtocol.IsOrigin(policy!.DocumentOrigin),
            $"DocumentOrigin '{policy.DocumentOrigin}' from serverUrl '{serverUrl}' should be valid according to HostSessionProtocol.IsOrigin");
    }


    [Fact]
    public void Protocol_ByteLimitBoundary_PureWhitespacePadding_MutantFailsOn4097()
    {
        var baseFrame = "{\"v\":1,\"type\":\"page.logout\",\"requestId\":\"req-1\"}";
        var frame4096 = baseFrame.PadRight(HostSessionProtocol.MaxFrameBytes, ' ');
        var frame4097 = baseFrame.PadRight(HostSessionProtocol.MaxFrameBytes + 1, ' ');
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(frame4096));
        Assert.Equal(4097, Encoding.UTF8.GetByteCount(frame4097));

        Assert.True(HostSessionProtocol.TryParse(Encoding.UTF8.GetBytes(frame4096), out var msg4096));
        Assert.NotNull(msg4096);
        Assert.False(HostSessionProtocol.TryParse(Encoding.UTF8.GetBytes(frame4097), out var msg4097));
        Assert.Null(msg4097);

        Assert.True(HostSessionProtocol.TryParseText(frame4096, out var msgText4096));
        Assert.NotNull(msgText4096);
        Assert.False(HostSessionProtocol.TryParseText(frame4097, out var msgText4097));
        Assert.Null(msgText4097);
    }

    [Fact]
    public void Protocol_StrictUtf8EncodingSeam_RejectsUnpairedSurrogatesEarly()
    {
        // 验证 StrictUtf8 接缝确实使用 EncoderExceptionFallback（不静默替换为 \uFFFD）
        Assert.IsType<EncoderExceptionFallback>(HostSessionProtocol.StrictUtf8.EncoderFallback);

        var textWithUnpairedSurrogate = "{\"v\":1,\"type\":\"page.logout\",\"requestId\":\"req-1\"}\uD800";

        // 反向对照（Control）：默认非 strict 的 Encoding.UTF8 会静默替换，绝不抛出 EncoderFallbackException
        var nonStrictBytes = Encoding.UTF8.GetBytes(textWithUnpairedSurrogate);
        Assert.NotEmpty(nonStrictBytes);

        // 正向断言：StrictUtf8 在编码阶段立即抛出 EncoderFallbackException
        Assert.Throws<EncoderFallbackException>(() => HostSessionProtocol.StrictUtf8.GetBytes(textWithUnpairedSurrogate));

        // 顶层入口 TryParseText 捕获异常并 fail-closed 返回 false
        Assert.False(HostSessionProtocol.TryParseText(textWithUnpairedSurrogate, out var message));
        Assert.Null(message);
    }

}
