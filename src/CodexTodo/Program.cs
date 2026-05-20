using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexTodo;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            TryConfigureConsoleEncoding();

            var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
            return command switch
            {
                "hook" => RunHook(),
                "install" => InstallHooks(interactive: false),
                "uninstall" => UninstallHooks(interactive: false),
                "status" => ShowStatus(),
                "-h" or "--help" or "help" => ShowHelp(),
                "" => RunMenu(),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void TryConfigureConsoleEncoding()
    {
        try
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Codex hook 模式可能没有完整交互式控制台；stdio 字节读写仍按 UTF-8 处理。
        }
    }

    private static int RunHook()
    {
        try
        {
            var stdin = ReadStandardInputUtf8();
            if (string.IsNullOrWhiteSpace(stdin))
            {
                WriteHookJson(new { @continue = true });
                return 0;
            }

            using var document = JsonDocument.Parse(stdin);
            var root = document.RootElement;
            var eventName = GetPayloadString(root, "hook_event_name");

            if (!string.Equals(eventName, "UserPromptSubmit", StringComparison.Ordinal))
            {
                WriteHookJson(new { @continue = true });
                return 0;
            }

            var workspaceRoot = ResolveWorkspaceRoot(root);
            var paths = StatePaths.ForWorkspace(workspaceRoot);
            var prompt = GetPayloadString(root, "prompt") ?? "";
            var output = TodoHook.HandleUserPromptSubmit(prompt, paths);
            WriteHookJson(output);
            return 0;
        }
        catch (Exception ex)
        {
            WriteHookJson(new { decision = "block", reason = "Codex Todo hook 运行失败：\n\n" + ex.Message });
            return 0;
        }
    }

    private static string ReadStandardInputUtf8()
    {
        using var input = Console.OpenStandardInput();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        var bytes = memory.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        return text.TrimStart('\uFEFF');
    }

    private static int RunMenu()
    {
        while (true)
        {
            Console.WriteLine("Codex Todo");
            Console.WriteLine();
            Console.WriteLine("1. 安装 hook");
            Console.WriteLine("2. 卸载 hook");
            Console.WriteLine("3. 查看状态");
            Console.WriteLine("4. 退出");
            Console.WriteLine();
            Console.Write("请选择：");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1":
                    InstallHooks(interactive: true);
                    break;
                case "2":
                    UninstallHooks(interactive: true);
                    break;
                case "3":
                    ShowStatus();
                    break;
                case "4":
                case "q":
                case "Q":
                    return 0;
                default:
                    Console.WriteLine("无效选择。");
                    break;
            }

            Console.WriteLine();
        }
    }

    private static int ShowHelp()
    {
        Console.WriteLine("Codex Todo");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  codex-todo.exe             打开安装/卸载菜单");
        Console.WriteLine("  codex-todo.exe install     安装全局 UserPromptSubmit hook");
        Console.WriteLine("  codex-todo.exe uninstall   卸载 Codex Todo hook");
        Console.WriteLine("  codex-todo.exe status      查看 hook 状态");
        Console.WriteLine("  codex-todo.exe hook        执行 Codex hook 协议处理");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        ShowHelp();
        return 2;
    }

    private static int InstallHooks(bool interactive)
    {
        var hooksPath = GetHooksPath();
        var config = HooksConfig.Load(hooksPath);
        config.RemoveCodexTodoHooks();
        config.AddUserPromptSubmitHook(BuildHookCommand());
        config.Save(hooksPath);

        Console.WriteLine($"已安装 Codex Todo hook：{hooksPath}");
        Console.WriteLine("请在 Codex 中打开 /hooks，并信任更新后的 hook。");
        return 0;
    }

    private static int UninstallHooks(bool interactive)
    {
        var hooksPath = GetHooksPath();
        var config = HooksConfig.Load(hooksPath);
        var removed = config.RemoveCodexTodoHooks();
        config.Save(hooksPath);

        Console.WriteLine(removed ? $"已卸载 Codex Todo hook：{hooksPath}" : $"未发现 Codex Todo hook：{hooksPath}");
        return 0;
    }

    private static int ShowStatus()
    {
        var hooksPath = GetHooksPath();
        var config = HooksConfig.Load(hooksPath);
        var commands = config.FindCodexTodoHookCommands();

        Console.WriteLine($"hooks.json：{hooksPath}");
        Console.WriteLine(commands.Count == 0 ? "状态：未安装" : "状态：已安装");
        foreach (var command in commands)
        {
            Console.WriteLine($"- {command}");
        }
        return 0;
    }

    private static string GetHooksPath()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Path.Combine(codexHome, "hooks.json");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".codex", "hooks.json");
    }

    private static string BuildHookCommand()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                ?? throw new InvalidOperationException("无法定位当前程序集。");
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            return $"dotnet \"{assemblyPath}\" hook";
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("无法定位当前可执行文件。");
        }

        return NeedsQuoting(processPath) ? $"\"{processPath}\" hook" : $"{processPath} hook";
    }

    private static bool NeedsQuoting(string path)
    {
        return path.Any(char.IsWhiteSpace);
    }

    private static string? GetPayloadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ResolveWorkspaceRoot(JsonElement root)
    {
        var cwd = GetPayloadString(root, "cwd");
        return string.IsNullOrWhiteSpace(cwd)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(cwd);
    }

    private static void WriteHookJson(object value)
    {
        Console.Out.Write(JsonSerializer.Serialize(value, JsonOptions));
    }
}

