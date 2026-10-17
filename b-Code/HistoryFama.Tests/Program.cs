using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HistoryFama;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

Console.OutputEncoding = Encoding.UTF8;

// 全部离线。最后一组把打好的包交给已发布宿主的命令行 `--probe` 装一遍（宿主 5.9.0 起的统一契约）——
// 版本漂移、manifest 写错、身份类缺失在宿主那边都是「静默跳过整个模块」，只有这样才能在出包前拦住。
// 中间两组用标准库起一个假桥（TcpListener 说 HTTP/1.1），把契约回执的形状钉死，
// 不依赖 2026-036-MessageHub 的容器、docker 或任何真桥。
var tests = new (string Name, Func<Task> Run)[]
{
    ("command registration", Sync(TestCommandRegistration)),
    ("page owner follows domain", Sync(TestPageOwner)),
    ("buttons, actions and commands line up", Sync(TestPageWiring)),
    ("REQ-FAMA-001 messenger interface", Sync(TestMessengerInterface)),
    ("REQ-FAMA-004 send and source controls", TestSendAndSourceControls),
    ("versions agree", Sync(TestVersionsAgree)),
    ("tunnel defaults and probe isolation", Sync(TestTunnelDefaults)),
    ("bridge config round-trips", Sync(TestBridgeConfig)),
    ("page rows follow the contract", Sync(TestPageRows)),
    ("REQ-FAMA-002 local read and unread ordering", TestLocalRead),
    ("bridge client speaks the contract", TestBridgeClient),
    ("host loads the package", TestHostLoadsPackage),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static Func<Task> Sync(Action test) => () =>
{
    test();
    return Task.CompletedTask;
};

static Registrar Registry()
{
    var registry = new Registrar();
    var state = new FamaState(null);
    HistoryFamaModule.Register(registry, state, new FamaLive(null, state));
    return registry;
}

static void TestCommandRegistration()
{
    var registry = Registry();
    var domain = FamaIdentity.Domain;
    string[] expected =
    [
        domain + ".bridge.list",
        domain + ".bridge.add",
        domain + ".bridge.remove",
        domain + ".bridge.test",
        domain + ".conv.list",
        domain + ".conv.read",
        domain + ".msg.list",
        domain + ".msg.send",
        domain + ".recv.auto",
        domain + ".recv.refresh",
        domain + ".ui.describe",
        domain + ".ui.actions",
        domain + ".ui.data",
        domain + ".ui.image",
    ];
    foreach (var name in expected)
    {
        True(registry.TryGet(name, out var descriptor), $"未注册 {name}");
        Equal(domain, descriptor!.Domain!);
    }

    Equal(expected.Length, registry.All.Count(d => d.Name.StartsWith(domain + ".", StringComparison.Ordinal)));

    foreach (var method in new[] { "describe", "actions", "data", "image" })
    {
        True(registry.TryGet(domain + ".ui." + method, out var ui), $"缺少 {domain}.ui.{method}");
        True(ui!.Readonly, $"{domain}.ui.{method} 必须只读");
        True(ui.HiddenReason is not null, $"{domain}.ui.{method} 是界面内部协议，必须 HiddenReason");
    }

    // 写配置、拨开关、重连和发送不能标只读；显式发送按钮不再弹二次确认。
    foreach (var name in new[] { domain + ".bridge.add", domain + ".bridge.remove", domain + ".recv.auto", domain + ".recv.refresh", domain + ".conv.read" })
    {
        registry.TryGet(name, out var write);
        True(!write!.Readonly, $"{name} 会改配置，不能标只读");
    }

    registry.TryGet(domain + ".msg.send", out var send);
    Equal(CommandLevel.Run, send!.Level);
    True(send.ConfirmPrompt is null, "发送不能再弹二次确认");
    True(!send.Readonly, "发送仍是写操作，不能投影为只读 MCP");
    True(send.Parameters.Any(p => p.Name == "file" && !p.Required), "发送必须接受可选的图片参数");
    True(send.Parameters.Any(p => p.Name == "text" && !p.Required), "纯图片发送不能要求正文");

    // 只读的查询指令才允许投影到 MCP，这里逐条钉住
    foreach (var name in new[] { domain + ".bridge.list", domain + ".bridge.test", domain + ".conv.list", domain + ".msg.list" })
    {
        registry.TryGet(name, out var query);
        True(query!.Readonly, $"{name} 是只读查询");
    }
}

static void TestPageOwner()
{
    // Aurora 的判据：owner = "History" + 首字母大写的指令域；对不上整页被静默拒收。
    var expected = "History" + char.ToUpperInvariant(FamaIdentity.Domain[0]) + FamaIdentity.Domain[1..];
    Equal(expected, FamaIdentity.PageOwner);

    using var description = JsonDocument.Parse(FamaPage.Describe());
    var root = description.RootElement;
    Equal(expected, root.GetProperty("owner").GetString()!);
    Equal(1, root.GetProperty("schemaVersion").GetInt32());
    var ids = new List<string>();
    foreach (var page in root.GetProperty("pages").EnumerateArray())
    {
        Equal(expected, page.GetProperty("scene").GetString()!);
        ids.Add(page.GetProperty("id").GetString()!);
    }

    // 两个窗口：左边通讯录、中间消息；页 id 小写无空格
    Equal(string.Join(",", FamaPage.ContactsPageId, FamaPage.MessagesPageId), string.Join(",", ids));
    foreach (var id in ids)
        True(id == id.ToLowerInvariant() && !id.Contains(' '), $"页 id 必须小写无空格：{id}");

    using var actions = JsonDocument.Parse(FamaPage.Actions());
    Equal(expected, actions.RootElement.GetProperty("owner").GetString()!);
}

static void TestPageWiring()
{
    var registry = Registry();
    using var actions = JsonDocument.Parse(FamaPage.Actions());
    var declared = new HashSet<string>(StringComparer.Ordinal);
    foreach (var action in actions.RootElement.GetProperty("actions").EnumerateArray())
    {
        var id = action.GetProperty("id").GetString()!;
        var command = action.GetProperty("command").GetString()!;
        True(declared.Add(id), $"动作 id 重复：{id}");
        if (command.StartsWith(FamaIdentity.Domain + ".", StringComparison.Ordinal))
            True(registry.TryGet(command, out _), $"动作 {id} 指向未注册的指令 {command}");
        else
            True(command.Contains('.'), $"动作 {id} 的指令名不像三段式：{command}");
    }

    using var description = JsonDocument.Parse(FamaPage.Describe());
    var nodes = Descendants(description.RootElement).ToList();
    foreach (var button in nodes.Where(node => node.TryGetProperty("kind", out var kind) && kind.GetString() == "button"))
    {
        var action = button.GetProperty("action").GetString()!;
        True(declared.Contains(action), $"按钮指向未声明的动作 {action}");
    }

    foreach (var toggle in nodes.Where(node => node.TryGetProperty("kind", out var kind) && kind.GetString() == "switch"))
    {
        var action = toggle.GetProperty("commitAction").GetString()!;
        True(declared.Contains(action), $"开关指向未声明的动作 {action}");
    }

    foreach (var cell in nodes.Where(node => node.TryGetProperty("cellAction", out _)))
    {
        var action = cell.GetProperty("cellAction").GetString()!;
        True(declared.Contains(action), $"可点单元格指向未声明的动作 {action}");
    }

    foreach (var source in nodes.Where(node => node.TryGetProperty("dataSource", out _)))
    {
        var command = source.GetProperty("dataSource").GetProperty("command").GetString()!;
        True(registry.TryGet(command, out _), $"表格取数指令未注册：{command}");
    }

    // 通讯录发布通道、消息表按通道取数、发送动作按通道取目标：断一条两页就联动不起来。
    // 找桥必须用会话行的 bridge 列（配置 id），不能用 platform——两者只在 id 恰好等于平台名时才碰巧一致。
    var conversation = Find(nodes, FamaPage.ConversationTableId);
    Equal(FamaPage.ConversationChannel, conversation.GetProperty("channel").GetString()!);

    var message = Find(nodes, FamaPage.MessageTableId);
    var args = message.GetProperty("dataSource").GetProperty("args");
    Equal("{selection." + FamaPage.ConversationChannel + ".bridge}", args.GetProperty("bridge").GetString()!);
    Equal("{selection." + FamaPage.ConversationChannel + ".conversation}", args.GetProperty("conversation").GetString()!);

    var send = actions.RootElement.GetProperty("actions").EnumerateArray()
        .First(action => action.GetProperty("id").GetString() == FamaPage.SendActionId);
    var sendArgs = send.GetProperty("args");
    Equal("{selection." + FamaPage.ConversationChannel + ".bridge}", sendArgs.GetProperty("bridge").GetString()!);
    Equal("{selection." + FamaPage.ConversationChannel + ".conversation}", sendArgs.GetProperty("conversation").GetString()!);
    Equal("{" + FamaPage.TextBoxId + "}", sendArgs.GetProperty("text").GetString()!);
    Equal("{" + FamaPage.FileBoxId + "}", sendArgs.GetProperty("file").GetString()!);
}

// REQ-FAMA-001：通讯式外观仍沿用桥 id 接线；搜索必须接到真实参数，输入换行不能折叠成会话摘要。
static void TestMessengerInterface()
{
    using var description = JsonDocument.Parse(FamaPage.Describe());
    var pages = description.RootElement.GetProperty("pages").EnumerateArray().ToList();
    Equal(2, pages.Count);
    Equal("通讯录", pages[0].GetProperty("title").GetString()!);
    Equal("left", pages[0].GetProperty("placement").GetProperty("side").GetString()!);
    Equal("消息", pages[1].GetProperty("title").GetString()!);
    Equal("center", pages[1].GetProperty("placement").GetProperty("side").GetString()!);
    var nodes = Descendants(description.RootElement).ToList();
    var contacts = Find(nodes, FamaPage.ConversationTableId);
    Equal("conversationList", contacts.GetProperty("type").GetString()!);
    Equal("{selection.fama.search.value}", contacts.GetProperty("dataSource").GetProperty("args").GetProperty("q").GetString()!);
    var query = Find(nodes, "query");
    Equal("fama.search", query.GetProperty("channel").GetString()!);
    var timeline = Find(nodes, FamaPage.MessageTableId);
    Equal("chatTimeline", timeline.GetProperty("type").GetString()!);
    Equal("fama.ui.image", timeline.GetProperty("imageSource").GetProperty("command").GetString()!);
    Equal("{selection.fama.conversation.bridge}", timeline.GetProperty("imageSource").GetProperty("args").GetProperty("bridge").GetString()!);
    Equal("{image.path}", timeline.GetProperty("imageSource").GetProperty("args").GetProperty("path").GetString()!);
    Equal(FamaPage.ReadActionId, timeline.GetProperty("readAction").GetString()!);
    Equal("{selection.fama.conversation.title}", timeline.GetProperty("source").GetString()!);
    var input = Find(nodes, FamaPage.TextBoxId);
    True(input.GetProperty("multiline").GetBoolean(), "聊天输入区必须支持换行");
    True(!input.TryGetProperty("commitAction", out _), "回车和失焦不能发送消息");
    var registry = Registry();
    registry.TryGet("fama.ui.data", out var data);
    True(data!.Parameters.Any(parameter => parameter.Name == "q"), "搜索词必须被取数指令接收");
}

// REQ-FAMA-004：来源来自配置（不依赖探活），筛选仍使用唯一的桥 id；只清理成功提交的草稿。
static async Task TestSendAndSourceControls()
{
    using var description = JsonDocument.Parse(FamaPage.Describe());
    var nodes = Descendants(description.RootElement).ToList();
    var source = Find(nodes, FamaPage.SourceBoxId);
    Equal("select", source.GetProperty("mode").GetString()!);
    Equal(FamaPage.SourceChannel, source.GetProperty("channel").GetString()!);
    Equal(FamaPage.ViewSource, source.GetProperty("optionsSource").GetProperty("args").GetProperty("view").GetString()!);
    var firstRow = Find(nodes, FamaPage.ReceivePanelId).GetProperty("rows")[0].GetProperty("widgets");
    Equal(FamaPage.SourceBoxId, firstRow[0].GetProperty("id").GetString()!);
    Equal("{selection.fama.source.value}", Find(nodes, FamaPage.ConversationTableId).GetProperty("dataSource").GetProperty("args").GetProperty("source").GetString()!);
    var attachment = Find(nodes, FamaPage.FileBoxId);
    Equal("sourcePicker", attachment.GetProperty("kind").GetString()!);
    True(!attachment.TryGetProperty("commitAction", out _), "选择图片不能触发发送");
    using var actions = JsonDocument.Parse(FamaPage.Actions());
    var send = actions.RootElement.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("id").GetString() == FamaPage.SendActionId);
    Equal("text,attachment", string.Join(",", send.GetProperty("clearOnSuccess").EnumerateArray().Select(e => e.GetString())));
    var directory = NewScratch("source-options");
    try
    {
        var state = new FamaState(new FakeContext(directory));
        using var work = new StubBridge();
        using var home = new StubBridge();
        Equal(1, state.SourceRows().Count);
        state.AddBridge("qq-work", "qq", "QQ", "http://127.0.0.1:" + work.Port, null);
        state.AddBridge("qq-home", "qq", "QQ", "http://127.0.0.1:" + home.Port, null);
        var options = state.SourceRows();
        Equal(3, options.Count);
        True(state.SourceBridge(options[0]["value"]) is null, "全部来源必须聚合");
        Equal("qq-work", state.SourceBridge(options[1]["value"])!);
        Equal("qq-home", state.SourceBridge(options[2]["value"])!);
        var registry = new Registrar();
        HistoryFamaModule.Register(registry, state, new FamaLive(null, state));
        registry.TryGet("fama.ui.data", out var data);
        True(data!.Parameters.Any(p => p.Name == "source"), "取数必须接收来源筛选");
        var parameters = new Dictionary<string, string>
        {
            ["view"] = FamaPage.ViewConversation, ["source"] = options[2]["value"], ["q"] = "通知",
        };
        var result = await data.Handler(new CommandContext(data, parameters, "test", null, CancellationToken.None));
        True(result.Success, result.Message);
        var rows = (IReadOnlyList<IReadOnlyDictionary<string, string>>)result.Data!;
        Equal(2, rows.Count);
        True(rows.All(row => row["bridge"] == "qq-home"), "来源筛选必须限定桥 id");
        Equal(0, work.Requests.Count);
        True(home.Requests.Single().Contains("q=%E9%80%9A%E7%9F%A5", StringComparison.Ordinal), "来源筛选不能丢搜索词");
        parameters["source"] = FamaPage.AllSources;
        result = await data.Handler(new CommandContext(data, parameters, "test", null, CancellationToken.None));
        True(result.Success, result.Message);
        Equal(4, ((IReadOnlyList<IReadOnlyDictionary<string, string>>)result.Data!).Count);
    }
    finally { Cleanup(directory); }
}

