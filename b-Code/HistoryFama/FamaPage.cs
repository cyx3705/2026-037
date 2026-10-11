using System.Text.Encodings.Web;
using System.Text.Json;

namespace HistoryFama;

/// <summary>
/// Aurora 页面协议 V1：左侧会话卡片和搜索，中间气泡消息时间线与多行发送栏（REQ-FAMA-001）。
/// </summary>
/// <remarks>
/// 两页之间只靠选择通道 <see cref="ConversationChannel"/> 接线：通讯录发布选中的会话，消息页按它取数、往它发。
/// 三条规矩（离线测试守着）：owner 与 scene 用 <see cref="FamaIdentity.PageOwner"/>；
/// 每个按钮、开关与可点单元格的 action 都在 <see cref="Actions()"/> 里声明；
/// 动作指向本模块自己的指令时，那条指令必须已注册（指向 aurora.* 的由 Aurora 自己保证）。
/// </remarks>
internal static class FamaPage
{
    /// <summary>通讯录页 id，也是 aurora.ui.show name= 要填的值。</summary>
    public const string ContactsPageId = FamaIdentity.Domain + "-contacts";

    /// <summary>消息页 id。</summary>
    public const string MessagesPageId = FamaIdentity.Domain + "-messages";

    /// <summary>通讯录顶端的接收面板（开关 + 刷新）。</summary>
    public const string ReceivePanelId = FamaIdentity.Domain + "-receive";

    /// <summary>消息页底部的发送栏。</summary>
    public const string SendPanelId = FamaIdentity.Domain + "-send";

    /// <summary>会话表（通讯录）节点 id；新消息到达时重取它。</summary>
    public const string ConversationTableId = FamaIdentity.Domain + "-conversations";

    /// <summary>消息表节点 id；新消息到达与发送成功后重取它。</summary>
    public const string MessageTableId = FamaIdentity.Domain + "-message-list";

    /// <summary>通讯录发布选中行的通道；消息表按它取数，发送按它定目标。</summary>
    public const string ConversationChannel = FamaIdentity.Domain + ".conversation";

    /// <summary>发送内容输入框的控件 id，动作参数里用 <c>{text}</c> 取它的当前值。</summary>
    public const string TextBoxId = "text";

    /// <summary>自动接收开关的控件 id。</summary>
    public const string AutoSwitchId = "auto";

    /// <summary>fama.ui.data 的三张视图；bridge 页面上已不用，留给控制台排障。</summary>
    public const string ViewBridge = "bridge";

    /// <summary>见 <see cref="ViewBridge"/>。</summary>
    public const string ViewConversation = "conversation";

    /// <summary>见 <see cref="ViewBridge"/>。</summary>
    public const string ViewMessage = "message";

    /// <summary>页面一次取多少行；页面不需要翻页，够用即可。</summary>
    public const int PageLimit = 200;

    /// <summary>发送动作 id。</summary>
    public const string SendActionId = FamaIdentity.Domain + ".msg.send";

    public const string ReadActionId = FamaIdentity.Domain + ".conv.read";

    /// <summary>自动接收开关的动作 id。</summary>
    public const string AutoActionId = FamaIdentity.Domain + ".recv.auto";

    /// <summary>手动刷新（重连实时流 + 重取两张表）的动作 id。</summary>
    public const string RefreshActionId = FamaIdentity.Domain + ".recv.refresh";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string DataCommand => FamaIdentity.Domain + ".ui.data";

    private static string Selected(string column) => "{selection." + ConversationChannel + "." + column + "}";

