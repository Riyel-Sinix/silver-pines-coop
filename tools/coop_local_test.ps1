# Silver Pines Coop - 双开测试启动器: two copies of your own game on this PC, linked over 127.0.0.1 by the mod
# (launch option --coop-local) instead of a Steam lobby. Lives in BepInEx\plugins\SilverPinesCoop\.
# Saved as UTF-8 with BOM: Windows PowerShell 5.1 reads BOM-less scripts in the ANSI code page.
param([string]$GameDirectory = '')

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

# The game's own Steam AppID: Steam sets these variables the same way when it starts the game, so
# SteamAPI_RestartAppIfNecessary does not send a directly started copy back through the Steam client.
$AppId = '2333000'
$PrefsKey = 'HKCU\Software\Wych Elm\Silver Pines'

function Say([string]$Text, [string]$Color = 'Gray') { Write-Host $Text -ForegroundColor $Color }
# Native tools write status text to stderr (reg import does even on success); under 'Stop' Windows PowerShell 5.1
# would turn redirected stderr into a terminating error, so run them with 'Continue' and keep only the exit code.
function Invoke-Native([string]$File, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $File @Arguments 2>&1 | Out-Null } finally { $ErrorActionPreference = $previous }
    return $LASTEXITCODE
}
function Fail([string]$Text) {
    Say $Text 'Red'
    [void](Read-Host '按回车关闭')
    exit 1
}

if (-not $GameDirectory) { $GameDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path }
$exe = Join-Path $GameDirectory 'Carpenter.exe'
if (-not (Test-Path -LiteralPath $exe)) { Fail "找不到游戏：$exe（这个脚本要放在 游戏目录\BepInEx\plugins\SilverPinesCoop 里）" }
if (Get-Process -Name 'Carpenter' -ErrorAction SilentlyContinue) { Fail '游戏已经在运行，请先退出 Silver Pines 再开始双开测试。' }
if (-not (Get-Process -Name 'steam' -ErrorAction SilentlyContinue)) { Fail '请先打开 Steam 并登录（两个窗口都用你自己的账号）。' }

Say '银松镇联机 Mod · 双开测试' 'Cyan'
Say '两个游戏窗口通过本机 127.0.0.1 直接互连，不走 Steam 大厅；用来一个人测试联机同步。'
Say ''

# 1. back up the display settings (Unity writes the window size back on exit) and the saves (both copies share them)
$work = Join-Path $GameDirectory 'BepInEx\coop-test'
$backup = Join-Path $work ('backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $backup | Out-Null
$prefsBackup = Join-Path $backup 'player-prefs.reg'
$havePrefs = ((Invoke-Native 'reg.exe' @('export', $PrefsKey, $prefsBackup, '/y')) -eq 0)
$saves = Join-Path $env:USERPROFILE 'AppData\LocalLow\Wych Elm\Silver Pines'
$savesBackup = Join-Path $backup 'saves'
if (Test-Path -LiteralPath $saves) {
    $code = Invoke-Native 'robocopy.exe' @($saves, $savesBackup, '/E', '/XF', '*.log', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    if ($code -ge 8) { Fail "存档备份失败（robocopy 退出码 $code），没有启动游戏。" }
}
Say "已备份显示设置和存档：$backup" 'DarkGray'

# 2. start the two copies
$env:SteamAppId = $AppId
$env:SteamGameId = $AppId
$copies = @()
foreach ($copy in 1, 2) {
    if ($copy -eq 2) {
        Say ''
        Say '等窗口 1 进到主菜单后，按回车启动窗口 2。' 'Yellow'
        [void](Read-Host)
    }
    $log = Join-Path $work "player-copy$copy.log"
    $arguments = "--coop-local -screen-fullscreen 0 -logFile `"$log`""
    $process = Start-Process -FilePath $exe -WorkingDirectory $GameDirectory -ArgumentList $arguments -PassThru
    $copies += $process
    Say "窗口 $copy 已启动（进程 $($process.Id)）"
}

Say ''
Say '两个窗口会自动排成左右两半。两边都读同一个存档（或都开新游戏），走到同一个房间就能看到对方。' 'Cyan'
Say '左上角显示“已连上另一个窗口”说明两边连通了；手柄/键盘只控制当前激活的那个窗口。' 'Cyan'
Say '测完把两个游戏窗口都关掉，这里会自动还原显示设置。' 'Cyan'
foreach ($process in $copies) {
    try { $process.WaitForExit() } catch { }
}

# 3. restore
if ($havePrefs) {
    [void](Invoke-Native 'reg.exe' @('import', $prefsBackup))
    Say ''
    Say '已还原显示设置（窗口大小 / 全屏）。' 'Green'
}
if (Test-Path -LiteralPath $savesBackup) {
    $answer = Read-Host '要把存档也还原成测试前的样子吗？输入 Y 还原，直接回车保留现在的存档'
    if ($answer -match '^[Yy]') {
        $code = Invoke-Native 'robocopy.exe' @($savesBackup, $saves, '/MIR', '/IS', '/IT', '/XF', '*.log', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
        if ($code -ge 8) { Say "存档还原失败（robocopy 退出码 $code），备份还在：$savesBackup" 'Red' }
        else { Say '存档已还原。' 'Green' }
    }
}
[void](Read-Host '按回车关闭')
