using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form = new AcceptanceForm(
            int.Parse(Value(args, "--run")),
            Path.GetFullPath(Value(args, "--assets")),
            Path.GetFullPath(Value(args, "--output")),
            Path.GetFullPath(Value(args, "--profile")));
        Application.Run(form);
        Environment.ExitCode = form.Succeeded ? 0 : 1;
    }

    private static string Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : throw new ArgumentException($"Missing {name} argument.");
    }
}

internal sealed class AcceptanceForm : Form
{
    private static readonly string[] RequiredAssets = ["index.html", "cytoscape.min.js", "graph.css", "graph.js"];
    private readonly int _run;
    private readonly string _assetsPath;
    private readonly string _outputPath;
    private readonly string _profilePath;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timeout = new() { Interval = 60_000 };
    private readonly Stopwatch _hostElapsed = new();
    private bool _snapshotSent;
    private bool _completed;
    private bool _visibleAtSend;
    private int _blockedRemoteRequests;

    public bool Succeeded { get; private set; }

    public AcceptanceForm(int run, string assetsPath, string outputPath, string profilePath)
    {
        _run = run;
        _assetsPath = assetsPath;
        _outputPath = outputPath;
        _profilePath = profilePath;
        Text = $"GuraFile graph first-frame acceptance — run {run}";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 800);
        TopMost = true;
        Controls.Add(_webView);
        _timeout.Tick += (_, _) => CompleteFailure("Timed out waiting for ready/firstFrameRendered.");
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            ValidateAssets();
            Directory.CreateDirectory(_profilePath);
            Activate();
            BringToFront();
            _timeout.Start();

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-background-networking --disable-component-update --disable-sync --no-first-run"
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, _profilePath, options);
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping(
                "graph.gurafile.local",
                _assetsPath,
                CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationStarting += (_, args) => args.Cancel = !IsLocalGraphUri(args.Uri);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (IsLocalGraphUri(args.Request.Uri)) return;
                _blockedRemoteRequests++;
                args.Response = core.Environment.CreateWebResourceResponse(
                    Stream.Null, 403, "Blocked", "Content-Type: text/plain");
            };
            core.WebMessageReceived += (_, args) => HandleWebMessage(args.WebMessageAsJson);
            core.Navigate("https://graph.gurafile.local/index.html");
        }
        catch (Exception exception)
        {
            CompleteFailure(exception.ToString());
        }
    }

    private void HandleWebMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "ready" && !_snapshotSent)
            {
                _snapshotSent = true;
                Activate();
                BringToFront();
                _visibleAtSend = Visible && WindowState != FormWindowState.Minimized && _webView.Visible;
                _hostElapsed.Restart();
                _webView.CoreWebView2.PostWebMessageAsJson(CreateSnapshotMessage());
                return;
            }

            if (type == "error")
            {
                CompleteFailure(root.TryGetProperty("payload", out var payload) ? payload.ToString() : json);
                return;
            }

            if (type != "firstFrameRendered" || !_snapshotSent) return;

            _hostElapsed.Stop();
            var metrics = root.GetProperty("payload");
            var jsMs = metrics.GetProperty("renderDurationMs").GetDouble();
            var nodeCount = metrics.GetProperty("nodeCount").GetInt32();
            var edgeCount = metrics.GetProperty("edgeCount").GetInt32();
            var hostMs = Math.Round(_hostElapsed.Elapsed.TotalMilliseconds, 2);
            var countsMatch = nodeCount == 310 && edgeCount == 300;
            var under1000 = jsMs < 1000 && hostMs < 1000;
            Succeeded = countsMatch && under1000 && _visibleAtSend && _blockedRemoteRequests == 0;

            WriteResult(new
            {
                run = _run,
                success = Succeeded,
                jsRenderDurationMs = jsMs,
                hostElapsedMs = hostMs,
                nodeCount,
                edgeCount,
                countsMatch,
                under1000,
                visibleAtSend = _visibleAtSend,
                clientSize = $"{ClientSize.Width}x{ClientSize.Height}",
                blockedRemoteRequests = _blockedRemoteRequests,
                webView2RuntimeVersion = _webView.CoreWebView2.Environment.BrowserVersionString,
                dotnetRuntime = Environment.Version.ToString(),
                os = Environment.OSVersion.VersionString,
                assetsPath = _assetsPath,
                assetSha256 = AssetHashes(),
                timestampUtc = DateTimeOffset.UtcNow
            });
            Finish();
        }
        catch (Exception exception)
        {
            CompleteFailure(exception.ToString());
        }
    }

    private static string CreateSnapshotMessage()
    {
        string[] categories = ["图片", "音频", "视频", "文档", "压缩包", "代码", "其他"];
        var files = Enumerable.Range(1, 300).Select(i => new
        {
            id = $"file:{i}",
            fileId = i,
            label = $"acceptance-file-{i:000}.txt",
            category = categories[(i - 1) % categories.Length]
        });
        var tags = Enumerable.Range(1, 10).Select(i => new
        {
            id = $"tag:{i}",
            tagId = i,
            label = $"acceptance-tag-{i:00}",
            source = "user",
            isBroad = false
        });
        var edges = Enumerable.Range(1, 300).Select(i => new
        {
            source = $"file:{i}",
            target = $"tag:{((i - 1) % 10) + 1}"
        });
        return JsonSerializer.Serialize(new
        {
            type = "renderSnapshot",
            version = "1.0",
            payload = new { fileCount = 300, files, tags, edges }
        });
    }

    private static bool IsLocalGraphUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("graph.gurafile.local", StringComparison.OrdinalIgnoreCase);

    private void ValidateAssets()
    {
        foreach (var name in RequiredAssets)
        {
            if (!File.Exists(Path.Combine(_assetsPath, name)))
                throw new FileNotFoundException($"Required graph asset missing: {name}");
        }
    }

    private Dictionary<string, string> AssetHashes() => RequiredAssets.ToDictionary(
        name => name,
        name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_assetsPath, name)))));

    private void CompleteFailure(string error)
    {
        if (_completed) return;
        _hostElapsed.Stop();
        WriteResult(new
        {
            run = _run,
            success = false,
            error,
            hostElapsedMs = _snapshotSent ? Math.Round(_hostElapsed.Elapsed.TotalMilliseconds, 2) : (double?)null,
            visibleAtSend = _visibleAtSend,
            blockedRemoteRequests = _blockedRemoteRequests,
            assetsPath = _assetsPath,
            timestampUtc = DateTimeOffset.UtcNow
        });
        Finish();
    }

    private void WriteResult(object result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        File.AppendAllText(_outputPath, JsonSerializer.Serialize(result) + Environment.NewLine);
    }

    private void Finish()
    {
        _completed = true;
        _timeout.Stop();
        BeginInvoke(Close);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timeout.Dispose();
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
