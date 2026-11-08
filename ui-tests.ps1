# UI checks for the Larik SQL Studio shell: vertical tabs, spaces, and Tab-accepted snippet completion.
# Run from C:\dev\larik-winui\Larik:  powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1
#
# The checks are ONE ordered scenario, not independent tests: they connect the seeded workspaces,
# create tabs and reuse them later, so a single block cannot be run on its own. While iterating,
# use -Through <part of a test name> to run the ordered prefix that covers the area you touched
# (the names are grouped: rail/workspaces, connection, editor/completion, results, transactions,
# settings). Run the whole suite before committing a behaviour change.
#   -Through 'editing another workspace'   # rail + workspace settings only (~2 min)
#   -Through 'SQL Server provider connects' # everything through the live SQL Server round trip
param(
    [Parameter(Mandatory)][ValidateSet('sandbox', 'local')][string]$Target,
    [int]$AppPid = 0,
    [string]$Through = '',
    [string]$ArtifactDirectory = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $RepoRoot 'ui-test-artifacts' }
$ScopeArgs = @(if ($Target -eq 'sandbox') { '--on'; 'sandbox' })
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$script:MainWindow = $null
$script:ThroughReached = $false

# Workspaces persist to a SQLite database under %LOCALAPPDATA%\Larik WinUI (larik-winui-dev.db in
# Debug builds). A direct/exe run writes there; the packaged app (winapp run or the installed MSIX)
# has AppData redirected by MSIX into the package's LocalCache, so both roots are parked. The WinUI
# build deliberately does not share the Tauri app's store and migrates its own old
# %LOCALAPPDATA%\Larik database on first launch, so that location is parked too. Every run then
# starts from the seeded spaces; Restore-StudioState puts the tester's files back untouched. When
# attaching to a running app (-AppPid) the state belongs to that app, so leave it alone.
$LaunchedHere = $AppPid -le 0
$NewStateNames = @('larik-winui-dev.db', 'larik-winui.db')
$LegacyStateNames = @('larik-dev.db', 'larik.db')
$StateRoots = @(Join-Path $env:LOCALAPPDATA 'Larik WinUI')
$LegacyStateRoots = @(Join-Path $env:LOCALAPPDATA 'Larik')
$Package = $null
$ManifestPath = Join-Path $RepoRoot 'Larik\Package.appxmanifest'
if (Test-Path $ManifestPath) {
    $PackageName = ([xml](Get-Content $ManifestPath -Raw)).Package.Identity.Name
    $Package = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($Package) {
    $CacheLocal = Join-Path $env:LOCALAPPDATA "Packages\$($Package.PackageFamilyName)\LocalCache\Local"
    $StateRoots += Join-Path $CacheLocal 'Larik WinUI'
    $LegacyStateRoots += Join-Path $CacheLocal 'Larik'
}
$StateFiles = @()
foreach ($root in $StateRoots) {
    foreach ($name in $NewStateNames) {
        foreach ($suffix in '', '-wal', '-shm') { $StateFiles += Join-Path $root "$name$suffix" }
    }
    $StateFiles += Join-Path $root 'studio.json'
}
foreach ($root in $LegacyStateRoots) {
    foreach ($name in $LegacyStateNames) {
        foreach ($suffix in '', '-wal', '-shm') { $StateFiles += Join-Path $root "$name$suffix" }
    }
    $StateFiles += Join-Path $root 'studio.json'
}
$StateMoved = @()
if ($LaunchedHere) {
    foreach ($path in $StateFiles) {
        if (Test-Path $path) {
            Move-Item $path "$path.ui-tests" -Force
            $StateMoved += $path
        }
    }
}

function Restore-StudioState {
    if (-not $LaunchedHere) { return }

    foreach ($path in $StateFiles) {
        Remove-Item $path -ErrorAction SilentlyContinue
    }
    foreach ($path in $StateMoved) {
        if (Test-Path "$path.ui-tests") { Move-Item "$path.ui-tests" $path -Force }
    }
}

# Which database the running app actually writes: the packaged app's LocalCache copy, or the
# profile path for an unpackaged run. The package can be registered by the launch itself, so the
# package roots are re-read here rather than only before parking.
function Get-StateDatabasePath {
    $roots = @()
    if ($LaunchedHere) {
        $roots += @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\Larik WinUI' })
    }
    $roots += @($StateRoots)
    foreach ($root in $roots | Select-Object -Unique) {
        foreach ($name in $NewStateNames) {
            $candidate = Join-Path $root $name
            if (Test-Path $candidate) { return $candidate }
        }
    }
    return $null
}

# The app keeps the SQLite files open and recent writes may sit in the WAL; read both with shared access.
function Get-StudioStateText {
    $statePath = Get-StateDatabasePath
    if (-not $statePath) { return '' }
    $state = ''
    foreach ($suffix in '', '-wal') {
        $path = "$statePath$suffix"
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
    return $state
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
    $data = (Invoke-Ui $Arguments) -join "`n" | ConvertFrom-Json
    # Windows PowerShell 5.1 emits a top-level JSON array as a single object; unwrap it so callers
    # can treat the result as a list of windows/matches on every PowerShell version.
    if ($data -is [System.Array]) { return $data }
    return $data
}

function Test-UI {
    param([string]$Name, [scriptblock]$Script)

    # -Through runs an ordered prefix; blocks after the match are reported as skipped, not run.
    if ($script:ThroughReached) {
        $results.Add([pscustomobject]@{ Name = $Name; Status = 'SKIP' })
        return
    }

    try { & $Script | Out-Null; $results.Add([pscustomobject]@{ Name = $Name; Status = 'PASS' }) }
    catch { $results.Add([pscustomobject]@{ Name = $Name; Status = 'FAIL'; Detail = "$_" }) }

    if ($Through -and $Name -like "*$Through*") { $script:ThroughReached = $true }
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
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
}
"@

function Focus-App {
    $windows = @(Get-UiJson @('list-windows', '--json'))
    $window = $windows |
        Where-Object { $_.processId -eq $AppPid -and [double]$_.width -ge 400 -and [double]$_.height -ge 400 } |
        Select-Object -First 1
    if (-not $window) {
        $window = $windows | Where-Object { $_.processId -eq $AppPid } | Select-Object -First 1
    }
    if (-not $window) { return }
    $handle = [IntPtr]([int64](@($window.hwnd) | Select-Object -First 1))
    $script:MainWindow = [string](@($window.hwnd) | Select-Object -First 1)
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
function Click-Element {
    param([string]$Selector)
    Focus-App
    try {
        Invoke-Ui @('click', $Selector) -Attempts 2 | Out-Null
        return
    }
    catch {
        # Text selectors can be ambiguous (a ComboBox item and its Text child, for example).
        # Fall back to the first match's stable slug so the click can still land.
    }

    $matches = @((Find-Element $Selector).matches)
    if ($matches.Count -eq 0) { throw "No element found matching '$Selector'." }
    Invoke-Ui @('click', $matches[0].selector) -Attempts 4 | Out-Null
}
function Right-Click-Element {
    param([string]$Selector)
    Focus-App
    try {
        Invoke-Ui @('click', $Selector, '--right') -Attempts 2 | Out-Null
        return
    }
    catch {
    }

    $matches = @((Find-Element $Selector).matches)
    if ($matches.Count -eq 0) { throw "No element found matching '$Selector'." }
    Invoke-Ui @('click', $matches[0].selector, '--right') -Attempts 4 | Out-Null
}
function Focus-Element { param([string]$Selector) Focus-App; $target = if ($script:MainWindow) { @('-w', $script:MainWindow) } else { @() }; Invoke-Ui (@('focus', $Selector) + $target) | Out-Null }
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
Invoke-Ui @('focus', 'Editor') | Out-Null

try {
    Test-UI 'window shows the studio title' {
        Assert-True ((Invoke-Ui @('list-windows', '--json')) -join '' -match 'Larik SQL Studio') 'main window not found'
    }

    Test-UI 'sidebar lists the active workspace tabs' {
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'workspace tab is not exposed'
    }

    Test-UI 'a tab title renames in place and commits on Enter' {
        Right-Click-Element 'Customer totals'
        Start-Sleep -Milliseconds 500
        Click-Element 'Rename'
        Wait-ForText 'Tab name'
        $editor = @((Find-Element 'Tab name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Assert-True ($null -ne $editor) 'the inline rename editor did not appear'
        Invoke-Ui @('set-value', $editor.selector, 'Customer totals renamed') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'Customer totals renamed').matchCount -gt 0) 'the inline rename did not commit'

        Right-Click-Element 'Customer totals renamed'
        Start-Sleep -Milliseconds 500
        Click-Element 'Rename'
        Wait-ForText 'Tab name'
        $editor = @((Find-Element 'Tab name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Customer totals') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'the tab name was not restored'
    }

    Test-UI 'pinning moves a tab into its own section' {
        Right-Click-Element 'Customer totals'
        Start-Sleep -Milliseconds 500
        Click-Element 'Pin tab'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'Pinned tabs').matchCount -gt 0) 'the pinned section did not appear'

        Right-Click-Element 'Customer totals'
        Start-Sleep -Milliseconds 500
        Click-Element 'Unpin tab'
        Start-Sleep -Milliseconds 600

        Right-Click-Element 'Customer totals'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Pin tab').matchCount -gt 0) 'the tab did not unpin'
        Send-Keys 'escape'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'the workspace switcher spans the rail' {
        $switcher = @((Find-Element 'Switch workspace').matches)[0]
        Assert-True ($null -ne $switcher) 'the workspace switcher was not found'
        Assert-True ($switcher.width -ge 200) "the workspace switcher is only $($switcher.width)px wide"
    }

    Test-UI 'the connect action sits with the workspace it opens' {
        $connect = @((Find-Element 'ConnectionToggleButton').matches)
        Assert-True ($connect.Count -eq 1) "the connect action appears $($connect.Count) times; it belongs only in the rail"
        $newTab = @((Find-Element 'New query tab').matches)[0]
        Assert-True ($null -ne $newTab) 'the tab list was not found'
        Assert-True ($connect[0].y -lt $newTab.y) 'the connect action is below the tab list, not beside the workspace'
    }

    Test-UI 'a workspace colour can be changed from its settings' {
        # A workspace is name + colour + connection, so all three live in one editor, opened from
        # the workspace's own row in the switcher.
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Edit Staging'
        Wait-ForText 'Workspace settings'
        Click-Element 'Colour Pastel Yellow'
        Start-Sleep -Milliseconds 400
        Click-Element 'Save'
        Start-Sleep -Milliseconds 1200
        Assert-True ((Get-StudioStateText) -match '#FDE047') 'the new workspace colour was not saved'
    }

    Test-UI 'a workspace renames from its edit button in the switcher' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Edit Staging'
        Wait-ForText 'Workspace settings'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Assert-True ($null -ne $editor) 'the name field did not appear'
        Invoke-Ui @('set-value', $editor.selector, 'Staging renamed') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Staging renamed').matchCount -gt 0) 'the workspace rename did not apply'
        Assert-True ((Get-StudioStateText) -match 'Staging renamed') 'the workspace rename was not saved'

        # Put the seeded name back so the rest of the suite starts from the same place.
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Edit Staging renamed'
        Wait-ForText 'Workspace settings'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Staging') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Workspace Staging').matchCount -gt 0) 'the workspace name was not restored'
    }

    Test-UI 'editing another workspace leaves the current one alone' {
        # Local SQL Server is not the active workspace here: saving it must not switch, connect,
        # or touch the editor.
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Edit Local SQL Server'
        Wait-ForText 'Workspace settings'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Local renamed') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 1000
        Assert-True ((Find-Element 'Local renamed').matchCount -gt 0) 'the other workspace was not renamed'
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'editing another workspace changed the active one'

        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Edit Local renamed'
        Wait-ForText 'Workspace settings'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Local SQL Server') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 1000
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'the active workspace changed while restoring names'
    }

    Test-UI 'the workspace indicator shows every workspace and switches' {
        Assert-True ((Find-Element 'Workspace Staging').matchCount -gt 0) 'indicator does not show the active workspace'
        Assert-True ((Find-Element 'Workspace Local SQL Server').matchCount -gt 0) 'indicator does not show every workspace'
        Click-Element 'Workspace Local SQL Server'
        Start-Sleep -Milliseconds 800
        Assert-True ((Get-EditorText) -match 'sys.databases') 'the indicator did not switch workspaces'
        Click-Element 'Workspace Staging'
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'the indicator did not switch back'
    }

    Test-UI 'a horizontal trackpad swipe over the rail switches workspaces' {
        Focus-App
        $rail = @((Find-Element 'Query tabs').matches)[0]
        Assert-True ($null -ne $rail) 'the rail was not found'
        [LarikWin32]::SetCursorPos([int]($rail.x + ($rail.width / 2)), [int]($rail.y + ($rail.height / 2))) | Out-Null
        Start-Sleep -Milliseconds 250

        # Positive horizontal wheel = swipe left, so the workspace on the right comes in.
        for ($i = 0; $i -lt 3; $i++) {
            [LarikWin32]::mouse_event(0x01000, 0, 0, 120, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 40
        }
        Start-Sleep -Milliseconds 900
        Assert-True ((Get-EditorText) -match 'sys.databases') 'the swipe did not select the next workspace'

        # Swipe back to where we were.
        for ($i = 0; $i -lt 3; $i++) {
            [LarikWin32]::mouse_event(0x01000, 0, 0, -120, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 40
        }
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'the swipe did not select the previous workspace'
    }

    Test-UI 'cancelling a new workspace creates nothing' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'New workspace'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Workspace name').matchCount -gt 0) 'the new workspace dialog did not open'
        Click-Element 'Cancel'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'Workspace Space 4').matchCount -eq 0) 'cancelling still created a workspace'
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'the active workspace changed after cancelling'
    }

    Test-UI 'completing the new workspace dialog creates and connects it' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'New workspace'
        Start-Sleep -Milliseconds 900
        Click-Element 'Create & connect'
        Start-Sleep -Milliseconds 1500
        Assert-True ((Find-Element 'Workspace Space 4').matchCount -gt 0) 'the workspace was not created'
        Assert-True ((Find-Element 'Query 1').matchCount -gt 0) 'the new workspace is not the active one'
        Click-Element 'Workspace Staging'
        Start-Sleep -Milliseconds 700
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

        $deadline = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $deadline -and (Find-Element 'SQL suggestions').matchCount -gt 0) {
            Start-Sleep -Milliseconds 200
        }
        Assert-True ((Find-Element 'SQL suggestions').matchCount -eq 0) 'popup stayed open after accepting'
    }

    Test-UI 'Ctrl+Enter runs the query through the provider' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'customers;' -Literal
        Start-Sleep -Milliseconds 400
        Send-Keys 'ctrl+enter'
        Wait-ForText 'FULL NAME'
        Assert-True ((Find-Element '24 rows returned').matchCount -gt 0) 'status bar did not report the row count'
    }

    Test-UI 'Ctrl+Enter runs only the statement under the caret' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from customers; select * from orders;' -Literal
        Start-Sleep -Milliseconds 300
        Send-Keys 'ctrl+home'
        Send-Keys 'ctrl+enter'
        Wait-ForText 'FULL NAME' 20
        Assert-True ((Find-Element 'TOTAL AMOUNT').matchCount -eq 0) 'Ctrl+Enter ran the whole batch'

        # The statement is selected; F5 must respect that selection and not run the batch.
        Send-Keys 'f5'
        Wait-ForText 'FULL NAME' 20
        Assert-True ((Find-Element 'TOTAL AMOUNT').matchCount -eq 0) 'F5 ignored the selected statement'
        Assert-True ((Find-Element 'Result 2').matchCount -eq 0) 'F5 with a selection opened a second result'

        # Collapse the selection, then F5 runs the whole editor.
        Send-Keys 'right'
        Send-Keys 'f5'
        Wait-ForText 'Result 2' 20
        Click-Element 'Result 2'
        Wait-ForText 'TOTAL AMOUNT' 20
    }

    Test-UI 'SQL tokens are syntax highlighted' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select 1 -- note' -Literal
        Start-Sleep -Milliseconds 800
        $editor = Get-UiJson @('get-property', 'SQL editor', '--json')
        Assert-True ($editor.properties.ForegroundColor -eq 'Mixed') "syntax highlighting was not applied (ForegroundColor=$($editor.properties.ForegroundColor))"
    }

    Test-UI 'undo still reverses typing under syntax highlighting' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select 42' -Literal
        Start-Sleep -Milliseconds 500
        Send-Keys 'ctrl+z'
        Start-Sleep -Milliseconds 500
        Assert-True ((Get-EditorText) -ne 'select 42') 'Ctrl+Z undid highlighting instead of typing'

        Send-Keys 'ctrl+y'
        Start-Sleep -Milliseconds 500
        Assert-True ((Get-EditorText) -eq 'select 42') 'Ctrl+Y did not redo the typing'
    }

    Test-UI 'Ctrl+F counts and cycles matches' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select customer_id from customers; select customer_id from orders;' -Literal
        Start-Sleep -Milliseconds 400

        Send-Keys 'ctrl+f'
        Wait-ForText 'Find text'
        Send-Keys 'customer_id' -Literal
        Wait-ForText '1 of 2'

        Send-Keys 'enter'
        Wait-ForText '2 of 2'
        Send-Keys 'shift+enter'
        Wait-ForText '1 of 2'

        Send-Keys 'escape'
        Start-Sleep -Milliseconds 400
        Assert-True ((Find-Element 'Find text').matchCount -eq 0) 'Escape left the find bar open'
        Assert-True ((Get-EditorText) -eq 'select customer_id from customers; select customer_id from orders;') 'finding changed the document'
    }

    Test-UI 'replace all rewrites every match in one undoable step' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select customer_id from customers; select customer_id from orders;' -Literal
        Start-Sleep -Milliseconds 400

        Send-Keys 'ctrl+h'
        Wait-ForText 'Replace text'
        Send-Keys 'customer_id' -Literal
        Wait-ForText '1 of 2'

        Focus-Element 'Replace text'
        Send-Keys 'id' -Literal
        Click-Element 'Replace all matches'
        Wait-ForText 'Replaced 2'
        Assert-True ((Get-EditorText) -eq 'select id from customers; select id from orders;') "replace all produced '$((Get-EditorText))'"

        # One find/replace edit must be one undo step, like typing.
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+z'
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText) -eq 'select customer_id from customers; select customer_id from orders;') 'Ctrl+Z did not undo the whole replace-all'
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

    Test-UI 'workspace settings render the provider schema' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'Edit Staging'
        Start-Sleep -Milliseconds 1000
        try {
            Assert-True ((Find-Element 'Workspace settings').matchCount -gt 0) 'the workspace editor did not open'
            Assert-True ((Find-Element 'Provider').matchCount -gt 0) 'provider picker missing'
            Assert-True ((Find-Element 'Databases').matchCount -gt 0) 'provider settings not rendered'
            # Opening the picker proves the catalog listed every loaded provider.
            Click-Element 'Provider'
            Start-Sleep -Milliseconds 600
            Assert-True ((Find-Element 'SQL Server').matchCount -gt 0) 'providers not listed'
            # The SQL Server form is the tall one: Test must keep its result on screen, not
            # just in the automation tree below the visible part of the dialog. Providers sort
            # by name (Demo first, SQL Server second), so Down + Enter is unambiguous.
            Send-Keys 'down'
            Send-Keys 'enter'
            Start-Sleep -Milliseconds 800
            Assert-True ((Find-Element 'Advanced').matchCount -gt 0) 'the SQL Server provider was not selected'
            Assert-True ((Find-Element 'Connect timeout (s)').matchCount -eq 0) 'advanced fields are not collapsed'
            Click-Element 'Advanced'
            Start-Sleep -Milliseconds 600
            Assert-True ((Find-Element 'Connect timeout (s)').matchCount -gt 0) 'advanced fields did not expand'
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
        # The rail is gone, so the toolbar badge must still reach the workspace and its connection.
        Click-Element 'Workspace and connection'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'WORKSPACES').matchCount -gt 0) 'the collapsed rail lost access to workspaces'
        Send-Keys 'escape'
        Start-Sleep -Milliseconds 400
        Send-Keys 'ctrl+b'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'DatabaseSelectorButton').matchCount -gt 0) 'sidebar did not come back'
    }

    Test-UI 'New query asks for a database before creating the tab' {
        Click-Element 'New query tab'
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element 'New query database').matchCount -gt 0) 'the database palette did not open'
        Focus-Element 'New tab database search'
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Query 1').matchCount -gt 0) 'new tab did not appear'
    }

    Test-UI 'typing in the new-tab palette filters the database and Enter opens it' {
        Click-Element 'New query tab'
        Start-Sleep -Milliseconds 800
        Focus-Element 'New tab database search'
        Send-Keys 'tempdb' -Literal
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'tempdb').matchCount -gt 0) 'the palette did not filter to tempdb'
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Query 2').matchCount -gt 0) 'new tab did not appear'
        Assert-True ((Find-Element 'tempdb').matchCount -gt 0) 'the database selector did not follow the tab database'
    }

    Test-UI 'each tab keeps its own result grid' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select name as database_name from sys.databases;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'database_name' 20

        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'database_name').matchCount -eq 0) 'the previous tab result stayed in the grid'
        Assert-True ((Find-Element 'tempdb').matchCount -eq 0) 'the database selector did not switch back with the tab'

        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'database_name').matchCount -gt 0) 'the tab result was not restored'
        Assert-True ((Find-Element 'tempdb').matchCount -gt 0) 'the database selector did not follow the tab'
    }

    Test-UI 'a large result switches tabs quickly' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys "select top 1000 'row_' + cast(row_number() over (order by (select null)) as varchar(10)) as label from sys.all_objects a cross join sys.all_objects b;" -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'row_1' 30
        Assert-True ((Find-Element '1000 rows').matchCount -gt 0) 'the large query did not report its row count'

        $start = Get-Date
        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 300
        Click-Element 'Query 2'
        Wait-ForText 'row_1' 10
        $elapsed = ((Get-Date) - $start).TotalSeconds
        Assert-True ($elapsed -lt 8) "switching back to the large result took $($elapsed.ToString('0.0'))s"
    }

    Test-UI 'the provider caps a result at 5000 rows' {
        Focus-Element 'Editor'
        Send-Keys 'ctrl+a'
        Send-Keys 'select top 6000 a.name from sys.all_objects a cross join sys.all_objects b;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'Showing first 5,000 rows' 30
        Assert-True ((Find-Element '5,000 rows').matchCount -gt 0) 'the truncation badge is missing'
    }

    Test-UI 'wide results scroll horizontally under the sticky header' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from sys.databases;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'database_id' 20

        # page_verify_option sits far enough right to be clipped initially; scrolling must bring
        # its sticky header into view. If the header stayed put, it would remain off-screen.
        $visibleBefore = @((Find-Element 'page_verify_option').matches) | Where-Object { $_.x -gt 100 }
        Assert-True ($visibleBefore.Count -eq 0) 'the far column was already visible before scrolling'

        Invoke-Ui @('scroll', 'Query results', '--direction', 'right') | Out-Null
        Start-Sleep -Milliseconds 500

        $revealed = @((Find-Element 'page_verify_option').matches) | Where-Object { $_.x -gt 100 } | Select-Object -First 1
        Assert-True ($null -ne $revealed) 'horizontal scrolling did not reveal later columns under the sticky header'

        # Focus the grid, then Ctrl+Home returns the active cell (and horizontal offset) to the
        # first column for the selection tests.
        Focus-Element 'Results grid'
        Send-Keys 'ctrl+home'
        Start-Sleep -Milliseconds 500
    }

    Test-UI 'grid cells select and copy like a spreadsheet' {
        # A sentinel, not an empty string: Windows PowerShell 5.1's Set-Clipboard rejects ''.
        Set-Clipboard -Value 'larik-test-clipboard'
        $cell = @((Find-Element 'master').matches) | Where-Object { $_.width -gt 0 -and $_.height -gt 0 } | Select-Object -First 1
        Assert-True ($null -ne $cell) 'the result cell was not found'
        Focus-App
        Invoke-Ui @('click', $cell.selector) -Attempts 4 | Out-Null
        Send-Keys 'ctrl+c'
        Start-Sleep -Milliseconds 300
        Assert-True ((Get-Clipboard -Raw) -eq 'master') "a single cell copied '$((Get-Clipboard -Raw))' instead of 'master'"

        Send-Keys 'shift+right'
        Send-Keys 'shift+down'
        Send-Keys 'ctrl+c'
        Start-Sleep -Milliseconds 300
        $block = Get-Clipboard -Raw
        Assert-True ($block -match "master`t1") "the selected range is missing 'master<TAB>1': $block"
        Assert-True ($block -match "tempdb`t2") "the selected range is missing 'tempdb<TAB>2': $block"
    }

    Test-UI 'Ctrl+Space selects a column and copy carries its header' {
        # A sentinel, not an empty string: Windows PowerShell 5.1's Set-Clipboard rejects ''.
        Set-Clipboard -Value 'larik-test-clipboard'
        Focus-Element 'Results grid'
        Send-Keys 'ctrl+home'
        Send-Keys 'ctrl+space'
        Send-Keys 'ctrl+shift+c'
        Start-Sleep -Milliseconds 300
        $withHeaders = Get-Clipboard -Raw
        Assert-True ($withHeaders -match '^name') "the header row is missing: $withHeaders"
        Assert-True ($withHeaders -match 'master') "the column values are missing: $withHeaders"
    }

    Test-UI 'a column width can be dragged from the header' {
        $handle = @((Find-Element 'Resize name column').matches) | Select-Object -First 1
        Assert-True ($null -ne $handle) 'the column resize handle was not found'
        $next = @((Find-Element 'database_id').matches) | Select-Object -First 1
        Assert-True ($null -ne $next) 'the next column header was not found'

        $y = [int]($handle.y + ($handle.height / 2))
        $from = "$([int]($handle.x + 2)),$y"
        $to = "$([int]($handle.x + 82)),$y"
        Invoke-Ui @('drag', $from, $to) | Out-Null
        Start-Sleep -Milliseconds 500

        $moved = @((Find-Element 'database_id').matches) | Select-Object -First 1
        Assert-True ($null -ne $moved) 'the next column disappeared after resizing'
        Assert-True ($moved.x -gt ($next.x + 50)) "the column did not widen (before=$($next.x) after=$($moved.x))"
    }

    Test-UI 'a batch opens one result tab per statement' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys "select name from sys.databases where database_id = 3; select name from sys.databases where database_id = 4;" -Literal
        Send-Keys 'f5'
        Wait-ForText 'Result 1' 20
        Assert-True ((Find-Element 'Result 2').matchCount -gt 0) 'the second result tab is missing'
        Wait-ForText 'model' 10

        Click-Element 'Result 2'
        Start-Sleep -Milliseconds 500
        Wait-ForText 'msdb' 10
    }

    Test-UI 'Ctrl+backslash appends a new result tab' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select name from sys.databases where database_id = 1;' -Literal
        Send-Keys 'ctrl+\'
        Wait-ForText 'master' 20
        Assert-True ((Find-Element 'Result 3').matchCount -gt 0) 'the appended result tab is missing'
    }

    Test-UI 'Ctrl+Enter replaces the active result tab' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select name from sys.databases where database_id = 4;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'msdb' 20
        Assert-True ((Find-Element 'Result 3').matchCount -gt 0) 'the active result tab disappeared'
        Assert-True ((Find-Element 'Result 4').matchCount -eq 0) 'Ctrl+Enter appended instead of replacing'
    }

    Test-UI 'a result tab can be closed' {
        Click-Element 'Close Result 3'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'Result 3').matchCount -eq 0) 'the result tab was not closed'
        Assert-True ((Find-Element 'Result 2').matchCount -gt 0) 'the remaining result tabs disappeared'
    }

    Test-UI 'each result remembers its scroll position' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys "select top 1000 'row_' + cast(row_number() over (order by (select null)) as varchar(10)) as label from sys.all_objects a cross join sys.all_objects b;" -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'row_1' 30

        $before = @((Find-Element 'row_1').matches) | Select-Object -First 1
        Assert-True ($null -ne $before) 'the first row was not visible before scrolling'

        Focus-Element 'Results grid'
        for ($i = 0; $i -lt 5; $i++) {
            Send-Keys 'pagedown'
        }
        Start-Sleep -Milliseconds 600
        $after = @((Find-Element 'row_1').matches) | Select-Object -First 1
        Assert-True ($null -eq $after -or $after.y -lt ($before.y - 30)) "scrolling did not move the grid (before=$($before.y) after=$($after.y))"

        Click-Element 'Result 1'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'model').matchCount -gt 0) 'the first result was not restored'

        Click-Element 'Result 2'
        Start-Sleep -Milliseconds 600
        $restored = @((Find-Element 'row_1').matches) | Select-Object -First 1
        if ($null -eq $after) {
            Assert-True ($null -eq $restored) "the second result lost its scroll position (row_1 y=$($restored.y))"
        }
        else {
            Assert-True ($null -ne $restored -and [Math]::Abs($restored.y - $after.y) -lt 30) "the second result lost its scroll position (before=$($after.y) after=$($restored.y))"
        }
        Assert-True ((Find-Element 'label').matchCount -gt 0) 'the restored grid is empty'

        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 600
        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 600
        $restoredTab = @((Find-Element 'row_1').matches) | Select-Object -First 1
        if ($null -eq $after) {
            Assert-True ($null -eq $restoredTab) "switching query tabs lost the result position (row_1 y=$($restoredTab.y))"
        }
        else {
            Assert-True ($null -ne $restoredTab -and [Math]::Abs($restoredTab.y - $after.y) -lt 30) "switching query tabs lost the result position (before=$($after.y) after=$($restoredTab.y))"
        }
    }

    Test-UI 'result tabs can be reordered by dragging' {
        $first = @((Find-Element 'Result 1').matches) | Where-Object { $_.width -gt 0 } | Select-Object -First 1
        $second = @((Find-Element 'Result 2').matches) | Where-Object { $_.width -gt 0 } | Select-Object -First 1
        Assert-True ($null -ne $first -and $null -ne $second) 'the result tabs were not found'
        Assert-True ($second.x -gt $first.x) 'the result tabs did not start in order'

        $from = "$([int]($first.x + ($first.width / 2))),$([int]($first.y + ($first.height / 2)))"
        $to = "$([int]($second.x + ($second.width / 2))),$([int]($second.y + ($second.height / 2)))"
        Invoke-Ui @('drag', $from, $to, '--hold-ms', '250', '--dwell-ms', '300') | Out-Null
        Start-Sleep -Milliseconds 700

        # Dragging the model result (old Result 1) to the right makes the label result first;
        # titles reindex, so the old second grid must now answer to Result 2.
        Click-Element 'Result 2'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'model').matchCount -gt 0) 'the first result did not move to the right'
        Assert-True ((Find-Element 'row_1').matchCount -eq 0) 'the second result stayed in place'

        # The query tab owns its results; switching away and back must keep the new order.
        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 500
        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 500
        Click-Element 'Result 2'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'model').matchCount -gt 0) 'the reordered results reverted on tab switch'
    }

    Test-UI 'new tabs open on top and query tabs can be reordered by dragging' {
        $first = @((Find-Element 'Query 1').matches) | Where-Object { $_.x -lt 300 -and $_.width -gt 0 } | Select-Object -First 1
        $second = @((Find-Element 'Query 2').matches) | Where-Object { $_.x -lt 300 -and $_.width -gt 0 } | Select-Object -First 1
        Assert-True ($null -ne $first -and $null -ne $second) 'the query tabs were not found'
        Assert-True ($second.y -lt $first.y) 'a new tab did not open above the older one'

        $from = "$([int]($second.x + ($second.width / 2))),$([int]($second.y + ($second.height / 2)))"
        $to = "$([int]($first.x + ($first.width / 2))),$([int]($first.y + ($first.height / 2)))"
        Invoke-Ui @('drag', $from, $to, '--hold-ms', '250', '--dwell-ms', '300') | Out-Null
        Start-Sleep -Milliseconds 700

        $movedFirst = @((Find-Element 'Query 1').matches) | Where-Object { $_.x -lt 300 -and $_.width -gt 0 } | Select-Object -First 1
        $movedSecond = @((Find-Element 'Query 2').matches) | Where-Object { $_.x -lt 300 -and $_.width -gt 0 } | Select-Object -First 1
        Assert-True ($null -ne $movedFirst -and $null -ne $movedSecond) 'the query tabs disappeared after dragging'
        Assert-True ($movedSecond.y -gt $movedFirst.y) "Query 2 did not move below Query 1 (Query 1 y=$($movedFirst.y), Query 2 y=$($movedSecond.y))"

        # The space owns the tab order; switching workspaces and back must preserve it.
        Click-Element 'Workspace Staging'
        Start-Sleep -Milliseconds 700
        Click-Element 'Workspace Local SQL Server'
        Start-Sleep -Milliseconds 700
        $persistedFirst = @((Find-Element 'Query 1').matches) | Where-Object { $_.x -lt 300 -and $_.width -gt 0 } | Select-Object -First 1
        $persistedSecond = @((Find-Element 'Query 2').matches) | Where-Object { $_.x -lt 300 -and $_.width -gt 0 } | Select-Object -First 1
        Assert-True ($null -ne $persistedFirst -and $null -ne $persistedSecond) 'the query tabs disappeared after switching workspaces'
        Assert-True ($persistedFirst.y -lt $persistedSecond.y) 'the dragged tab order was not saved to the workspace'
    }

    Test-UI 'transactions belong to the query tab that opened them' {
        # Query 2 is active. Open a transaction and check the guard.
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'begin tran; select @@trancount as tran_count;' -Literal
        Send-Keys 'ctrl+a'
        Send-Keys 'ctrl+enter'

        # Wait for the guard, not for the alias: the alias also exists in the editor text.
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline -and (Find-Element '1 open transaction').matchCount -eq 0) {
            Start-Sleep -Milliseconds 250
        }
        Assert-True ((Find-Element '1 open transaction').matchCount -gt 0) 'the tab did not show its open transaction'
        Assert-True ((Find-Element 'Commit transaction').matchCount -gt 0) 'no commit action'
        Assert-True ((Find-Element 'Roll back transaction').matchCount -gt 0) 'no rollback action'

        # Query 1 has its own session, so it must not see Query 2's transaction.
        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 800
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select 12345 * (1 + @@trancount) as session_state;' -Literal
        Send-Keys 'ctrl+a'
        Send-Keys 'ctrl+enter'
        Wait-ForText '1 rows returned' 20
        # Clean is 12345; a leaked transaction would make it 24690. The value is computed,
        # so it cannot be satisfied by the editor text itself.
        Assert-True ((Find-Element '24690').matchCount -eq 0) 'the transaction leaked into another tab'

        # Switching back must restore the tab's own guard.
        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element '1 open transaction').matchCount -gt 0) 'the transaction indicator was lost when switching back'

        # Disconnecting is refused while that transaction is open.
        Click-Element 'ConnectionToggleButton'
        Wait-ForText 'Roll back the open transaction' 10
        Assert-True ((Find-Element 'Connected').matchCount -gt 0) 'the workspace disconnected with an open transaction'

        Click-Element 'Roll back transaction'
        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline -and (Find-Element '1 open transaction').matchCount -gt 0) {
            Start-Sleep -Milliseconds 200
        }
        Assert-True ((Find-Element '1 open transaction').matchCount -eq 0) 'rollback did not clear the transaction indicator'
    }

    Test-UI 'F5 splits GO batches instead of sending GO to the server' {
        # A fresh tab starts with no result tabs, so the two GO batches are Result 1 and 2.
        # Pin it to master first: the test then proves USE carries across the GO boundary.
        Click-Element 'New query tab'
        Start-Sleep -Milliseconds 800
        Focus-Element 'New tab database search'
        Send-Keys 'master' -Literal
        Start-Sleep -Milliseconds 500
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900

        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'use tempdb;' -Literal
        Send-Keys 'enter'
        Send-Keys 'GO' -Literal
        Send-Keys 'enter'
        Send-Keys "select db_name() + '-suffix' as db_name;" -Literal
        Send-Keys 'f5'
        Wait-ForText 'Result 2' 20
        Assert-True ((Find-Element 'Incorrect syntax').matchCount -eq 0) 'GO reached SQL Server'
        Click-Element 'Result 2'
        Wait-ForText 'tempdb-suffix' 20
    }

    Test-UI 'a running query can be stopped from the toolbar' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys "waitfor delay '00:00:20'; select 1 as stopped;" -Literal
        Send-Keys 'ctrl+a'
        $start = Get-Date
        Send-Keys 'ctrl+enter'

        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and (Find-Element 'Stop query').matchCount -eq 0) {
            Start-Sleep -Milliseconds 200
        }
        Assert-True ((Find-Element 'Stop query').matchCount -gt 0) 'the Stop button did not appear'
        $run = @((Find-Element 'Run query').matches) | Where-Object { $_.automationId -eq 'RunButton' } | Select-Object -First 1
        Assert-True ($null -ne $run -and -not $run.isEnabled) 'Run stayed enabled while the query was running'

        Click-Element 'Stop query'
        Wait-ForText 'Cancelled' 20
        Assert-True (((Get-Date) - $start).TotalSeconds -lt 15) 'the running query was not cancelled promptly'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'Stop query').matchCount -eq 0) 'the Stop button stayed visible after the query ended'
        $run = @((Find-Element 'Run query').matches) | Where-Object { $_.automationId -eq 'RunButton' } | Select-Object -First 1
        Assert-True ($null -ne $run -and $run.isEnabled) 'Run stayed disabled after the query ended'
    }

    Test-UI 'workspace state is saved to disk' {
        Start-Sleep -Milliseconds 900
        Assert-True ($null -ne (Get-StateDatabasePath)) 'no workspace database was written'

        $state = Get-StudioStateText
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

    # Last: the settings page replaces the shell in the same frame, so nothing after it is checked.
    Test-UI 'settings lives at the bottom of the rail, not in the query toolbar' {
        $gear = @((Find-Element 'SettingsButton').matches)[0]
        Assert-True ($null -ne $gear) 'no settings action in the rail'
        $newTab = @((Find-Element 'New query tab').matches)[0]
        Assert-True ($gear.y -gt $newTab.y) 'settings is not in the rail footer'

        Click-Element 'Settings'
        Wait-ForText 'Snippets' 10

        # The About page names this build's own store, which is what keeps the WinUI app and the
        # Tauri app installable side by side: neither opens (or migrates) the other's database.
        Click-Element 'About'
        Wait-ForText 'larik-winui' 10

        Click-Element 'Back'
        Wait-ForText 'Switch workspace' 10
    }

    $shot = Join-Path $ArtifactDirectory 'larik-shell.png'
    Invoke-Ui @('screenshot', '--output', $shot) | Out-Null
    $screenshots.Add($shot)
}
finally {
    $ran = @($results | Where-Object Status -ne 'SKIP').Count
    $passed = @($results | Where-Object Status -eq 'PASS').Count
    $failed = @($results | Where-Object Status -eq 'FAIL').Count
    if ($Through) {
        Write-Host "Ran $ran of $($results.Count) checks through '$Through' (-Through): $passed passed, $failed failed."
    }

    $results | Format-Table -AutoSize | Out-String | Write-Host
    if ($failed -gt 0) {
        $results | Where-Object Status -eq 'FAIL' | ForEach-Object { Write-Host ("FAIL: " + $_.Name + " :: " + $_.Detail) }
    }
    $screenshots | ForEach-Object { Write-Host "screenshot: $_" }

    # The state written during this run is test data: stop the app, drop it, restore the tester's.
    if ($LaunchedHere -and $AppPid -gt 0) {
        Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    Restore-StudioState
}

# Explicit exit codes so a targeted run can be used in a script: 0 green, 1 failed, 2 -Through typo.
if ($Through -and -not $script:ThroughReached) {
    Write-Host "No check name matched -Through '$Through'; nothing was verified."
    exit 2
}

exit @($results | Where-Object Status -eq 'FAIL').Count
