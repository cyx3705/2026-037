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

    /// <summary>发一条文字消息，返回桥落库后的那条 out 消息。</summary>
    public async Task<FamaMessage> SendAsync(
        FamaBridgeConfig config, string conversationId, string text, CancellationToken cancellation)
    {
        var path = "/api/v1/conversations/" + Uri.EscapeDataString(conversationId) + "/messages";
        var body = JsonSerializer.Serialize(new { text }, WriteOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var data = await RequestAsync(config, HttpMethod.Post, path, content, cancellation).ConfigureAwait(false);
        return ReadMessage(data);
    }

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
            attachments.ValueKind == JsonValueKind.Array ? attachments.GetArrayLength() : 0);
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
