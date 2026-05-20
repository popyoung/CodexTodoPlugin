Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $Utf8NoBom
[Console]::OutputEncoding = $Utf8NoBom

function Write-HookJson {
    param([hashtable]$Value)

    $json = $Value | ConvertTo-Json -Depth 20 -Compress
    [Console]::Out.Write($json)
}

function New-DefaultState {
    return [ordered]@{
        version = 1
        nextId = 1
        pendingExecution = $null
        items = @()
    }
}

function Convert-ToHashtable {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $result = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $result[$property.Name] = Convert-ToHashtable $property.Value
        }
        return $result
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $result[$key] = Convert-ToHashtable $Value[$key]
        }
        return $result
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @()
        foreach ($item in $Value) {
            $items += ,(Convert-ToHashtable $item)
        }
        return $items
    }

    return $Value
}

function Normalize-State {
    param($State)

    if ($null -eq $State) {
        $State = New-DefaultState
    }

    $State = Convert-ToHashtable $State
    if (-not $State.Contains("version")) { $State["version"] = 1 }
    if (-not $State.Contains("nextId")) { $State["nextId"] = 1 }
    if (-not $State.Contains("pendingExecution")) { $State["pendingExecution"] = $null }
    if ($State.Contains("suppressReminderTurnIds")) {
        $State.Remove("suppressReminderTurnIds")
    }
    if (-not $State.Contains("items") -or $null -eq $State["items"]) {
        $State["items"] = @()
    }

    return $State
}

function Get-OpenTodos {
    param([System.Collections.IDictionary]$State)

    $todos = @()
    foreach ($item in @($State["items"])) {
        if ($null -ne $item -and $item["status"] -eq "open") {
            $todos += ,$item
        }
    }
    return $todos
}

function Format-TodoList {
    param([array]$Todos)

    if ($Todos.Count -eq 0) {
        return (New-StringFromCodepoints @(0x5F53, 0x524D, 0x6CA1, 0x6709, 0x672A, 0x5B8C, 0x6210, 0x5F85, 0x529E, 0x3002))
    }

    $lines = @()
    for ($i = 0; $i -lt $Todos.Count; $i++) {
        $lines += ("{0}. {1}" -f ($i + 1), $Todos[$i]["text"])
    }
    return ($lines -join "`n")
}

function New-StringFromCodepoints {
    param([int[]]$Codepoints)

    $chars = @()
    foreach ($codepoint in $Codepoints) {
        $chars += [char]$codepoint
    }
    return (-join $chars)
}

function Get-PayloadValue {
    param(
        $Payload,
        [string]$Name,
        $Default = $null
    )

    if ($null -ne $Payload -and $Payload.PSObject.Properties.Name -contains $Name) {
        return $Payload.PSObject.Properties[$Name].Value
    }
    return $Default
}

function Resolve-WorkspaceRoot {
    param($Payload)

    $cwd = Get-PayloadValue $Payload "cwd" $null
    if ($cwd) {
        return [System.IO.Path]::GetFullPath([string]$cwd)
    }
    return [System.IO.Directory]::GetCurrentDirectory()
}

function Get-StatePaths {
    param([string]$WorkspaceRoot)

    $stateDir = Join-Path $WorkspaceRoot ".codex-todo"
    return @{
        StateDir = $stateDir
        StatePath = Join-Path $stateDir "todos.json"
        LockPath = Join-Path $stateDir "todos.lock"
    }
}

function Read-State {
    param([string]$StatePath)

    if (-not (Test-Path -LiteralPath $StatePath)) {
        return (New-DefaultState)
    }

    $raw = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return (New-DefaultState)
    }

    return (Normalize-State ($raw | ConvertFrom-Json))
}

