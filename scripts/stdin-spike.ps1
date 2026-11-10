<#
.SYNOPSIS
    Proves that bedrock_server.exe can be driven over redirected stdin/stdout.

.DESCRIPTION
    Milestone 4 builds a process supervisor on one assumption: that BDS, launched
    with no console of its own, still reads commands from a redirected stdin and
    still writes its replies promptly to a redirected stdout. Backups
    (`save hold` / `save query` / `save resume`), chat command replies, scheduled
    restarts and allowlist edits are all that same operation. If it misbehaves,
    it is much cheaper to find out here than after the supervisor is built around
    it.

    The classic failure is not "no output" but *late* output: a program whose C
    runtime switches stdout from line-buffered to 4 KB block-buffered the moment
    it detects it is not attached to a terminal. Replies then sit in the pipe
    until enough text accumulates. That looks like a hang, and no amount of
    supervisor code fixes it. So this script measures the round-trip latency of
    every command rather than only whether a reply eventually turned up.

    It exercises the real backup handshake, not a synthetic one, and it sends
    `save resume` from a finally block — the same invariant the production code
    has to hold. A spike that leaves a world with saves held would be teaching
    the wrong habit on its first day.

    -AsSystem re-runs the whole thing as a scheduled task under the SYSTEM
    account, which is session 0 and non-interactive: the session the Windows
    Service launches BDS in. The service starts each server under a restricted
    copy of its own account, which this run does not reproduce. Running it only
    from an interactive admin console proves less than it appears to.

.PARAMETER ServerPath
    The BDS folder, or bedrock_server.exe itself. The folder is used as the
    working directory, exactly as an instance will.

.PARAMETER ReadyTimeoutSeconds
    How long to wait for "Server started". First launch generates a world and is
    much slower than later ones.

.PARAMETER StopTimeoutSeconds
    How long a graceful `stop` may take before the spike gives up and kills the
    process. The measured value here is what Section 5.2's shutdown budget gets
    derived from.

.PARAMETER LogPath
    Full transcript, timestamped from process start. Defaults under ProgramData
    so the SYSTEM run can write to the same place.

.PARAMETER AsSystem
    Re-run under the SYSTEM account in session 0 via a temporary scheduled task,
    then print that run's transcript. Requires an elevated shell.

.EXAMPLE
    .\stdin-spike.ps1 -ServerPath C:\bds

.EXAMPLE
    .\stdin-spike.ps1 -ServerPath C:\bds -AsSystem
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ServerPath,

    [int] $ReadyTimeoutSeconds = 120,

    [int] $StopTimeoutSeconds = 60,

    [string] $LogPath,

    [switch] $AsSystem
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')

if (-not $LogPath) {
    $logDir = Join-Path $env:ProgramData 'BedrockSentry\logs'
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    $LogPath = Join-Path $logDir "stdin-spike-$stamp.log"
}
$LogPath = [System.IO.Path]::GetFullPath($LogPath)

if (-not (Test-Path $ServerPath)) {
    throw "ServerPath not found: $ServerPath"
}

$resolved = (Resolve-Path $ServerPath).Path
if (Test-Path $resolved -PathType Container) {
    $exePath = Join-Path $resolved 'bedrock_server.exe'
    $workDir = $resolved
}
else {
    $exePath = $resolved
    $workDir = Split-Path -Parent $resolved
}

if (-not (Test-Path $exePath -PathType Leaf)) {
    throw "bedrock_server.exe not found. Looked for: $exePath"
}

# ---------------------------------------------------------------------------
# -AsSystem: hand the same script to a one-shot scheduled task running as SYSTEM
# ---------------------------------------------------------------------------

if ($AsSystem) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principalCheck = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this from an elevated PowerShell. Registering a SYSTEM task needs administrator rights.'
    }

    $taskName = "BedrockSentry-StdinSpike-$stamp"
    $inner = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ServerPath "{1}" -LogPath "{2}" -ReadyTimeoutSeconds {3} -StopTimeoutSeconds {4}' -f `
        $PSCommandPath, $exePath, $LogPath, $ReadyTimeoutSeconds, $StopTimeoutSeconds

    Write-Host "Running the spike as SYSTEM (session 0) via scheduled task '$taskName'..." -ForegroundColor Cyan

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $inner
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null

    try {
        Start-ScheduledTask -TaskName $taskName

        # Give the scheduler a moment to move the task out of Ready before polling,
        # otherwise the first read looks like an instant completion.
        Start-Sleep -Seconds 2

        $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds + $StopTimeoutSeconds + 120)
        while ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
            if ((Get-Date) -gt $deadline) {
                Stop-ScheduledTask -TaskName $taskName
                throw 'The SYSTEM run did not finish in time. Transcript: ' + $LogPath
            }
            Start-Sleep -Seconds 2
        }

        $result = (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }

    Write-Host ''
    if (Test-Path $LogPath) {
        Get-Content $LogPath | ForEach-Object { Write-Host $_ }
    }
    else {
        Write-Warning "The SYSTEM run produced no transcript at $LogPath. It probably failed before opening the log."
    }

    Write-Host ''
    if ($result -eq 0) {
        Write-Host 'SYSTEM (session 0) run: PASS' -ForegroundColor Green
    }
    else {
        Write-Host "SYSTEM (session 0) run: FAIL (exit code $result)" -ForegroundColor Red
    }
    exit $result
}