static void TestVersionsAgree()
{
    var manifest = ReadManifest(Path.Combine(AppContext.BaseDirectory, "module.manifest.json"));
    Equal(FamaIdentity.Name, manifest.GetProperty("name").GetString()!);
    Equal(FamaIdentity.Name + ".dll", manifest.GetProperty("artifact").GetString()!);
    Equal(new ModuleInfo().Version, manifest.GetProperty("version").GetString()!);
    Equal(FamaIdentity.Name, new ModuleInfo().ModuleName);
    Equal(FamaIdentity.Description, manifest.GetProperty("description").GetString()!);
}

static void TestTunnelDefaults()
{
    var settings = new FamaTunnelSettings();
    True(settings.Enabled, "正式服务默认应托管云端隧道");
    Equal("root@101.132.190.76", settings.Server);
    Equal("%USERPROFILE%\\.ssh\\messagehub-cloud.pem", settings.KeyPath);
    Equal("8010,8011,8012,6080,6081,6099", string.Join(",", settings.Ports));

    var directory = NewScratch("tunnel-probe");
    try
    {
        True(FamaTunnel.Start(directory, HostRunMode.Probe, []) is null, "probe 不得启动隧道");
        True(FamaTunnel.Start(directory, HostRunMode.Service, []) is null, "没有本机桥配置时不得启动隧道");
        True(!File.Exists(Path.Combine(directory, "tunnel.json")), "probe 不得写入隧道配置");
    }
    finally { Cleanup(directory); }
}

