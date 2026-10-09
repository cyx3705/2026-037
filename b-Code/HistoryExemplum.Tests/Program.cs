using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HistoryExemplum;
using HistoryVulcan.Core.Commands;
using System.Diagnostics;
using System.Reflection;

Console.OutputEncoding = Encoding.UTF8;

// 全部离线。最后一组把打好的包交给已发布宿主的命令行 `--probe` 装一遍（宿主 5.9.0 起的统一契约）——
// 版本漂移、manifest 写错、身份类缺失在宿主那边都是「静默跳过整个模块」，只有这样才能在出包前拦住。
// 测试只看宿主的装载结果与指令结果，不引用宿主的实现程序集，宿主内部怎么改都不影响这里。
var tests = new (string Name, Func<Task> Run)[]
{
    ("command registration", Sync(TestCommandRegistration)),
    ("page owner follows domain", Sync(TestPageOwner)),
    ("buttons, actions and commands line up", Sync(TestPageWiring)),
    ("versions agree", Sync(TestVersionsAgree)),
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
    HistoryExemplumModule.Register(registry, new ExemplumState(null));
    return registry;
}

static void TestCommandRegistration()
{
    var registry = Registry();
    var domain = ExemplumIdentity.Domain;
    string[] expected =
    [
        domain + ".hello.ping",
        domain + ".hello.echo",
        domain + ".hello.list",
        domain + ".ui.describe",
        domain + ".ui.actions",
        domain + ".ui.data",
    ];
    foreach (var name in expected)
    {
        True(registry.TryGet(name, out var descriptor), $"未注册 {name}");
        Equal(domain, descriptor!.Domain!);
    }

    Equal(expected.Length, registry.All.Count(d => d.Name.StartsWith(domain + ".", StringComparison.Ordinal)));

    foreach (var method in new[] { "describe", "actions", "data" })
    {
        True(registry.TryGet(domain + ".ui." + method, out var ui), $"缺少 {domain}.ui.{method}");
        True(ui!.Readonly, $"{domain}.ui.{method} 必须只读");
        True(ui.HiddenReason is not null, $"{domain}.ui.{method} 是界面内部协议，必须 HiddenReason");
    }
}

static void TestPageOwner()
{
    // Aurora 的判据：owner = "History" + 首字母大写的指令域；对不上整页被静默拒收。
    var expected = "History" + char.ToUpperInvariant(ExemplumIdentity.Domain[0]) + ExemplumIdentity.Domain[1..];
    Equal(expected, ExemplumIdentity.PageOwner);

    using var description = JsonDocument.Parse(ExemplumPage.Describe());
    var root = description.RootElement;
    Equal(expected, root.GetProperty("owner").GetString()!);
    Equal(1, root.GetProperty("schemaVersion").GetInt32());
    foreach (var page in root.GetProperty("pages").EnumerateArray())
        Equal(expected, page.GetProperty("scene").GetString()!);

    using var actions = JsonDocument.Parse(ExemplumPage.Actions());
    Equal(expected, actions.RootElement.GetProperty("owner").GetString()!);
}

static void TestPageWiring()
{
    var registry = Registry();
    using var actions = JsonDocument.Parse(ExemplumPage.Actions());
    var declared = new HashSet<string>(StringComparer.Ordinal);
    foreach (var action in actions.RootElement.GetProperty("actions").EnumerateArray())
    {
        var id = action.GetProperty("id").GetString()!;
        var command = action.GetProperty("command").GetString()!;
        True(declared.Add(id), $"动作 id 重复：{id}");
        True(registry.TryGet(command.Split(' ')[0], out _), $"动作 {id} 指向未注册的指令 {command}");
    }

    using var description = JsonDocument.Parse(ExemplumPage.Describe());
    var nodes = Descendants(description.RootElement).ToList();
    foreach (var button in nodes.Where(node => node.TryGetProperty("kind", out var kind) && kind.GetString() == "button"))
    {
        var action = button.GetProperty("action").GetString()!;
        True(declared.Contains(action), $"按钮指向未声明的动作 {action}");
    }

    foreach (var source in nodes.Where(node => node.TryGetProperty("dataSource", out _)))
    {
        var command = source.GetProperty("dataSource").GetProperty("command").GetString()!;
        True(registry.TryGet(command, out _), $"表格取数指令未注册：{command}");
    }
}

static void TestVersionsAgree()
{
    var manifest = ReadManifest(Path.Combine(AppContext.BaseDirectory, "module.manifest.json"));
    Equal(ExemplumIdentity.Name, manifest.GetProperty("name").GetString()!);
    Equal(ExemplumIdentity.Name + ".dll", manifest.GetProperty("artifact").GetString()!);
    Equal(new ModuleInfo().Version, manifest.GetProperty("version").GetString()!);
    Equal(ExemplumIdentity.Name, new ModuleInfo().ModuleName);
    Equal(ExemplumIdentity.Description, manifest.GetProperty("description").GetString()!);
}

static async Task TestHostLoadsPackage()
{
    var root = Path.Combine(Path.GetTempPath(), ExemplumIdentity.Name + ".Tests", Guid.NewGuid().ToString("N"));
    var package = Path.Combine(root, ExemplumIdentity.Name);
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

        WriteChecksums(package);

        var echo = await Probe(package, ExemplumIdentity.Domain + ".hello.echo text=OneHistory");
        var module = echo.GetProperty("data").GetProperty("module");
        True(module.GetProperty("attached").GetBoolean(),
            "宿主没有接上模块：" + string.Join("；", module.GetProperty("attachFailures").EnumerateArray().Select(item => item.GetString()))
            + "；诊断：" + string.Join("；", echo.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetString())));
        Equal(ExemplumIdentity.Name, module.GetProperty("name").GetString());
        Equal(6, module.GetProperty("commandCount").GetInt32());
        var result = echo.GetProperty("data").GetProperty("result");
        True(result.GetProperty("success").GetBoolean(), result.GetProperty("message").GetString() ?? "");
        Equal("OneHistory", result.GetProperty("message").GetString());

        var ping = await Probe(package, ExemplumIdentity.Domain + ".hello.ping");
        True(ping.GetProperty("success").GetBoolean(), ping.GetRawText());
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

// 与 eng/Build-*Package.ps1 同一格式：大写十六进制、两个空格、正斜杠路径、无 BOM。
static void WriteChecksums(string package)
{
    var lines = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFileName(path).Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}  "
                        + Path.GetRelativePath(package, path).Replace('\\', '/'));
    File.WriteAllLines(Path.Combine(package, "SHA256SUMS"), lines, new UTF8Encoding(false));
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
