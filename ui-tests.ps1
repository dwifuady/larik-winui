# UI checks for the Larik SQL Studio shell: vertical tabs, spaces, and Tab-accepted snippet completion.
# Run from C:\dev\larik-winui\Larik:  powershell -ExecutionPolicy Bypass -File ..\ui-tests.ps1
#
# The checks are ONE ordered scenario, not independent tests. They start on the empty onboarding
# surface, create the workspaces they reuse, then connect them, so a single block cannot be run on
# its own. While iterating, use -Through <part of a test name> to run the ordered prefix that covers
# the area you touched (the names are grouped: onboarding/rail, connection, editor/completion,
# results, transactions, settings). Run the whole suite before committing a behaviour change.
#   -Through 'editing another workspace'   # rail + workspace settings only (~2 min)
#   -Through 'SQL Server provider connects' # everything through the live SQL Server round trip
#
# Every run writes last-run.json (status, seconds, commit, fingerprint) and verified.json (the best
# known PASS per check for that exact tree) to ui-test-artifacts. The prefix saves a rail checkpoint
# at the end of the drag-reorder check; -From '<check>' resumes there, marks the skipped prefix
# CACHED (same fingerprint) or SKIP, and reconnects the workspace. Use it to re-check the tab/group
# area without replaying onboarding and the editor checks.
param(
    [Parameter(Mandatory)][ValidateSet('sandbox', 'local')][string]$Target,
    [int]$AppPid = 0,
    [string]$Through = '',
    [string]$ArtifactDirectory = '',
    # Resume from a saved state checkpoint instead of replaying the scenario from onboarding.
    [string]$From = ''
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
$script:RunStartedAt = Get-Date
$script:Commit = (& git -C $RepoRoot rev-parse --short HEAD 2>$null) -join ''

# The fingerprint answers "did the code change since the last run?". It covers the commit, the
# working-tree diff and the content of untracked files, so a results file can be trusted for a
# tree it was produced from (artifacts are excluded; they are output, not code).
function Get-CodeFingerprint {
    $parts = [Collections.Generic.List[string]]::new()
    $parts.Add('head:' + ((& git -C $RepoRoot rev-parse HEAD 2>$null) -join ''))
    $parts.Add('diff:' + (((& git -C $RepoRoot diff --binary HEAD 2>$null) -join "`n")))
    foreach ($file in @(& git -C $RepoRoot ls-files --others --exclude-standard 2>$null)) {
        $path = Join-Path $RepoRoot $file
        if (Test-Path $path -PathType Leaf) {
            $parts.Add("untracked:${file}:" + (Get-FileHash $path -Algorithm SHA256).Hash)
        }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($parts -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose() }
}

function Save-TestResults {
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    $report = [ordered]@{
        schemaVersion = 1
        fingerprint = $script:Fingerprint
        commit = $script:Commit
        target = $Target
        through = $Through
        from = $From
        startedAt = $script:RunStartedAt.ToString('o')
        updatedAt = (Get-Date).ToString('o')
        checks = @($results | ForEach-Object {
            [ordered]@{
                name = $_.Name
                status = $_.Status
                detail = $_.Detail
                seconds = $_.Seconds
            }
        })
    }
    $path = Join-Path $ArtifactDirectory 'last-run.json'
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $path -Encoding UTF8

    # Keep the best-known PASS per check for exactly this tree. A later flaky run must not erase a
    # green result, otherwise the cache punishes the tester for the harness being temperamental.
    $baselinePath = Join-Path $ArtifactDirectory 'verified.json'
    $known = @{}
    if (Test-Path $baselinePath) {
        try {
            $previous = Get-Content $baselinePath -Raw | ConvertFrom-Json
            if ($previous.fingerprint -eq $script:Fingerprint) {
                foreach ($check in $previous.checks) {
                    if ($check.status -eq 'PASS') { $known[$check.name] = $check.verifiedAt }
                }
            }
        }
        catch { }
    }

    foreach ($check in $results) {
        if ($check.Status -eq 'PASS') { $known[$check.Name] = (Get-Date).ToString('o') }
    }

    [ordered]@{
        schemaVersion = 1
        fingerprint = $script:Fingerprint
        commit = $script:Commit
        updatedAt = (Get-Date).ToString('o')
        checks = @($known.GetEnumerator() | ForEach-Object {
            [ordered]@{ name = $_.Key; status = 'PASS'; verifiedAt = $_.Value }
        })
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $baselinePath -Encoding UTF8

    return $path
}

$script:Fingerprint = Get-CodeFingerprint
$script:CheckpointRoot = Join-Path $ArtifactDirectory 'checkpoints'
$script:RailCheckpoint = Join-Path $script:CheckpointRoot 'tab-organization'
$script:FromReached = -not $From

# A resumed run can mark the prefix it skipped as CACHED, but only when the tree is the one that
# produced the previous results. Otherwise the checks are reported SKIP: they were not verified.
function Get-CachedPasses {
    $map = @{}
    $path = Join-Path $ArtifactDirectory 'verified.json'
    if (-not (Test-Path $path)) { return $map }
    try { $previous = Get-Content $path -Raw | ConvertFrom-Json } catch { return $map }
    if ($previous.fingerprint -ne $script:Fingerprint) { return $map }
    foreach ($check in $previous.checks) {
        if ($check.status -eq 'PASS') { $map[$check.name] = $true }
    }
    return $map
}

$script:CachedPasses = Get-CachedPasses

# Workspaces persist to a SQLite database under %LOCALAPPDATA%\Larik WinUI (larik-winui-dev.db in
# Debug builds). A direct/exe run writes there; the packaged app (winapp run or the installed MSIX)
# has AppData redirected by MSIX into the package's LocalCache, so both roots are parked. The WinUI
# build deliberately does not share the Tauri app's store and migrates its own old
# %LOCALAPPDATA%\Larik database on first launch, so that location is parked too. Every run then
# starts from the empty onboarding surface; the first checks create the workspaces the rest of the
# scenario reuses. Restore-StudioState puts the tester's files back untouched. When attaching to a
# running app (-AppPid) the state belongs to that app, so leave it alone.
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
    # The packaged build keeps its store in the package's LocalState; older packaged builds wrote
    # under LocalCache, and a packaged debug identity may still have left files there.
    $StateRoots += Join-Path $env:LOCALAPPDATA "Packages\$($Package.PackageFamilyName)\LocalState"
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
$FixtureRoots = @()
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

    foreach ($root in $FixtureRoots) {
        Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    }

    foreach ($path in $StateFiles) {
        Remove-Item $path -ErrorAction SilentlyContinue
    }
    foreach ($path in $StateMoved) {
        if (Test-Path "$path.ui-tests") { Move-Item "$path.ui-tests" $path -Force }
    }
}

# The add-on check needs a real provider assembly outside the app. Build the fixture, then install
# it as a versioned add-on next to the parked state. It is removed again in Restore-StudioState.
if ($LaunchedHere) {
    try {
        $fixtureProject = Join-Path $RepoRoot 'Larik.Data.Tests.Fixture\Larik.Data.Tests.Fixture.csproj'
        $fixtureDll = Join-Path $RepoRoot 'Larik.Data.Tests.Fixture\bin\Debug\net10.0\Larik.Data.Tests.Fixture.dll'

        & dotnet build $fixtureProject -c Debug --nologo -v quiet | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $fixtureDll)) {
            throw 'the add-on fixture did not build'
        }

        $fixtureManifest = '{"manifestVersion":1,"id":"larik.fixture","version":"1.0.0","publisher":"Larik","contract":">=1.0 <2.0","providerIds":["fixture"],"entryAssembly":"Larik.Data.Tests.Fixture.dll","platforms":["win-x64"],"license":"MIT"}'

        foreach ($root in $StateRoots) {
            $addonRoot = Join-Path $root 'Plugins\larik.fixture'
            if (Test-Path $addonRoot) { continue }

            $versionRoot = Join-Path $addonRoot '1.0.0'
            New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null
            Copy-Item $fixtureDll (Join-Path $versionRoot 'Larik.Data.Tests.Fixture.dll') -Force
            [System.IO.File]::WriteAllText(
                (Join-Path $versionRoot 'larik-addon.json'),
                $fixtureManifest,
                [System.Text.UTF8Encoding]::new($false))
            [System.IO.File]::WriteAllText(
                (Join-Path $addonRoot 'current.json'),
                '{"version":"1.0.0"}',
                [System.Text.UTF8Encoding]::new($false))
            $FixtureRoots += $addonRoot
        }
    }
    catch {
        # The tester's database files are already parked; hand them back before failing.
        Restore-StudioState
        throw
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
        $roots += @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'LocalState' })
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

# A checkpoint is the test database at a section boundary. Resuming from it replays only the checks
# after that boundary; the earlier ones are reported CACHED (same tree) or SKIP (not verified).
# VACUUM INTO gives a consistent copy while the app still has the database open. Best effort:
# a machine without sqlite3 still runs the suite, it just cannot resume.
function Save-RailCheckpoint {
    try {
        $db = Get-StateDatabasePath
        if (-not $db) { throw 'the test database was not found' }
        if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
            throw 'sqlite3 is not on PATH'
        }

        New-Item -ItemType Directory -Force -Path $script:RailCheckpoint | Out-Null
        $destination = Join-Path $script:RailCheckpoint 'state.db'
        Remove-Item $destination -ErrorAction SilentlyContinue
        $sqlPath = $destination.Replace('\', '/') -replace "'", "''"
        $output = @(& sqlite3 -cmd '.timeout 5000' $db "VACUUM INTO '$sqlPath';" 2>&1)
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $destination)) {
            throw ($output -join ' ')
        }

        [ordered]@{
            dbRoot = Split-Path -Parent $db
            dbName = Split-Path -Leaf $db
            nextCheck = 'a tab can be grouped, collapsed and joined by a new tab'
            createdAt = (Get-Date).ToString('o')
            fingerprint = $script:Fingerprint
        } | ConvertTo-Json | Set-Content (Join-Path $script:RailCheckpoint 'meta.json') -Encoding UTF8
    }
    catch {
        Write-Host "checkpoint skipped: $($_.Exception.Message)"
    }
}