static void TestBridgeConfig()
{
    var directory = NewScratch("config");
    try
    {
        var state = new FamaState(new FakeContext(directory));
        Equal(Path.Combine(directory, "bridges.json"), state.ConfigPath);
        Equal(0, state.Bridges().Count);
        True(!File.Exists(state.ConfigPath), "没配过就不该凭空写出配置文件");

        state.AddBridge("qq", "qq", "QQ", "http://127.0.0.1:8010/", "tok-qq");
        state.AddBridge("dingtalk", "dingtalk", "钉钉", "http://127.0.0.1:8011", null);
        var bridges = state.Bridges();
        Equal(2, bridges.Count);
        Equal("http://127.0.0.1:8010", bridges[0].BaseUrl);
        Equal("tok-qq", bridges[0].Token);
        Equal("", bridges[1].Token);

        // 省略 token 是「别动它」，不是「清空它」
        state.AddBridge("qq", null, null, "http://127.0.0.1:8010", null);
        Equal("tok-qq", state.Bridges()[0].Token);
        Equal("QQ", state.Bridges()[0].Title);

        // 大小写不敏感地命中同一条，不会写出第二份
        state.AddBridge("QQ", "qq", "QQ", "http://127.0.0.1:8010", null);
        Equal(2, state.Bridges().Count);

        state.RemoveBridge("dingtalk");
        Equal(1, state.Bridges().Count);

        var threw = false;
        try
        {
            state.RemoveBridge("nope");
        }
        catch (FamaBridgeException)
        {
            threw = true;
        }

        True(threw, "移除不存在的桥要报错，不能静默成功");

        // 新实例从磁盘读回来的内容要和内存里一致（配置文件是唯一真源）
        var reloaded = new FamaState(new FakeContext(directory));
        Equal(1, reloaded.Bridges().Count);
        Equal("qq", reloaded.Bridges()[0].Id);
        Equal("tok-qq", reloaded.Bridges()[0].Token);

        // 残缺配置（没地址）不参与连接
        File.WriteAllText(state.ConfigPath,
            """{"schemaVersion":1,"bridges":[{"id":"broken","baseUrl":""},{"id":"ok","baseUrl":"http://127.0.0.1:9"}]}""",
            new UTF8Encoding(false));
        var filtered = new FamaState(new FakeContext(directory));
        Equal(1, filtered.Bridges().Count);
        Equal("ok", filtered.Bridges()[0].Id);
    }
    finally
    {
        Cleanup(directory);
    }
}