# ---------------------------------------------------------------------------
# Transcript
# ---------------------------------------------------------------------------

$script:log = New-Object System.IO.StreamWriter($LogPath, $false, (New-Object System.Text.UTF8Encoding($false)))
$script:log.AutoFlush = $true
$script:started = [datetime]::UtcNow

function Write-Line {
    param([string] $Text, [string] $Colour = 'Gray')
    $ms = [int]([datetime]::UtcNow - $script:started).TotalMilliseconds
    $entry = '{0,8}ms  {1}' -f $ms, $Text
    $script:log.WriteLine($entry)
    Write-Host $entry -ForegroundColor $Colour
}

Write-Line "spike     exe=$exePath"
Write-Line "spike     cwd=$workDir"

# Who and where we are is the single most useful line in the transcript when
# comparing an interactive run against the SYSTEM one. It is also pure
# diagnostics, so it must never be the reason a run dies.
try {
    $whoami = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $session = (Get-Process -Id $PID).SessionId
    Write-Line "spike     identity=$whoami  interactive=$([Environment]::UserInteractive)  session=$session"
}
catch {
    Write-Line "spike     identity unavailable: $($_.Exception.Message)" 'Yellow'
}

# ---------------------------------------------------------------------------
# Launch
# ---------------------------------------------------------------------------

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exePath
$psi.WorkingDirectory = $workDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

# BDS emits UTF-8. Without this, .NET decodes using the console's code page
# (often 437 or 1252), which silently mangles every non-ASCII player name — the
# kind of bug that only shows up once a real player joins.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$psi.StandardOutputEncoding = $utf8
$psi.StandardErrorEncoding = $utf8

$proc = [System.Diagnostics.Process]::Start($psi)
Write-Line "spike     launched pid=$($proc.Id)" 'Cyan'

$script:outReader = $proc.StandardOutput
$script:errReader = $proc.StandardError
$script:outPending = $null
$script:errPending = $null
$script:transcript = New-Object System.Collections.Generic.List[string]

# Read with a deadline instead of async events. Register-ObjectEvent only pumps
# its actions when the main runspace goes idle, which makes "did the reply take
# 20ms or 2s" — the entire question this spike exists to answer — unmeasurable.
# A pending ReadLineAsync that we re-wait on is exact, and the C# supervisor will
# use the event model anyway; what is being proven here is BDS's behaviour, not
# PowerShell's.
function Read-Available {
    param([int] $TimeoutMs = 200)

    $lines = New-Object System.Collections.Generic.List[string]
    $deadline = [datetime]::UtcNow.AddMilliseconds($TimeoutMs)

    while ($true) {
        # Wait the full window for the *first* line, then take only what is
        # already sitting in the pipe. Blocking for the whole window regardless
        # would add up to TimeoutMs to every measurement, and the measurement is
        # the entire point of this spike.
        $remaining = if ($lines.Count -gt 0) { 0 }
                     else { [int]($deadline - [datetime]::UtcNow).TotalMilliseconds }
        if ($remaining -lt 0) { break }
        if ($remaining -eq 0 -and $lines.Count -eq 0) { break }

        # A closed stream keeps completing ReadLineAsync with null forever, so a
        # drained reader is dropped rather than re-armed. Without that, the first
        # stream to end turns this into a spin loop.
        if ($null -ne $script:outReader -and $null -eq $script:outPending) {
            $script:outPending = $script:outReader.ReadLineAsync()
        }
        if ($null -ne $script:errReader -and $null -eq $script:errPending) {
            $script:errPending = $script:errReader.ReadLineAsync()
        }

        $waiting = New-Object System.Collections.Generic.List[System.Threading.Tasks.Task]
        $sources = New-Object System.Collections.Generic.List[string]
        if ($null -ne $script:outPending) { $waiting.Add($script:outPending); $sources.Add('stdout') }
        if ($null -ne $script:errPending) { $waiting.Add($script:errPending); $sources.Add('stderr') }
        if ($waiting.Count -eq 0) { break }   # both streams are done

        # WaitAny is overloaded on Task[]; an object[] from @() does not bind.
        $index = [System.Threading.Tasks.Task]::WaitAny($waiting.ToArray(), $remaining)
        if ($index -lt 0) { break }   # nothing arrived inside the window

        $source = $sources[$index]
        if ($source -eq 'stdout') {
            $line = $script:outPending.Result
            $script:outPending = $null
            if ($null -eq $line) { $script:outReader = $null; continue }
        }
        else {
            $line = $script:errPending.Result
            $script:errPending = $null
            if ($null -eq $line) { $script:errReader = $null; continue }
        }

        Write-Line "$source    $line" $(if ($source -eq 'stderr') { 'Yellow' } else { 'Gray' })
        $lines.Add($line)
        $script:transcript.Add($line)
    }

    return $lines
}

