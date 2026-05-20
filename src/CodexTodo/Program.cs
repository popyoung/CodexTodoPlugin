using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
            var options = HookOptions.FromArgs(args.Skip(1));
            return command switch
            {
                "hook" => RunHook(options),
                "install" => InstallHooks(interactive: false, options),
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

    private static int RunHook(HookOptions options)
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
            var output = TodoHook.HandleUserPromptSubmit(prompt, paths, options);
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
                    InstallHooks(interactive: true, HookOptions.Default);
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
        Console.WriteLine("  codex-todo.exe install     安装全局 UserPromptSubmit hook，默认启用数字待办自动粘贴");
        Console.WriteLine("  codex-todo.exe install --clipboard-only");
        Console.WriteLine("                              安装 hook，但数字待办只复制到剪贴板");
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

    private static int InstallHooks(bool interactive, HookOptions options)
    {
        var hooksPath = GetHooksPath();
        var config = HooksConfig.Load(hooksPath);
        config.RemoveCodexTodoHooks();
        config.AddUserPromptSubmitHook(BuildHookCommand(options));
        config.Save(hooksPath);

        Console.WriteLine($"已安装 Codex Todo hook：{hooksPath}");
        Console.WriteLine(options.AutoPasteNumberLookup
            ? "数字待办查找：自动粘贴已启用。"
            : "数字待办查找：仅复制到剪贴板。");
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

    private static string BuildHookCommand(HookOptions options)
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                ?? throw new InvalidOperationException("无法定位当前程序集。");
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            return AppendHookOptions($"dotnet \"{assemblyPath}\" hook", options);
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("无法定位当前可执行文件。");
        }

        var command = NeedsQuoting(processPath) ? $"\"{processPath}\" hook" : $"{processPath} hook";
        return AppendHookOptions(command, options);
    }

    private static string AppendHookOptions(string command, HookOptions options)
    {
        return options.AutoPasteNumberLookup ? command : command + " --clipboard-only";
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

internal sealed record HookOptions(bool AutoPasteNumberLookup, bool DryRunTransfer)
{
    public static HookOptions Default { get; } = new(AutoPasteNumberLookup: true, DryRunTransfer: false);

    public static HookOptions FromArgs(IEnumerable<string> args)
    {
        var autoPaste = Default.AutoPasteNumberLookup;
        var dryRunTransfer = Default.DryRunTransfer;
        foreach (var rawArg in args)
        {
            var arg = rawArg.Trim().ToLowerInvariant();
            switch (arg)
            {
                case "--paste":
                case "--auto-paste":
                    autoPaste = true;
                    break;
                case "--clipboard-only":
                case "--no-paste":
                    autoPaste = false;
                    break;
                case "--dry-run-transfer":
                    dryRunTransfer = true;
                    break;
            }
        }

        var env = Environment.GetEnvironmentVariable("CODEX_TODO_AUTO_PASTE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            autoPaste = !string.Equals(env, "0", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(env, "no", StringComparison.OrdinalIgnoreCase);
        }

        return new HookOptions(autoPaste, dryRunTransfer);
    }
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
    public static object HandleUserPromptSubmit(string prompt, StatePaths paths, HookOptions options)
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
            return ShowTodoByNumber(paths.StatePath, state, trimmed, options);
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

    private static object ShowTodoByNumber(string statePath, TodoState state, string argument, HookOptions options)
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
        var text = todos[index - 1].Text;
        var result = TodoContentTransfer.Transfer(text, options);
        return Block(result.Message);
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

internal sealed record TransferResult(string Message);

internal static class TodoContentTransfer
{
    public static TransferResult Transfer(string text, HookOptions options)
    {
        if (options.DryRunTransfer)
        {
            return new TransferResult($"dry-run：将粘贴待办内容，未自动发送：\n\n{text}");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new TransferResult(text);
        }

        ClipboardSnapshot? originalClipboard = null;
        try
        {
            originalClipboard = ClipboardApi.Capture();
            ClipboardApi.SetUnicodeText(text);
        }
        catch (Exception ex)
        {
            return new TransferResult($"无法写入剪贴板：{ex.Message}\n\n{text}");
        }

        if (!options.AutoPasteNumberLookup)
        {
            return new TransferResult($"已复制到剪贴板：\n\n{text}");
        }

        if (!ForegroundWindowGuard.IsCodexForeground(out var foregroundDescription))
        {
            return new TransferResult($"已复制到剪贴板，自动粘贴已跳过：前台窗口不是 Codex（{foregroundDescription}）。\n\n{text}");
        }

        try
        {
            NativeScreenTip.Show("正在粘贴待办内容，请勿操作", 900);
            Thread.Sleep(80);

            using var block = InputBlockScope.TryEnter();
            if (!block.IsActive)
            {
                return new TransferResult($"已复制到剪贴板，自动粘贴已跳过：无法临时屏蔽用户输入。\n\n{text}");
            }

            Thread.Sleep(30);
            KeyboardInput.PasteByKeybdEvent();
            Thread.Sleep(160);
        }
        catch (Exception ex)
        {
            InputBlockScope.ReleaseAll();
            return new TransferResult($"已复制到剪贴板，自动粘贴失败：{ex.Message}\n\n{text}");
        }
        finally
        {
            InputBlockScope.ReleaseAll();
        }

        Thread.Sleep(250);
        TryRestoreClipboard(originalClipboard, text);
        return new TransferResult($"已粘贴待办内容，未自动发送：\n\n{text}");
    }

    private static void TryRestoreClipboard(ClipboardSnapshot? originalClipboard, string pastedText)
    {
        if (originalClipboard is null)
        {
            return;
        }

        try
        {
            var currentText = ClipboardApi.GetUnicodeText();
            if (string.Equals(currentText, pastedText, StringComparison.Ordinal))
            {
                ClipboardApi.Restore(originalClipboard);
            }
        }
        catch
        {
            // 恢复剪贴板失败不应影响 todo 命令结果。
        }
    }
}

internal sealed class InputBlockScope : IDisposable
{
    private static readonly object Gate = new();
    private static int _activeCount;
    private bool _disposed;

    static InputBlockScope()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ReleaseAll();
        Console.CancelKeyPress += (_, _) => ReleaseAll();
    }

    private InputBlockScope(bool isActive)
    {
        IsActive = isActive;
    }

    ~InputBlockScope()
    {
        Dispose();
    }

    public bool IsActive { get; }

    public static InputBlockScope TryEnter()
    {
        lock (Gate)
        {
            if (_activeCount == 0 && !NativeMethods.BlockInput(true))
            {
                return new InputBlockScope(false);
            }

            _activeCount++;
            return new InputBlockScope(true);
        }
    }

    public static void ReleaseAll()
    {
        lock (Gate)
        {
            if (_activeCount <= 0)
            {
                return;
            }

            _activeCount = 0;
            NativeMethods.BlockInput(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!IsActive)
        {
            GC.SuppressFinalize(this);
            return;
        }

        lock (Gate)
        {
            if (_activeCount > 0)
            {
                _activeCount--;
                if (_activeCount == 0)
                {
                    NativeMethods.BlockInput(false);
                }
            }
        }

        GC.SuppressFinalize(this);
    }
}

internal static class KeyboardInput
{
    private const byte VkControl = 0x11;
    private const byte VkV = 0x56;
    private const int KeyEventKeyUp = 0x0002;

    public static void PasteByKeybdEvent()
    {
        NativeMethods.keybd_event(VkControl, 0, 0, 0);
        NativeMethods.keybd_event(VkV, 0, 0, 0);
        NativeMethods.keybd_event(VkV, 0, KeyEventKeyUp, 0);
        NativeMethods.keybd_event(VkControl, 0, KeyEventKeyUp, 0);
    }
}

internal static class ForegroundWindowGuard
{
    public static bool IsCodexForeground(out string description)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            description = "无前台窗口";
            return false;
        }

        var title = NativeMethods.GetWindowTitle(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        var processName = "";
        var processPath = "";

        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            try
            {
                processPath = process.MainModule?.FileName ?? "";
            }
            catch
            {
                processPath = "";
            }
        }
        catch
        {
            processName = $"pid {processId}";
        }

        description = string.IsNullOrWhiteSpace(title)
            ? processName
            : $"{processName} / {title}";

        return ContainsCodex(processName)
            || ContainsCodex(processPath)
            || ContainsCodex(title);
    }

    private static bool ContainsCodex(string value)
    {
        return value.Contains("codex", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ClipboardSnapshot(List<ClipboardFormatData> Formats, bool HadAnyFormat);

internal sealed record ClipboardFormatData(uint Format, byte[] Data);

internal static class ClipboardApi
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static ClipboardSnapshot Capture()
    {
        using var clipboard = ClipboardAccess.Open();
        var formats = new List<ClipboardFormatData>();
        var hadAnyFormat = false;
        uint format = 0;
        while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
        {
            hadAnyFormat = true;
            var handle = NativeMethods.GetClipboardData(format);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            var size = NativeMethods.GlobalSize(handle);
            if (size == UIntPtr.Zero || size.ToUInt64() > int.MaxValue)
            {
                continue;
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var data = new byte[(int)size.ToUInt64()];
                Marshal.Copy(pointer, data, 0, data.Length);
                formats.Add(new ClipboardFormatData(format, data));
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }

        return new ClipboardSnapshot(formats, hadAnyFormat);
    }

    public static string? GetUnicodeText()
    {
        using var clipboard = ClipboardAccess.Open();
        if (!NativeMethods.IsClipboardFormatAvailable(CfUnicodeText))
        {
            return null;
        }

        var handle = NativeMethods.GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    public static void SetUnicodeText(string text)
    {
        using var clipboard = ClipboardAccess.Open();
        if (!NativeMethods.EmptyClipboard())
        {
            throw new InvalidOperationException("无法清空剪贴板。");
        }

        var data = Encoding.Unicode.GetBytes(text + "\0");
        SetClipboardBytes(CfUnicodeText, data);
    }

    public static void Restore(ClipboardSnapshot snapshot)
    {
        if (snapshot.HadAnyFormat && snapshot.Formats.Count == 0)
        {
            return;
        }

        using var clipboard = ClipboardAccess.Open();
        if (!NativeMethods.EmptyClipboard())
        {
            throw new InvalidOperationException("无法清空剪贴板。");
        }

        foreach (var format in snapshot.Formats)
        {
            SetClipboardBytes(format.Format, format.Data);
        }
    }

    private static void SetClipboardBytes(uint format, byte[] data)
    {
        var handle = NativeMethods.GlobalAlloc(GmemMoveable, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法分配剪贴板内存。");
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            throw new InvalidOperationException("无法锁定剪贴板内存。");
        }

        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            throw new InvalidOperationException("无法写入剪贴板数据。");
        }
    }
}

internal sealed class ClipboardAccess : IDisposable
{
    private ClipboardAccess()
    {
    }

    public static ClipboardAccess Open()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return new ClipboardAccess();
            }

            Thread.Sleep(25);
        }

        throw new InvalidOperationException("无法打开剪贴板。");
    }

    public void Dispose()
    {
        NativeMethods.CloseClipboard();
    }
}