static void TestPageRows()
{
    var conversations = new[]
    {
        // 桥 id 故意与平台名不同：行里必须带配置 id，取数与发送才找得到桥
        new FamaConversation("qq", "qq:group:1", "通知群", "group", 124, 1791537512673, "第一行\n第二行", 0)
            { Bridge = "qq-work" },
        new FamaConversation("dingtalk", "76768247043", "某群", "group", 0, 0, "", 0) { Bridge = "dingtalk" },
    };
    var rows = FamaState.ConversationRows(conversations);
    Equal(2, rows.Count);
    Equal("qq-work|qq:group:1", rows[0]["id"]);
    Equal("qq-work", rows[0]["bridge"]);
    Equal("qq", rows[0]["platform"]);
    Equal("qq", rows[0]["source"]); // 没填桥标题时来源回退到平台名
    Equal("", rows[1]["when"]);

    // 通讯录短时间：今天只写时分，今年写月日，往年写日期
    var now = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.FromHours(8));
    long Ms(int y, int mo, int d, int h, int mi) => new DateTimeOffset(y, mo, d, h, mi, 0, now.Offset).ToUnixTimeMilliseconds();
    Equal("09:05", FamaState.ShortTime(Ms(2026, 10, 9, 9, 5), now));
    Equal("10-08 23:59", FamaState.ShortTime(Ms(2026, 10, 8, 23, 59), now));
    Equal("2025-12-31", FamaState.ShortTime(Ms(2025, 12, 31, 12, 0), now));
    Equal("群聊", rows[0]["kind"]);
    Equal("124", rows[0]["members"]);
    Equal("第一行 第二行", rows[0]["last"]);
    True(rows[0]["time"].Length > 0, "有效时间戳要格式化出来");
    Equal("", rows[1]["time"]);
    Equal("", rows[1]["members"]);

    var messages = new[]
    {
        new FamaMessage("qq", "m1", "qq:group:1", 1791537512673, "in", "7", "李四", "text", "大家好\n第二行", 0),
        new FamaMessage("qq", "m2", "qq:group:1", 1791537513000, "in", "8", "", "image", "[图片]", 1)
            { Files = [new("photo.png", "image/png", 100, "/api/v1/attachments/image.png")] },
        new FamaMessage("qq", "out-1", "qq:group:1", 1791537514000, "out", "", "", "text", "收到", 0),
    };
    var messageRows = FamaState.MessageRows(messages);
    Equal(3, messageRows.Count);
    Equal("李四", messageRows[0]["who"]);
    Equal("大家好\n第二行", messageRows[0]["text"]);
    Equal("qq · 群聊 · 124 人", rows[0]["subtitle"]);
    Equal("8", messageRows[1]["who"]);           // 名字空时用 id 兜底，不显示空白
    Equal("我", messageRows[2]["who"]);          // 自己发的
    Equal("", messageRows[1]["text"]);
    using (var images = JsonDocument.Parse(messageRows[1]["images"]))
        Equal("/api/v1/attachments/image.png", images.RootElement[0].GetProperty("path").GetString()!);
    Equal("qq|out-1", messageRows[2]["id"]);
    var image = messages[1] with { Text = "photo.png" };
    Equal("", FamaState.MessageRows([image])[0]["text"]);
    Equal("实际图片说明", FamaState.MessageRows([image with { Text = "实际图片说明" }])[0]["text"]);
    True(FamaState.MessageRows([image with { Files = [] }])[0]["text"].Length > 0, "没有预览地址时必须保留回退文字");
}

