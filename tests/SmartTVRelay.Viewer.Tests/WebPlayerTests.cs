using System.Net;
using System.Text.Json;

namespace SmartTVRelay.Viewer.Tests;

public sealed class WebPlayerTests : IDisposable
{
    private readonly ViewerFactory _f = new();
    private readonly HttpClient _c;

    public WebPlayerTests() => _c = _f.CreateClient();

    public void Dispose() { _c.Dispose(); _f.Dispose(); }

    [Fact]
    public async Task Root_ServesHtmlReferencingManifestAndScript()
    {
        var res = await _c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType!.MediaType);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("rel=\"manifest\" href=\"/manifest.webmanifest\"", html);
        Assert.Contains("/app.js", html);
        Assert.Contains("name=\"viewport\"", html);
    }

    [Fact]
    public async Task Manifest_IsValidJsonWithRequiredMembers()
    {
        var res = await _c.GetAsync("/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var m = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("name").GetString()));
        Assert.Equal("/", m.GetProperty("start_url").GetString());
        Assert.Equal("standalone", m.GetProperty("display").GetString());
        var icons = m.GetProperty("icons");
        Assert.True(icons.GetArrayLength() >= 2);
        foreach (var icon in icons.EnumerateArray())
        {
            var r = await _c.GetAsync(icon.GetProperty("src").GetString());
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
    }

    [Fact]
    public async Task ServiceWorker_ServedAsJavascript()
    {
        var res = await _c.GetAsync("/sw.js");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("javascript", res.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task ServiceWorker_NeverCachesApiOrHls()
    {
        var js = await (await _c.GetAsync("/sw.js")).Content.ReadAsStringAsync();
        Assert.Contains("NEVER_CACHE = ['/api/', '/hls/']", js);
        // the shell list (what gets cached) must not mention live routes
        var shellLine = js.Split('\n').Single(l => l.StartsWith("const SHELL"));
        Assert.DoesNotContain("/api", shellLine);
        Assert.DoesNotContain("/hls", shellLine);
        // and the fetch handler must bail out before respondWith for them
        Assert.True(js.IndexOf("NEVER_CACHE.some", StringComparison.Ordinal) < js.IndexOf("respondWith", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AppJs_RegistersServiceWorkerAndStaticFilesDoNotShadowApi()
    {
        var app = await (await _c.GetAsync("/app.js")).Content.ReadAsStringAsync();
        Assert.Contains("serviceWorker.register('/sw.js')", app);

        var channels = await _c.GetAsync("/api/channels");
        Assert.Equal(HttpStatusCode.OK, channels.StatusCode);
        Assert.Equal("application/json", channels.Content.Headers.ContentType!.MediaType);

        var hls = await _c.GetAsync("/hls/9.9/index.m3u8");
        Assert.Equal(HttpStatusCode.NotFound, hls.StatusCode);
    }
}