internal static class NativeScreenTip
{
    public static void Show(string text, int durationMs)
    {
        var thread = new Thread(() =>
        {
            try
            {
                new NativeTipWindow(text, durationMs).Run();
            }
            catch
            {
                // 屏幕提示失败不影响粘贴流程。
            }
        })
        {
            IsBackground = true,
            Name = "Codex Todo Tip"
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        thread.Start();
    }
}

internal sealed class NativeTipWindow
{
    private const int Width = 620;
    private const int Height = 110;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const int SwShowNoActivate = 4;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint WmPaint = 0x000F;
    private const uint WmTimer = 0x0113;
    private const uint WmDestroy = 0x0002;
    private const uint LwaAlpha = 0x00000002;
    private const int Transparent = 1;
    private const uint DtCenter = 0x00000001;
    private const uint DtVcenter = 0x00000004;
    private const uint DtSingleLine = 0x00000020;

    private readonly string _text;
    private readonly int _durationMs;
    private NativeMethods.WndProc? _wndProc;
    private IntPtr _font;

    public NativeTipWindow(string text, int durationMs)
    {
        _text = text;
        _durationMs = durationMs;
    }

    public void Run()
    {
        _wndProc = WndProc;
        var className = "CodexTodoTip_" + Guid.NewGuid().ToString("N");
        var hInstance = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = className
        };

