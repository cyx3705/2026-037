using System.Text.Encodings.Web;
using System.Text.Json;

namespace HistoryFama;

/// <summary>
/// Aurora 页面协议 V1：一页，上面一块收发面板，下面三张表（桥 / 会话 / 消息）。
/// </summary>
/// <remarks>
/// 三条规矩（离线测试守着）：owner 与 scene 用 <see cref="FamaIdentity.PageOwner"/>；
/// 每个按钮与可点单元格的 action 都在 <see cref="Actions()"/> 里声明；
/// 动作指向本模块自己的指令时，那条指令必须已注册（指向 aurora.* 的由 Aurora 自己保证）。
/// </remarks>
internal static class FamaPage
{
    /// <summary>页面 id，也是 aurora.ui.refreshdata page= 要填的值。</summary>
    public const string PageId = FamaIdentity.Domain;

    /// <summary>收发面板。</summary>
    public const string ActionPanelId = FamaIdentity.Domain + "-actions";

    /// <summary>桥表节点 id。</summary>
    public const string BridgeTableId = FamaIdentity.Domain + "-bridges";

    /// <summary>会话表节点 id。</summary>
    public const string ConversationTableId = FamaIdentity.Domain + "-conversations";

    /// <summary>消息表节点 id，也是发送成功后要刷新的那个节点。</summary>
    public const string MessageTableId = FamaIdentity.Domain + "-messages";

    /// <summary>会话表发布选中行的通道；消息表按它取数。</summary>
    public const string ConversationChannel = FamaIdentity.Domain + ".conversation";

    /// <summary>发送内容输入框的控件 id，动作参数里用 <c>{text}</c> 取它的当前值。</summary>
    public const string TextBoxId = "text";

    /// <summary>fama.ui.data 的三张视图。</summary>
    public const string ViewBridge = "bridge";

    /// <summary>见 <see cref="ViewBridge"/>。</summary>
    public const string ViewConversation = "conversation";

    /// <summary>见 <see cref="ViewBridge"/>。</summary>
    public const string ViewMessage = "message";

    /// <summary>页面一次取多少行；页面不需要翻页，够用即可。</summary>
    public const int PageLimit = 200;

    /// <summary>发送动作 id。</summary>
    public const string SendActionId = FamaIdentity.Domain + ".msg.send";

    /// <summary>桥探活动作 id。</summary>
    public const string TestActionId = FamaIdentity.Domain + ".bridge.test";

    /// <summary>刷新动作 id。</summary>
    public const string ReloadActionId = FamaIdentity.Domain + ".reload";

    /// <summary>刷新走 Aurora 自己的取数指令，不由本模块实现。</summary>
    public const string ReloadCommand = "aurora.ui.refreshdata";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string DataCommand => FamaIdentity.Domain + ".ui.data";

    /// <summary>页面描述；Aurora 拿它画页面。</summary>
    public static string Describe() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        owner = FamaIdentity.PageOwner,
        pages = new object[]
        {
            new
            {
                id = PageId,
                title = FamaIdentity.PageTitle,
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
                            type = "panel",
                            id = ActionPanelId,
                            text = "收发",
                            rows = new object[]
                            {
                                new
                                {
                                    widgets = new object[]
                                    {
                                        new { kind = "textbox", id = TextBoxId, label = "发送内容", flex = true },
                                        new { kind = "button", action = SendActionId, text = "发送" },
                                    },
                                },
                                new
                                {
                                    mode = "even",
                                    widgets = new object[]
                                    {
                                        new { kind = "button", action = ReloadActionId, text = "刷新" },
                                    },
                                },
                            },
                        },
                        new
                        {
                            type = "text",
                            style = "caption",
                            text = "消息的真值在各平台的桥里，这里只做聚合：先选一条会话，再看它的消息。"
                                   + "桥不通时在桥表里写明原因，不显示旧数据。",
                        },
                        new
                        {
                            type = "table",
                            id = BridgeTableId,
                            dataSource = new { command = DataCommand, args = new { view = ViewBridge } },
                            columns = new object[]
                            {
                                new { key = "state", title = "状态", width = "55" },
                                new { key = "title", title = "桥", width = "80" },
                                new { key = "account", title = "账号", width = "110" },
                                new
                                {
                                    key = "ability", title = "能力（点一下探活）", width = "*",
                                    cellAction = TestActionId, cellStyle = "button",
                                },
                            },
                        },
                        new
                        {
                            type = "table",
                            id = ConversationTableId,
                            channel = ConversationChannel,
                            dataSource = new { command = DataCommand, args = new { view = ViewConversation } },
                            columns = new object[]
                            {
                                new { key = "platform", title = "平台", width = "70" },
                                new { key = "title", title = "会话", width = "*" },
                                new { key = "kind", title = "类型", width = "55" },
                                new { key = "last", title = "最后一条", width = "250" },
                                new { key = "time", title = "时间", width = "140" },
                            },
                        },
                        new
                        {
                            type = "table",
                            id = MessageTableId,
                            dataSource = new
                            {
                                command = DataCommand,
                                args = new Dictionary<string, string>
                                {
                                    ["view"] = ViewMessage,
                                    ["bridge"] = "{selection." + ConversationChannel + ".platform}",
                                    ["conversation"] = "{selection." + ConversationChannel + ".conversation}",
                                },
                            },
                            columns = new object[]
                            {
                                new { key = "time", title = "时间", width = "140" },
                                new { key = "who", title = "发送者", width = "110" },
                                new { key = "text", title = "内容", width = "*" },
                            },
                        },
                    },
                },
            },
        },
    }, Options);

    /// <summary>动作声明；按钮、可点单元格都只绑这里的 id。</summary>
    public static string Actions() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        owner = FamaIdentity.PageOwner,
        actions = new object[]
        {
            new
            {
                id = SendActionId,
                title = "发送",
                command = SendActionId,
                args = new Dictionary<string, string>
                {
                    ["bridge"] = "{selection." + ConversationChannel + ".platform}",
                    ["conversation"] = "{selection." + ConversationChannel + ".conversation}",
                    ["text"] = "{" + TextBoxId + "}",
                },
                summary = "把输入框里的文字发到选中的会话",
            },
            new
            {
                id = TestActionId,
                title = "探活",
                command = TestActionId,
                args = new Dictionary<string, string> { ["id"] = "{id}" },
                summary = "探一下这个桥通不通，结果显示在控制台",
            },
            new
            {
                id = ReloadActionId,
                title = "刷新",
                command = ReloadCommand,
                args = new Dictionary<string, string> { ["page"] = PageId },
                summary = "重新拉取这一页三张表的数据",
            },
        },
    }, Options);
}
