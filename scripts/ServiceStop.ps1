<#
    Stopping the service so its files can be replaced or deleted.

    ONE RULE, TWO CALLERS, AND IT WAS ENFORCED IN ONE OF THEM. `install.ps1`
    carried this wait and its explanation since the self-update work;
    `uninstall.ps1` called `Stop-Service`, waited on the SCM, and deleted
    immediately. So an uninstall of a RUNNING install threw part-way through the
    delete with the service already deregistered, the install root half gone, the
    event source still registered, and the "your data was left in place" message
    -- the whole reassurance the script exists to give -- never printed (`T243`).

    The behaviour is Windows's, not this project's: "Stopped" is the service
    control manager's opinion and it arrives before the process has necessarily
    exited. Windows keeps a process's mapped images until it really goes, so
    anything that starts on the SCM's word finds the executable and its DLLs
    still locked. Measured at 0.03s to ~8.8s on real stops (`T82`, `T167`).

    Dot-sourced rather than copied, and it ships in the release archive beside
    both scripts -- see `ReleaseContents.ps1`, which fails the release if it is
    missing. A copy in each script is the shape this codebase keeps paying for.
#>

Set-StrictMode -Version Latest

<#
    .SYNOPSIS
    Stops a service and waits for its PROCESS to exit, not merely for the SCM.

    .DESCRIPTION
    Returns $true when the process is known to have gone, and $false when the
    wait expired or the process could not be identified. A caller that is about
    to delete files wants that distinction: $false does not mean the stop failed,
    it means the caller cannot assume the files are free.

    THE RETURN VALUE HAS TO BE READ. Ignoring it puts the caller back where
    `T243` was.

    .PARAMETER ServiceName
    The Windows service to stop.

    .PARAMETER TimeoutSeconds
    How long to wait for the process after the SCM reports STOPPED. The default
    is generous against a measured worst case of ~8.8s; expiry is reported, never
    thrown, because a service that is slow to exit is not a reason to abandon an
    uninstall half-way.
#>
function Stop-SentryServiceAndWait {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ServiceName,
        [int] $TimeoutSeconds = 30
    )

    # Captured BEFORE the stop, because once it is stopped there is nothing left
    # to ask. This ordering is the whole of it.
    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue).ProcessId

    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))

    if (-not $servicePid) {
        # Already stopped when we looked, or WMI would not say. Either way this
        # function cannot claim the files are free.
        return $false
    }

    $serviceProcess = Get-Process -Id $servicePid -ErrorAction SilentlyContinue

    if (-not $serviceProcess) {
        # Gone between the CIM read and here, which is the common fast case.
        return $true
    }

    return [bool] $serviceProcess.WaitForExit($TimeoutSeconds * 1000)
}