function Restore-RailCheckpoint {
    $database = Join-Path $script:RailCheckpoint 'state.db'
    $metaPath = Join-Path $script:RailCheckpoint 'meta.json'
    if (-not (Test-Path $database) -or -not (Test-Path $metaPath)) {
        throw "no checkpoint at $($script:RailCheckpoint); run the prefix once with -Through 'new tabs open on top and query tabs can be reordered by dragging' to create it"
    }

    $meta = Get-Content $metaPath -Raw | ConvertFrom-Json
    $root = if ($meta.dbRoot) { $meta.dbRoot } else { $StateRoots[0] }
    $name = if ($meta.dbName) { $meta.dbName } else { $NewStateNames[0] }
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    foreach ($suffix in '', '-wal', '-shm') {
        Remove-Item (Join-Path $root "$name$suffix") -ErrorAction SilentlyContinue
    }
    Copy-Item $database (Join-Path $root $name) -Force
    return $meta
}

function Ensure-ActiveWorkspaceConnected {
    $disconnect = @((Find-Element 'Disconnect').matches) | Where-Object {
        $_.type -eq 'Button' -and $_.name -eq 'Disconnect'
    }
    if ($disconnect.Count -gt 0) { return }

    Click-Element 'ConnectionToggleButton'
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $ready = @((Find-Element 'Disconnect').matches) | Where-Object {
            $_.type -eq 'Button' -and $_.name -eq 'Disconnect'
        }
        if ($ready.Count -gt 0) { return }
        Start-Sleep -Milliseconds 300
    }
    throw 'the resumed workspace did not connect'
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

# list-windows without the app filter: the save picker is a separate process (PickerHost).
function Get-AllWindows {
    $data = (Invoke-WinAppChecked (@('ui', 'list-windows', '--json') + $ScopeArgs)) -join "`n" | ConvertFrom-Json
    if ($data -is [System.Array]) { return $data }
    return $data
}

# The save picker is hosted by PickerHost, a different process, so an -a <app> filter cannot act on
# its controls. Target the dialog HWND explicitly and leave the app filter off.
function Invoke-PickerUi {
    param([string[]]$Arguments, [string]$Hwnd, [int]$Attempts = 1)
    Invoke-WinAppChecked (@('ui') + $Arguments + $ScopeArgs + @('-w', $Hwnd)) -Attempts $Attempts
}

function Get-PickerJson {
    param([string[]]$Arguments, [string]$Hwnd)
    $data = (Invoke-PickerUi $Arguments $Hwnd) -join "`n" | ConvertFrom-Json
    if ($data -is [System.Array]) { return $data }
    return $data
}

function Test-UI {
    param([string]$Name, [scriptblock]$Script)

    # -From resumes from a state checkpoint: the prefix was already verified (CACHED when the tree
    # is unchanged, SKIP otherwise) and its interactions are not replayed.
    if ($From -and -not $script:FromReached) {
        if ($Name -like "*$From*") {
            $script:FromReached = $true
        }
        else {
            $status = if ($script:CachedPasses.ContainsKey($Name)) { 'CACHED' } else { 'SKIP' }
            $results.Add([pscustomobject]@{ Name = $Name; Status = $status; Seconds = 0 })
            Save-TestResults | Out-Null
            return
        }
    }

    # -Through runs an ordered prefix; blocks after the match are reported as skipped, not run.
    if ($script:ThroughReached) {
        $results.Add([pscustomobject]@{ Name = $Name; Status = 'SKIP'; Seconds = 0 })
        Save-TestResults | Out-Null
        return
    }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Script | Out-Null
        $results.Add([pscustomobject]@{
            Name = $Name
            Status = 'PASS'
            Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
        })
    }
    catch {
        $slug = ($Name -replace '[^A-Za-z0-9]+', '-').Trim('-')
        $shot = Join-Path $ArtifactDirectory "fail-$slug.png"
        try {
            Invoke-Ui @('screenshot', '--output', $shot) | Out-Null
            $screenshots.Add($shot)
        }
        catch { }

        $results.Add([pscustomobject]@{
            Name = $Name
            Status = 'FAIL'
            Detail = "$_"
            Seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
        })
    }

    Save-TestResults | Out-Null

    if ($Through -and $Name -like "*$Through*") { $script:ThroughReached = $true }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-EditorText { (Get-UiJson @('get-value', 'Editor', '--json')).text }

# UIA exposes a TextBlock's resolved ForegroundColor, which is how the theme check proves the
# workspace switcher's code-built rows switched tokens with the window (not just the XAML surfaces).
function Get-ElementForegroundArgb {
    param([string]$Selector)
    return [int]((Get-UiJson @('get-property', $Selector, '--json')).properties.ForegroundColor)
}

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
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
"@

function Focus-App {
    $windows = @(Get-UiJson @('list-windows', '--json'))
    # A popped-out tab is a second window of the same process, and it is smaller than the shell.
    # The largest window is the shell, so the helpers keep targeting it after a pop-out opens.
    $window = $windows |
        Where-Object { $_.processId -eq $AppPid -and [double]$_.width -ge 400 -and [double]$_.height -ge 400 } |
        Sort-Object { [double]$_.width * [double]$_.height } -Descending |
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
# A tab window is a second HWND of the same process; -a would keep picking the shell, so the
# pop-out helpers target its handle explicitly (the app filter would hide it from the tree).
function Invoke-PopoutUi {
    param([string[]]$Arguments, [string]$Hwnd)
    Invoke-WinAppChecked (@('ui') + $Arguments + $ScopeArgs + @('-w', $Hwnd))
}
function Get-PopoutJson {
    param([string[]]$Arguments, [string]$Hwnd)
    $data = (Invoke-PopoutUi $Arguments $Hwnd) -join "`n" | ConvertFrom-Json
    if ($data -is [System.Array]) { return $data }
    return $data
}
function Focus-Popout {
    param([string]$Hwnd)
    $handle = [IntPtr]([int64]$Hwnd)
    [LarikWin32]::ShowWindow($handle, 9) | Out-Null
    if ([LarikWin32]::GetForegroundWindow() -ne $handle) {
        [LarikWin32]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        [LarikWin32]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        [LarikWin32]::SetForegroundWindow($handle) | Out-Null
    }
    Start-Sleep -Milliseconds 300
}
function Send-PopoutKeys {
    param([string]$Keys, [string]$Hwnd, [switch]$Literal)
    Focus-Popout $Hwnd
    Invoke-PopoutUi (@('send-keys', $Keys, '--via', 'send-input') + $(if ($Literal) { @('--verbatim') } else { @() })) $Hwnd | Out-Null
}
# The shell's own hwnd (not a pop-out): the largest window of the process.
function Get-PopoutWindow {
    param([int]$Seconds = 8)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $window = @(Get-UiJson @('list-windows', '--json')) |
            Where-Object { $_.processId -eq $AppPid -and $_.label -eq 'window' -and [string]$_.hwnd -ne $script:MainWindow } |
            Select-Object -First 1
        if ($window) { return $window }
        Start-Sleep -Milliseconds 300
    }
    return $null
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
# Switch by invoking the rail's workspace indicator: mouse clicks outside an open flyout can be
# swallowed by its light-dismiss layer, and the indicator is the stable target above six workspaces.
function Switch-Workspace {
    param([string]$Name)
    Send-Keys 'escape'
    Start-Sleep -Milliseconds 250
    $button = @((Find-Element "Workspace $Name").matches | Where-Object { $_.type -eq 'Button' } | Select-Object -First 1)
    if ($null -eq $button) { throw "Workspace '$Name' is not in the rail." }
    Invoke-Ui @('invoke', $button.selector) -Attempts 2 | Out-Null
    Start-Sleep -Milliseconds 600
}
# Workspace editing lives on the Workspaces page now; the switcher only points at it.
function Open-WorkspaceSettings {
    param([string]$Name)
    Send-Keys 'escape'
    Start-Sleep -Milliseconds 250
    Click-Element 'Switch workspace'
    Start-Sleep -Milliseconds 500
    Click-Element 'Manage workspaces'
    Wait-ForText 'Drag a row' 10
    Click-Element "Edit $Name"
    Wait-ForText 'Workspace settings'
}
function Close-WorkspaceManager {
    Click-Element 'Back'
    Wait-ForText 'Switch workspace' 10
}
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
# Buttons inside a dialog body can sit below the scroll fold; the Invoke pattern reaches them
# without depending on screen coordinates.
function Invoke-Element {
    param([string]$Name)
    Focus-App
    $match = @((Find-Element $Name).matches | Where-Object { $_.type -eq 'Button' -and $_.isEnabled } | Select-Object -First 1)
    if ($null -eq $match) { throw "No enabled button matches '$Name'." }
    Invoke-Ui @('invoke', $match.selector) -Attempts 4 | Out-Null
}
# Result tabs with the same title can hold different sessions (a USE message vs PRINT), so select
# them in turn until the expected content is on screen.
function Click-ResultTabWithText {
    param([string]$Title, [string]$Text, [int]$Seconds = 8)
    Focus-App
    $tabs = @((Find-Element $Title).matches) |
        Where-Object { $_.type -eq 'ListItem' -and $_.width -gt 0 -and $_.height -gt 0 } |
        Sort-Object x
    foreach ($tab in $tabs) {
        Invoke-Ui @('click', $tab.selector) -Attempts 4 | Out-Null
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) {
            if ((Find-Element $Text).matchCount -gt 0) { return }
            Start-Sleep -Milliseconds 250
        }
    }
    throw "No '$Title' result tab shows '$Text'."
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
# The diagnostics log sits beside the state database (`logs\larik-*.log`), so the check follows
# whichever root the running build writes. The newest file belongs to this session.
function Get-DiagnosticsLogPath {
    $database = Get-StateDatabasePath
    if (-not $database) { return $null }

    $directory = Join-Path (Split-Path -Parent $database) 'logs'
    if (-not (Test-Path $directory)) { return $null }

    Get-ChildItem $directory -Filter 'larik-*.log' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Wait-ForText {
    param([string]$Text, [int]$Seconds = 8)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Find-Element $Text).matchCount -gt 0) { return }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out waiting for '$Text'"
}