static async Task TestLocalRead()
{
    var directory = NewScratch("local-read");
    try
    {
        var state = new FamaState(new FakeContext(directory));
        state.AddBridge("feishu", "feishu", "飞书", "http://127.0.0.1:9", null);
        var unread = new FamaConversation("feishu", "attendance", "Attendance", "private", 0, 1000, "提醒", 3) { Bridge = "feishu" };
        var recent = unread with { ConversationId = "recent", LastTsMs = 2000, Unread = 0 };
        Equal("attendance", state.OrderConversations([recent, unread])[0].ConversationId);
        await state.MarkReadAsync("feishu", "attendance", 500, 1000, 3);
        Equal(3, state.OrderConversations([unread])[0].Unread);
        await state.MarkReadAsync("feishu", "attendance", 1000, 1000, 3);
        var reopened = new FamaState(new FakeContext(directory));
        Equal(0, reopened.OrderConversations([unread])[0].Unread);
        Equal("recent", reopened.OrderConversations([unread, recent])[0].ConversationId);
        var next = unread with { LastTsMs = 3000, Unread = 4 };
        Equal(1, reopened.OrderConversations([next])[0].Unread);
        await reopened.MarkReadAsync("feishu", "attendance", 1000, 1000, 3);
        Equal(1, reopened.OrderConversations([next])[0].Unread);
        Equal(3, reopened.OrderConversations([unread with { Bridge = "another-account" }])[0].Unread);
        await reopened.MarkReadAsync("feishu", "attendance", 3000, 3000, 4);
        await reopened.MarkReadAsync("feishu", "attendance", 3000, 3000, 3);
        Equal(0, reopened.OrderConversations([next])[0].Unread);
        Equal(1, reopened.OrderConversations([next with { LastTsMs = 4000, Unread = 1 }])[0].Unread);
        var rows = FamaState.ConversationRows(reopened.OrderConversations([next]));
        Equal("", rows[0]["unread"]);
        Equal("4", rows[0]["bridge_unread"]);
        Equal("3000", rows[0]["last_ts_ms"]);
    }
    finally { Cleanup(directory); }
}

