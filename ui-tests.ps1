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

# Workspaces persist to a SQLite database under %LOCALAPPDATA%\Larik (larik-dev.db in Debug
# builds). Park the tester's databases, WAL sidecars included, so every run starts from the
# seeded spaces; Restore-StudioState puts them back untouched. When attaching to a running app
# (-AppPid) the state belongs to that app, so leave it alone.
$LaunchedHere = $AppPid -le 0
$StateDir = Join-Path $env:LOCALAPPDATA 'Larik'
$StateBases = @('larik-dev.db', 'larik.db') | ForEach-Object { Join-Path $StateDir $_ }
$StatePath = $StateBases[0]
$StateMoved = @()
if ($LaunchedHere) {
    foreach ($base in $StateBases) {
        foreach ($suffix in '', '-wal', '-shm') {
            $path = "$base$suffix"
            if (Test-Path $path) {
                Move-Item $path "$path.ui-tests" -Force
                $StateMoved += $path
            }
        }
    }
}

function Restore-StudioState {
    if (-not $LaunchedHere) { return }

    foreach ($base in $StateBases) {
        foreach ($suffix in '', '-wal', '-shm') {
            Remove-Item "$base$suffix" -ErrorAction SilentlyContinue
        }
    }
    foreach ($path in $StateMoved) {
        if (Test-Path "$path.ui-tests") { Move-Item "$path.ui-tests" $path -Force }
    }
}

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

# Synthetic input only reaches the app while it owns the foreground. Windows blocks a background
# process from stealing focus, so a synthetic Alt press unlocks SetForegroundWindow first.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class LarikWin32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@

