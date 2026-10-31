# UI checks for the Larik WinUI shell: vertical tabs, spaces, and Tab-accepted snippet completion.
# Run from C:\dev\larik-winui\Larik:  powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1
param(
    [Parameter(Mandatory)][ValidateSet('sandbox', 'local')][string]$Target,
    [int]$AppPid = 0,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\ui-test-artifacts')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$ScopeArgs = @(if ($Target -eq 'sandbox') { '--on'; 'sandbox' })
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()

function Invoke-WinAppChecked {
    param([string[]]$Arguments, [int]$Attempts = 1)
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $global:LASTEXITCODE = $null
        $output = @(& winapp @Arguments)
        $code = $global:LASTEXITCODE
        if ($null -ne $code -and $code -eq 0) { return $output }
        if ($attempt -eq $Attempts) {
            throw "winapp $($Arguments -join ' ') failed (exit $code). $($output -join "`n")"
        }
        Start-Sleep -Milliseconds 700
    }
}

function Invoke-Ui {
    param([string[]]$Arguments, [int]$Attempts = 1)
    Invoke-WinAppChecked (@('ui') + $Arguments + $ScopeArgs + @('-a', "$AppPid")) -Attempts $Attempts
}

function Get-UiJson {
    param([string[]]$Arguments)
    (Invoke-Ui $Arguments) -join "`n" | ConvertFrom-Json
}

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try { & $Script | Out-Null; $results.Add([pscustomobject]@{ Name = $Name; Status = 'PASS' }) }
    catch { $results.Add([pscustomobject]@{ Name = $Name; Status = 'FAIL'; Detail = "$_" }) }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-EditorText { (Get-UiJson @('get-value', 'Editor', '--json')).text }

# winapp ui refuses synthetic input while another window owns the foreground.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class LarikWin32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

function Focus-App {
    $windows = @(Get-UiJson @('list-windows', '--json'))
    $window = $windows | Where-Object { $_.processId -eq $AppPid } | Select-Object -First 1
    if (-not $window) { return }
    $handle = [IntPtr]([int64](@($window.hwnd) | Select-Object -First 1))
    [LarikWin32]::ShowWindow($handle, 9) | Out-Null
    [LarikWin32]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 400
}
function Find-Element {
    param([string]$Text)
    # 'search' exits 1 when nothing matches; that is a result, not a harness failure.
    $searchArgs = @('ui', 'search', $Text, '--json') + $ScopeArgs + @('-a', "$AppPid")
    $raw = @(& winapp $searchArgs)
    if (-not $raw -or -not ($raw -join '').Trim()) { return [pscustomobject]@{ matchCount = 0 } }
    ($raw -join "`n") | ConvertFrom-Json
}
function Send-Keys { param([string]$Keys, [switch]$Literal) Focus-App; Invoke-Ui (@('send-keys', $Keys, '--via', 'send-input') + $(if ($Literal) { @('--verbatim') } else { @() })) | Out-Null }
function Click-Element { param([string]$Selector) Focus-App; Invoke-Ui @('click', $Selector) -Attempts 4 | Out-Null }
function Wait-ForText {
    param([string]$Text, [int]$Seconds = 8)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Find-Element $Text).matchCount -gt 0) { return }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out waiting for '$Text'"
}

if ($AppPid -le 0) {
    $launch = (Invoke-WinAppChecked (@('run', '.', '--detach', '--json') + $ScopeArgs)) -join "`n" | ConvertFrom-Json
    $AppPid = [int]$launch.ProcessId
}
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
Start-Sleep -Seconds 3

# Wait for the shell, then focus it so synthetic input does not land in the console.
try { Invoke-Ui @('wait-for', 'Editor') -Attempts 20 | Out-Null } catch { }
Focus-App
Invoke-Ui @('focus', 'Editor') -Attempts 5 | Out-Null

