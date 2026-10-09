using System.Text.Json.Serialization;

namespace HistoryFama;

/// <summary>
/// 一个消息桥的接入配置。桥的地址与 token 由用户填；模块不认识任何平台细节，
/// 只按统一契约（见 2026-036-MessageHub/b-Office/b-Code-Bridge.md）跟它说话。
/// </summary>
internal sealed class FamaBridgeConfig
{
    /// <summary>本模块内唯一的桥标识，指令里用它指定桥，例如 qq。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>平台名，与桥 /api/v1/meta 的 platform 一致，例如 qq。</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    /// <summary>界面上显示的名字，例如 QQ。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>桥的根地址，例如 http://127.0.0.1:8010（不带 /api/v1）。</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    /// <summary>桥的 Bearer token，与桥容器的 *_BRIDGE_TOKEN 一致。</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>地址与标识齐了才算能用；残缺的配置直接跳过，不让它去连。</summary>
    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>界面上怎么称呼这个桥。</summary>
    [JsonIgnore]
    public string Display => !string.IsNullOrWhiteSpace(Title)
        ? Title
        : !string.IsNullOrWhiteSpace(Platform) ? Platform : Id;
}

/// <summary>桥配置文件的整体形状；写进模块数据目录的 bridges.json。</summary>
internal sealed class FamaBridgeFile
{
    /// <summary>配置文件版本，当前只有 1。</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>已配置的桥。</summary>
    [JsonPropertyName("bridges")]
    public List<FamaBridgeConfig> Bridges { get; set; } = [];
}

/// <summary>桥 <c>/api/v1/meta</c> 的能力与计数，只取模块用得上的字段。</summary>
internal sealed record FamaBridgeMeta(
    string Platform,
    string Account,
    string BridgeVersion,
    bool Receive,
    bool Send,
    bool SendAttachments,
    bool Realtime,
    int Conversations,
    int Messages,
    long MinTsMs,
    long MaxTsMs);

/// <summary>一个会话（契约 3.1）。</summary>
internal sealed record FamaConversation(
    string Platform,
    string ConversationId,
    string Title,
    string Kind,
    int Members,
    long LastTsMs,
    string LastText,
    int Unread)
{
    /// <summary>
    /// 会话来自本模块配置里的哪个桥（bridges.json 的 id），取数与发送都按它找桥。
    /// 不是契约字段：桥只知道自己的 platform，由聚合时填上。
    /// </summary>
    public string Bridge { get; init; } = "";

    /// <summary>那个桥在界面上的名字（bridges.json 的 title，例如 钉钉），通讯录的「来源」列用它。</summary>
    public string BridgeTitle { get; init; } = "";
}

/// <summary>一条消息（契约 3.2）。</summary>
internal sealed record FamaMessage(
    string Platform,
    string MessageId,
    string ConversationId,
    long TsMs,
    string Direction,
    string SenderId,
    string SenderName,
    string Kind,
    string Text,
    int Attachments);

/// <summary>一次跨桥取数的结果：拿到的行，以及没答上来的桥各自的原因。</summary>
internal sealed record FamaGather<T>(IReadOnlyList<T> Items, IReadOnlyList<string> Problems)
{
    /// <summary>没有桥答上来时，取数算失败。</summary>
    public bool AllFailed => Items.Count == 0 && Problems.Count > 0;
}