function Focus-App {
    $windows = @(Get-UiJson @('list-windows', '--json'))
    $window = $windows | Where-Object { $_.processId -eq $AppPid } | Select-Object -First 1
    if (-not $window) { return }
    $handle = [IntPtr]([int64](@($window.hwnd) | Select-Object -First 1))
    [LarikWin32]::ShowWindow($handle, 9) | Out-Null
    if ([LarikWin32]::GetForegroundWindow() -ne $handle) {
        [LarikWin32]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [LarikWin32]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        [LarikWin32]::SetForegroundWindow($handle) | Out-Null
    }
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
    try {
        $launch = (Invoke-WinAppChecked (@('run', '.', '--detach', '--json') + $ScopeArgs)) -join "`n" | ConvertFrom-Json
    }
    catch {
        # The app never started, so hand the tester's workspaces back before failing.
        Restore-StudioState
        throw
    }
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

    Test-UI 'sidebar lists the active workspace tabs' {
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'workspace tab is not exposed'
    }

    Test-UI 'a workspace starts disconnected and connects on demand' {
        # Fresh start: the connection belongs to the workspace and is off until asked for.
        Assert-True ((Find-Element 'Disconnected').matchCount -gt 0) 'workspace did not start disconnected'
        Assert-True ((Find-Element 'Connect').matchCount -gt 0) 'no way to connect'
        Click-Element 'ConnectionToggleButton'
        Wait-ForText 'Connected' 15
        Assert-True ((Find-Element 'Disconnect').matchCount -gt 0) 'toggle did not offer Disconnect'
        # Databases come from the provider once the connection opens.
        Wait-ForText 'larik_staging' 10
    }

    Test-UI 'typing a snippet trigger opens the suggestion popup' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'sel' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'SQL suggestions').matchCount -gt 0) 'completion popup did not open'
        Assert-True ((Find-Element 'SELECT *').matchCount -gt 0) 'SELECT * snippet not suggested'
        # The popup must sit under the caret, not offset by the editor's own position.
        $popup = @((Find-Element 'SQL suggestions').matches)[0]
        $editor = @((Find-Element 'SQL editor').matches)[0]
        Assert-True (($popup.y - $editor.y) -lt 60) "popup is not below the first line (popup y=$($popup.y), editor y=$($editor.y))"
        Assert-True (($popup.x - $editor.x) -lt 100) "popup is not at the caret (popup x=$($popup.x), editor x=$($editor.x))"
    }

    Test-UI 'Tab accepts the snippet and leaves the caret at the placeholder' {
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 900
        Assert-True ((Get-EditorText) -eq 'SELECT * FROM ') "editor text was '$((Get-EditorText))', expected 'SELECT * FROM '"
        Assert-True ((Find-Element 'SQL suggestions').matchCount -eq 0) 'popup stayed open after accepting'
    }

    Test-UI 'Ctrl+Enter runs the query through the provider' {
        Send-Keys 'customers;' -Literal
        Start-Sleep -Milliseconds 400
        Send-Keys 'ctrl+enter'
        Wait-ForText 'FULL NAME'
        Assert-True ((Find-Element '24 rows returned').matchCount -gt 0) 'status bar did not report the row count'
    }

    Test-UI 'FROM suggests every table in the active database' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from ' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'SQL suggestions').matchCount -gt 0) 'table suggestions did not open'
        foreach ($table in 'customers', 'orders', 'order_items', 'products') {
            Assert-True ((Find-Element $table).matchCount -gt 0) "table '$table' is not in the suggestions"
        }
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'FROM table suggestions append a generated alias' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from cust' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'customers').matchCount -gt 0) 'customers is not suggested'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText) -eq 'select * from customers AS c') "alias was not appended: '$((Get-EditorText))'"
    }

    Test-UI 'join condition is suggested after ON' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from customers c join orders o on ' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Join on').matchCount -gt 0) 'join condition is not suggested'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText).TrimEnd().EndsWith('o.customer_id = c.id')) "join condition was not inserted: '$((Get-EditorText))'"
    }

    Test-UI 'columns are suggested through their table alias' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from customers c where ' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'c.full_name').matchCount -gt 0) 'alias-qualified column is not suggested'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'UPDATE SET suggests the target columns with =' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'update customers set ' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'full_name').matchCount -gt 0) 'target column is not suggested'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText).TrimEnd().EndsWith('set id =')) "assignment was not inserted: '$((Get-EditorText))'"
    }

    Test-UI 'INSERT INTO suggests the target columns' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'insert into order_items (' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'order_id').matchCount -gt 0) 'insert column is not suggested'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText).EndsWith('id')) "column was not inserted: '$((Get-EditorText))'"
    }

    Test-UI 'declared variables are suggested' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'declare @order_id int; select * from orders where ' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element '@order_id').matchCount -gt 0) 'variable is not suggested'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'USE suggests the workspace databases' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'use lar' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'larik_sandbox').matchCount -gt 0) 'database is not suggested'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'CTE names are suggested after FROM' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'with recent as (select * from orders) select * from rec' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'recent').matchCount -gt 0) 'CTE is not suggested'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText).EndsWith('from recent')) "CTE was not inserted: '$((Get-EditorText))'"
    }

    Test-UI 'CTE columns complete through their name' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'with recent as (select * from orders) select * from recent r where r.' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'customer_id').matchCount -gt 0) 'CTE column is not suggested'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'switching database updates the rail, status bar and tab' {
        Click-Element 'Select database'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'larik_sandbox').matchCount -gt 0) 'database list did not open'
        Click-Element 'larik_sandbox'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'larik_sandbox').matchCount -ge 2) 'database selection did not propagate'
    }

    Test-UI 'connection status belongs to the workspace, not to a database' {
        Click-Element 'Select database'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'larik_sandbox').matchCount -gt 0) 'database list did not open'
        Assert-True ((Find-Element 'Disconnected').matchCount -eq 0) 'a database carried its own connection status'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 400
    }

    Test-UI 'connection settings dialog renders the provider schema' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Connection settings'
        Start-Sleep -Milliseconds 1000
        try {
            Assert-True ((Find-Element 'Provider').matchCount -gt 0) 'provider picker missing'
            Assert-True ((Find-Element 'Databases').matchCount -gt 0) 'provider settings not rendered'
            # Opening the picker proves the catalog listed every loaded provider.
            Click-Element 'Provider'
            Start-Sleep -Milliseconds 600
            Assert-True ((Find-Element 'SQL Server').matchCount -gt 0) 'providers not listed'
            # The SQL Server form is the tall one: Test must keep its result on screen, not
            # just in the automation tree below the visible part of the dialog.
            Click-Element 'SQL Server'
            Start-Sleep -Milliseconds 800
            Click-Element 'Test'
            $status = $null
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $deadline) {
                $status = @((Find-Element 'Connected ·').matches) | Where-Object { $_.width -gt 0 } | Select-Object -First 1
                if ($status) { break }
                Start-Sleep -Milliseconds 300
            }
            Assert-True ($null -ne $status) 'Test did not report its result on screen'
        }
        finally {
            Click-Element 'Cancel'
            Start-Sleep -Milliseconds 700
        }
    }

    Test-UI 'switching workspaces swaps connection, tabs and editor' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Local SQL Server'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'SQL Server').matchCount -gt 0) 'status bar still shows the old provider'
        Assert-True ((Find-Element 'Databases').matchCount -gt 0) 'mssql tab missing'
        Assert-True ((Get-EditorText) -match 'sys.databases') 'tab content not loaded'
        Assert-True ((Find-Element 'Disconnected').matchCount -gt 0) 'new workspace did not start disconnected'
    }

    Test-UI 'SQL Server provider connects, lists databases and returns rows' {
        Click-Element 'ConnectionToggleButton'
        Wait-ForText 'master' 30
        Assert-True ((Find-Element 'Connected').matchCount -gt 0) 'SQL Server did not report Connected'
        Click-Element 'RunButton'
        Wait-ForText 'tempdb' 30
        Assert-True ((Find-Element 'rows returned').matchCount -gt 0) 'query did not report rows'
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

    Test-UI 'workspace state is saved to disk' {
        Start-Sleep -Milliseconds 900
        Assert-True (Test-Path $StatePath) 'no workspace database was written'

        # Recent writes may still sit in the WAL, and SQLite keeps the files open, so read
        # both pages and the WAL with shared access.
        $state = ''
        foreach ($suffix in '', '-wal') {
            $path = "$StatePath$suffix"
            if (Test-Path $path) {
                $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                try {
                    $state += [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8).ReadToEnd()
                }
                finally {
                    $stream.Dispose()
                }
            }
        }

        Assert-True ($state -match 'Customer totals') 'seeded tabs are missing from the database'
        Assert-True ($state -match 'Query 1') 'the new tab is missing from the database'
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

    # The state written during this run is test data: stop the app, drop it, restore the tester's.
    if ($LaunchedHere -and $AppPid -gt 0) {
        Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    Restore-StudioState
}
