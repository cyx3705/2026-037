using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

namespace HistoryFama;

/// <summary>
/// 模块装配入口：桥管理、跨桥取数与发送，加 Aurora 页面协议三条指令（describe / actions / data）。
/// </summary>
/// <remarks>
/// 模块只是桥的消费方：指令一律转成对统一桥 HTTP API 的调用（契约见 2026-036-MessageHub 的
/// b-Code-Bridge.md），自己不存消息、不解析平台格式。桥答不上来时如实失败并把原因带出来，
/// 不返回上次的旧数据。
/// </remarks>
public sealed class HistoryFamaModule : IModuleContextAware, IDisposable
{
    private const string Hidden = "Aurora 页面内部协议，不对远程消费面暴露";

    private FamaLive? _live;

    /// <summary>宿主在装载时注入权威指令总线与命令注册器；装上就开始实时接收。</summary>
    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = new FamaState(context);
        var live = new FamaLive(context, state);
        _live = live;
        context.RegisterCommands(registry => Register(registry, state, live));
        live.Restart();
    }

    /// <summary>宿主卸载模块前调用：断开各桥的实时流，不留孤儿连接。</summary>
    public void Dispose()
    {
        _live?.Dispose();
        _live = null;
    }

    internal static void Register(ICommandRegistrar registry, FamaState state, FamaLive live)
    {
        const string domain = FamaIdentity.Domain;

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".bridge.list",
            Domain = domain,
            CommandClass = "bridge",
            Summary = "列出已配置的消息桥，逐个探活并给出在线状态、账号与收发能力。",
            Example = domain + ".bridge.list",
            Level = CommandLevel.Run,
            Readonly = true,
            Handler = context => GuardAsync(async () =>
            {
                var rows = await state.BridgeRowsAsync(context.Cancellation).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    return CommandResult.Ok(
                        $"还没有配置任何桥。用 {domain}.bridge.add 加一个（地址与 token 见 b-Deploy/Start.ps1 的输出）。",
                        rows);
                }

                var online = rows.Count(row => row["state"] == "在线");
                return CommandResult.Ok($"共 {rows.Count} 个桥，{online} 个在线", rows);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".bridge.add",
            Domain = domain,
            CommandClass = "bridge",
            Summary = "新增或更新一个消息桥的接入配置，写进模块数据目录的 bridges.json。",
            Example = domain + ".bridge.add id=qq platform=qq title=QQ baseurl=http://127.0.0.1:8010 token=abc123",
            Level = CommandLevel.Run,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "id",
                    Description = "本模块内唯一的桥标识，之后各条指令都用它指定桥，例如 qq；重复给同一个 id 就是更新。",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "baseurl",
                    Description = "桥的根地址，形如 http://127.0.0.1:8010（不带 /api/v1；末尾斜杠会被去掉）。",
                    Required = true,
                    Position = 1,
                },
                new ParameterSpec
                {
                    Name = "platform",
                    Description = "平台名，与桥 meta 里的 platform 一致，例如 qq、dingtalk；省略时按 id 填。",
                    Required = false,
                },
                new ParameterSpec
                {
                    Name = "title",
                    Description = "界面上显示的名字，例如 QQ；省略时按 id 填。",
                    Required = false,
                },
                new ParameterSpec
                {
                    Name = "token",
                    Description = "桥的 Bearer token，与 b-Deploy/.env 里那条 *_BRIDGE_TOKEN 相同；省略表示不改动已有的 token。",
                    Required = false,
                },
            ],
            Handler = context => GuardAsync(() =>
            {
                var message = state.AddBridge(
                    context.RequireString("id").Trim(),
                    context.GetString("platform"),
                    context.GetString("title"),
                    context.RequireString("baseurl").Trim(),
                    context.GetString("token"));
                live.Restart();
                return Task.FromResult(CommandResult.Ok(message));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".bridge.remove",
            Domain = domain,
            CommandClass = "bridge",
            Summary = "从配置里移除一个消息桥（只删本模块的配置，不动桥容器与桥里的数据）。",
            Example = domain + ".bridge.remove id=qq",
            Level = CommandLevel.Run,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "id",
                    Description = "要移除的桥标识，取 bridge.list 里 id 那列，例如 qq。",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = context => GuardAsync(() =>
            {
                var message = state.RemoveBridge(context.RequireString("id").Trim());
                live.Restart();
                return Task.FromResult(CommandResult.Ok(message));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".bridge.test",
            Domain = domain,
            CommandClass = "bridge",
            Summary = "探活一个消息桥，返回它的平台、账号与收发能力。",
            Example = domain + ".bridge.test id=qq",
            Level = CommandLevel.Run,
            Readonly = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "id",
                    Description = "要探活的桥标识，取 bridge.list 里 id 那列，例如 qq。",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = context => GuardAsync(async () =>
            {
                var meta = await state.MetaAsync(context.RequireString("id").Trim(), context.Cancellation)
                    .ConfigureAwait(false);
                var rows = new List<IReadOnlyDictionary<string, string>>
                {
                    new Dictionary<string, string>
                    {
                        ["platform"] = meta.Platform,
                        ["account"] = meta.Account,
                        ["bridge_version"] = meta.BridgeVersion,
                        ["receive"] = meta.Receive ? "是" : "否",
                        ["send"] = meta.Send ? "是" : "否",
                        ["send_attachments"] = meta.SendAttachments ? "是" : "否",
                        ["realtime"] = meta.Realtime ? "是" : "否",
                        ["conversations"] = meta.Conversations.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["messages"] = meta.Messages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                };
                return CommandResult.Ok(
                    $"桥在线：{meta.Platform} 账号 {meta.Account}，"
                    + $"{(meta.Receive ? "能收" : "不能收")}/{(meta.Send ? "能发" : "不能发")}",
                    rows);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".conv.list",
            Domain = domain,
            CommandClass = "conv",
            Summary = "列出各平台桥上的会话，按最后一条消息的时间倒序；指定 bridge 就只看那一个桥。",
            Example = domain + ".conv.list limit=20",
            Level = CommandLevel.Run,
            Readonly = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "bridge",
                    Description = "只看这个桥，取 bridge.list 里 id 那列，例如 qq；省略表示问所有已配置的桥。",
                    Required = false,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "q",
                    Description = "按会话标题模糊匹配（大小写不敏感），例如 志愿者；省略表示不过滤。",
                    Required = false,
                },
                new ParameterSpec
                {
                    Name = "kind",
                    Description = "只看单聊或只看群聊；省略表示都要。",
                    Required = false,
                    AllowedValues = ["private", "group"],
                },
                new ParameterSpec
                {
                    Name = "limit",
                    Description = "最多返回多少个会话，1~500 的整数；省略按 50。",
                    Type = ParamType.Int,
                    Required = false,
                    Default = "50",
                },
            ],
            Handler = context => GuardAsync(async () =>
            {
                var limit = context.GetInt("limit", 50);
                if (limit is < 1 or > 500)
                    return CommandResult.Fail("limit 要在 1~500 之间。");

                var gather = await state.ConversationsAsync(
                    context.GetString("bridge"), context.GetString("q"), context.GetString("kind"),
                    limit, context.Cancellation).ConfigureAwait(false);
                if (gather.AllFailed)
                    return CommandResult.Fail(string.Join("；", gather.Problems));

                var rows = FamaState.ConversationRows(gather.Items);
                return CommandResult.Ok(WithProblems($"共 {rows.Count} 个会话", gather.Problems), rows);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".msg.list",
            Domain = domain,
            CommandClass = "msg",
            Summary = "列出某个会话最近的消息，按时间升序；会话主键取 conv.list 里 conversation 那列。",
            Example = domain + ".msg.list bridge=qq conversation=qq:group:123456 limit=50",
            Level = CommandLevel.Run,
            Readonly = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "bridge",
                    Description = "会话所在的桥，取 bridge.list 里 id 那列，例如 qq。",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "conversation",
                    Description = "会话主键，取 conv.list 里 conversation 那列（例如 qq:group:123456 或钉钉的 76768247043）。",
                    Required = true,
                    Position = 1,
                },
                new ParameterSpec
                {
                    Name = "limit",
                    Description = "最多返回多少条消息，1~500 的整数；省略按 50。",
                    Type = ParamType.Int,
                    Required = false,
                    Default = "50",
                },
            ],
            Handler = context => GuardAsync(async () =>
            {
                var limit = context.GetInt("limit", 50);
                if (limit is < 1 or > 500)
                    return CommandResult.Fail("limit 要在 1~500 之间。");

                var messages = await state.MessagesAsync(
                    context.RequireString("bridge").Trim(),
                    context.RequireString("conversation").Trim(),
                    limit, context.Cancellation).ConfigureAwait(false);
                var rows = FamaState.MessageRows(messages);
                return CommandResult.Ok($"共 {rows.Count} 条消息", rows);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".msg.send",
            Domain = domain,
            CommandClass = "msg",
            Summary = "往某个会话发一条文字消息；桥不支持发送时直接说明，不假装成功。",
            Example = domain + ".msg.send bridge=qq conversation=qq:private:10001 text=收到",
            Level = CommandLevel.Ask,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "bridge",
                    Description = "要发到哪个桥，取 bridge.list 里 id 那列，例如 qq。",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "conversation",
                    Description = "目标会话主键，取 conv.list 里 conversation 那列，例如 qq:private:10001。",
                    Required = true,
                    Position = 1,
                },
                new ParameterSpec
                {
                    Name = "text",
                    Description = "正文，任意非空白字符串；一条指令只发一条文字消息，附件暂不支持。",
                    Required = true,
                    Position = 2,
                },
            ],
            ConfirmPrompt = context =>
                $"确认把这条消息发给 {context.GetString("conversation")}？\n{context.GetString("text")}",
            Handler = context => GuardAsync(async () =>
            {
                var text = context.RequireString("text").Trim();
                if (text.Length == 0)
                    return CommandResult.Fail("text 不能是空白。");

                var sent = await state.SendAsync(
                    context.RequireString("bridge").Trim(),
                    context.RequireString("conversation").Trim(),
                    text, context.Cancellation).ConfigureAwait(false);
                return CommandResult.Ok($"已发出：{sent.Text}", FamaState.MessageRows([sent]));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".recv.auto",
            Domain = domain,
            CommandClass = "recv",
            Summary = "开关自动接收：开着时各桥来新消息就自动刷新通讯录与消息；关掉后断开实时流，只能手动刷新。",
            Example = domain + ".recv.auto on=false",
            Level = CommandLevel.Run,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "on",
                    Description = "true 打开、false 关闭；模块装上时默认打开，重装后回到打开。",
                    Required = true,
                    AllowedValues = ["true", "false"],
                    Position = 0,
                },
            ],
            Handler = context => GuardAsync(() =>
            {
                var on = context.RequireString("on").Trim();
                if (!bool.TryParse(on, out var enabled))
                    return Task.FromResult(CommandResult.Fail($"on 只能是 true 或 false，收到的是 {on}"));

                var count = live.SetEnabled(enabled);
                return Task.FromResult(CommandResult.Ok(enabled
                    ? $"自动接收已打开，正在监听 {count} 个桥的实时流"
                    : "自动接收已关闭，只在点「刷新」时更新"));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = domain + ".recv.refresh",
            Domain = domain,
            CommandClass = "recv",
            Summary = "重连各桥的实时流并重取通讯录与消息两张表；收消息卡住时用，自动接收关着时只重取不重连。",
            Example = domain + ".recv.refresh",
            Level = CommandLevel.Run,
            Handler = _ => GuardAsync(async () =>
            {
                var count = live.Restart();
                await live.RefreshTablesAsync().ConfigureAwait(false);
                return CommandResult.Ok(live.Enabled
                    ? $"已重连 {count} 个桥的实时流并刷新"
                    : "已刷新（自动接收关着，没有重连实时流）");
            }),
        });

        registry.Register(Internal("describe", "返回通讯录与消息两页的页面描述", _ => Json(FamaPage.Describe(live.Enabled))));
        registry.Register(Internal("actions", "返回通讯录与消息两页的动作声明", _ => Json(FamaPage.Actions())));
        registry.Register(new CommandDescriptor
        {
            Name = domain + ".ui.data",
            Domain = domain,
            CommandClass = "ui",
            Summary = "返回页面某一张表的行（桥 / 会话 / 消息）；桥表页面上已不用，留给排障。",
            Example = domain + ".ui.data view=conversation",
            Level = CommandLevel.Run,
            Readonly = true,
            HiddenReason = Hidden,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "view",
                    Description = "取哪张表：bridge 桥表、conversation 会话表、message 消息表。",
                    Required = false,
                    AllowedValues = [FamaPage.ViewBridge, FamaPage.ViewConversation, FamaPage.ViewMessage],
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "bridge",
                    Description = "会话表限定某个桥（bridge.list 的 id），例如 qq；省略表示所有桥。",
                    Required = false,
                },
                new ParameterSpec
                {
                    Name = "conversation",
                    Description = "消息表必填的会话主键（conv.list 的 conversation 那列）；会话没选中时页面不会发这条取数。",
                    Required = false,
                },
            ],
            Handler = context => GuardAsync(async () =>
            {
                switch (context.GetString("view")?.Trim().ToLowerInvariant())
                {
                    case FamaPage.ViewBridge:
                    {
                        var rows = await state.BridgeRowsAsync(context.Cancellation).ConfigureAwait(false);
                        return CommandResult.Ok("桥", rows);
                    }

                    case FamaPage.ViewConversation:
                    {
                        var gather = await state.ConversationsAsync(
                            context.GetString("bridge"), null, null, FamaPage.PageLimit,
                            context.Cancellation).ConfigureAwait(false);
                        if (gather.AllFailed)
                            return CommandResult.Fail(string.Join("；", gather.Problems));
                        return CommandResult.Ok("会话", FamaState.ConversationRows(gather.Items));
                    }

                    case FamaPage.ViewMessage:
                    {
                        var bridge = context.GetString("bridge");
                        var conversation = context.GetString("conversation");
                        if (string.IsNullOrWhiteSpace(bridge) || string.IsNullOrWhiteSpace(conversation))
                        {
                            // 还没选会话：给空表，不算失败（不算失败才不会往控制台刷红字）
                            return CommandResult.Ok("消息", Array.Empty<IReadOnlyDictionary<string, string>>());
                        }

                        var messages = await state.MessagesAsync(
                            bridge.Trim(), conversation.Trim(), FamaPage.PageLimit,
                            context.Cancellation).ConfigureAwait(false);
                        return CommandResult.Ok("消息", FamaState.MessageRows(messages));
                    }

                    default:
                        return CommandResult.Fail(
                            $"未知 view；支持 {FamaPage.ViewBridge} / {FamaPage.ViewConversation} / {FamaPage.ViewMessage}");
                }
            }),
        });
    }

    private static CommandDescriptor Internal(string method, string summary, Func<CommandContext, CommandResult> handler)
        => new()
        {
            Name = FamaIdentity.Domain + ".ui." + method,
            Domain = FamaIdentity.Domain,
            CommandClass = "ui",
            Summary = summary,
            Level = CommandLevel.Run,
            Readonly = true,
            HiddenReason = Hidden,
            Handler = CommandDescriptor.Sync(handler),
        };

    private static CommandResult Json(string json) => CommandResult.Ok(json, json);

    /// <summary>把「哪个桥没答上来」带进结果提要；总线对超长回执只留提要，所以只带第一条。</summary>
    private static string WithProblems(string head, IReadOnlyList<string> problems)
        => problems.Count == 0
            ? head
            : $"{head}；{problems.Count} 个桥没答上（{problems[0]}）";

    /// <summary>网络与契约错误翻成一句人话；不吞异常类型，方便排障。</summary>
    private static async Task<CommandResult> GuardAsync(Func<Task<CommandResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (FamaBridgeException ex)
        {
            return CommandResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail("已取消。");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