internal sealed class TodoState
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("nextId")]
    public int NextId { get; set; } = 1;

    [JsonPropertyName("pendingExecution")]
    public JsonNode? PendingExecution { get; set; }

    [JsonPropertyName("items")]
    public List<TodoItem> Items { get; set; } = [];

    public void Normalize()
    {
        if (Version <= 0)
        {
            Version = 1;
        }

        if (NextId <= 0)
        {
            NextId = 1;
        }

        Items ??= [];
    }
}

internal sealed class TodoItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "open";
}

internal sealed record StatePaths(string StateDir, string StatePath, string LockPath)
{
    public static StatePaths ForWorkspace(string workspaceRoot)
    {
        var stateDir = Path.Combine(workspaceRoot, ".codex-todo");
        return new StatePaths(
            stateDir,
            Path.Combine(stateDir, "todos.json"),
            Path.Combine(stateDir, "todos.lock"));
    }
}

internal static class TodoHook
{
    public static object HandleUserPromptSubmit(string prompt, StatePaths paths)
    {
        var trimmed = prompt.Trim();
        var lower = trimmed.ToLowerInvariant();
        var isTodoCommand = Regex.IsMatch(lower, @"^todo(\s|$)");
        var isNumberLookup = Regex.IsMatch(trimmed, @"^\d+$");
        var (command, argument) = ParseCommand(trimmed, lower);

        if (command == "help")
        {
            return Block(UiText.Help);
        }

        if (!isTodoCommand && !isNumberLookup)
        {
            return Continue();
        }

        using var stateLock = TodoStateStore.AcquireLock(paths.LockPath);
        var state = TodoStateStore.Read(paths.StatePath);
        state.PendingExecution = null;

        if (isNumberLookup)
        {
            return ShowTodoByNumber(paths.StatePath, state, trimmed);
        }

        switch (command)
        {
            case "add":
                return AddTodos(paths.StatePath, state, argument);
            case "list":
                state.PendingExecution = null;
                TodoStateStore.Save(paths.StatePath, state);
                return Block(NewListMessage(GetOpenTodos(state)));
            case "remove":
                return RemoveTodos(paths.StatePath, state, argument);
            case "do":
                TodoStateStore.Save(paths.StatePath, state);
                return Block(UiText.DoRemoved);
            default:
                TodoStateStore.Save(paths.StatePath, state);
                return Block(UiText.Usage);
        }
    }