function Save-State {
    param(
        [string]$StatePath,
        [System.Collections.IDictionary]$State
    )

    $directory = Split-Path -Parent $StatePath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $tmp = "$StatePath.tmp"
    $json = $State | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($tmp, $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmp -Destination $StatePath -Force
}

function Invoke-WithStateLock {
    param(
        [string]$LockPath,
        [scriptblock]$ScriptBlock
    )

    $directory = Split-Path -Parent $LockPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $stream = $null
    while ($null -eq $stream) {
        try {
            $stream = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) {
                $message = (New-StringFromCodepoints @(0x7B49, 0x5F85, 0x0020, 0x0074, 0x006F, 0x0064, 0x006F, 0x0020, 0x72B6, 0x6001, 0x9501, 0x8D85, 0x65F6, 0xFF1A)) + $LockPath
                throw $message
            }
            Start-Sleep -Milliseconds 50
        }
    }

    try {
        & $ScriptBlock
    }
    finally {
        $stream.Dispose()
    }
}

function New-AdditionalContextOutput {
    param([string]$Context)

    return @{
        hookSpecificOutput = @{
            hookEventName = "UserPromptSubmit"
            additionalContext = $Context
        }
    }
}

function New-BlockOutput {
    param([string]$Reason)

    return @{
        decision = "block"
        reason = $Reason
    }
}

function Get-UiText {
    param([string]$Key)

    switch ($Key) {
        "Added" { return (New-StringFromCodepoints @(0x5DF2, 0x6DFB, 0x52A0, 0x3002)) }
        "CurrentTodos" { return (New-StringFromCodepoints @(0x5F53, 0x524D, 0x672A, 0x5B8C, 0x6210, 0x5F85, 0x529E, 0xFF1A)) }
        "NoTodos" { return (New-StringFromCodepoints @(0x5F53, 0x524D, 0x6CA1, 0x6709, 0x672A, 0x5B8C, 0x6210, 0x5F85, 0x529E, 0x3002)) }
        "Removed" { return (New-StringFromCodepoints @(0x5DF2, 0x5220, 0x9664, 0xFF1A)) }
        "RemovedAll" { return (New-StringFromCodepoints @(0x5DF2, 0x5220, 0x9664, 0x5168, 0x90E8, 0x5F85, 0x529E, 0x3002)) }
        "OutOfRange" { return (New-StringFromCodepoints @(0x5E8F, 0x53F7, 0x8D85, 0x51FA, 0x8303, 0x56F4, 0x3002)) }
        "RemoveAllEmpty" { return (New-StringFromCodepoints @(0x6CA1, 0x6709, 0x53EF, 0x5220, 0x9664, 0x7684, 0x5F85, 0x529E, 0x3002)) }
        "RemoveUsage" { return (New-StringFromCodepoints @(0x8BF7, 0x4F7F, 0x7528)) + " todo remove " + (New-StringFromCodepoints @(0x6216)) + " todo remove 1" + (New-StringFromCodepoints @(0x3002)) }
        "Usage" {
            $content = New-StringFromCodepoints @(0x5185, 0x5BB9)
            $number = New-StringFromCodepoints @(0x5E8F, 0x53F7)
            return "todo <$content>`ntodo list`ntodo remove [$number]`n<$number>"
        }
    }

    return ""
}

function Format-UiTodoList {
    param([array]$Todos)

    if ($Todos.Count -eq 0) {
        return (Get-UiText "NoTodos")
    }

    return (Format-TodoList $Todos)
}

function New-ListMessage {
    param([array]$Todos)

    $label = Get-UiText "CurrentTodos"
    $list = Format-UiTodoList $Todos
    return "$label`n`n$list"
}

function Remove-TodoById {
    param(
        [System.Collections.IDictionary]$State,
        [int]$Id
    )

    $remaining = @()
    $removed = $null
    foreach ($item in @($State["items"])) {
        if ($null -ne $item -and [int]$item["id"] -eq $Id -and $item["status"] -eq "open" -and $null -eq $removed) {
            $removed = $item
        }
        else {
            $remaining += ,$item
        }
    }
    $State["items"] = @($remaining)
    return $removed
}

function Clear-OpenTodos {
    param([System.Collections.IDictionary]$State)

    $remaining = @()
    $removed = @()
    foreach ($item in @($State["items"])) {
        if ($null -ne $item -and $item["status"] -eq "open") {
            $removed += ,$item
        }
        else {
            $remaining += ,$item
        }
    }
    $State["items"] = @($remaining)
    return $removed
}