static async Task TestBridgeClient()
{
    using var stub = new StubBridge();
    var client = new FamaBridgeClient();
    var bridge = new FamaBridgeConfig
    {
        Id = "qq", Platform = "qq", Title = "QQ",
        BaseUrl = "http://127.0.0.1:" + stub.Port, Token = "test-token",
    };

    var meta = await client.MetaAsync(bridge, CancellationToken.None);
    Equal("qq", meta.Platform);
    Equal("10001", meta.Account);
    Equal("2.0.0", meta.BridgeVersion);
    True(meta.Receive && meta.Send && meta.SendAttachments && meta.Realtime, "能力要读全");
    Equal(2, meta.Conversations);
    Equal(3, meta.Messages);
    Equal(1000L, meta.MinTsMs);
    Equal(2000L, meta.MaxTsMs);

    var conversations = await client.ConversationsAsync(bridge, "通知", "group", 20, CancellationToken.None);
    Equal(2, conversations.Count);
    Equal("qq:group:1", conversations[0].ConversationId);
    Equal("通知群", conversations[0].Title);
    Equal(124, conversations[0].Members);
    Equal("private", conversations[1].Kind);
    Equal(3, conversations[1].Unread);

    var messages = await client.MessagesAsync(bridge, "qq:group:1", 50, CancellationToken.None);
    Equal(2, messages.Count);
    Equal("m1", messages[0].MessageId);
    Equal("李四", messages[0].SenderName);
    Equal("", messages[1].SenderName);
    Equal(1, messages[1].Attachments);
    Equal("image", messages[1].Kind);
    Equal("/api/v1/attachments/x.png", messages[1].Files.Single().Url!);
    var rows = FamaState.MessageRows(messages);
    using (var images = JsonDocument.Parse(rows[1]["images"]))
        Equal("a.png", images.RootElement[0].GetProperty("name").GetString()!);

    // REQ-FAMA-003：下载只走配置桥，不能把凭据带到附件给出的任意 URL。
    var imageBytes = await client.ImageAsync(bridge, "/api/v1/attachments/x.png", CancellationToken.None);
    Equal(4, imageBytes.Length);
    True(stub.Requests.Last().Contains("Authorization: Bearer test-token", StringComparison.Ordinal), "图片请求也必须带鉴权头");
    var requestCount = stub.Requests.Count;
    foreach (var invalid in new[] { "https://example.invalid/a.png", "//elsewhere/a.png", "/api/v1/attachments/../meta", "/api/v1/attachments/%2e%2e", "/api/v1/attachments/x.png?token=secret", "/api/v1/attachments/%2fabc", "/api/v1/attachments/%252fabc" })
    {
        var rejected = false;
        try { await client.ImageAsync(bridge, invalid, CancellationToken.None); }
        catch (FamaBridgeException) { rejected = true; }
        True(rejected, "必须拒绝非法图片路径：" + invalid);
    }
    Equal(requestCount, stub.Requests.Count);
    foreach (var file in new[] { "redirect.png", "large.png", "wrong.png", "empty.png" })
    {
        var rejected = false;
        try { await client.ImageAsync(bridge, "/api/v1/attachments/" + file, CancellationToken.None); }
        catch (FamaBridgeException) { rejected = true; }
        True(rejected, "必须拒绝非法图片响应：" + file);
    }
    Equal(requestCount + 4, stub.Requests.Count);

    var sent = await client.SendAsync(bridge, "qq:group:1", "收到", CancellationToken.None);
    Equal("out-1", sent.MessageId);
    Equal("out", sent.Direction);
    Equal("收到", sent.Text);

    var uploaded = await client.SendAsync(bridge, "qq:group:1", "", [new FamaUpload("a.png", "image/png", "AQI=")], CancellationToken.None);
    Equal("out-1", uploaded.MessageId);
    True(stub.Requests.Any(r => r.Contains("\"name\":\"a.png\"", StringComparison.Ordinal)
        && r.Contains("\"content_type\":\"image/png\"", StringComparison.Ordinal)
        && r.Contains("\"data_base64\":\"AQI=\"", StringComparison.Ordinal)), "附件必须保留桥发送契约");

    // 契约里的错误码要原样透出来，指令层才能按 unsupported 说人话
    var unsupported = false;
    try
    {
        await client.SendAsync(bridge, "dingtalk:76768247043", "在吗", CancellationToken.None);
    }
    catch (FamaBridgeException ex)
    {
        unsupported = true;
        Equal("unsupported", ex.Code);
    }

    True(unsupported, "钉钉桥返回 unsupported 时客户端要抛出带该码的异常");

    // 鉴权头、查询串、POST 体都要按契约发出去
    True(stub.Requests.Any(r => r.Contains("Authorization: Bearer test-token", StringComparison.Ordinal)),
        "每个请求都要带 Bearer token：" + string.Join(" | ", stub.Requests));
    True(stub.Requests.Any(r => r.StartsWith("GET /api/v1/conversations?", StringComparison.Ordinal)
                                && r.Contains("q=%E9%80%9A%E7%9F%A5", StringComparison.Ordinal)
                                && r.Contains("kind=group", StringComparison.Ordinal)
                                && r.Contains("limit=20", StringComparison.Ordinal)),
        "会话查询串要带 limit/q/kind：" + string.Join(" | ", stub.Requests));
    True(stub.Requests.Any(r => r.Contains("POST /api/v1/conversations/qq%3Agroup%3A1/messages", StringComparison.Ordinal)
                                && r.Contains("\"text\":\"收到\"", StringComparison.Ordinal)),
        "发送要用 POST 并带 text：" + string.Join(" | ", stub.Requests));

    // 连不上时给一句能读懂的话，不要把裸异常抛给用户
    var offline = new FamaBridgeConfig { Id = "gone", BaseUrl = "http://127.0.0.1:" + FreePort(), Token = "" };
    var reported = false;
    try
    {
        await client.MetaAsync(offline, CancellationToken.None);
    }
    catch (FamaBridgeException ex)
    {
        reported = true;
        True(ex.Message.Contains("连不上桥 gone", StringComparison.Ordinal), ex.Message);
    }

    True(reported, "连不上桥要报 FamaBridgeException");
}

