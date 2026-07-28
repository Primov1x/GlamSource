# fix-plugin.ps1
# Behebt zwei Bugs in Plugin.cs:
# 1. DisposeAsync: OpenMainUi += sollte -= sein (Copy-Paste-Fehler)
# 2. Fuegt Debug-Log nach der Draw-Handler-Registrierung ein
#
# Aufruf im GlamSource-Projektordner:
#   .\fix-plugin.ps1

$path = "Plugin.cs"
$content = Get-Content $path -Raw

# Fix 1: += zu -= in DisposeAsync
$disposePattern = "PluginInterface\.UiBuilder\.OpenMainUi \+= ToggleMainUi;(\r?\n\s+)WindowSystem\.RemoveAllWindows"
$disposeReplacement = 'PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;$1WindowSystem.RemoveAllWindows'
$newContent = [regex]::Replace($content, $disposePattern, $disposeReplacement)

if ($newContent -eq $content) {
    Write-Host "WARNUNG: Fix 1 (Dispose += -> -=) hat nichts geaendert - Pattern nicht gefunden." -ForegroundColor Yellow
} else {
    Write-Host "Fix 1 angewendet: OpenMainUi += -> -= in DisposeAsync" -ForegroundColor Green
    $content = $newContent
}

# Fix 2: Debug-Log nach der Konstruktor-Registrierung einfuegen
$ctorPattern = '(PluginInterface\.UiBuilder\.OpenMainUi \+= ToggleMainUi;\r?\n)(\s+)(Log\.Information\(\$"===A cool log message)'
$ctorReplacement = '$1$2Log.Information($"[DEBUG] WindowSystem.Windows.Count = {WindowSystem.Windows.Count}, mainWindow.IsOpen = {mainWindow.IsOpen}");' + "`r`n" + '$2$3'
$newContent2 = [regex]::Replace($content, $ctorPattern, $ctorReplacement)

if ($newContent2 -eq $content) {
    Write-Host "WARNUNG: Fix 2 (Debug-Log einfuegen) hat nichts geaendert - Pattern nicht gefunden." -ForegroundColor Yellow
} else {
    Write-Host "Fix 2 angewendet: Debug-Log eingefuegt" -ForegroundColor Green
    $content = $newContent2
}

Set-Content $path -Value $content -NoNewline

Write-Host ""
Write-Host "Fertig. Pruefen mit:" -ForegroundColor Cyan
Write-Host '  Get-Content Plugin.cs | Select-String -Pattern "DEBUG|OpenMainUi"' -ForegroundColor Gray