        NativeMethods.RegisterClassEx(ref wc);
        var screenWidth = NativeMethods.GetSystemMetrics(SmCxScreen);
        var screenHeight = NativeMethods.GetSystemMetrics(SmCyScreen);
        var hwnd = NativeMethods.CreateWindowEx(
            WsExTopmost | WsExToolWindow | WsExNoActivate | WsExLayered | WsExTransparent,
            className,
            "",
            WsPopup,
            Math.Max(0, (screenWidth - Width) / 2),
            Math.Max(0, screenHeight / 4),
            Width,
            Height,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 225, LwaAlpha);
        NativeMethods.SetTimer(hwnd, new UIntPtr(1), (uint)_durationMs, IntPtr.Zero);
        NativeMethods.ShowWindow(hwnd, SwShowNoActivate);
        NativeMethods.UpdateWindow(hwnd);

        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        if (_font != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_font);
            _font = IntPtr.Zero;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmPaint:
                Paint(hwnd);
                return IntPtr.Zero;
            case WmTimer:
                NativeMethods.DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void Paint(IntPtr hwnd)
    {
        var hdc = NativeMethods.BeginPaint(hwnd, out var ps);
        try
        {
            NativeMethods.GetClientRect(hwnd, out var rect);
            var brush = NativeMethods.CreateSolidBrush(0x202020);
            try
            {
                NativeMethods.FillRect(hdc, ref rect, brush);
            }
            finally
            {
                NativeMethods.DeleteObject(brush);
            }

            _font = _font == IntPtr.Zero
                ? NativeMethods.CreateFontW(-34, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei")
                : _font;
            var oldFont = NativeMethods.SelectObject(hdc, _font);
            NativeMethods.SetBkMode(hdc, Transparent);
            NativeMethods.SetTextColor(hdc, 0xFFFFFF);
            NativeMethods.DrawTextW(hdc, _text, -1, ref rect, DtCenter | DtVcenter | DtSingleLine);
            NativeMethods.SelectObject(hdc, oldFont);
        }
        finally
        {
            NativeMethods.EndPaint(hwnd, ref ps);
        }
    }
}

internal static class NativeMethods
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool BlockInput(bool fBlockIt);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        GetWindowTextW(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    public static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(
        int cHeight,
        int cWidth,
        int cEscapement,
        int cOrientation,
        int cWeight,
        uint bItalic,
        uint bUnderline,
        uint bStrikeOut,
        uint iCharSet,
        uint iOutPrecision,
        uint iClipPrecision,
        uint iQuality,
        uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);
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