# Waits for a line matching $Pattern, returning the elapsed milliseconds, or -1
# if the deadline passed. Elapsed time is the finding; a boolean would hide the
# buffering problem this is looking for.
#
# $RejectPattern is a reply that definitively means "not yet" — BDS answers a
# too-early `save query` that way. Returning as soon as it arrives, instead of
# sitting out the timeout, is also the algorithm milestone 7's backup path needs.
function Wait-ForLine {
    param(
        [Parameter(Mandatory = $true)][string] $Pattern,
        [string] $RejectPattern,
        [int] $TimeoutSeconds = 15
    )

    $begin = [datetime]::UtcNow
    $deadline = $begin.AddSeconds($TimeoutSeconds)

    while ([datetime]::UtcNow -lt $deadline) {
        if ($proc.HasExited) {
            Write-Line "spike     process exited while waiting for /$Pattern/" 'Red'
            return -1
        }
        foreach ($line in (Read-Available -TimeoutMs 250)) {
            if ($line -match $Pattern) {
                return [int]([datetime]::UtcNow - $begin).TotalMilliseconds
            }
            if ($RejectPattern -and $line -match $RejectPattern) {
                return -2
            }
        }
    }
    return -1
}

function Send-Command {
    param([Parameter(Mandatory = $true)][string] $Command)
    Write-Line "stdin  -> $Command" 'Cyan'
    $proc.StandardInput.WriteLine($Command)
    $proc.StandardInput.Flush()
}

# ---------------------------------------------------------------------------
# The checks
# ---------------------------------------------------------------------------

$results = [ordered]@{}
$holdIssued = $false

