using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryFama;

/// <summary>
/// 实时接收：对每个桥开一条 <c>/api/v1/events</c>（SSE），来了新消息就让 Aurora 重取通讯录与消息表。
/// </summary>
/// <remarks>
/// 只负责「提醒界面去取」，不拿事件里的消息拼表——消息的真值仍以桥的查询接口为准，
/// 所以漏掉一条事件也只是晚一点显示，不会显示错。断线按契约用 <c>since_ms=本桥最后一条 ts_ms</c> 重连。
/// 「自动接收」关掉时所有连接都断开，只剩手动刷新。
/// </remarks>
internal sealed class FamaLive : IDisposable
{
    private const string LogCategory = FamaIdentity.Domain + ".live";

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>一阵消息连着来时只刷一次。</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>契约里心跳约 20 秒一次；这么久一个字节都没收到，就当连接卡死，断开重连。</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(60);

    private readonly IModuleContext? _context;
    private readonly FamaState _state;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastTs = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _run;
    private int _refreshPending;
    private bool _disposed;

    /// <summary>离线测试传 null 上下文：不连任何桥。</summary>
    public FamaLive(IModuleContext? context, FamaState state)
    {
        _context = context;
        _state = state;
    }

    /// <summary>「自动接收」开关；模块装上时默认开。</summary>
    public bool Enabled { get; private set; } = true;

    /// <summary>拨开关，并按新状态重建连接；返回连着几个桥。</summary>
    public int SetEnabled(bool enabled)
    {
        Enabled = enabled;
        return Restart();
    }

    /// <summary>断开全部连接，按当前开关与桥配置重新连；返回连着几个桥（开关关着时为 0）。</summary>
    public int Restart()
    {
        lock (_gate)
        {
            // 只取消不释放：还在读流的循环手里拿着这个令牌，释放了它们再挂回调会抛
            _run?.Cancel();
            _run = null;
            if (!Enabled || _disposed || _context is null)
                return 0;

            IReadOnlyList<FamaBridgeConfig> bridges;
            try
            {
                bridges = _state.Bridges();
            }
            catch (FamaBridgeException ex)
            {
                Log(ShellLogLevel.Warn, $"实时接收没起来：{ex.Message}");
                return 0;
            }

            var run = new CancellationTokenSource();
            _run = run;
            foreach (var bridge in bridges)
                _ = Task.Run(() => ListenAsync(bridge, run.Token));
            return bridges.Count;
        }
    }

    /// <summary>让 Aurora 重取通讯录与消息表；安静执行，不往控制台回显。页面没开着时刷不到，不算失败。</summary>
    public async Task RefreshTablesAsync()
    {
        if (_context is null)
            return;
        foreach (var node in new[] { FamaPage.ConversationTableId, FamaPage.MessageTableId })
        {
            try
            {
                // 来源由宿主盖章（module:HistoryFama:live），模块不自填模块名。
                await _context.Bus.InvokeAsync($"aurora.ui.refreshdata node={node}", "live").ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>模块卸载时由宿主调用：断开全部连接。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _run?.Cancel();
            _run = null;
        }

        _http.Dispose();
    }

    private async Task ListenAsync(FamaBridgeConfig bridge, CancellationToken cancellation)
    {
        var backoff = MinBackoff;
        var healthy = true;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await ReadStreamAsync(bridge, () =>
                {
                    backoff = MinBackoff;
                    if (!healthy)
                        Log(ShellLogLevel.Info, $"{bridge.Display} 的实时流已恢复");
                    healthy = true;
                }, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (FamaBridgeException ex) when (ex.Code == "unsupported")
            {
                Log(ShellLogLevel.Info, $"{bridge.Display} 不支持实时流，只能手动刷新");
                return;
            }
            catch (Exception ex)
            {
                // 桥离线时每 30 秒重试一次，只在「好 → 坏」那一下记 Warn，免得刷屏
                Log(healthy ? ShellLogLevel.Warn : ShellLogLevel.Debug,
                    $"{bridge.Display} 的实时流断了，{backoff.TotalSeconds:0} 秒后重连：{ex.Message}");
                healthy = false;
            }

            try
            {
                await Task.Delay(backoff, cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>读一条 SSE 长连接直到它断；正常情况下永不返回，断了就抛出让外层重连。</summary>
    private async Task ReadStreamAsync(FamaBridgeConfig bridge, Action connected, CancellationToken cancellation)
    {
        long since;
        lock (_gate)
            _lastTs.TryGetValue(bridge.Id, out since);

        var url = bridge.BaseUrl.TrimEnd('/') + "/api/v1/events";
        if (since > 0)
            url += "?since_ms=" + since.ToString(CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(bridge.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bridge.Token);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new FamaBridgeException("桥不支持实时流", "unsupported");
        if (!response.IsSuccessStatusCode)
            throw new FamaBridgeException($"HTTP {(int)response.StatusCode}", "http");

        connected();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            using (var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                watchdog.CancelAfter(Silence);
                try
                {
                    line = await reader.ReadLineAsync(watchdog.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    throw new FamaBridgeException($"{Silence.TotalSeconds:0} 秒没有心跳");
                }
            }

            if (line is null)
                throw new FamaBridgeException("桥关闭了实时流");

            if (line.Length == 0)
            {
                Dispatch(bridge, eventName, data.ToString());
                eventName = null;
                data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }

            // 以冒号开头的是注释行，其余字段（id / retry）用不上，一律跳过
        }
    }

    private void Dispatch(FamaBridgeConfig bridge, string? eventName, string data)
    {
        if (eventName != "message")
            return;

        var ts = 0L;
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.TryGetProperty("ts_ms", out var value) && value.TryGetInt64(out var parsed))
                ts = parsed;
        }
        catch (JsonException)
        {
        }

        if (ts > 0)
        {
            lock (_gate)
            {
                if (!_lastTs.TryGetValue(bridge.Id, out var last) || ts > last)
                    _lastTs[bridge.Id] = ts;
            }
        }

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(Debounce).ConfigureAwait(false);
            Interlocked.Exchange(ref _refreshPending, 0);
            await RefreshTablesAsync().ConfigureAwait(false);
        });
    }

    private void Log(ShellLogLevel level, string message)
    {
        try
        {
            _context?.Log.Log(level, LogCategory, message);
        }
        catch (Exception)
        {
        }
    }
}