if ($From) {
    $resumeChecks = @(
        'a tab can be grouped',
        'Group by database is one action with undo',
        'a group can be pinned and survives a workspace switch',
        'a tab pops out into its own window and docks back',
        'transactions belong to the query tab that opened them',
        'F5 splits GO batches',
        'a running query can be stopped',
        'workspace state is saved to disk',
        'window size and position are remembered',
        'refuses a mutating statement without WHERE',
        'settings lives at the bottom of the rail',
        'deleting the last workspace returns to onboarding'
    )

    if (-not ($resumeChecks | Where-Object { $From -like "*$_*" })) {
        Restore-StudioState
        throw "-From '$From' is before the tab checkpoint. Resumable checks: $($resumeChecks -join '; ')"
    }

    try {
        $resume = Restore-RailCheckpoint
        Write-Host "Resuming from checkpoint taken $($resume.createdAt); next check: $($resume.nextCheck)"
    }
    catch {
        Restore-StudioState
        throw
    }
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
if (-not $From) {
    try { Invoke-Ui @('wait-for', 'Welcome to Larik SQL Studio') -Attempts 20 | Out-Null } catch { }
}
Focus-App
if ($From) { Ensure-ActiveWorkspaceConnected }

try {
    Test-UI 'window shows the studio title' {
        Assert-True ((Invoke-Ui @('list-windows', '--json')) -join '' -match 'Larik SQL Studio') 'main window not found'
    }

    Test-UI 'a first run starts on the empty onboarding surface' {
        Assert-True ((Find-Element 'Welcome to Larik SQL Studio').matchCount -gt 0) 'the welcome surface is missing'
        Assert-True ((Find-Element 'Create your first workspace').matchCount -gt 0) 'onboarding offers no way to start'
        Assert-True ((Find-Element 'Customer totals').matchCount -eq 0) 'sample workspaces were seeded'
        Assert-True ((Find-Element 'SQL editor').matchCount -eq 0) 'the editor is visible before a workspace exists'
    }

    Test-UI 'onboarding creates the workspaces the rest of the suite reuses' {
        Click-Element 'Create your first workspace'
        Wait-ForText 'Create & connect'

        $name = @((Find-Element 'Workspace name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Assert-True ($null -ne $name) 'the workspace name field is missing'
        Invoke-Ui @('set-value', $name.selector, 'Staging') | Out-Null

        # The Demo provider is the offline fixture: no server is needed for the editor checks.
        $provider = @((Find-Element 'Database').matches | Where-Object { $_.type -eq 'ComboBox' } | Select-Object -First 1)
        Assert-True ($null -ne $provider) 'the provider picker is missing'
        Invoke-Ui @('click', $provider.selector) | Out-Null
        Start-Sleep -Milliseconds 400

        # Installed add-ons are discovered before the dialog is built, so the fixture must appear
        # beside the built-ins. Skipped when attaching to an app the harness did not prepare.
        if ($LaunchedHere) {
            $fixture = @((Find-Element 'Fixture (add-on)').matches | Where-Object { $_.type -eq 'ListItem' } | Select-Object -First 1)
            Assert-True ($null -ne $fixture) 'the add-on provider was not listed'
        }

        $demo = @((Find-Element 'Demo (in-memory)').matches | Where-Object { $_.type -eq 'ListItem' } | Select-Object -First 1)
        Assert-True ($null -ne $demo) 'the Demo provider was not offered'
        Invoke-Ui @('click', $demo.selector) | Out-Null
        Start-Sleep -Milliseconds 500

        $databases = @((Find-Element 'Databases').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Assert-True ($null -ne $databases) 'the Demo databases field is missing'
        Invoke-Ui @('set-value', $databases.selector, 'larik_staging,larik_sandbox,!larik_archive') | Out-Null
        Start-Sleep -Milliseconds 300
        $demoNames = ((Invoke-Ui @('get-value', $databases.selector, '--json')) -join "`n" | ConvertFrom-Json).text
        Assert-True ($demoNames -match 'larik_staging') "the Demo databases field kept '$demoNames'"

        Click-Element 'Create & connect'
        Wait-ForText 'Connected' 15

        # Keep the first tab on larik_staging: the suite's rest-of-scenario fixture depends on it.
        Click-Element 'Select database'
        Start-Sleep -Milliseconds 500
        Click-Element 'larik_staging'
        Start-Sleep -Milliseconds 500
        Wait-ForText 'larik_staging' 10

        Right-Click-Element 'Query 1'
        Start-Sleep -Milliseconds 500
        Click-Element 'Rename'
        Wait-ForText 'Tab name'
        $tab = @((Find-Element 'Tab name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Invoke-Ui @('set-value', $tab.selector, 'Customer totals') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 700

        # Start disconnected, like every workspace: connecting is the rail's explicit action.
        Click-Element 'ConnectionToggleButton'
        Wait-ForText 'Disconnected' 15

        # The second workspace exercises the SQL Server path later.
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'New workspace'
        Wait-ForText 'Create & connect'
        $name = @((Find-Element 'Workspace name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Invoke-Ui @('set-value', $name.selector, 'Local SQL Server') | Out-Null
        Click-Element 'Create & connect'
        Start-Sleep -Seconds 4

        if ((Find-Element 'Disconnect').matchCount -gt 0) {
            Click-Element 'ConnectionToggleButton'
            Wait-ForText 'Disconnected' 30
        }

        Right-Click-Element 'Query 1'
        Start-Sleep -Milliseconds 500
        Click-Element 'Rename'
        Wait-ForText 'Tab name'
        $tab = @((Find-Element 'Tab name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Invoke-Ui @('set-value', $tab.selector, 'Databases') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 700

        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'SELECT name, state_desc FROM sys.databases ORDER BY name;' -Literal
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText) -match 'sys.databases') 'the SQL Server tab content was not set'

        Switch-Workspace 'Staging'
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'the suite did not return to Staging'
    }

    Test-UI 'the results pane starts collapsed' {
        Assert-True ((Find-Element 'Results grid').matchCount -eq 0) 'the results pane was visible before a query'
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

    Test-UI 'the workspace manager shows provider and identity, and hides without deleting' {
        # The provider is a fact on every switcher row, and the manager adds the saved identity.
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Staging, Demo (in-memory)').matchCount -gt 0) 'the switcher does not name the provider'
        Click-Element 'Manage workspaces'
        Wait-ForText 'Drag a row' 10
        Assert-True ((Find-Element 'Windows authentication').matchCount -gt 0) 'the manager does not show the connection identity'
        Assert-True ((Find-Element 'Edit Local SQL Server').matchCount -gt 0) 'the manager row has no edit action'

        Click-Element 'Hide Local SQL Server from the rail'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'HIDDEN FROM RAIL').matchCount -gt 0) 'the hidden row is not labelled'
        Assert-True ((Find-Element 'Show Local SQL Server in the rail').matchCount -gt 0) 'hide did not stick'
        Close-WorkspaceManager
        Assert-True ((Find-Element 'Workspace Local SQL Server').matchCount -eq 0) 'a hidden workspace is still in the rail'

        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element '1 hidden workspace').matchCount -gt 0) 'the switcher does not account for the hidden workspace'
        Click-Element 'Manage workspaces'
        Wait-ForText 'Drag a row' 10
        Click-Element 'Show Local SQL Server in the rail'
        Start-Sleep -Milliseconds 600
        Close-WorkspaceManager
        Assert-True ((Find-Element 'Workspace Local SQL Server').matchCount -gt 0) 'show did not return the workspace to the rail'
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
        # its row on the Workspaces page.
        Open-WorkspaceSettings 'Staging'
        Click-Element 'Workspace colour'
        Start-Sleep -Milliseconds 400
        Click-Element 'Colour Pastel Yellow'
        Start-Sleep -Milliseconds 400
        Click-Element 'Save'
        Start-Sleep -Milliseconds 1200
        Assert-True ((Get-StudioStateText) -match '#FDE047') 'the new workspace colour was not saved'
        Close-WorkspaceManager
    }

    Test-UI 'a workspace renames from the manager' {
        Open-WorkspaceSettings 'Staging'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Assert-True ($null -ne $editor) 'the name field did not appear'
        Invoke-Ui @('set-value', $editor.selector, 'Staging renamed') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Staging renamed').matchCount -gt 0) 'the workspace rename did not apply'
        Assert-True ((Get-StudioStateText) -match 'Staging renamed') 'the workspace rename was not saved'
        Close-WorkspaceManager

        # Put the seeded name back so the rest of the suite starts from the same place.
        Open-WorkspaceSettings 'Staging renamed'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Staging') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900
        Close-WorkspaceManager
        Assert-True ((Find-Element 'Workspace Staging').matchCount -gt 0) 'the workspace name was not restored'
    }

    Test-UI 'editing another workspace leaves the current one alone' {
        # Local SQL Server is not the active workspace here: saving it must not switch, connect,
        # or touch the editor.
        Open-WorkspaceSettings 'Local SQL Server'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Local renamed') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 1000
        Assert-True ((Find-Element 'Local renamed').matchCount -gt 0) 'the other workspace was not renamed'
        Close-WorkspaceManager
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'editing another workspace changed the active one'

        Open-WorkspaceSettings 'Local renamed'
        $editor = @((Find-Element 'Workspace name').matches | Where-Object type -eq 'Edit' | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Local SQL Server') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 1000
        Close-WorkspaceManager
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
        Assert-True ((Find-Element 'Workspace Space 3').matchCount -eq 0) 'cancelling still created a workspace'
        Assert-True ((Find-Element 'Customer totals').matchCount -gt 0) 'the active workspace changed after cancelling'
    }

    Test-UI 'completing the new workspace dialog creates and connects it' {
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        Click-Element 'New workspace'
        Start-Sleep -Milliseconds 900
        Click-Element 'Create & connect'
        Start-Sleep -Milliseconds 1500
        Assert-True ((Find-Element 'Workspace Space 3').matchCount -gt 0) 'the workspace was not created'
        Assert-True ((Find-Element 'Query 1').matchCount -gt 0) 'the new workspace is not the active one'
        Click-Element 'Workspace Staging'
        Start-Sleep -Milliseconds 700
    }

    Test-UI 'deleting a workspace archives its SQL and removes it from the rail' {
        # The workspace from the previous check has SQL, so deletion must preserve it in Archive.
        Switch-Workspace 'Space 3'
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select 1' -Literal
        Start-Sleep -Milliseconds 400

        Open-WorkspaceSettings 'Space 3'
        Invoke-Element 'Delete workspace'
        Wait-ForText 'Delete workspace?'
        Click-Element 'Delete'
        Start-Sleep -Milliseconds 1200
        Close-WorkspaceManager
        Assert-True ((Find-Element 'Workspace Space 3').matchCount -eq 0) 'the workspace is still in the switcher'
        Assert-True ((Find-Element 'Query 1').matchCount -eq 0) 'deletion did not switch away from the workspace'

        # The tab's SQL survives in Archive, so a delete never discards work.
        Click-Element 'Archive'
        Wait-ForText 'select 1' 10
        Click-Element 'Back to workspace'
        Start-Sleep -Milliseconds 700

        Switch-Workspace 'Staging'
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

        # "SELECT * FROM " ends at the FROM clause, so table suggestions reopen immediately.
        $deadline = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $deadline -and (Find-Element 'SQL suggestions').matchCount -eq 0) {
            Start-Sleep -Milliseconds 200
        }
        Assert-True ((Find-Element 'SQL suggestions').matchCount -gt 0) 'accepting the snippet did not reopen table suggestions'
        Assert-True ((Find-Element 'customers').matchCount -gt 0) 'the reopened list does not suggest tables'
    }

    Test-UI 'Ctrl+Enter runs the query through the provider' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'customers;' -Literal
        Start-Sleep -Milliseconds 400
        Send-Keys 'ctrl+enter'
        Wait-ForText 'FULL NAME'
        Assert-True ((Find-Element '24 rows returned').matchCount -gt 0) 'status bar did not report the row count'
    }

    Test-UI 'Space ticks columns only in the SELECT list' {
        # In the projection, Space multi-selects the highlighted column: the text does not change.
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select ' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'SQL suggestions').matchCount -gt 0) 'column suggestions did not open'
        Send-Keys 'space'
        Start-Sleep -Milliseconds 300
        Assert-True ((Get-EditorText) -eq 'select ') "Space did not tick in the SELECT list: '$((Get-EditorText))'"

        # In a WHERE condition the checkbox makes no sense, so Space is an ordinary space.
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from customers where ' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'SQL suggestions').matchCount -gt 0) 'WHERE column suggestions did not open'
        Send-Keys 'space'
        Start-Sleep -Milliseconds 300
        Assert-True ((Get-EditorText) -eq 'select * from customers where  ') "Space was swallowed in WHERE: '$((Get-EditorText))'"
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 200
    }

    Test-UI 'Format preserves string literals and is undoable' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys "select * from customers where country = 'ID';" -Literal
        Start-Sleep -Milliseconds 400
        Click-Element 'Format query'
        Start-Sleep -Milliseconds 500
        $formatted = Get-EditorText
        Assert-True ($formatted -match 'SELECT') "keywords were not uppercased: '$formatted'"
        Assert-True ($formatted -match 'FROM') "FROM is missing after format: '$formatted'"
        Assert-True ($formatted.Contains("'ID'")) "the string literal was lost: '$formatted'"

        # Formatting is one undo step; Ctrl+Z must bring the original text back.
        Send-Keys 'ctrl+z'
        Start-Sleep -Milliseconds 400
        Assert-True ((Get-EditorText) -eq "select * from customers where country = 'ID';") "Ctrl+Z did not revert the format: '$((Get-EditorText))'"
    }

    Test-UI 'Ctrl+R toggles the results pane, and a run reopens it' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+r'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Results grid').matchCount -eq 0) 'Ctrl+R did not hide the results pane'

        Send-Keys 'ctrl+r'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Results grid').matchCount -gt 0) 'Ctrl+R did not show the results pane'

        Send-Keys 'ctrl+r'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Results grid').matchCount -eq 0) 'the second hide did not work'

        Send-Keys 'ctrl+a'
        Send-Keys 'select * from customers;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'FULL NAME' 20
        Assert-True ((Find-Element 'Results grid').matchCount -gt 0) 'running a query did not reopen the results pane'
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
        Assert-True ((Get-EditorText) -eq 'select * from dbo.customers AS c') "schema/alias was not appended: '$((Get-EditorText))'"

        # The inserted reference must parse back, or the next alias completion silently dies.
        Send-Keys ' where c.' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'full_name').matchCount -gt 0) 'columns do not complete after a schema-qualified insert'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'typing a schema narrows table suggestions without repeating it' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from dbo.' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'orders').matchCount -gt 0) 'schema-qualified table is not suggested'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 600
        Assert-True ((Get-EditorText) -eq 'select * from dbo.orders AS o') "the typed schema was repeated or the alias was lost: '$((Get-EditorText))'"
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
        Open-WorkspaceSettings 'Staging'
        Start-Sleep -Milliseconds 1000
        try {
            Assert-True ((Find-Element 'Workspace settings').matchCount -gt 0) 'the workspace editor did not open'
            $databasePicker = @((Find-Element 'Database').matches | Where-Object { $_.type -eq 'ComboBox' } | Select-Object -First 1)
            Assert-True ($null -ne $databasePicker) 'database picker missing'
            Assert-True ((Find-Element 'Databases').matchCount -gt 0) 'provider settings not rendered'
            # Opening the picker proves the catalog listed every loaded provider.
            Invoke-Ui @('click', $databasePicker.selector) | Out-Null
            Start-Sleep -Milliseconds 600
            Assert-True ((Find-Element 'SQL Server').matchCount -gt 0) 'providers not listed'
            Assert-True ((Find-Element 'PostgreSQL').matchCount -gt 0) 'the PostgreSQL provider was not listed'

            # The built-in PostgreSQL assembly is loaded and its declared settings drive the form.
            $postgres = @((Find-Element 'PostgreSQL').matches | Where-Object { $_.type -eq 'ListItem' } | Select-Object -First 1)
            Assert-True ($null -ne $postgres) 'the PostgreSQL provider item is missing'
            Invoke-Ui @('click', $postgres.selector) | Out-Null
            Start-Sleep -Milliseconds 700
            Assert-True ((Find-Element 'Host').matchCount -gt 0) 'PostgreSQL settings did not render'
            Assert-True ((Find-Element 'Password').matchCount -gt 0) 'the PostgreSQL password field is missing'

            # A provider can declare a file setting: the path stays editable and a Browse… action
            # sits beside it. The fixture add-on proves the shell renders that kind without the
            # suite installing a real file provider.
            if ($LaunchedHere) {
                Invoke-Ui @('click', $databasePicker.selector) | Out-Null
                Start-Sleep -Milliseconds 600
                $fixture = @((Find-Element 'Fixture (add-on)').matches | Where-Object { $_.type -eq 'ListItem' } | Select-Object -First 1)
                Assert-True ($null -ne $fixture) 'the fixture provider item is missing'
                Invoke-Ui @('click', $fixture.selector) | Out-Null
                Start-Sleep -Milliseconds 700
                Assert-True ((Find-Element 'Browse for Database file').matchCount -gt 0) 'a file setting has no browse action'

                $fileField = @((Find-Element 'Database file').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
                Assert-True ($null -ne $fileField) 'the file setting is not an editable path'
                Invoke-Ui @('set-value', $fileField.selector, 'C:\data\app.db') | Out-Null
                Start-Sleep -Milliseconds 300
                $typed = ((Invoke-Ui @('get-value', $fileField.selector, '--json')) -join "`n" | ConvertFrom-Json).text
                Assert-True ($typed -eq 'C:\data\app.db') "the file field kept '$typed'"
            }

            # MongoDB is an add-on now: the built-in picker must not advertise it, and the dialog
            # offers the install path instead.
            Assert-True ((Find-Element 'MongoDB').matchCount -eq 0) 'MongoDB is still built in'
            Assert-True ((Find-Element 'Install another database driver').matchCount -gt 0) 'the install-driver entry point is missing'

            # The SQL Server form is the tall one: Test must keep its result on screen, not
            # just in the automation tree below the visible part of the dialog. Pick it by name so
            # adding a provider never depends on the picker's order.
            Invoke-Ui @('click', $databasePicker.selector) | Out-Null
            Start-Sleep -Milliseconds 600
            $sqlServer = @((Find-Element 'SQL Server').matches | Where-Object { $_.type -eq 'ListItem' } | Select-Object -First 1)
            Assert-True ($null -ne $sqlServer) 'the SQL Server provider item is missing'
            Invoke-Ui @('click', $sqlServer.selector) | Out-Null
            Start-Sleep -Milliseconds 800
            Assert-True ((Find-Element 'Initial database').matchCount -gt 0) 'the SQL Server provider was not selected'
            Assert-True ((Find-Element 'Connect timeout (s)').matchCount -eq 0) 'advanced fields are not collapsed'
            Click-Element 'Advanced'
            Start-Sleep -Milliseconds 600
            Assert-True ((Find-Element 'Connect timeout (s)').matchCount -gt 0) 'advanced fields did not expand'
            Click-Element 'Done'
            Start-Sleep -Milliseconds 400
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
            Close-WorkspaceManager
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

    Test-UI 'USE switches the tab, picker and completion to another database' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'use msdb' -Literal
        Start-Sleep -Milliseconds 400
        Send-Keys 'ctrl+enter'
        Wait-ForText 'msdb' 30

        # Completion now reads msdb: only a USE that reached the shell reloads the schema, and the
        # insert is schema-qualified and bracketed for T-SQL.
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from back' -Literal
        Start-Sleep -Milliseconds 1500
        Assert-True ((Find-Element 'backupset').matchCount -gt 0) 'msdb tables are not suggested after USE'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 700
        Assert-True ((Get-EditorText) -eq 'select * from [dbo].[backupset] AS [b]') "schema/alias insert was '$((Get-EditorText))'"

        # A bracketed schema and alias must parse back for column completion.
        Send-Keys ' where b.' -Literal
        Start-Sleep -Milliseconds 1200
        Assert-True ((Find-Element 'backup_set_id').matchCount -gt 0) 'columns do not complete through the bracketed alias'
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'Shift+Space previews the referenced rows of a foreign key' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select top 5 dbms_id from dbo.MSdbms_datatype;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'dbms_id' 30
        Start-Sleep -Milliseconds 1500

        Focus-Element 'Results grid'
        Send-Keys 'home'
        Assert-True ((Find-Element 'References dbo.MSdbms.dbms_id').matchCount -gt 0) 'the foreign-key header icon is missing'
        Send-Keys 'shift+space'
        Wait-ForText 'Reference preview' 20
        Wait-ForText 'MSdbms' 20
        Assert-True ((Find-Element 'MSSQLSERVER').matchCount -gt 0) 'the full lookup table was not listed'
        Assert-True ((Find-Element 'match on row 1').matchCount -gt 0) 'the matching reference row is not reported'

        Send-Keys 'escape'
        Start-Sleep -Milliseconds 400
        Assert-True ((Find-Element 'Reference preview').matchCount -eq 0) 'Esc did not close the reference preview'

        # The header link icon is the discoverable equivalent of Shift+Space.
        Click-Element 'References dbo.MSdbms.dbms_id'
        Wait-ForText 'Reference preview' 20
        Wait-ForText 'MSSQLSERVER' 20
        Send-Keys 'escape'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'a large reference table is looked up by key, not listed in full' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select top 5 default_datatype_mapping_id from dbo.MSdbms_map;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'default_datatype_mapping_id' 30
        Start-Sleep -Milliseconds 1500

        Focus-Element 'Results grid'
        Send-Keys 'home'
        Send-Keys 'shift+space'
        Wait-ForText 'Reference preview' 20
        Wait-ForText 'Matching rows' 20
        Assert-True ((Find-Element 'Full list').matchCount -eq 0) 'a large reference table was listed in full'
        Assert-True ((Find-Element 'match on row 1').matchCount -gt 0) 'the matching lookup row is not reported'

        Send-Keys 'escape'
        Start-Sleep -Milliseconds 300
    }

    Test-UI 'Ctrl+K browses the schema and inserts a table name' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from ' -Literal
        Send-Keys 'ctrl+k'
        Wait-ForText 'Search schema' 10
        Send-Keys 'backup' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'backupset').matchCount -gt 0) 'schema search did not find msdb tables'
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 800
        Assert-True ((Get-EditorText) -eq 'select * from [dbo].[backupset]') "schema insert produced '$((Get-EditorText))'"
        Assert-True ((Find-Element 'Search schema').matchCount -eq 0) 'the schema peek stayed open after inserting'
    }

    Test-UI 'completion suggests views and executes a procedure with its parameters' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select * from autoadmin_backup_configurations' -Literal
        Start-Sleep -Milliseconds 1500
        Assert-True ((Find-Element 'autoadmin_backup_configurations').matchCount -gt 0) 'views are not suggested in FROM'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 700
        Assert-True ((Get-EditorText) -eq 'select * from [dbo].[autoadmin_backup_configurations] AS [abc]') "view insert was '$((Get-EditorText))'"

        Send-Keys 'ctrl+a'
        Send-Keys 'exec autoadmin_metadata_query_dbs' -Literal
        Start-Sleep -Milliseconds 1500
        Assert-True ((Find-Element 'autoadmin_metadata_query_dbs').matchCount -gt 0) 'procedures are not suggested after EXEC'
        Send-Keys 'tab'
        Start-Sleep -Milliseconds 700
        Assert-True ((Get-EditorText) -eq 'exec [dbo].[autoadmin_metadata_query_dbs] @task_agent_guid = NULL, @schema_version = NULL, @db_name = NULL') "procedure insert was '$((Get-EditorText))'"

        # Accepting the signature selects the first NULL; typing over it proves the placeholder.
        Send-Keys '1'
        Start-Sleep -Milliseconds 400
        Assert-True ((Get-EditorText) -like '*@task_agent_guid = 1,*') "the first parameter was not selected: '$((Get-EditorText))'"
        Send-Keys 'esc'
        Start-Sleep -Milliseconds 200
    }

    Test-UI 'the schema browser lists a view and inserts a procedure call' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'ctrl+k'
        Wait-ForText 'Search schema' 10
        Send-Keys 'autoadmin_backup_configurations' -Literal
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'autoadmin_backup_configurations').matchCount -gt 0) 'schema search did not find the view'
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 700
        Assert-True ((Get-EditorText) -eq '[dbo].[autoadmin_backup_configurations]') "view insert was '$((Get-EditorText))'"

        Send-Keys 'ctrl+a'
        Send-Keys 'ctrl+k'
        Wait-ForText 'Search schema' 10
        Send-Keys 'autoadmin_metadata_query_dbs' -Literal
        Start-Sleep -Milliseconds 900
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 700
        Assert-True ((Get-EditorText) -eq 'EXEC [dbo].[autoadmin_metadata_query_dbs] @task_agent_guid = NULL, @schema_version = NULL, @db_name = NULL') "procedure insert was '$((Get-EditorText))'"
    }

    Test-UI 'the schema peek can be pinned beside the editor' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+k'
        Wait-ForText 'Search schema' 10
        Click-Element 'Pin schema browser'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Search schema').matchCount -gt 0) 'pinning closed the schema surface'
        Assert-True ((Find-Element 'Preview 100 rows').matchCount -eq 0) 'the pinned pane still shows the peek detail'
        Click-Element 'Pin schema browser'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'Search schema').matchCount -eq 0) 'unpinning left the inspector open'
    }

    Test-UI 'PRINT opens a Messages tab with the server output' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys "print 'larik message test';" -Literal
        Send-Keys 'f5'
        Start-Sleep -Milliseconds 1500
        Click-ResultTabWithText 'Messages' 'larik message test'
        Assert-True ((Find-Element 'Messages').matchCount -gt 0) 'PRINT did not open a Messages tab'
    }

    Test-UI 'switching back from Messages hides the message text over the rows' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys "print 'larik behind rows';" -Literal
        Send-Keys 'f5'
        Start-Sleep -Milliseconds 1500
        Click-ResultTabWithText 'Messages' 'larik behind rows'
        Wait-ForText 'larik behind rows' 20

        # Running a row query replaces the active Messages result. The message panel must clear
        # instead of staying painted over the grid.
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'select 1 as alpha;' -Literal
        Send-Keys 'ctrl+enter'
        Wait-ForText 'alpha' 30
        Start-Sleep -Milliseconds 500

        Assert-True ((Find-Element 'larik behind rows').matchCount -eq 0) 'the message text is still visible over the result rows'
        Assert-True ((Find-Element 'alpha').matchCount -gt 0) 'the result rows did not render after Messages'
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

    Test-UI 'the new-tab palette disables a database the login cannot open' {
        Click-Element 'New query tab'
        Start-Sleep -Milliseconds 800
        Focus-Element 'New tab database search'
        Send-Keys 'larik_archive' -Literal
        Start-Sleep -Milliseconds 500

        $locked = @((Find-Element 'larik_archive').matches | Where-Object { $_.type -eq 'ListItem' } | Select-Object -First 1)
        Assert-True ($null -ne $locked) 'the locked database is not listed'
        Assert-True (-not $locked.isEnabled) 'the locked database is selectable'

        # Enter must not fall through to the first row when the only match has no access.
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'New query database').matchCount -gt 0) 'Enter opened a tab on a database with no access'
        Send-Keys 'escape'
        Start-Sleep -Milliseconds 400
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

    Test-UI 'the cell preview is remembered per tab, not shared' {
        Focus-Element 'Results grid'
        Send-Keys 'ctrl+home'
        Send-Keys 'space'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Cell preview').matchCount -gt 0) 'the preview did not open on Query 2'

        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element 'Cell preview').matchCount -eq 0) 'Query 1 inherited Query 2 preview'

        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 800
        Assert-True ((Find-Element 'Cell preview').matchCount -gt 0) 'Query 2 did not restore its own preview'

        Focus-Element 'Results grid'
        Send-Keys 'escape'
        Start-Sleep -Milliseconds 400
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

    Test-UI 'Space opens the cell preview and Esc closes it' {
        Focus-Element 'Results grid'
        Send-Keys 'ctrl+home'
        Send-Keys 'space'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Cell preview').matchCount -gt 0) 'the cell preview did not open'

        # The panel follows the focused cell, so arrowing must not close it.
        Send-Keys 'right'
        Start-Sleep -Milliseconds 400
        Assert-True ((Find-Element 'Cell preview').matchCount -gt 0) 'the preview closed while moving between cells'

        Send-Keys 'escape'
        Start-Sleep -Milliseconds 400
        Assert-True ((Find-Element 'Cell preview').matchCount -eq 0) 'Esc did not close the cell preview'
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

    Test-UI 'the grid exports the whole result to a CSV file' {
        # The save picker's file type is the format choice; drive it end to end so the bytes on
        # disk (header, CRLF rows, NULL handling) are what a person would actually get.
        $path = Join-Path $env:TEMP ("larik-export-{0}.csv" -f [guid]::NewGuid().ToString('n'))
        Focus-Element 'Results grid'
        $cell = @((Find-Element 'master').matches) | Where-Object { $_.width -gt 0 -and $_.height -gt 0 } | Select-Object -First 1
        Assert-True ($null -ne $cell) 'no visible result cell to right-click'
        Focus-App
        Invoke-Ui @('click', $cell.selector, '--right') -Attempts 4 | Out-Null
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'Export results').matchCount -gt 0) 'the grid menu has no export action'

        Invoke-Ui @('click', 'Export results') | Out-Null
        Wait-ForText 'Save As' 15

        # The picker runs in its own process, so find its window and drive that.
        $dialog = @((Get-AllWindows) | Where-Object { $_.title -eq 'Save As' -and $_.hwnd } | Select-Object -First 1)
        Assert-True ($dialog.Count -gt 0) 'the save picker window was not found'
        $picker = [string]$dialog[0].hwnd

        $nameBox = @((Get-PickerJson @('search', 'File name', '--json') $picker).matches |
                Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Assert-True ($null -ne $nameBox) 'the save picker has no file-name field'
        Invoke-PickerUi @('focus', $nameBox.selector) $picker | Out-Null
        Invoke-PickerUi @('set-value', $nameBox.selector, $path) $picker | Out-Null

        $save = @((Get-PickerJson @('search', 'Save', '--json') $picker).matches |
                Where-Object { $_.type -eq 'Button' } | Select-Object -First 1)
        Assert-True ($null -ne $save) 'the save picker has no Save button'
        Invoke-PickerUi @('invoke', $save.selector) $picker | Out-Null
        Wait-ForText 'Exported' 15

        Assert-True (Test-Path $path) "no file was written at $path"
        $csv = Get-Content $path -Raw
        Assert-True ($csv -match '^name,database_id') "the CSV header is missing: $($csv.Substring(0, [math]::Min(80, $csv.Length)))"
        Assert-True ($csv -match 'master') 'the exported CSV has no result rows'
        Remove-Item $path -ErrorAction SilentlyContinue
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

        # Dragging the model result (old Result 1) to the right makes the label result first.
        # Names are stable, so the old first grid keeps answering to Result 1 in its new place.
        Click-Element 'Result 1'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'model').matchCount -gt 0) 'the first result did not move to the right'
        Assert-True ((Find-Element 'row_1').matchCount -eq 0) 'the second result stayed in place'

        # The query tab owns its results; switching away and back must keep the new order.
        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 500
        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 500
        Click-Element 'Result 1'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'model').matchCount -gt 0) 'the reordered results reverted on tab switch'
    }

    Test-UI 'a result tab renames on double-click and keeps the name' {
        $tab = @((Find-Element 'Result 1').matches) |
            Where-Object { $_.type -eq 'ListItem' -and $_.width -gt 0 } |
            Select-Object -First 1
        Assert-True ($null -ne $tab) 'the result tab was not found'

        Invoke-Ui @('click', $tab.selector, '--double') -Attempts 4 | Out-Null
        Wait-ForText 'Result name' 10
        $editor = @((Find-Element 'Result name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Assert-True ($null -ne $editor) 'the inline result rename editor did not appear'
        Invoke-Ui @('set-value', $editor.selector, 'Model rows') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'Model rows').matchCount -gt 0) 'the result rename did not commit'
        Assert-True ((Find-Element 'Result 1').matchCount -eq 0) 'the old result name is still visible'

        # The name belongs to the result, not the position: it survives a query-tab round trip.
        Click-Element 'Query 1'
        Start-Sleep -Milliseconds 500
        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'Model rows').matchCount -gt 0) 'the result name was lost on tab switch'

        # Restore the generated name so later checks keep the plain Result N labels.
        $renamed = @((Find-Element 'Model rows').matches) |
            Where-Object { $_.type -eq 'ListItem' -and $_.width -gt 0 } |
            Select-Object -First 1
        Invoke-Ui @('click', $renamed.selector, '--double') -Attempts 4 | Out-Null
        Wait-ForText 'Result name' 10
        $editor = @((Find-Element 'Result name').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Invoke-Ui @('set-value', $editor.selector, 'Result 1') | Out-Null
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 600
        Assert-True ((Find-Element 'Result 1').matchCount -gt 0) 'the result name was not restored'
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

        # The rail checks are a clean resumable boundary: no transaction or temp table is live.
        # Future runs can start here with -From 'a tab can be grouped'.
        Save-RailCheckpoint
    }

    Test-UI 'a tab can be grouped, collapsed and joined by a new tab' {
        # Query 2 is on tempdb, so the group takes the database name without asking for one.
        Right-Click-Element 'Query 2'
        Start-Sleep -Milliseconds 500
        Click-Element 'New group from tab'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'tempdb, 1 tab, expanded').matchCount -gt 0) 'the group header did not appear'

        # Collapsing hides the row but keeps the group; expanding brings the same tab back.
        Click-Element 'tempdb, 1 tab, expanded'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'tempdb, 1 tab, collapsed').matchCount -gt 0) 'the group did not collapse'
        $hidden = @((Find-Element 'Query 2').matches) | Where-Object { $_.x -lt 252 -and $_.type -eq 'ListItem' }
        Assert-True ($hidden.Count -eq 0) 'a collapsed group still shows its tab'

        Click-Element 'tempdb, 1 tab, collapsed'
        Start-Sleep -Milliseconds 500
        Assert-True ((Find-Element 'tempdb, 1 tab, expanded').matchCount -gt 0) 'the group did not expand'

        # A new tab opened on the group's database joins it instead of landing loose.
        Click-Element 'New query tab'
        Wait-ForText 'New query database' 10
        Focus-Element 'New tab database search'
        Send-Keys 'tempdb' -Literal
        Start-Sleep -Milliseconds 400
        Send-Keys 'enter'
        Start-Sleep -Milliseconds 900
        Assert-True ((Find-Element 'Query 3').matchCount -gt 0) 'the new tab did not appear'
        Assert-True ((Find-Element 'tempdb, 2 tabs, expanded').matchCount -gt 0) 'the new tab did not join its database group'
    }

    Test-UI 'Group by database is one action with undo' {
        # Two tabs on a database the suite does not otherwise use, so the action has something to do.
        foreach ($ignored in 1..2) {
            Click-Element 'New query tab'
            Wait-ForText 'New query database' 10
            Focus-Element 'New tab database search'
            Send-Keys 'model' -Literal
            Start-Sleep -Milliseconds 400
            Send-Keys 'enter'
            Start-Sleep -Milliseconds 800
        }

        Click-Element 'Group tabs by database'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'tabs by database').matchCount -gt 0) 'the grouping action did not report its result'
        Assert-True ((Find-Element 'model, 2 tabs, expanded').matchCount -gt 0) 'grouping did not create the model group'

        # The status bar is a small target at the window edge; invoke it directly.
        Invoke-Element 'Undo grouping'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'Grouping undone').matchCount -gt 0) 'undo did not run'
        Assert-True ((Find-Element 'model, 2 tabs').matchCount -eq 0) 'undo left the created group behind'
    }

    Test-UI 'a group can be pinned and survives a workspace switch' {
        $tabsLabel = @((Find-Element 'TABS').matches) | Where-Object {
            $_.x -lt 252 -and $_.type -eq 'Text' -and $_.name -eq 'TABS'
        } | Select-Object -First 1
        Assert-True ($null -ne $tabsLabel) 'the TABS header was not found'

        Right-Click-Element 'tempdb, 2 tabs, expanded'
        Start-Sleep -Milliseconds 500
        Click-Element 'Pin group'
        Wait-ForText 'Pinned group tempdb' 10

        # Let the list finish moving the row before reading its bounds; both rows move together.
        $pinnedGroup = $null
        $tabsNow = $null
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline) {
            $pinnedGroup = @((Find-Element 'tempdb, 2 tabs, expanded').matches) | Where-Object { $_.x -lt 252 } | Select-Object -First 1
            $tabsNow = @((Find-Element 'TABS').matches) | Where-Object {
                $_.x -lt 252 -and $_.type -eq 'Text' -and $_.name -eq 'TABS'
            } | Select-Object -First 1
            if ($null -ne $pinnedGroup -and $null -ne $tabsNow -and $pinnedGroup.y -lt $tabsNow.y) { break }
            Start-Sleep -Milliseconds 250
        }

        Assert-True ($null -ne $pinnedGroup) 'the pinned group disappeared'
        Assert-True ($null -ne $tabsNow) 'the TABS header disappeared'
        Assert-True ($pinnedGroup.y -lt $tabsNow.y) 'a pinned group stayed below the TABS header'

        Right-Click-Element 'tempdb, 2 tabs, expanded'
        Start-Sleep -Milliseconds 500
        Click-Element 'Unpin group'
        Start-Sleep -Milliseconds 700
        Assert-True ((Find-Element 'Unpinned group tempdb').matchCount -gt 0) 'the group did not unpin'

        # Groups live in the space, so switching away and back must restore them.
        Switch-Workspace 'Staging'
        Switch-Workspace 'Local SQL Server'
        Assert-True ((Find-Element 'tempdb, 2 tabs, expanded').matchCount -gt 0) 'the group was not restored with the workspace'
    }

    Test-UI 'a tab pops out into its own window and docks back' {
        # Anchor on Query 2 so the title and the editor text the check compares are the same tab.
        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 600
        $before = (Invoke-Ui @('get-value', 'Editor', '--json')) -join "`n" | ConvertFrom-Json
        Assert-True ($before.text -match 'select top 1000') 'Query 2 is not the active editor'

        Focus-App
        Send-Keys 'ctrl+alt+o'

        $popout = Get-PopoutWindow
        Assert-True ($null -ne $popout) 'the pop-out window did not open'
        Assert-True ([string]$popout.title -eq 'Query 2') "the pop-out is titled '$($popout.title)'"

        # The shell keeps the tab and its rail row, but hands the editor and results over.
        Focus-App
        Wait-ForText 'This tab is open in its own window'
        Assert-True ((Find-Element 'Open in its own window').matchCount -gt 0) 'the rail does not mark the popped-out tab'

        try {
            # The window renders the same tab: same text, and typing there reaches the shell's model.
            $loaded = Get-PopoutJson @('get-value', 'Editor', '--json') ([string]$popout.hwnd)
            Assert-True ($loaded.text -eq $before.text) 'the pop-out did not load the tab content'

            Send-PopoutKeys 'ctrl+end' ([string]$popout.hwnd)
            Send-PopoutKeys ' -- popped out' ([string]$popout.hwnd) -Literal
            Start-Sleep -Milliseconds 500
            $typed = Get-PopoutJson @('get-value', 'Editor', '--json') ([string]$popout.hwnd)
            Assert-True ($typed.text -match '-- popped out') 'typing in the pop-out did not reach its editor'

            # Ctrl+W docks the tab back; the shell's editor returns with the pop-out's edit.
            Send-PopoutKeys 'ctrl+w' ([string]$popout.hwnd)
        }
        finally {
            # A failed assertion above must not leave a second window for the rest of the scenario.
            $open = @(Get-UiJson @('list-windows', '--json')) |
                Where-Object { $_.processId -eq $AppPid -and $_.label -eq 'window' }
            if ($open.Count -gt 1) {
                try { Send-PopoutKeys 'ctrl+w' ([string]$popout.hwnd) } catch { }
                Start-Sleep -Milliseconds 800
            }
        }

        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline) {
            $remaining = @(Get-UiJson @('list-windows', '--json')) |
                Where-Object { $_.processId -eq $AppPid -and $_.label -eq 'window' }
            if ($remaining.Count -eq 1) { break }
            Start-Sleep -Milliseconds 300
        }
        Assert-True ($remaining.Count -eq 1) 'the pop-out window did not close'

        Focus-App
        Assert-True ((Find-Element 'This tab is open in its own window').matchCount -eq 0) 'the placeholder stayed after docking'
        $after = (Invoke-Ui @('get-value', 'Editor', '--json')) -join "`n" | ConvertFrom-Json
        Assert-True ($after.text -match '-- popped out') 'the docked editor lost the pop-out edit'
    }

    Test-UI 'transactions belong to the query tab that opened them' {
        # The grouping checks run before this one and leave another tab active. Select the tab
        # this check reasons about, and prove the editor shows it before typing.
        Click-Element 'Query 2'
        Start-Sleep -Milliseconds 600
        $editor = (Invoke-Ui @('get-value', 'Editor', '--json')) -join "`n" | ConvertFrom-Json
        Assert-True ($editor.text -match 'select top 1000') 'Query 2 is not the active editor'

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
        Wait-ForText '1 result set' 20
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

        # Clean up through the session. The guard's rollback action lives in a flyout in this
        # layout, so invoking a Button is not reliable; the SQL round trip is.
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'rollback;' -Literal
        Send-Keys 'ctrl+enter'
        Start-Sleep -Milliseconds 800

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

        # The two GO batches get their own result tabs (the USE batch also carries a Messages tab),
        # so find the tab that renders the select instead of assuming its number.
        Wait-ForText 'Statement completed' 20
        Assert-True ((Find-Element 'Incorrect syntax').matchCount -eq 0) 'GO reached SQL Server'
        Click-ResultTabWithText 'Result' 'tempdb-suffix'
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

    Test-UI 'the diagnostics log records the session' {
        Start-Sleep -Milliseconds 900
        $log = Get-DiagnosticsLogPath
        Assert-True ($null -ne $log) 'no diagnostics log was written'
        $text = Get-Content $log.FullName -Raw
        Assert-True ($text -match '\[INFO \] session: starting Larik') 'the log has no session header'
        Assert-True ($text -match '\[INFO \] providers: \d+ loaded') 'the log does not name the loaded providers'
        Assert-True ($text -match '\[INFO \] query: batch start') 'the log does not record query runs'
        Assert-True ($text -match '\[INFO \] preview: (open|formatted):') 'the log does not record cell previews'
        Assert-True ($text -notmatch '\[ERROR\] crash') 'the log recorded a crash in the happy path'
    }

    Test-UI 'window size and position are remembered' {
        Focus-App
        $hwnd = [IntPtr]([int64]$script:MainWindow)
        [LarikWin32]::ShowWindow($hwnd, 9) | Out-Null
        Start-Sleep -Milliseconds 300

        # Resize in physical pixels; the app stores DIPs, so the saved value stays 820x620.
        $scale = [LarikWin32]::GetDpiForWindow($hwnd) / 96.0
        [LarikWin32]::SetWindowPos(
            $hwnd, [IntPtr]::Zero, 140, 100, [int](820 * $scale), [int](620 * $scale), 0x0014) | Out-Null
        Start-Sleep -Milliseconds 1500

        # Written from AppWindow.Changed (debounced), not only on close, so a killed process keeps it.
        $state = Get-StudioStateText
        $width = [regex]::Match($state, '"Width":\s*(\d+)')
        $height = [regex]::Match($state, '"Height":\s*(\d+)')
        Assert-True ($width.Success -and $height.Success) 'the window rectangle was not saved with the session'
        Assert-True ([math]::Abs([int]$width.Groups[1].Value - 820) -le 3) "saved width is $($width.Groups[1].Value), expected 820"
        Assert-True ([math]::Abs([int]$height.Groups[1].Value - 620) -le 3) "saved height is $($height.Groups[1].Value), expected 620"
        Assert-True ($state -match '"Maximized":\s*false') 'the saved window was not in its restored state'
    }

    Test-UI 'refuses a mutating statement without WHERE' {
        Invoke-Ui @('focus', 'Editor') | Out-Null
        Send-Keys 'ctrl+a'
        Send-Keys 'DELETE FROM orders' -Literal
        Start-Sleep -Milliseconds 400
        Click-Element 'RunButton'
        Wait-ForText 'Refused'
    }

    Test-UI 'a long server name stays inside the workspace switcher' {
        # The switcher shows the endpoint above Connect. A long host must trim to one bounded line
        # with the full value in its tooltip, instead of widening the flyout. Edit the workspace
        # while another space is active: saving a changed active connection reconnects, which
        # would hold the dialog open for the lifetime of the fixture host's connect timeout.
        Switch-Workspace 'Staging'
        Open-WorkspaceSettings 'Local SQL Server'
        $server = @((Find-Element 'Server').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Assert-True ($null -ne $server) 'the server field is missing'
        Invoke-Ui @('set-value', $server.selector, 'larik-marquee-fixture-with-a-very-long-server-name.example.internal\SQLEXPRESS') | Out-Null
        Click-Element 'Save'
        Start-Sleep -Milliseconds 1500
        Close-WorkspaceManager

        Switch-Workspace 'Local SQL Server'
        Click-Element 'Switch workspace'
        Start-Sleep -Milliseconds 500
        $line = @((Find-Element 'larik-marquee-fixture').matches | Where-Object { $_.type -eq 'Text' } | Select-Object -First 1)
        Assert-True ($null -ne $line) 'the switcher did not show the long endpoint'
        Assert-True ($line.width -le 340) "the long endpoint widened the switcher to $($line.width)px"
        Send-Keys 'escape'
        Start-Sleep -Milliseconds 300

        # Put the live LocalDB back; the remaining checks and the next run depend on it.
        Switch-Workspace 'Staging'
        Open-WorkspaceSettings 'Local SQL Server'
        $server = @((Find-Element 'Server').matches | Where-Object { $_.type -eq 'Edit' } | Select-Object -First 1)
        Invoke-Ui @('set-value', $server.selector, '(localdb)\MSSQLLocalDB') | Out-Null
        Click-Element 'Save'
        Start-Sleep -Milliseconds 1500
        Close-WorkspaceManager
    }

    # Last: the settings page replaces the shell in the same frame, so nothing after it is checked.
    Test-UI 'settings lives at the bottom of the rail, not in the query toolbar' {
        $gear = @((Find-Element 'SettingsButton').matches)[0]
        Assert-True ($null -ne $gear) 'no settings action in the rail'
        $newTab = @((Find-Element 'New query tab').matches)[0]
        Assert-True ($gear.y -gt $newTab.y) 'settings is not in the rail footer'

        Click-Element 'Settings'
        Wait-ForText 'Snippets' 10

        # The About page names this build's own store, so local data is easy to find.
        Click-Element 'About'
        Wait-ForText 'larik-winui' 10

        # A packaged copy keeps its store inside the package: an uninstall must take it with the
        # app, and it must not land in the shared %LOCALAPPDATA%\Larik WinUI of the GitHub build.
        if ($Package) {
            $storePath = @((Find-Element 'larik-winui').matches |
                Where-Object { $_.name -like '*larik-winui*.db' } |
                Select-Object -First 1)
            Assert-True ($null -ne $storePath) 'the About page does not name the store'
            Assert-True ($storePath.name -match '\\Packages\\[^\\]+\\LocalState\\') `
                "the packaged store is outside the package: $($storePath.name)"
        }

        # The crash log is discoverable from About: the path must be the live state folder's log
        # directory, and there must be a way to open it in Explorer.
        $logPath = @((Find-Element 'LogPathText').matches | Where-Object { $_.type -eq 'Text' } | Select-Object -First 1)
        Assert-True ($null -ne $logPath) 'the About page does not name the diagnostics log folder'
        $expectedLogs = Join-Path (Split-Path -Parent (Get-StateDatabasePath)) 'logs'
        Assert-True ($logPath.name -eq $expectedLogs) "About shows '$($logPath.name)', expected '$expectedLogs'"
        $openLog = @((Find-Element 'Open log folder').matches | Where-Object { $_.type -eq 'Button' } | Select-Object -First 1)
        Assert-True ($null -ne $openLog) 'the About page has no way to open the log folder'

        # The suite drives a packaged debug identity, and a packaged copy must never offer the
        # GitHub updater: it cannot replace files inside its package. The Store edition updates
        # through the Store instead.
        Assert-True ((Find-Element 'Check for updates automatically').matchCount -eq 0) 'a packaged build offered the GitHub updater'
        Wait-ForText 'Updates are delivered by the Microsoft Store.' 10

        Click-Element 'Back'
        Wait-ForText 'Switch workspace' 10
    }

    Test-UI 'settings offers the drivers page' {
        Click-Element 'Settings'
        Wait-ForText 'Snippets' 10

        Focus-App
        Invoke-Ui @('click', 'AddonsNavItem') | Out-Null
        Wait-ForText 'Install driver from file' 10
        Assert-True ((Find-Element 'Installed').matchCount -gt 0) 'the installed section is missing'
        Assert-True ((Find-Element 'Available').matchCount -gt 0) 'the available section is missing'

        Click-Element 'Back'
        Wait-ForText 'Switch workspace' 10
    }

    Test-UI 'the workspace switcher follows a runtime theme switch' {
        # The switcher's rows are built in code, so they must resolve their tokens from the live
        # theme. Switch to both themes and read the active row's resolved foreground: a stale
        # app-level lookup leaves near-white text on the light flyout (and vice versa).
        function Assert-SwitcherRowContrast {
            param([string]$Theme, [bool]$ExpectDarkText)

            Click-Element 'Switch workspace'
            Start-Sleep -Milliseconds 500

            $row = @((Find-Element 'Staging').matches | Where-Object {
                $_.type -eq 'Text' -and $_.invokableAncestor -and $_.invokableAncestor.name -like 'Staging,*'
            } | Select-Object -First 1)
            Assert-True ($null -ne $row) "the switcher has no Staging row in $Theme"

            $argb = Get-ElementForegroundArgb $row.selector
            # UIA reports this as 0x00BBGGRR, so read the channels back in RGB order.
            $r = $argb -band 0xFF
            $g = ($argb -shr 8) -band 0xFF
            $b = ($argb -shr 16) -band 0xFF
            $average = ($r + $g + $b) / 3
            $hex = '{0:X2}{1:X2}{2:X2}' -f $r, $g, $b

            Send-Keys 'escape'
            Start-Sleep -Milliseconds 300

            if ($ExpectDarkText) {
                Assert-True ($average -lt 128) "the workspace name is light (#$hex) on the light flyout"
            }
            else {
                Assert-True ($average -gt 128) "the workspace name is dark (#$hex) on the dark flyout"
            }
        }

        $steps = @(
            @{ Theme = 'Light'; Radio = 'Light'; ExpectDarkText = $true },
            @{ Theme = 'Dark'; Radio = 'Dark'; ExpectDarkText = $false },
            # Put the launch preference back: the rest of the run and the next launch share this app.
            @{ Theme = 'System'; Radio = 'Use Windows setting'; ExpectDarkText = $null }
        )

        foreach ($step in $steps) {
            Click-Element 'Settings'
            Wait-ForText 'Snippets' 10
            Focus-App
            Invoke-Ui @('click', 'AppearanceNavItem') | Out-Null
            Wait-ForText 'Theme' 10

            $radio = @((Find-Element $step.Radio).matches |
                Where-Object { $_.type -eq 'RadioButton' -and $_.isEnabled } |
                Select-Object -First 1)
            Assert-True ($null -ne $radio) "the $($step.Theme) theme option is missing"
            Invoke-Ui @('invoke', $radio.selector) | Out-Null
            Start-Sleep -Milliseconds 600

            Click-Element 'Back'
            Wait-ForText 'Switch workspace' 10

            if ($null -ne $step.ExpectDarkText) {
                Assert-SwitcherRowContrast $step.Theme $step.ExpectDarkText
            }
        }
    }

    Test-UI 'deleting the last workspace returns to onboarding' {
        Open-WorkspaceSettings 'Staging'
        Invoke-Element 'Delete workspace'
        Wait-ForText 'Delete workspace?'
        Click-Element 'Delete'
        Start-Sleep -Milliseconds 1200
        Assert-True ((Find-Element 'Edit Staging').matchCount -eq 0) 'Staging was not deleted'
        Close-WorkspaceManager

        Open-WorkspaceSettings 'Local SQL Server'
        Invoke-Element 'Delete workspace'
        Wait-ForText 'Delete workspace?'
        Click-Element 'Delete'
        Start-Sleep -Milliseconds 1500
        Click-Element 'Back'
        Wait-ForText 'Welcome to Larik SQL Studio' 10

        Assert-True ((Find-Element 'Welcome to Larik SQL Studio').matchCount -gt 0) 'the empty onboarding surface did not return'
        Assert-True ((Find-Element 'Create your first workspace').matchCount -gt 0) 'onboarding lost its way to start'
        Assert-True ((Find-Element 'SQL editor').matchCount -eq 0) 'the editor stayed visible with no workspace'
    }

    $shot = Join-Path $ArtifactDirectory 'larik-shell.png'
    Invoke-Ui @('screenshot', '--output', $shot) | Out-Null
    $screenshots.Add($shot)
}
finally {
    $ran = @($results | Where-Object { $_.Status -in @('PASS', 'FAIL') }).Count
    $passed = @($results | Where-Object Status -eq 'PASS').Count
    $failed = @($results | Where-Object Status -eq 'FAIL').Count
    $cached = @($results | Where-Object Status -eq 'CACHED').Count
    if ($Through) {
        Write-Host "Ran $ran of $($results.Count) checks through '$Through' (-Through): $passed passed, $failed failed, $cached cached."
    }
    elseif ($From) {
        Write-Host "Ran $ran checks from '$From': $passed passed, $failed failed, $cached cached."
    }

    $results | Format-Table -AutoSize | Out-String | Write-Host
    if ($failed -gt 0) {
        $results | Where-Object Status -eq 'FAIL' | ForEach-Object { Write-Host ("FAIL: " + $_.Name + " :: " + $_.Detail) }
    }
    $screenshots | ForEach-Object { Write-Host "screenshot: $_" }
    $resultsPath = Save-TestResults
    Write-Host "results: $resultsPath (fingerprint $script:Fingerprint)"

    # The state written during this run is test data: stop the app, drop it, restore the tester's.
    if ($LaunchedHere -and $AppPid -gt 0) {
        Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    Restore-StudioState
}

# Explicit exit codes so a targeted run can be used in a script: 0 green, 1 failed, 2 a typo.
if ($Through -and -not $script:ThroughReached) {
    Write-Host "No check name matched -Through '$Through'; nothing was verified."
    exit 2
}

if ($From -and -not $script:FromReached) {
    Write-Host "No check name matched -From '$From'; nothing was verified."
    exit 2
}

exit @($results | Where-Object Status -eq 'FAIL').Count