try {
    # 1. Readiness. Also the first proof that stdout reaches us at all.
    $readyMs = Wait-ForLine -Pattern 'Server started' -TimeoutSeconds $ReadyTimeoutSeconds
    $results['stdout is readable'] = if ($readyMs -ge 0) { "PASS  server ready in $readyMs ms" }
                                     else { 'FAIL  never saw "Server started"' }
    if ($readyMs -lt 0) { throw 'Server never reported started. Nothing further can be tested; a stdout that never arrives is exactly the block-buffering failure this spike looks for.' }

    # 2. stdin round trip. The latency here is the whole point: a slow reply
    #    means block buffering, and block buffering breaks every later feature.
    Send-Command 'list'
    $listMs = Wait-ForLine -Pattern 'players online' -TimeoutSeconds 15
    $results['stdin accepted, reply prompt'] =
        if ($listMs -lt 0) { 'FAIL  no reply to `list`' }
        elseif ($listMs -gt 2000) { "WARN  reply to `list` took $listMs ms - looks buffered" }
        else { "PASS  reply to `list` in $listMs ms" }

    # 3. The backup handshake, exactly as milestone 7 will drive it.
    Send-Command 'save hold'
    $holdIssued = $true
    $holdMs = Wait-ForLine -Pattern 'Saving|already being saved|The command is already running' -TimeoutSeconds 15
    $results['save hold acknowledged'] = if ($holdMs -ge 0) { "PASS  in $holdMs ms" } else { 'FAIL  no acknowledgement' }

    # save query answers "not completed yet" until the snapshot is ready, so the
    # real implementation polls. Proving the poll terminates matters more than
    # proving a single call works.
    $queryMs = -1
    $attempts = 0
    while ($attempts -lt 30 -and $queryMs -lt 0) {
        $attempts++
        Send-Command 'save query'
        $queryMs = Wait-ForLine -Pattern 'Data saved|files? are now ready' `
                                -RejectPattern 'previous save has not been completed' `
                                -TimeoutSeconds 3
        if ($queryMs -lt 0) { Start-Sleep -Milliseconds 500 }
    }
    $results['save query returns a manifest'] =
        if ($queryMs -ge 0) { "PASS  ready after $attempts poll(s)" }
        else { "FAIL  still not ready after $attempts polls" }

    # The manifest arrives on the line *after* "Data saved". Capturing it proves
    # we can read a multi-line reply, which single-line command tests never show.
    # Scanning only from that marker forward matters: BDS's own startup banner
    # contains lines that match any loose "path plus a number" pattern, so a
    # whole-transcript search reports a false pass.
    Read-Available -TimeoutMs 1500 | Out-Null

    $marker = -1
    for ($i = $script:transcript.Count - 1; $i -ge 0; $i--) {
        if ($script:transcript[$i] -match 'Data saved') { $marker = $i; break }
    }

    $manifest = $null
    if ($marker -ge 0) {
        for ($i = $marker + 1; $i -lt $script:transcript.Count; $i++) {
            if ($script:transcript[$i] -match '\S+:\d+(,|$)') { $manifest = $script:transcript[$i]; break }
        }
    }

    $results['manifest line captured'] =
        if ($manifest) { 'PASS  ' + $manifest.Substring(0, [Math]::Min(64, $manifest.Length)) }
        else { 'WARN  no file list seen - check the transcript for its shape' }
}
catch {
    # An abort is a result, not a crash. The check that failed is the finding, and
    # it is worth as much as a pass — but only if the report below still prints
    # and the server below still gets stopped. An unhandled throw here would
    # leave a live bedrock_server.exe with no console attached to anything.
    Write-Line "spike     ABORTED: $($_.Exception.Message)" 'Red'
    $results['run completed'] = "FAIL  aborted: $($_.Exception.Message)"
}
finally {
    # Unconditional, for the same reason production is: a world left with saves
    # held is the worst state this codebase can leave a server in, and "the spike
    # threw" is not an excuse the server understands.
    if ($holdIssued -and -not $proc.HasExited) {
        Send-Command 'save resume'
        $resumeMs = Wait-ForLine -Pattern 'resumed|not (currently )?being saved' -TimeoutSeconds 15
        $results['save resume (finally)'] = if ($resumeMs -ge 0) { "PASS  in $resumeMs ms" } else { 'FAIL  no confirmation' }
    }
}

# 4. Graceful stop. The measured duration is what Section 5.2's shutdown budget
#    has to be larger than.
if ($proc.HasExited) {
    $results['graceful stop'] = "SKIP  process had already exited (code $($proc.ExitCode))"
}
else {
    $stopBegin = [datetime]::UtcNow
    try {
        Send-Command 'stop'
    }
    catch {
        Write-Line "spike     could not write `stop` to stdin: $($_.Exception.Message)" 'Red'
    }

    while (-not $proc.HasExited -and ([datetime]::UtcNow - $stopBegin).TotalSeconds -lt $StopTimeoutSeconds) {
        Read-Available -TimeoutMs 250 | Out-Null
    }

    if ($proc.HasExited) {
        $stopMs = [int]([datetime]::UtcNow - $stopBegin).TotalMilliseconds
        $results['graceful stop'] = "PASS  exited in $stopMs ms, exit code $($proc.ExitCode)"
    }
    else {
        # Killing it is the failure being reported, not a tidy-up. Leaving a
        # server running that we have lost the ability to talk to would be worse.
        $proc.Kill()
        $results['graceful stop'] = "FAIL  still running after ${StopTimeoutSeconds}s; killed"
    }
}

# 5. Encoding. U+FFFD anywhere means the decode was wrong, which would corrupt
#    every non-ASCII player name the chat features depend on.
$mangled = $script:transcript | Where-Object { $_ -match "\uFFFD" }
$results['utf-8 decoded cleanly'] = if ($mangled) { 'FAIL  replacement characters present' } else { 'PASS' }

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------

Write-Line ''
Write-Line '=== stdin spike results ==='
$failed = 0
foreach ($key in $results.Keys) {
    $value = $results[$key]
    $colour = if ($value -like 'PASS*') { 'Green' }
              elseif ($value -like 'WARN*') { 'Yellow' }
              elseif ($value -like 'SKIP*') { 'DarkGray' }
              else { 'Red' }
    if ($value -like 'FAIL*') { $failed++ }
    Write-Line ('{0,-32} {1}' -f $key, $value) $colour
}
Write-Line ''
Write-Line "transcript: $LogPath"

$script:log.Flush()
$script:log.Dispose()

if ($failed -gt 0) {
    Write-Host "$failed check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All checks passed.' -ForegroundColor Green
exit 0
