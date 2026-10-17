using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HistoryVulcan.Core.Modules;

namespace HistoryFama;

/// <summary>
/// 模块的运行时状态与业务：管桥配置（bridges.json）、跨桥取数与发送、把契约记录翻成页面表格的行。
/// </summary>
/// <remarks>
/// 自己**不存消息**：消息的真值在各平台的桥里，这里只做聚合与呈现，所以没有本地缓存要清理，
/// 也不存在「模块里的历史跟桥对不上」这种状态。桥掉线时如实报告离线，不返回上次的旧数据。
/// </remarks>
internal sealed class FamaState
{
    private const string ConfigFileName = "bridges.json";

    private static readonly JsonSerializerOptions ConfigOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IModuleContext? _context;
    private readonly FamaBridgeClient _client = new();
    private readonly object _gate = new();
    private List<FamaBridgeConfig>? _cache;
    private readonly FamaReadState _readState;

    /// <summary>宿主给的上下文；离线测试传 null，此时没有配置文件可读。</summary>
    public FamaState(IModuleContext? context)
    {
        _context = context;
        _readState = new(context == null ? null : Path.Combine(context.Environment.DataDirectory, "read-state.json"));
    }

    /// <summary>桥配置文件的绝对路径；没有上下文时是空串。</summary>
    public string ConfigPath => _context is null
        ? ""
        : Path.Combine(_context.Environment.DataDirectory, ConfigFileName);

    // ---------------------------------------------------------------- 配置

    /// <summary>已配置且可用的桥。配置文件坏了会抛出，不静默当没有桥。</summary>
    public IReadOnlyList<FamaBridgeConfig> Bridges()
    {
        lock (_gate)
        {
            _cache ??= Load();
            return _cache.ToList();
        }
    }