function Handle-UserPromptSubmit {
    param(
        $Payload,
        [string]$StatePath,
        [string]$LockPath
    )

    $prompt = [string](Get-PayloadValue $Payload "prompt" "")
    $trimmed = $prompt.Trim()
    $output = $null

    Invoke-WithStateLock $LockPath {
        $state = Normalize-State (Read-State $StatePath)
        $lower = $trimmed.ToLowerInvariant()
        $isTodoCommand = $lower -match '^todo(\s|$)' -or $lower -match '^\d+$'
        $clearedPending = $false

        if ($state["pendingExecution"]) {
            $state["pendingExecution"] = $null
            $clearedPending = $true
        }

        if (-not $isTodoCommand) {
            if ($clearedPending) {
                Save-State $StatePath $state
            }
            $script:output = @{ continue = $true }
            return
        }

        $match = [regex]::Match($trimmed, '^todo(?:\s+([\s\S]*?))?\s*$')
        $command = ""
        $argument = ""
        if ($match.Success) {
            $rest = $match.Groups[1].Value.Trim()
            if ([string]::IsNullOrWhiteSpace($rest)) {
                $command = "list"
            }
            else {
                $parts = $rest -split '\s+', 2
                $first = $parts[0].ToLowerInvariant()
                $remaining = ""
                if ($parts.Count -gt 1) {
                    $remaining = $parts[1].Trim()
                }

                switch ($first) {
                    "list" {
                        $command = "list"
                    }
                    "remove" {
                        $command = "remove"
                        $argument = $remaining
                    }
                    "do" {
                        $command = "do"
                        $argument = $remaining
                    }
                    default {
                        $command = "add"
                        $argument = $rest
                    }
                }
            }
        }
        elseif ($lower -match '^\d+$') {
            $command = "do"
            $argument = $lower
        }

        switch ($command) {
            "add" {
                $id = [int]$state["nextId"]
                $state["nextId"] = $id + 1
                $state["items"] = @($state["items"]) + ,([ordered]@{
                    id = $id
                    text = $argument
                    createdAt = [DateTimeOffset]::UtcNow.ToString("o")
                    status = "open"
                })
                $state["pendingExecution"] = $null
                Save-State $StatePath $state

                $message = (Get-UiText "Added") + "`n`n" + (New-ListMessage @(Get-OpenTodos $state))
                $script:output = New-BlockOutput $message
                return
            }

            "list" {
                $state["pendingExecution"] = $null
                Save-State $StatePath $state
                $script:output = New-BlockOutput (New-ListMessage @(Get-OpenTodos $state))
                return
            }

            "remove" {
                $index = 0
                if ([string]::IsNullOrWhiteSpace($argument)) {
                    $removed = @(Clear-OpenTodos $state)
                    $state["pendingExecution"] = $null
                    Save-State $StatePath $state

                    if ($removed.Count -eq 0) {
                        $script:output = New-BlockOutput (Get-UiText "RemoveAllEmpty")
                    }
                    else {
                        $script:output = New-BlockOutput ((Get-UiText "RemovedAll") + "`n`n" + (New-ListMessage @(Get-OpenTodos $state)))
                    }
                    return
                }

                if (-not [int]::TryParse($argument, [ref]$index)) {
                    Save-State $StatePath $state
                    $script:output = New-BlockOutput (Get-UiText "RemoveUsage")
                    return
                }

                $todos = @(Get-OpenTodos $state)
                if ($index -lt 1 -or $index -gt $todos.Count) {
                    Save-State $StatePath $state
                    $message = (Get-UiText "OutOfRange") + "`n`n" + (New-ListMessage $todos)
                    $script:output = New-BlockOutput $message
                    return
                }

                $removed = Remove-TodoById $state ([int]$todos[$index - 1]["id"])
                $state["pendingExecution"] = $null
                Save-State $StatePath $state

                $message = (Get-UiText "Removed") + $removed["text"] + "`n`n" + (New-ListMessage @(Get-OpenTodos $state))
                $script:output = New-BlockOutput $message
                return
            }

            "do" {
                $index = 0
                if (-not [int]::TryParse($argument, [ref]$index)) {
                    Save-State $StatePath $state
                    $script:output = New-BlockOutput (Get-UiText "Usage")
                    return
                }

                $todos = @(Get-OpenTodos $state)
                if ($index -lt 1 -or $index -gt $todos.Count) {
                    Save-State $StatePath $state
                    $message = (Get-UiText "OutOfRange") + "`n`n" + (New-ListMessage $todos)
                    $script:output = New-BlockOutput $message
                    return
                }

                $todo = $todos[$index - 1]
                $removed = Remove-TodoById $state ([int]$todo["id"])
                $state["pendingExecution"] = $null
                Save-State $StatePath $state

                $todoText = [string]$removed["text"]
                $selectedPrefix = New-StringFromCodepoints @(0x7528, 0x6237, 0x9009, 0x62E9, 0x4E86, 0x5DE5, 0x4F5C, 0x533A, 0x0020, 0x0074, 0x006F, 0x0064, 0x006F, 0x0020, 0x7B2C, 0x0020)
                $selectedSuffix = New-StringFromCodepoints @(0x0020, 0x9879, 0x3002, 0x9009, 0x4E2D, 0x7684, 0x0020, 0x0074, 0x006F, 0x0064, 0x006F, 0x0020, 0x5DF2, 0x7ECF, 0x4ECE, 0x0020, 0x002E, 0x0063, 0x006F, 0x0064, 0x0065, 0x0078, 0x002D, 0x0074, 0x006F, 0x0064, 0x006F, 0x002F, 0x0074, 0x006F, 0x0064, 0x006F, 0x0073, 0x002E, 0x006A, 0x0073, 0x006F, 0x006E, 0x0020, 0x4E2D, 0x5220, 0x9664, 0x3002)
                $requestLine = New-StringFromCodepoints @(0x8BF7, 0x628A, 0x4E0B, 0x9762, 0x7684, 0x0020, 0x0074, 0x006F, 0x0064, 0x006F, 0x0020, 0x539F, 0x6587, 0x5F53, 0x4F5C, 0x7528, 0x6237, 0x5F53, 0x524D, 0x8BF7, 0x6C42, 0x6765, 0x5904, 0x7406, 0xFF1A)
                $instructionLine = New-StringFromCodepoints @(0x6309, 0x6B63, 0x5E38, 0x0020, 0x0043, 0x006F, 0x0064, 0x0065, 0x0078, 0x0020, 0x5DE5, 0x4F5C, 0x6D41, 0x7A0B, 0x56DE, 0x7B54, 0x6216, 0x6267, 0x884C, 0x3002, 0x9664, 0x975E, 0x0020, 0x0074, 0x006F, 0x0064, 0x006F, 0x0020, 0x672C, 0x8EAB, 0x8981, 0x6C42, 0x8BA8, 0x8BBA, 0x5B9E, 0x73B0, 0x601D, 0x8DEF, 0xFF0C, 0x5426, 0x5219, 0x4E0D, 0x8981, 0x8F6C, 0x6210, 0x5B9E, 0x73B0, 0x601D, 0x8DEF, 0x8BA8, 0x8BBA, 0x3002)
                $context = "$selectedPrefix$index$selectedSuffix`n`n$requestLine`n$todoText`n`n$instructionLine"
                $script:output = New-AdditionalContextOutput $context
                return
            }

            default {
                Save-State $StatePath $state
                $script:output = New-BlockOutput (Get-UiText "Usage")
                return
            }
        }
    }

    if ($null -eq $script:output) {
        $script:output = @{ continue = $true }
    }
    Write-HookJson $script:output
}

$stdin = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($stdin)) {
    Write-HookJson @{ continue = $true }
    exit 0
}

$payload = $stdin | ConvertFrom-Json
$workspaceRoot = Resolve-WorkspaceRoot $payload
$paths = Get-StatePaths $workspaceRoot
$eventName = [string](Get-PayloadValue $payload "hook_event_name" "")

switch ($eventName) {
    "UserPromptSubmit" { Handle-UserPromptSubmit $payload $paths.StatePath $paths.LockPath }
    default { Write-HookJson @{ continue = $true } }
}