try {
    Test-UI 'window shows the studio title' {
        Assert-True ((Invoke-Ui @('list-windows', '--json')) -join '' -match 'Larik SQL Studio') 'main window not found'
    }

    Test-UI 'sidebar lists the active space tabs' {
        Assert-True ((Find-Element 'Revenue by month').matchCount -gt 0) 'first tab is not exposed'
        Assert-True ((Find-Element 'Scratch').matchCount -gt 0) 'third tab is not exposed'
    }

    Test-UI 'a workspace starts disconnected and connects on demand' {
        # Fresh start: the connection belongs to the workspace and is off until asked for.
        Assert-True ((Find-Element 'Disconnected').matchCount -gt 0) 'workspace did not start disconnected'
        Assert-True ((Find-Element 'Connect').matchCount -gt 0) 'no way to connect'
        Click-Element 'ConnectionToggleButton'
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element 'Connected').matchCount -gt 0) 'workspace did not connect'
        Assert-True ((Find-Element 'Disconnect').matchCount -gt 0) 'toggle did not offer Disconnect'
    }

    Test-UI 'switching a tab loads its query into the editor' {
        Click-Element 'Slow customers'
        Start-Sleep -Milliseconds 800
        Assert-True ((Get-EditorText) -match 'orders_count') 'editor did not switch to the other tab'
    }

    Test-UI 'typing a snippet trigger opens the suggestion popup' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'sel' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'SQL suggestions').matchCount -gt 0) 'completion popup did not open'
        Assert-True ((Find-Element 'SELECT *').matchCount -gt 0) 'SELECT * snippet not suggested'
    }

    Test-UI 'Tab accepts the snippet and leaves the caret at the placeholder' {
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 900
        Assert-True ((Get-EditorText) -eq 'SELECT * FROM ') "editor text was '$((Get-EditorText))', expected 'SELECT * FROM '"
        Assert-True ((Find-Element 'SQL suggestions').matchCount -eq 0) 'popup stayed open after accepting'
    }

    Test-UI 'Ctrl+Enter runs the query and fills the results grid' {
        Send-Keys 'customers;' -Literal
        Start-Sleep -Milliseconds 400
        Send-Keys 'ctrl+enter'
        Wait-ForText 'FULL NAME'
        Assert-True ((Find-Element '24 rows returned').matchCount -gt 0) 'status bar did not report the row count'
    }

    Test-UI 'switching spaces swaps connection, tabs and editor' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Staging'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'PostgreSQL').matchCount -gt 0) 'status bar still shows the old connection'
        Assert-True ((Find-Element 'Migration check').matchCount -gt 0) 'staging tab missing'
        Assert-True ((Get-EditorText) -match 'information_schema') 'staging tab content not loaded'
    }

    Test-UI 'switching database updates the rail, status bar and tab' {
        Click-Element 'Select database'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'larik_sandbox').matchCount -gt 0) 'database list did not open'
        Click-Element 'larik_sandbox'
        Start-Sleep -Milliseconds 800
        # The selector row, the tab subtitle and the status line all follow the active database.
        Assert-True ((Find-Element 'larik_sandbox').matchCount -ge 2) 'database selection did not propagate'
    }

    Test-UI 'connection status belongs to the workspace, not to a database' {
        # Staging starts disconnected; connect it so a stale 'Disconnected' can only come from a database row.
        Click-Element 'ConnectionToggleButton'
        Start-Sleep -Milliseconds 700
        Click-Element 'Select database'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'larik_sandbox').matchCount -gt 0) 'database list did not open'
        Assert-True ((Find-Element 'Disconnected').matchCount -eq 0) 'a database carried its own connection status'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 400
    }

    Test-UI 'a workspace can gain another database' {
        Click-Element 'Select database'
        Start-Sleep -Milliseconds 500
        # State-changing click: no retries, the flyout closes as soon as it lands.
        try { Invoke-Ui @('click', 'NewDatabaseButton') | Out-Null } catch { }
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element 'database_').matchCount -gt 0) 'new database did not appear in the rail'
    }

    Test-UI 'Ctrl+B collapses and restores the sidebar' {
        Send-Keys 'ctrl+b'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'DatabaseSelectorButton').matchCount -eq 0) 'sidebar did not collapse'
        Send-Keys 'ctrl+b'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'DatabaseSelectorButton').matchCount -gt 0) 'sidebar did not come back'
    }

    Test-UI 'New query adds a vertical tab' {
        Click-Element 'New query tab'
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element 'Query 1').matchCount -gt 0) 'new tab did not appear'
    }

    Test-UI 'refuses a mutating statement without WHERE' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'DELETE FROM orders' -Literal
        Start-Sleep -Milliseconds 400
        Click-Element 'RunButton'
        Wait-ForText 'Refused'
    }

    $shot = Join-Path $ArtifactDirectory 'larik-shell.png'
    Invoke-Ui @('screenshot', '--output', $shot) | Out-Null
    $screenshots.Add($shot)
}
finally {
    $results | Format-Table -AutoSize | Out-String | Write-Host
    if (($results | Where-Object Status -eq 'FAIL').Count -gt 0) {
        $results | Where-Object Status -eq "FAIL" | ForEach-Object { Write-Host ("FAIL: " + $_.Name + " :: " + $_.Detail) }
    }
    $screenshots | ForEach-Object { Write-Host "screenshot: $_" }
}