    private List<FamaBridgeConfig> Load()
    {
        var path = ConfigPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return [];

        FamaBridgeFile? file;
        try
        {
            file = JsonSerializer.Deserialize<FamaBridgeFile>(File.ReadAllText(path), ConfigOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new FamaBridgeException($"读不了桥配置 {path}：{ex.Message}");
        }

        return file?.Bridges?.Where(bridge => bridge.IsUsable).ToList() ?? [];
    }

    private void Save(IEnumerable<FamaBridgeConfig> bridges)
    {
        var path = ConfigPath;
        if (string.IsNullOrEmpty(path))
            throw new FamaBridgeException("模块没有数据目录，改不了桥配置。");

        var list = bridges.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new FamaBridgeFile { Bridges = list }, ConfigOptions),
            new UTF8Encoding(false));
        lock (_gate)
        {
            _cache = list;
        }
    }

    /// <summary>新增或更新一个桥；token 传空表示保留原来的，不覆盖。</summary>
    public string AddBridge(string id, string? platform, string? title, string baseUrl, string? token)
    {
        var list = Bridges().ToList();
        var bridge = list.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var created = bridge is null;
        if (bridge is null)
        {
            bridge = new FamaBridgeConfig { Id = id };
            list.Add(bridge);
        }

        if (!string.IsNullOrWhiteSpace(platform))
            bridge.Platform = platform.Trim();
        if (!string.IsNullOrWhiteSpace(title))
            bridge.Title = title.Trim();
        bridge.BaseUrl = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(bridge.Platform))
            bridge.Platform = bridge.Id;
        if (string.IsNullOrEmpty(bridge.Title))
            bridge.Title = bridge.Id.ToUpperInvariant();
        if (!string.IsNullOrEmpty(token))
            bridge.Token = token;

        Save(list);
        return created
            ? $"已新增桥 {bridge.Id} → {bridge.BaseUrl}（配置在 {ConfigPath}）"
            : $"已更新桥 {bridge.Id} → {bridge.BaseUrl}";
    }

    /// <summary>移除一个桥；没有这个桥就报错，不静默成功。</summary>
    public string RemoveBridge(string id)
    {
        var list = Bridges().ToList();
        var bridge = list.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                     ?? throw new FamaBridgeException($"没有这个桥：{id}（用 {FamaIdentity.Domain}.bridge.list 看有哪些）");
        list.Remove(bridge);
        Save(list);
        return $"已移除桥 {bridge.Id}";
    }

    private FamaBridgeConfig Require(string id)
        => Bridges().FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
           ?? throw new FamaBridgeException($"没有这个桥：{id}（用 {FamaIdentity.Domain}.bridge.list 看有哪些）");

    // ---------------------------------------------------------------- 取数

    /// <summary>探活一个桥。</summary>
    public Task<FamaBridgeMeta> MetaAsync(string id, CancellationToken cancellation)
        => _client.MetaAsync(Require(id), cancellation);

    /// <summary>聚合各桥的会话；指定了桥就只问那一个。</summary>
    public async Task<FamaGather<FamaConversation>> ConversationsAsync(
        string? bridgeId, string? query, string? kind, int limit, CancellationToken cancellation)
    {
        var targets = Targets(bridgeId);
        var items = new List<FamaConversation>();
        var problems = new List<string>();
        foreach (var bridge in targets)
        {
            try
            {
                var found = await _client.ConversationsAsync(bridge, query, kind, limit, cancellation)
                    .ConfigureAwait(false);
                items.AddRange(found.Select(item => item with { Bridge = bridge.Id, BridgeTitle = bridge.Display }));
            }
            catch (FamaBridgeException ex)
            {
                problems.Add($"{bridge.Display}：{ex.Message}");
            }
        }

        items = OrderConversations(items).ToList();
        if (items.Count > limit)
            items = items.GetRange(0, limit);
        return new FamaGather<FamaConversation>(items, problems);
    }

    /// <summary>REQ-FAMA-002：本地未读优先，每组再按最新消息倒序，时间相同时按身份保持稳定。</summary>
    internal IReadOnlyList<FamaConversation> OrderConversations(IEnumerable<FamaConversation> items)
        => items.Select(item => item with { BridgeUnread = item.Unread, Unread = _readState.Unread(item) })
            .OrderByDescending(item => item.Unread > 0).ThenByDescending(item => item.LastTsMs)
            .ThenBy(item => item.Bridge, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ConversationId, StringComparer.Ordinal).ToList();

    /// <summary>只确认已经成功显示的消息；迟到的旧水位不能吞掉更晚的新消息。</summary>
    public async Task MarkReadAsync(string bridgeId, string conversation, long through, long last, int unread)
    {
        var bridge = Require(bridgeId);
        if (last <= 0 || through < last || unread <= 0) return;
        if (_readState.Mark(bridge.Id, conversation, last, unread))
            await RefreshAsync(FamaPage.ConversationTableId).ConfigureAwait(false);
    }

    /// <summary>取某个桥里某个会话的消息。</summary>
    public async Task<IReadOnlyList<FamaMessage>> MessagesAsync(
        string bridgeId, string conversationId, int limit, CancellationToken cancellation)
        => await _client.MessagesAsync(Require(bridgeId), conversationId, limit, cancellation)
            .ConfigureAwait(false);

    public Task<byte[]> ImageAsync(string bridgeId, string path, CancellationToken cancellation)
        => _client.ImageAsync(Require(bridgeId), path, cancellation);

    /// <summary>把一个桥的配置与在线状态列出来，供 bridge.list 与页面桥表使用。</summary>
    public async Task<IReadOnlyList<FamaBridgeStatus>> StatusesAsync(CancellationToken cancellation)
    {
        var result = new List<FamaBridgeStatus>();
        foreach (var bridge in Bridges())
        {
            try
            {
                var meta = await _client.MetaAsync(bridge, cancellation).ConfigureAwait(false);
                result.Add(new FamaBridgeStatus(bridge, meta, ""));
            }
            catch (FamaBridgeException ex)
            {
                result.Add(new FamaBridgeStatus(bridge, null, ex.Message));
            }
        }

        return result;
    }

    /// <summary>发文字或图片，成功后刷新消息表。</summary>
    public async Task<FamaMessage> SendAsync(
        string bridgeId, string conversationId, string text, string? filePath, CancellationToken cancellation)
    {
        var bridge = Require(bridgeId);
        IReadOnlyList<FamaUpload>? attachments = string.IsNullOrWhiteSpace(filePath) ? null : [ReadAttachment(filePath)];
        FamaMessage sent;
        try
        {
            sent = await _client.SendAsync(bridge, conversationId, text, attachments, cancellation).ConfigureAwait(false);
        }
        catch (FamaBridgeException ex) when (ex.Code == "unsupported")
        {
            throw new FamaBridgeException($"{bridge.Display} 这条链路还不支持发送（桥把 send 能力标成 false）。", ex.Code);
        }

        await RefreshAsync(FamaPage.MessageTableId).ConfigureAwait(false);
        return sent;
    }

    private static FamaUpload ReadAttachment(string filePath)
    {
        var info = new FileInfo(Path.GetFullPath(filePath.Trim()));
        if (!info.Exists) throw new FamaBridgeException($"找不到附件文件：{filePath}", "bad_request");
        if (info.Length > 8L * 1024 * 1024) throw new FamaBridgeException("附件不能超过 8 MiB。", "bad_request");
        var contentType = info.Extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => throw new FamaBridgeException("目前只支持 PNG、JPEG、GIF 和 WebP 图片。", "bad_request"),
        };
        return new FamaUpload(info.Name, contentType, Convert.ToBase64String(File.ReadAllBytes(info.FullName)));
    }

    private IReadOnlyList<FamaBridgeConfig> Targets(string? bridgeId)
    {
        if (string.IsNullOrWhiteSpace(bridgeId))
            return Bridges();
        return [Require(bridgeId)];
    }

    // ---------------------------------------------------------------- 页面行

    private static string SourceLabel(FamaBridgeConfig bridge) => bridge.Display + " · " + bridge.Id;

    public IReadOnlyList<IReadOnlyDictionary<string, string>> SourceRows()
        => new[] { FamaPage.AllSources }.Concat(Bridges().Select(SourceLabel))
            .Select(value => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["value"] = value }).ToList();

    public string? SourceBridge(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source == FamaPage.AllSources) return null;
        return Bridges().FirstOrDefault(bridge => SourceLabel(bridge).Equals(source, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new FamaBridgeException("来源已失效，请重新打开通讯录选择来源。");
    }

    /// <summary>桥表：状态、账号与收发能力，离线时把原因写在能力列。</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> BridgeRowsAsync(CancellationToken cancellation)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var status in await StatusesAsync(cancellation).ConfigureAwait(false))
        {
            rows.Add(new Dictionary<string, string>
            {
                ["id"] = status.Config.Id,
                ["platform"] = status.Config.Platform,
                ["title"] = status.Config.Display,
                ["baseUrl"] = status.Config.BaseUrl,
                ["state"] = status.Meta is null ? "离线" : "在线",
                ["account"] = status.Meta?.Account ?? "",
                ["ability"] = status.Meta is null ? status.Error : Ability(status.Meta),
            });
        }

        return rows;
    }

    /// <summary>会话行：沿用聚合结果的本地未读优先、组内最后消息倒序。</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ConversationRows(
        IEnumerable<FamaConversation> conversations)
        => conversations.Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            // 行键必须非空且唯一：桥 + 会话主键（同一平台可以配两个桥，平台名不唯一）
            ["id"] = item.Bridge + "|" + item.ConversationId,
            ["bridge"] = item.Bridge,
            ["source"] = string.IsNullOrWhiteSpace(item.BridgeTitle) ? item.Platform : item.BridgeTitle,
            ["platform"] = item.Platform,
            ["conversation"] = item.ConversationId,
            ["title"] = item.Title,
            ["subtitle"] = string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(item.BridgeTitle) ? item.Platform : item.BridgeTitle,
                KindText(item.Kind),
                item.Members > 0 ? $"{item.Members} 人" : "",
            }.Where(value => value.Length > 0)),
            ["kind"] = KindText(item.Kind),
            ["members"] = item.Members > 0 ? item.Members.ToString(CultureInfo.InvariantCulture) : "",
            ["last"] = OneLine(item.LastText),
            ["time"] = Timestamp(item.LastTsMs),
            ["when"] = ShortTime(item.LastTsMs, DateTimeOffset.Now),
            ["unread"] = item.Unread > 0 ? item.Unread.ToString(CultureInfo.InvariantCulture) : "",
            ["bridge_unread"] = (item.BridgeUnread ?? item.Unread).ToString(CultureInfo.InvariantCulture),
            ["last_ts_ms"] = item.LastTsMs.ToString(CultureInfo.InvariantCulture),
        }).ToList();

    /// <summary>消息表：某会话按时间升序。</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> MessageRows(IEnumerable<FamaMessage> messages)
        => messages.Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["id"] = item.Platform + "|" + item.MessageId,
            ["time"] = Timestamp(item.TsMs),
            ["ts_ms"] = item.TsMs.ToString(CultureInfo.InvariantCulture),
            ["who"] = item.Direction == "out"
                ? "我"
                : string.IsNullOrWhiteSpace(item.SenderName) ? item.SenderId : item.SenderName,
            ["text"] = item.Files.Any(file => file.IsImage && !string.IsNullOrWhiteSpace(file.Url))
                ? ImageCaption(item)
                : item.Text + (item.Attachments > 0 && item.Files.Count == 0
                ? $"（{item.Attachments.ToString(CultureInfo.InvariantCulture)} 个附件）"
                : ""),
            ["kind"] = item.Kind,
            ["direction"] = item.Direction,
            ["images"] = JsonSerializer.Serialize(item.Files.Where(file => file.IsImage).Select(file => new { name = file.Name, path = file.Url ?? "" })),
            ["attachment_summary"] = string.Join("\n", item.Files.Where(file => !file.IsImage).Select(file => file.Name + (file.Size > 0 ? $" · {file.Size:N0} 字节" : ""))),
            ["media_hint"] = item.Kind == "image" && !item.Files.Any(file => file.IsImage) ? "图片原件暂不可用" : "",
        }).ToList();

    private static string ImageCaption(FamaMessage item)
    {
        var text = item.Text.Trim();
        if (text.Length == 0 || text.Equals("[图片]", StringComparison.OrdinalIgnoreCase)
            || text.Equals("图片", StringComparison.OrdinalIgnoreCase)
            || text.Equals("[image]", StringComparison.OrdinalIgnoreCase)
            || text.Equals("image", StringComparison.OrdinalIgnoreCase)
            || item.Files.Any(file => file.IsImage && text.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
            return "";
        return item.Text;
    }

    private static string Ability(FamaBridgeMeta meta)
    {
        var receive = meta.Receive ? "收 ✓" : "收 ✗";
        var send = meta.Send ? "发 ✓" : "发 ✗";
        var files = meta.SendAttachments ? "附件 ✓" : "附件 ✗";
        return $"{receive} {send} {files}｜{meta.Conversations} 会话 / {meta.Messages} 条";
    }

    private static string KindText(string kind) => kind switch
    {
        "private" => "单聊",
        "group" => "群聊",
        _ => kind,
    };

    private static string Timestamp(long tsMs) => tsMs <= 0
        ? ""
        : DateTimeOffset.FromUnixTimeMilliseconds(tsMs).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>通讯录窄栏用的短时间：今天只写时分，今年写月日时分，更早写日期。</summary>
    internal static string ShortTime(long tsMs, DateTimeOffset now)
    {
        if (tsMs <= 0)
            return "";
        var local = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).ToOffset(now.Offset);
        var format = local.Date == now.Date ? "HH:mm"
            : local.Year == now.Year ? "MM-dd HH:mm"
            : "yyyy-MM-dd";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    // ---------------------------------------------------------------- 页面刷新

    /// <summary>数据在界面之外被改了，让 Aurora 重取那一张表；页面没开着或没装 Aurora 时刷不到，不算失败。</summary>
    public async Task RefreshAsync(string nodeId)
    {
        if (_context is null)
            return;
        try
        {
            await _context.Bus.ExecuteAsync(
                $"aurora.ui.refreshdata node={nodeId}",
                // 来源由宿主盖章（module:HistoryFama），模块不自填。
                "").ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>一个桥的配置加它的在线状态；Meta 为 null 表示没连上，原因在 Error 里。</summary>
internal sealed record FamaBridgeStatus(FamaBridgeConfig Config, FamaBridgeMeta? Meta, string Error);