    private static (string Command, string Argument) ParseCommand(string trimmed, string lower)
    {
        var match = Regex.Match(trimmed, @"^todo(?:\s+([\s\S]*?))?\s*$");
        if (match.Success)
        {
            var rest = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(rest))
            {
                return ("list", "");
            }

            var split = Regex.Match(rest, @"^(\S+)(?:\s+([\s\S]*))?$");
            var first = split.Success ? split.Groups[1].Value.ToLowerInvariant() : rest.ToLowerInvariant();
            var remaining = split.Success && split.Groups[2].Success ? split.Groups[2].Value.Trim() : "";
            return first switch
            {
                "help" => ("help", ""),
                "list" => ("list", ""),
                "remove" => ("remove", remaining),
                "do" => ("do", remaining),
                _ => ("add", rest)
            };
        }

        return ("", "");
    }

    private static object Continue() => new { @continue = true };

    private static object AddTodos(string statePath, TodoState state, string argument)
    {
        var itemsToAdd = SplitTodoAddItems(argument);
        if (itemsToAdd.Count == 0)
        {
            TodoStateStore.Save(statePath, state);
            return Block(UiText.Usage);
        }

        foreach (var itemText in itemsToAdd)
        {
            var id = state.NextId++;
            state.Items.Add(new TodoItem
            {
                Id = id,
                Text = itemText,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Status = "open"
            });
        }

        state.PendingExecution = null;
        TodoStateStore.Save(statePath, state);
        return Block($"{UiText.Added}\n\n{NewListMessage(GetOpenTodos(state))}");
    }

    private static object RemoveTodos(string statePath, TodoState state, string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var removed = ClearOpenTodos(state);
            state.PendingExecution = null;
            TodoStateStore.Save(statePath, state);
            return Block(removed.Count == 0
                ? UiText.RemoveAllEmpty
                : $"{UiText.RemovedAll}\n\n{NewListMessage(GetOpenTodos(state))}");
        }

        if (!int.TryParse(argument, out var index))
        {
            TodoStateStore.Save(statePath, state);
            return Block(UiText.RemoveUsage);
        }

        var todos = GetOpenTodos(state);
        if (index < 1 || index > todos.Count)
        {
            TodoStateStore.Save(statePath, state);
            return Block($"{UiText.OutOfRange}\n\n{NewListMessage(todos)}");
        }

        var removedItem = RemoveTodoById(state, todos[index - 1].Id);
        state.PendingExecution = null;
        TodoStateStore.Save(statePath, state);
        return Block($"{UiText.Removed}{removedItem?.Text}\n\n{NewListMessage(GetOpenTodos(state))}");
    }

    private static object ShowTodoByNumber(string statePath, TodoState state, string argument)
    {
        if (!int.TryParse(argument, out var index))
        {
            TodoStateStore.Save(statePath, state);
            return Continue();
        }

        var todos = GetOpenTodos(state);
        if (index < 1 || index > todos.Count)
        {
            TodoStateStore.Save(statePath, state);
            return Continue();
        }

        TodoStateStore.Save(statePath, state);
        return Block(todos[index - 1].Text);
    }

    private static object Block(string reason) => new { decision = "block", reason };

    private static List<TodoItem> GetOpenTodos(TodoState state)
    {
        return state.Items
            .Where(item => string.Equals(item.Status, "open", StringComparison.Ordinal))
            .ToList();
    }

    private static string NewListMessage(IReadOnlyList<TodoItem> todos)
    {
        return $"{UiText.CurrentTodos}\n\n{FormatTodoList(todos)}";
    }

    private static string FormatTodoList(IReadOnlyList<TodoItem> todos)
    {
        if (todos.Count == 0)
        {
            return UiText.NoTodos;
        }

        return string.Join("\n", todos.Select((todo, index) => $"{index + 1}. {todo.Text}"));
    }

    private static List<string> SplitTodoAddItems(string text)
    {
        return Regex.Split(text, @"\r?\n")
            .Select(RemoveTodoLinePrefix)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static string RemoveTodoLinePrefix(string line)
    {
        var value = line.Trim();
        if (value.Length == 0)
        {
            return "";
        }

        var changed = true;
        while (changed && value.Length > 0)
        {
            changed = false;
            var first = value[0];

            if (first is >= '\u2460' and <= '\u2473')
            {
                value = value[1..].Trim();
                changed = true;
                continue;
            }

            if ("-*+•".Contains(first) && (value.Length == 1 || char.IsWhiteSpace(value[1])))
            {
                value = value[1..].Trim();
                changed = true;
                continue;
            }

            var closing = first switch
            {
                '(' => ')',
                '[' => ']',
                '（' => '）',
                _ => '\0'
            };

            if (closing != '\0')
            {
                var closeIndex = value.IndexOf(closing, 1);
                if (closeIndex > 1)
                {
                    var inside = value[1..closeIndex];
                    if (inside.All(char.IsDigit) || inside.All(IsChineseNumeral))
                    {
                        value = value[(closeIndex + 1)..].Trim();
                        changed = true;
                        continue;
                    }
                }
            }

            var tokenLength = 0;
            while (tokenLength < value.Length && char.IsDigit(value[tokenLength]))
            {
                tokenLength++;
            }

            if (tokenLength == 0)
            {
                while (tokenLength < value.Length && IsChineseNumeral(value[tokenLength]))
                {
                    tokenLength++;
                }
            }

            if (tokenLength > 0 && tokenLength < value.Length)
            {
                var next = value[tokenLength];
                if (IsListMarker(next) || char.IsWhiteSpace(next))
                {
                    value = value[(tokenLength + (char.IsWhiteSpace(next) ? 0 : 1))..].Trim();
                    changed = true;
                }
            }
        }

        return value;
    }

    private static bool IsListMarker(char value)
    {
        return value is ')' or ']' or '.' or ':' or '-' or ',' or '、' or '．' or '：' or '，' or '）';
    }

    private static bool IsChineseNumeral(char value)
    {
        return "一二三四五六七八九十百千万零〇两".Contains(value);
    }

    private static TodoItem? RemoveTodoById(TodoState state, int id)
    {
        var index = state.Items.FindIndex(item =>
            item.Id == id && string.Equals(item.Status, "open", StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var item = state.Items[index];
        state.Items.RemoveAt(index);
        return item;
    }

    private static List<TodoItem> ClearOpenTodos(TodoState state)
    {
        var removed = GetOpenTodos(state);
        state.Items = state.Items
            .Where(item => !string.Equals(item.Status, "open", StringComparison.Ordinal))
            .ToList();
        return removed;
    }
}

internal static class TodoStateStore
{
    public static TodoState Read(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return new TodoState();
        }

        var raw = File.ReadAllText(statePath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TodoState();
        }

        var state = JsonSerializer.Deserialize<TodoState>(raw, ProgramStateJson.Options) ?? new TodoState();
        state.Normalize();
        return state;
    }

    public static void Save(string statePath, TodoState state)
    {
        var directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, ProgramStateJson.Options), new UTF8Encoding(false));
        File.Move(tmp, statePath, overwrite: true);
    }

    public static FileStream AcquireLock(string lockPath)
    {
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }
}