static async Task TestHostLoadsPackage()
{
    var root = Path.Combine(Path.GetTempPath(), FamaIdentity.Name + ".Tests", Guid.NewGuid().ToString("N"));
    var package = Path.Combine(root, FamaIdentity.Name);
    Directory.CreateDirectory(package);
    try
    {
        var manifest = ReadManifest(Path.Combine(AppContext.BaseDirectory, "module.manifest.json"));
        foreach (var file in new[]
                 {
                     "module.manifest.json",
                     manifest.GetProperty("artifact").GetString()!,
                     manifest.GetProperty("docs").GetString()!,
                 })
        {
            var from = Path.Combine(AppContext.BaseDirectory, file);
            True(File.Exists(from), $"构建产物缺少 {file}");
            File.Copy(from, Path.Combine(package, file));
        }

        // 宿主 7.0 只接受 nupkg；只在临时目录构造测试候选，不安装或修改运行区。
        var archive = Path.Combine(root, FamaIdentity.Name + ".nupkg");
        WriteNugetPackage(package, archive, manifest.GetProperty("version").GetString()!);

        var list = await Probe(archive, FamaIdentity.Domain + ".bridge.list");
        True(list.GetProperty("data").ValueKind == JsonValueKind.Object, "宿主探针未返回装载数据：" + list.GetRawText());
        var module = list.GetProperty("data").GetProperty("module");
        True(module.GetProperty("attached").GetBoolean(),
            "宿主没有接上模块：" + string.Join("；", module.GetProperty("attachFailures").EnumerateArray().Select(item => item.GetString()))
            + "；诊断：" + string.Join("；", list.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetString())));
        Equal(FamaIdentity.Name, module.GetProperty("name").GetString());
        // 宿主接上的指令数必须等于离线登记的数目：少了说明有指令在宿主那边登记失败
        Equal(Registry().All.Count(d => d.Name.StartsWith(FamaIdentity.Domain + ".", StringComparison.Ordinal)),
            module.GetProperty("commandCount").GetInt32());
        var result = list.GetProperty("data").GetProperty("result");
        True(result.GetProperty("success").GetBoolean(), result.GetProperty("message").GetString() ?? "");

        // 干净数据目录里没有任何桥：查询类指令要老老实实返回空，而不是报错
        var empty = await Probe(archive, FamaIdentity.Domain + ".conv.list");
        True(empty.GetProperty("data").GetProperty("result").GetProperty("success").GetBoolean(),
            empty.GetProperty("data").GetProperty("result").GetRawText());
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

// 调已发布宿主的 `HistoryVulcan.Cli.exe --probe`：宿主根目录在构建时写进本程序集（见 csproj 的 HistoryVulcanHostRoot）。
static async Task<JsonElement> Probe(string package, string command)
{
    var hostRoot = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(item => item.Key == "HistoryVulcanHostRoot")?.Value;
    if (string.IsNullOrEmpty(hostRoot))
        throw new InvalidOperationException("构建时没有写入 HistoryVulcanHostRoot。");
    var cli = Path.Combine(hostRoot, "HistoryVulcan.Cli.exe");
    True(File.Exists(cli), $"找不到宿主命令行：{cli}（宿主 5.9.0 起提供 --probe）");

    var start = new ProcessStartInfo(cli)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
    };
    foreach (var argument in new[] { "--probe", package, "--format", "json", "--cli" }.Concat(command.Split(' ')))
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start)!;
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    True(output.TrimStart().StartsWith('{'), $"--probe 没有输出 JSON（退出码 {process.ExitCode}）：{output}{error}");
    using var document = JsonDocument.Parse(output);
    return document.RootElement.Clone();
}

static JsonElement ReadManifest(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.Clone();
}

