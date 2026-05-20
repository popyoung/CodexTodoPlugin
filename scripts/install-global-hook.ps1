param(
    [string]$CodexHome = (Join-Path $env:USERPROFILE ".codex"),
    [switch]$Uninstall
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

function New-StringFromCodepoints {
    param([int[]]$Codepoints)

    $chars = @()
    foreach ($codepoint in $Codepoints) {
        $chars += [char]$codepoint
    }
    return (-join $chars)
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

function Remove-TodoHookReferences {
    param(
        [System.Collections.IDictionary]$Hooks,
        [string]$EventName
    )

    if (-not $Hooks.Contains($EventName)) {
        return
    }

    $keptEntries = @()
    foreach ($entryValue in @($Hooks[$EventName])) {
        $entry = Convert-ToHashtable $entryValue
        if ($null -eq $entry -or -not $entry.Contains("hooks")) {
            $keptEntries += ,$entry
            continue
        }

        $keptHookCommands = @()
        foreach ($hookValue in @($entry["hooks"])) {
            $hook = Convert-ToHashtable $hookValue
            $command = ""
            if ($null -ne $hook -and $hook.Contains("command")) {
                $command = [string]$hook["command"]
            }

            if ($command -notlike "*todo-hook.ps1*") {
                $keptHookCommands += ,$hook
            }
        }

        if ($keptHookCommands.Count -gt 0) {
            $entry["hooks"] = @($keptHookCommands)
            $keptEntries += ,$entry
        }
    }

    if ($keptEntries.Count -eq 0) {
        $Hooks.Remove($EventName)
    }
    else {
        $Hooks[$EventName] = @($keptEntries)
    }
}

$hookScript = Join-Path $PSScriptRoot "todo-hook.ps1"
if (-not (Test-Path -LiteralPath $hookScript)) {
    $message = (New-StringFromCodepoints @(0x627E, 0x4E0D, 0x5230, 0x0020, 0x0074, 0x006F, 0x0064, 0x006F, 0x0020, 0x0068, 0x006F, 0x006F, 0x006B, 0x0020, 0x811A, 0x672C, 0xFF1A)) + $hookScript
    throw $message
}

$hooksPath = Join-Path $CodexHome "hooks.json"
if (-not (Test-Path -LiteralPath $CodexHome)) {
    New-Item -ItemType Directory -Force -Path $CodexHome | Out-Null
}

if (Test-Path -LiteralPath $hooksPath) {
    $raw = Get-Content -LiteralPath $hooksPath -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) {
        $config = [ordered]@{ hooks = [ordered]@{} }
    }
    else {
        $config = Convert-ToHashtable ($raw | ConvertFrom-Json)
    }
}
else {
    $config = [ordered]@{ hooks = [ordered]@{} }
}

if (-not $config.Contains("hooks") -or $null -eq $config["hooks"]) {
    $config["hooks"] = [ordered]@{}
}

$hooks = Convert-ToHashtable $config["hooks"]
$config["hooks"] = $hooks

Remove-TodoHookReferences $hooks "UserPromptSubmit"
Remove-TodoHookReferences $hooks "Stop"

if (-not $Uninstall) {
    $escapedHookScript = $hookScript.Replace("'", "''")
    $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$escapedHookScript`""
    $statusMessage = New-StringFromCodepoints @(0x68C0, 0x67E5, 0x5DE5, 0x4F5C, 0x533A, 0x0020, 0x0074, 0x006F, 0x0064, 0x006F, 0x0020, 0x547D, 0x4EE4)
    $todoEntry = [ordered]@{
        hooks = @(
            [ordered]@{
                type = "command"
                command = $command
                timeout = 10
                statusMessage = $statusMessage
            }
        )
    }

    $existing = @()
    if ($hooks.Contains("UserPromptSubmit")) {
        $existing = @($hooks["UserPromptSubmit"])
    }
    $hooks["UserPromptSubmit"] = @($existing + $todoEntry)
}

$json = $config | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($hooksPath, $json, [System.Text.UTF8Encoding]::new($false))

if ($Uninstall) {
    $prefix = New-StringFromCodepoints @(0x5DF2, 0x4ECE, 0x0020)
    $suffix = New-StringFromCodepoints @(0x0020, 0x79FB, 0x9664, 0x0020, 0x0043, 0x006F, 0x0064, 0x0065, 0x0078, 0x0020, 0x0054, 0x006F, 0x0064, 0x006F, 0x0020, 0x94A9, 0x5B50)
    Write-Host "$prefix$hooksPath$suffix"
}
else {
    $prefix = New-StringFromCodepoints @(0x5DF2, 0x5728, 0x0020)
    $suffix = New-StringFromCodepoints @(0x0020, 0x5B89, 0x88C5, 0x0020, 0x0043, 0x006F, 0x0064, 0x0065, 0x0078, 0x0020, 0x0054, 0x006F, 0x0064, 0x006F, 0x0020, 0x0055, 0x0073, 0x0065, 0x0072, 0x0050, 0x0072, 0x006F, 0x006D, 0x0070, 0x0074, 0x0053, 0x0075, 0x0062, 0x006D, 0x0069, 0x0074, 0x0020, 0x94A9, 0x5B50)
    $notice = New-StringFromCodepoints @(0x6D4B, 0x8BD5, 0x524D, 0x8BF7, 0x5728, 0x0020, 0x0043, 0x006F, 0x0064, 0x0065, 0x0078, 0x0020, 0x4E2D, 0x6253, 0x5F00, 0x0020, 0x002F, 0x0068, 0x006F, 0x006F, 0x006B, 0x0073, 0xFF0C, 0x5E76, 0x4FE1, 0x4EFB, 0x66F4, 0x65B0, 0x540E, 0x7684, 0x94A9, 0x5B50, 0x3002)
    Write-Host "$prefix$hooksPath$suffix"
    Write-Host $notice
}