internal static class ProgramStateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}

internal static class UiText
{
    public const string Added = "已添加。";
    public const string CurrentTodos = "当前未完成待办：";
    public const string NoTodos = "当前没有未完成待办。";
    public const string Removed = "已删除：";
    public const string RemovedAll = "已删除全部待办。";
    public const string OutOfRange = "序号超出范围。";
    public const string RemoveAllEmpty = "没有可删除的待办。";
    public const string RemoveUsage = "请使用 todo remove 或 todo remove 1。";
    public const string DoRemoved = "todo do 已停用。请先用 todo 查看待办，然后把要处理的待办内容作为普通消息发送。";
    public const string Usage = "todo <内容>\ntodo list\ntodo remove [序号]\ntodo help";
    public const string Help = """
Codex Todo 用法：

todo <内容>
  添加一条待办。

todo
todo list
  显示当前未完成待办。

todo 后接多行内容
  按非空行批量添加；每行开头的 1、1.、1、 、（1）、-、* 等列表前缀会自动省略。

todo remove
  删除全部未完成待办。

todo remove <序号>
  删除指定序号的待办。

todo help
  显示这段帮助。
""";
}

internal sealed class HooksConfig
{
    private readonly JsonObject _root;

    private HooksConfig(JsonObject root)
    {
        _root = root;
    }