// 与宿主实际 nupkg 相同：nuspec 与模块有效载荷平铺，不带运行区状态或数据。
static void WriteNugetPackage(string package, string archive, string version)
{
    File.WriteAllText(Path.Combine(package, FamaIdentity.Name + ".nuspec"), $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata><id>{FamaIdentity.Name}</id><version>{version}</version>
          <authors>HistoryVulcan</authors><description>{FamaIdentity.Name}</description></metadata>
        </package>
        """, new UTF8Encoding(false));
    ZipFile.CreateFromDirectory(package, archive);
}

static IEnumerable<JsonElement> Descendants(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        yield return element;
        foreach (var property in element.EnumerateObject())
            foreach (var child in Descendants(property.Value))
                yield return child;
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
            foreach (var child in Descendants(item))
                yield return child;
    }
}

static JsonElement Find(IReadOnlyList<JsonElement> nodes, string id)
    => nodes.FirstOrDefault(node => node.TryGetProperty("id", out var value) && value.GetString() == id);

static string NewScratch(string purpose)
{
    var path = Path.Combine(AppContext.BaseDirectory, "scratch", purpose + "-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(path);
    return path;
}

static void Cleanup(string path)
{
    try
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
    catch (IOException)
    {
    }
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"期望 {expected}，实际 {actual}");
}

/// <summary>
/// 模块登记口的测试替身：只记下登记了哪些指令。宿主 6.0.0 起注册表是宿主内部类，
/// 登记口 ICommandRegistrar 就是模块能看到的全部；来源由宿主盖章，这里不再核对。
/// </summary>
sealed class Registrar : ICommandRegistrar
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CommandDescriptor> All => _commands.Values;

    public void Register(CommandDescriptor descriptor)
    {
        if (!_commands.TryAdd(descriptor.Name, descriptor))
            throw new InvalidOperationException($"重复登记 {descriptor.Name}");
    }

    public bool TryGet(string name, out CommandDescriptor? descriptor) => _commands.TryGetValue(name, out descriptor);
}

/// <summary>上下文的测试替身：只给数据目录与一条什么都不做的总线。</summary>
sealed class FakeContext(string dataDirectory) : IModuleContext
{
    public ICommandBus Bus { get; } = new FakeBus();

    public IModuleEnvironment Environment { get; } = new FakeEnvironment(dataDirectory);

    public void RegisterCommands(Action<ICommandRegistrar> configure) => configure(new Registrar());
}

sealed class FakeBus : ICommandBus
{
    public List<string> Executed { get; } = [];

    public Task<CommandResult> ExecuteAsync(string text, string source, CancellationToken cancellation = default)
    {
        Executed.Add(text);
        return Task.FromResult(CommandResult.Ok("ok"));
    }

    public Task<CommandResult> InvokeAsync(string text, string source, CancellationToken cancellation = default)
        => Task.FromResult(CommandResult.Ok("ok"));

    public bool RequestConfirmation(string prompt) => true;
}

sealed class FakeEnvironment(string dataDirectory) : IModuleEnvironment
{
    public string ModuleName => FamaIdentity.Name;

    public string DataDirectory { get; } = dataDirectory;

    public string PackageDirectory => DataDirectory;

    public HostRunMode RunMode => HostRunMode.Probe;

    public string HostVersion => "6.1.1";
}

/// <summary>
/// 假桥：用 TcpListener 说最小可用的 HTTP/1.1，回执形状照 b-Code-Bridge.md。
/// 不用 HttpListener（非管理员注册前缀要 urlacl），所以这里自己读请求行与 Content-Length。
/// </summary>
sealed class StubBridge : IDisposable
{
    private const string MetaJson =
        """{"ok":true,"data":{"platform":"qq","account":"10001","bridge_version":"2.0.0","capabilities":{"receive":true,"send":true,"send_attachments":true,"realtime":true,"history":true,"unread":false},"counts":{"conversations":2,"messages":3},"range":{"min_ts_ms":1000,"max_ts_ms":2000}}}""";

    private const string ConversationsJson =
        """{"ok":true,"data":{"items":[{"platform":"qq","conversation_id":"qq:group:1","title":"通知群","kind":"group","members":124,"last_ts_ms":2000,"last_text":"最后一条","unread":0},{"platform":"qq","conversation_id":"qq:private:2","title":"张三","kind":"private","members":null,"last_ts_ms":1500,"last_text":"在吗","unread":3}],"has_more":false}}""";

    private const string MessagesJson =
        """{"ok":true,"data":{"items":[{"platform":"qq","message_id":"m1","conversation_id":"qq:group:1","ts_ms":1000,"direction":"in","sender_id":"7","sender_name":"李四","kind":"text","text":"大家好","attachments":[]},{"platform":"qq","message_id":"m2","conversation_id":"qq:group:1","ts_ms":2000,"direction":"in","sender_id":"8","sender_name":"","kind":"image","text":"[图片]","attachments":[{"name":"a.png","content_type":"image/png","size":10,"url":"/api/v1/attachments/x.png"}]}],"has_more":false}}""";

    private const string SentJson =
        """{"ok":true,"data":{"platform":"qq","message_id":"out-1","conversation_id":"qq:group:1","ts_ms":3000,"direction":"out","sender_id":"","sender_name":"","kind":"text","text":"收到","attachments":[]}}""";

    private const string UnsupportedJson =
        """{"ok":false,"error":{"code":"unsupported","message":"dingtalk 桥当前不支持发送"}}""";

    private readonly TcpListener _listener;
    private readonly List<string> _requests = [];
    private readonly object _gate = new();

    public StubBridge()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    public void Dispose() => _listener.Stop();

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return; // 监听器被 Dispose
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            using var received = new MemoryStream();

            var headerEnd = -1;
            var contentLength = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                    return;
                received.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                    continue;
                contentLength = text[..headerEnd].Split("\r\n")
                    .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => int.TryParse(line.Split(':')[1].Trim(), out var parsed) ? parsed : 0)
                    .FirstOrDefault();
                if (received.Length >= headerEnd + 4 + contentLength)
                    break;
            }

            var head = Encoding.UTF8.GetString(received.GetBuffer(), 0, headerEnd);
            var body = Encoding.UTF8.GetString(received.GetBuffer(), headerEnd + 4, contentLength);
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var method = requestLine[0];
            var path = requestLine[1];

            lock (_gate)
            {
                _requests.Add($"{method} {path} | "
                              + string.Join(" | ", lines.Skip(1).Where(line =>
                                  line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)))
                              + $" | {body}");
            }

            if (path.StartsWith("/api/v1/attachments/", StringComparison.Ordinal))
            {
                var redirect = path.EndsWith("redirect.png", StringComparison.Ordinal);
                var large = path.EndsWith("large.png", StringComparison.Ordinal);
                var empty = path.EndsWith("empty.png", StringComparison.Ordinal);
                var mime = path.EndsWith("wrong.png", StringComparison.Ordinal) ? "text/html" : "image/png";
                var length = large ? FamaBridgeClient.MaxImageBytes + 1 : empty ? 0 : 4;
                var status = redirect ? "302 Found\r\nLocation: /api/v1/meta" : "200 OK";
                var header = $"HTTP/1.1 {status}\r\nContent-Type: {mime}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                if (!large && !empty) await stream.WriteAsync(new byte[] { 137, 80, 78, 71 });
                await stream.FlushAsync();
                return;
            }
            var payload = Respond(method, path);
            var bytes = Encoding.UTF8.GetBytes(payload);
            var responseHead = "HTTP/1.1 200 OK\r\n"
                               + "Content-Type: application/json; charset=utf-8\r\n"
                               + $"Content-Length: {bytes.Length}\r\n"
                               + "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHead));
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }

    private static string Respond(string method, string path)
    {
        if (path.StartsWith("/api/v1/meta", StringComparison.Ordinal))
            return MetaJson;
        if (path.StartsWith("/api/v1/conversations?", StringComparison.Ordinal))
            return ConversationsJson;
        if (path.StartsWith("/api/v1/conversations/", StringComparison.Ordinal) && path.Contains("/messages", StringComparison.Ordinal))
        {
            if (method == "POST")
                return path.Contains("dingtalk", StringComparison.Ordinal) ? UnsupportedJson : SentJson;
            return MessagesJson;
        }

        return """{"ok":false,"error":{"code":"not_found","message":"没有这个端点"}}""";
    }
}
