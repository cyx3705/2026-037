using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HistoryVulcan.Core.Modules;

namespace HistoryFama;

/// <summary>
/// Temporary local connector for the cloud MessageHub bridges. The connector is
/// deliberately isolated so Vulcan can take ownership of it later.
/// </summary>
internal sealed class FamaTunnel : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly FamaTunnelSettings _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _logPath;
    private readonly object _gate = new();
    private Process? _process;
    private Task? _loop;

    private FamaTunnel(FamaTunnelSettings settings, string logPath)
    {
        _settings = settings;
        _logPath = logPath;
    }

    public static FamaTunnel? Start(
        string dataDirectory, HostRunMode runMode, IEnumerable<FamaBridgeConfig> bridges)
    {
        if (runMode != HostRunMode.Service)
            return null;

        var bridgePorts = FamaTunnelSettings.Default.BridgePorts;
        var needsTunnel = bridges.Any(bridge =>
            Uri.TryCreate(bridge.BaseUrl, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address))
            && bridgePorts.Contains(uri.Port));
        if (!needsTunnel)
            return null;

        var settingsPath = Path.Combine(dataDirectory, "tunnel.json");
        FamaTunnelSettings? settings;
        try
        {
            if (File.Exists(settingsPath))
            {
                settings = JsonSerializer.Deserialize<FamaTunnelSettings>(
                    File.ReadAllText(settingsPath), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
            }
            else
            {
                settings = new FamaTunnelSettings();
                Directory.CreateDirectory(dataDirectory);
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"读不了隧道配置 {settingsPath}：{ex.Message}", ex);
        }

        if (settings is not { Enabled: true } || string.IsNullOrWhiteSpace(settings.Server))
            return null;

        var tunnel = new FamaTunnel(settings, Path.Combine(dataDirectory, "tunnel.log"));
        tunnel._loop = Task.Run(tunnel.RunAsync);
        return tunnel;
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
        }
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (await BridgesReachableAsync(_stop.Token).ConfigureAwait(false))
            {
                await DelayAsync(_stop.Token).ConfigureAwait(false);
                continue;
            }

            try
            {
                using var process = StartProcess();
                lock (_gate)
                    _process = process;
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error))
                    AppendLog(error.Trim());
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppendLog(ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _process = null;
                }
            }

            await DelayAsync(_stop.Token).ConfigureAwait(false);
        }
    }

    private Process StartProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Expand(_settings.SshPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ExitOnForwardFailure=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=15");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveInterval=15");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveCountMax=3");
        if (!string.IsNullOrWhiteSpace(_settings.KeyPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(Expand(_settings.KeyPath));
        }

        foreach (var port in _settings.Ports.Distinct().Where(port => port is > 0 and <= 65535))
        {
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add($"{port}:127.0.0.1:{port}");
        }
        psi.ArgumentList.Add(_settings.Server.Trim());

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"启动 SSH 失败：{psi.FileName}");
        return process;
    }

    private async Task<bool> BridgesReachableAsync(CancellationToken cancellation)
    {
        foreach (var port in _settings.BridgePorts)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", port, cancellation).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellation.IsCancellationRequested)
            {
                return false;
            }
        }
        return true;
    }

    private async Task DelayAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(RetryDelay, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void AppendLog(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Expand(string value)
        => Environment.ExpandEnvironmentVariables(value.Trim());
}

internal sealed class FamaTunnelSettings
{
    public static FamaTunnelSettings Default => new();

    public bool Enabled { get; set; } = true;
    public string Server { get; set; } = "root@101.132.190.76";
    public string SshPath { get; set; } = "ssh.exe";
    public string KeyPath { get; set; } = "%USERPROFILE%\\.ssh\\messagehub-cloud.pem";
    public int[] Ports { get; set; } = [8010, 8011, 8012, 6080, 6081, 6099];
    public int[] BridgePorts { get; set; } = [8010, 8011, 8012];
}
