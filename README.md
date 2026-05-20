# Codex Todo

Codex Desktop 的快速待办命令工具。它用全局 `UserPromptSubmit` 钩子拦截 todo 命令，并按工作区分别保存待办状态。

当前实现是一个 .NET 8 C# 控制台程序：

- 直接运行 `codex-todo.exe`：显示安装、卸载、查看状态菜单。
- 运行 `codex-todo.exe install`：安装全局 hook。
- 运行 `codex-todo.exe uninstall`：卸载全局 hook。
- 运行 `codex-todo.exe status`：查看全局 hook 状态。
- 运行 `codex-todo.exe hook`：执行 Codex hook 协议处理。这个模式只供 Codex 调用。

## 运行依赖

Windows 版发布包是 framework-dependent single-file exe，体积较小，但需要先安装：

- Microsoft .NET 8 Runtime x64

Win10 / Win11 不内置 .NET 8 Runtime。

## 命令

- `todo <内容>`：添加一条未完成待办。
- `todo` 后接多行内容：按非空行批量添加；每行开头的 `1`、`1.`、`1、`、`（1）`、`-`、`*` 等列表前缀会自动省略。
- `todo` 或 `todo list`：显示未完成待办。
- `todo help`：显示命令帮助。
- `todo remove`：删除全部未完成待办。
- `todo remove <序号>`：删除列表中对应序号的待办。
- `<序号>`：删除对应待办，并把待办原文作为当前请求交给 Codex 处理。

## 安装

下载并解压发布包后，运行：

```powershell
.\codex-todo.exe
```

也可以直接安装：

```powershell
.\codex-todo.exe install
```

安装完成后，在 Codex 中打开 `/hooks`，信任更新后的全局 `UserPromptSubmit` 钩子。

卸载：

```powershell
.\codex-todo.exe uninstall
```

默认会修改 `%USERPROFILE%\.codex\hooks.json`。如需测试或自定义 Codex Home，可设置 `CODEX_HOME` 环境变量。

## 从源码构建

```powershell
dotnet build .\src\CodexTodo\CodexTodo.csproj
```

发布 Windows x64 小体积单文件 exe：

```powershell
dotnet publish .\src\CodexTodo\CodexTodo.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\artifacts\publish\win-x64
```

发布产物在：

```text
artifacts\publish\win-x64\codex-todo.exe
```

## 说明

- 命令刻意不使用 `/todo`，因为斜杠命令可能绕过 `UserPromptSubmit` 钩子。
- 如果当前工作区有未完成待办，普通消息会通过 `UserPromptSubmit` 给模型附加提醒上下文，要求回复末尾按 `待办事项：` 加编号列表的固定格式提醒。todo 命令会在进入模型前返回 `decision: "block"`，因此响应更快。
- 默认不安装 `Stop` 钩子。`Stop` 提醒会额外触发续接轮次，速度更慢。
- 运行态数据 `.codex-todo/` 已被 Git 忽略。