    /// <summary>页面描述；Aurora 拿它画页面。<paramref name="autoReceive"/> 是开关的初值。</summary>
    public static string Describe(bool autoReceive = true) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        owner = FamaIdentity.PageOwner,
        pages = new object[]
        {
            new
            {
                id = ContactsPageId,
                title = "通讯录",
                scene = FamaIdentity.PageOwner,
                placement = new { side = "left", ratio = 0.3, visible = true, singleton = true },
                content = new
                {
                    type = "stack",
                    gap = "tight",
                    children = new object[]
                    {
                        new
                        {
                            type = "panel",
                            id = ReceivePanelId,
                            text = "消息中心",
                            rows = new object[]
                            {
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new
                                        {
                                            kind = "switch",
                                            id = AutoSwitchId,
                                            label = "自动接收",
                                            value = autoReceive ? "true" : "false",
                                            commitAction = AutoActionId,
                                        },
                                        new { kind = "button", action = RefreshActionId, text = "刷新", icon = "refresh-cw" },
                                    },
                                },
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new { kind = "textbox", id = "query", label = "搜索", channel = "fama.search", value = "", flex = true },
                                    },
                                },
                            },
                        },
                        new
                        {
                            type = "conversationList",
                            id = ConversationTableId,
                            channel = ConversationChannel,
                            dataSource = new { command = DataCommand, args = new { view = ViewConversation, q = "{selection.fama.search.value}" } },
                        },
                    },
                },
            },
            new
            {
                id = MessagesPageId,
                title = "消息",
                scene = FamaIdentity.PageOwner,
                placement = new { side = "center", visible = true, singleton = true },
                content = new
                {
                    type = "stack",
                    gap = "tight",
                    children = new object[]
                    {
                        new
                        {
                            type = "chatTimeline",
                            readAction = ReadActionId,
                            id = MessageTableId,
                            source = Selected("title"),
                            text = "消息",
                            dataSource = new
                            {
                                command = DataCommand,
                                args = new Dictionary<string, string>
                                {
                                    ["view"] = ViewMessage,
                                    ["bridge"] = Selected("bridge"),
                                    ["conversation"] = Selected("conversation"),
                                },
                            },
                        },
                        new
                        {
                            type = "panel",
                            id = SendPanelId,
                            text = "写消息",
                            rows = new object[]
                            {
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new { kind = "textbox", id = TextBoxId, label = "", multiline = true, flex = true },
                                    },
                                },
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new { kind = "text", text = "Enter 换行 · 发送前确认收件人", flex = true },
                                        new
                                        {
                                            kind = "button",
                                            action = SendActionId,
                                            text = "发送",
                                            enabledWhen = new { selected = ConversationChannel },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        },
    }, Options);

    /// <summary>动作声明；按钮、开关、可点单元格都只绑这里的 id。</summary>
    public static string Actions() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        owner = FamaIdentity.PageOwner,
        actions = new object[]
        {
            new
            {
                id = ReadActionId,
                title = "本地已读",
                command = ReadActionId,
                args = new Dictionary<string, string>
                {
                    ["bridge"] = Selected("bridge"),
                    ["conversation"] = Selected("conversation"),
                    ["through"] = "{readThrough}",
                    ["last"] = Selected("last_ts_ms"),
                    ["unread"] = Selected("bridge_unread"),
                },
                summary = "成功显示最新消息后清除 Fama 本地未读，不影响平台端",
            },
            new
            {
                id = SendActionId,
                title = "发送",
                command = SendActionId,
                args = new Dictionary<string, string>
                {
                    ["bridge"] = Selected("bridge"),
                    ["conversation"] = Selected("conversation"),
                    ["text"] = "{" + TextBoxId + "}",
                },
                summary = "把输入框里的文字发到通讯录里选中的会话",
            },
            new
            {
                id = AutoActionId,
                title = "自动接收",
                command = AutoActionId,
                args = new Dictionary<string, string> { ["on"] = "{value}" },
                summary = "开：桥来新消息就自动显示；关：只在点「刷新」时更新",
            },
            new
            {
                id = RefreshActionId,
                title = "刷新",
                command = RefreshActionId,
                args = new Dictionary<string, string>(),
                summary = "重连各桥的实时流并重取通讯录与消息（收消息卡住时用）",
            },
        },
    }, Options);
}
