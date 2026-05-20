# Codex Todo

Codex Desktop 的快速待办命令工具。它用一个全局 `UserPromptSubmit` 钩子拦截 todo 命令，并按工作区分别保存待办状态。

本仓库本身就是一个本地 Codex 插件，同时包含可复用的 PowerShell 钩子脚本。推荐安装方式是在全局 `~/.codex/hooks.json` 中注册本仓库的钩子脚本；每个项目的待办数据仍保存在该项目自己的 `.codex-todo/todos.json`。

## 命令

- `todo <内容>`：添加一条未完成待办。
- `todo` 或 `todo list`：显示未完成待办。
- `todo remove`：删除全部未完成待办。
- `todo remove <序号>`：删除列表中对应序号的待办。
- `<序号>`：删除对应待办，并把待办原文作为当前请求交给 Codex 处理。

## 安装

在本仓库根目录运行安装脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-global-hook.ps1
```

然后在 Codex 中打开 `/hooks`，信任更新后的全局 `UserPromptSubmit` 钩子。

卸载全局钩子：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-global-hook.ps1 -Uninstall
```

## 说明

- 命令刻意不使用 `/todo`，因为斜杠命令可能绕过 `UserPromptSubmit` 钩子。
- 普通消息会原样放行。todo 命令会在进入模型前返回 `decision: "block"`，因此响应更快。
- 默认不安装 `Stop` 钩子。`Stop` 提醒会额外触发续接轮次，速度更慢。
- 运行态数据 `.codex-todo/` 已被 Git 忽略。
