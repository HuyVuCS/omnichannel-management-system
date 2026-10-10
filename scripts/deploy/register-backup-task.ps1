# Registers (or removes) the Windows scheduled task that runs backup-prod.ps1 every day for the current user.
# Windows PowerShell 5.1 compatible, ASCII only. docs/runbook.md "Backup".
#
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\register-backup-task.ps1 [-At 03:00] [-Destination <folder>]
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\register-backup-task.ps1 -Unregister
#
# The task runs only while this user is logged on: Docker Desktop runs in the user's session. A start that was missed
# (machine off or asleep at that time) is made up as soon as possible. No password is stored with the task.
param(
    [string]$At = '03:00',
    [string]$Destination,
    [string]$ProjectName = 'omnichannel-prod',
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$taskName = "Omnichannel backup ($ProjectName)"

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Removed the scheduled task '$taskName'."
    } else { Write-Host "There is no scheduled task '$taskName'." }
    exit 0
}

if ($At -notmatch '^([01]\d|2[0-3]):[0-5]\d$') { throw '-At must be a time of day as HH:mm.' }
$script = Join-Path $PSScriptRoot 'backup-prod.ps1'
$arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -ProjectName $ProjectName"
if ($Destination) {
    if (-not (Test-Path $Destination)) { [void](New-Item -ItemType Directory -Path $Destination) }
    $arguments += " -Destination `"$((Resolve-Path $Destination).Path)`""
}

$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact($At, 'HH:mm', [System.Globalization.CultureInfo]::InvariantCulture))
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
[void](Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force `
        -Description 'Daily backup of the Omnichannel production stack (scripts/deploy/backup-prod.ps1).')
Write-Host "Registered '$taskName': every day at $At for $user, only while logged on."
Write-Host 'Result of each run: backup.log in the destination folder.'