    public static HooksConfig Load(string hooksPath)
    {
        if (!File.Exists(hooksPath))
        {
            return new HooksConfig(new JsonObject { ["hooks"] = new JsonObject() });
        }

        var raw = File.ReadAllText(hooksPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HooksConfig(new JsonObject { ["hooks"] = new JsonObject() });
        }

        var root = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        root["hooks"] ??= new JsonObject();
        return new HooksConfig(root);
    }

    public void Save(string hooksPath)
    {
        var directory = Path.GetDirectoryName(hooksPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(hooksPath, _root.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        }), new UTF8Encoding(false));
    }

    public void AddUserPromptSubmitHook(string command)
    {
        var hooks = GetHooksObject();
        var entries = hooks["UserPromptSubmit"] as JsonArray ?? new JsonArray();
        hooks["UserPromptSubmit"] = entries;

        entries.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 10,
                    ["statusMessage"] = "检查工作区 todo 命令"
                }
            }
        });
    }

    public bool RemoveCodexTodoHooks()
    {
        var removed = false;
        removed |= RemoveCodexTodoHookReferences("UserPromptSubmit");
        removed |= RemoveCodexTodoHookReferences("Stop");
        return removed;
    }

    public List<string> FindCodexTodoHookCommands()
    {
        var commands = new List<string>();
        var hooks = GetHooksObject();
        foreach (var eventName in new[] { "UserPromptSubmit", "Stop" })
        {
            if (hooks[eventName] is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (entry["hooks"] is not JsonArray hookCommands)
                {
                    continue;
                }

                foreach (var hook in hookCommands.OfType<JsonObject>())
                {
                    var command = hook["command"]?.GetValue<string>() ?? "";
                    if (IsCodexTodoCommand(command))
                    {
                        commands.Add(command);
                    }
                }
            }
        }

        return commands;
    }

    private bool RemoveCodexTodoHookReferences(string eventName)
    {
        var hooks = GetHooksObject();
        if (hooks[eventName] is not JsonArray entries)
        {
            return false;
        }

        var removed = false;
        var keptEntries = new JsonArray();
        foreach (var entryNode in entries)
        {
            if (entryNode is not JsonObject entry || entry["hooks"] is not JsonArray hookCommands)
            {
                keptEntries.Add(entryNode?.DeepClone());
                continue;
            }

            var keptHookCommands = new JsonArray();
            foreach (var hookNode in hookCommands)
            {
                var command = hookNode?["command"]?.GetValue<string>() ?? "";
                if (IsCodexTodoCommand(command))
                {
                    removed = true;
                    continue;
                }

                keptHookCommands.Add(hookNode?.DeepClone());
            }

            if (keptHookCommands.Count > 0)
            {
                var clonedEntry = entry.DeepClone().AsObject();
                clonedEntry["hooks"] = keptHookCommands;
                keptEntries.Add(clonedEntry);
            }
        }

        if (keptEntries.Count == 0)
        {
            hooks.Remove(eventName);
        }
        else
        {
            hooks[eventName] = keptEntries;
        }

        return removed;
    }

    private JsonObject GetHooksObject()
    {
        if (_root["hooks"] is JsonObject hooks)
        {
            return hooks;
        }

        hooks = new JsonObject();
        _root["hooks"] = hooks;
        return hooks;
    }

    private static bool IsCodexTodoCommand(string command)
    {
        return command.Contains("codex-todo", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CodexTodo", StringComparison.OrdinalIgnoreCase)
            || command.Contains("todo-hook.ps1", StringComparison.OrdinalIgnoreCase);
    }
}
