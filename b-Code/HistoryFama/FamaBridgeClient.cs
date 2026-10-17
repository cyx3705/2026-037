using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HistoryFama;

/// <summary>桥按契约回了一个错误码，或压根没连上。</summary>
/// <remarks>Code 取契约里的 error.code（unauthorized / not_found / unsupported / upstream_error…），
/// 网络层失败是 transport。指令层把它翻成一句人话，不把原始英文吞掉。</remarks>
internal sealed class FamaBridgeException(string message, string code = "transport") : Exception(message)
{
    /// <summary>契约里的 error.code；连不上桥时为 transport。</summary>
    public string Code { get; } = code;
}

/// <summary>
/// 统一桥 HTTP API 的客户端。路径与字段照 b-Code-Bridge.md，字段只增不减，
/// 所以这里一律按名字取、取不到给默认值，多出来的字段不理会。
/// </summary>
internal sealed class FamaBridgeClient
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _images = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    internal const int MaxImageBytes = 8 * 1024 * 1024;

    internal static bool IsAttachmentPath(string? path)
    {
        const string prefix = "/api/v1/attachments/";
        if (path == null || !path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var name = Uri.UnescapeDataString(path[prefix.Length..]);
        return name.Length > 0 && name is not "." and not ".."
            && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '%');
    }

    /// <summary>REQ-FAMA-003：图片只从配置桥的附件端点读取，令牌只放请求头，拒绝跳转和超限响应。</summary>
    public async Task<byte[]> ImageAsync(FamaBridgeConfig config, string path, CancellationToken cancellation)
    {
        if (!IsAttachmentPath(path)) throw new FamaBridgeException("图片附件路径无效。", "invalid_attachment");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        cancellation = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, config.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(config.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        using var response = await _images.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new FamaBridgeException($"图片下载失败（HTTP {(int)response.StatusCode}）。");
        if (!(response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))
            throw new FamaBridgeException("附件不是可预览的图片。");
        if (response.Content.Headers.ContentLength > MaxImageBytes) throw new FamaBridgeException("图片超过 8 MB，暂不预览。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxImageBytes) throw new FamaBridgeException("图片超过 8 MB，暂不预览。");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0) throw new FamaBridgeException("图片内容为空。");
        return output.ToArray();
    }

    /// <summary>探活并取回桥的能力与计数。</summary>
    public async Task<FamaBridgeMeta> MetaAsync(FamaBridgeConfig config, CancellationToken cancellation)
    {
        var data = await RequestAsync(config, HttpMethod.Get, "/api/v1/meta", null, cancellation)
            .ConfigureAwait(false);
        var capabilities = Member(data, "capabilities");
        var counts = Member(data, "counts");
        var range = Member(data, "range");
        return new FamaBridgeMeta(
            Text(data, "platform"),
            Text(data, "account"),
            Text(data, "bridge_version"),
            Flag(capabilities, "receive"),
            Flag(capabilities, "send"),
            Flag(capabilities, "send_attachments"),
            Flag(capabilities, "realtime"),
            Number(counts, "conversations"),
            Number(counts, "messages"),
            Long(range, "min_ts_ms"),
            Long(range, "max_ts_ms"));
    }

    /// <summary>取会话列表（按最后消息倒序）。</summary>
    public async Task<IReadOnlyList<FamaConversation>> ConversationsAsync(
        FamaBridgeConfig config, string? query, string? kind, int limit, CancellationToken cancellation)
    {
        var parts = new List<string> { "limit=" + limit.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(query))
            parts.Add("q=" + Uri.EscapeDataString(query));
        if (!string.IsNullOrWhiteSpace(kind))
            parts.Add("kind=" + Uri.EscapeDataString(kind));
        var data = await RequestAsync(config, HttpMethod.Get,
            "/api/v1/conversations?" + string.Join("&", parts), null, cancellation).ConfigureAwait(false);

        var list = new List<FamaConversation>();
        foreach (var item in Items(data))
        {
            list.Add(new FamaConversation(
                Text(item, "platform"),
                Text(item, "conversation_id"),
                Text(item, "title"),
                Text(item, "kind"),
                Number(item, "members"),
                Long(item, "last_ts_ms"),
                Text(item, "last_text"),
                Number(item, "unread")));
        }

        return list;
    }

    /// <summary>取某个会话的消息（按时间升序）。</summary>
    public async Task<IReadOnlyList<FamaMessage>> MessagesAsync(
        FamaBridgeConfig config, string conversationId, int limit, CancellationToken cancellation)
    {
        var path = "/api/v1/conversations/" + Uri.EscapeDataString(conversationId) + "/messages"
                   + "?limit=" + limit.ToString(CultureInfo.InvariantCulture);
        var data = await RequestAsync(config, HttpMethod.Get, path, null, cancellation).ConfigureAwait(false);
        return ReadMessages(data);
    }

    /// <summary>发文字或图片，返回桥落库后的 out 消息。</summary>
    public async Task<FamaMessage> SendAsync(
        FamaBridgeConfig config, string conversationId, string text,
        IReadOnlyList<FamaUpload>? attachments, CancellationToken cancellation)
    {
        var path = "/api/v1/conversations/" + Uri.EscapeDataString(conversationId) + "/messages";
        var body = JsonSerializer.Serialize(new { text, attachments }, WriteOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var data = await RequestAsync(config, HttpMethod.Post, path, content, cancellation).ConfigureAwait(false);
        return ReadMessage(data);
    }

    public Task<FamaMessage> SendAsync(FamaBridgeConfig config, string conversationId, string text, CancellationToken cancellation)
        => SendAsync(config, conversationId, text, null, cancellation);

    private static IReadOnlyList<FamaMessage> ReadMessages(JsonElement data)
    {
        var list = new List<FamaMessage>();
        foreach (var item in Items(data))
            list.Add(ReadMessage(item));
        return list;
    }

    private static FamaMessage ReadMessage(JsonElement item)
    {
        var attachments = Member(item, "attachments");
        return new FamaMessage(
            Text(item, "platform"),
            Text(item, "message_id"),
            Text(item, "conversation_id"),
            Long(item, "ts_ms"),
            Text(item, "direction"),
            Text(item, "sender_id"),
            Text(item, "sender_name"),
            Text(item, "kind"),
            Text(item, "text"),
            attachments.ValueKind == JsonValueKind.Array ? attachments.GetArrayLength() : 0)
        {
            Files = attachments.ValueKind == JsonValueKind.Array
                ? attachments.EnumerateArray().Select(file => new FamaAttachment(Text(file, "name"), Text(file, "content_type"),
                    Long(file, "size"), IsAttachmentPath(Text(file, "url")) ? Text(file, "url") : null)).ToList()
                : [],
        };
    }

    private async Task<JsonElement> RequestAsync(
        FamaBridgeConfig config, HttpMethod method, string path, HttpContent? content, CancellationToken cancellation)
    {
        var url = config.BaseUrl.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(config.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new FamaBridgeException($"连不上桥 {config.Display}（{config.BaseUrl}）：{ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return Unwrap(config, body);
        }
    }

    /// <summary>拆契约的回执信封：成功取 data，失败按 error.code 抛出。</summary>
    private static JsonElement Unwrap(FamaBridgeConfig config, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new FamaBridgeException($"桥 {config.Display} 返回的不是 JSON：{Clip(body)}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("data", out var data))
            {
                return data.Clone();
            }

            var code = "internal";
            var message = Clip(body);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String)
                    code = codeValue.GetString() ?? code;
                if (error.TryGetProperty("message", out var textValue) && textValue.ValueKind == JsonValueKind.String)
                    message = textValue.GetString() ?? message;
            }

            throw new FamaBridgeException($"桥 {config.Display} 拒绝：{message}", code);
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement data)
        => data.ValueKind == JsonValueKind.Object
           && data.TryGetProperty("items", out var items)
           && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    private static JsonElement Member(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string Text(JsonElement element, string name)
    {
        var value = Member(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static long Long(JsonElement element, string name)
    {
        var value = Member(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return long.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int Number(JsonElement element, string name)
    {
        var parsed = Long(element, name);
        return parsed is > int.MaxValue or < int.MinValue ? 0 : (int)parsed;
    }

    private static bool Flag(JsonElement element, string name)
        => Member(element, name).ValueKind == JsonValueKind.True;

    private static string Clip(string body)
    {
        var one = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 200 ? one : one[..200] + "…";
    }
}
