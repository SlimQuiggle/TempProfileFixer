[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Import-Module (Join-Path $repoRoot 'src\TempProfileFixer.Core.psm1') -Force

$script:Passed = 0
$script:Failed = 0

function Assert-Equal {
    param(
        [object]$Actual,
        [object]$Expected,
        [string]$Message
    )

    if ($Actual -ne $Expected) {
        $script:Failed++
        Write-Host "FAIL: $Message Expected '$Expected' but got '$Actual'." -ForegroundColor Red
        return
    }

    $script:Passed++
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        $script:Failed++
        Write-Host "FAIL: $Message Expected true." -ForegroundColor Red
        return
    }

    $script:Passed++
}

function Assert-False {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if ($Condition) {
        $script:Failed++
        Write-Host "FAIL: $Message Expected false." -ForegroundColor Red
        return
    }

    $script:Passed++
}

function New-FolderMock {
    param([string]$FolderName, [string]$Path)
    [pscustomobject]@{
        Name           = $FolderName
        FullName       = $Path
        NormalizedPath = ConvertTo-TempProfileFixerNormalizedPath -Path $Path
    }
}

function New-EntryMock {
    param([string]$KeyName, [string]$ProfileImagePath)
    $baseSid = Get-TempProfileFixerBaseSid -SidOrKeyName $KeyName
    [pscustomobject]@{
        KeyName               = $KeyName
        BaseSid               = $baseSid
        IsBak                 = $KeyName.EndsWith('.bak', [StringComparison]::OrdinalIgnoreCase)
        ProfileImagePath      = $ProfileImagePath
        NormalizedProfilePath = ConvertTo-TempProfileFixerNormalizedPath -Path $ProfileImagePath
    }
}

$sid = 'S-1-5-21-100-200-300-1001'
$sid2 = 'S-1-5-21-100-200-300-1002'

Assert-Equal `
    -Actual (Get-TempProfileFixerBaseSid -SidOrKeyName "$sid.bak") `
    -Expected $sid `
    -Message 'Base SID strips .bak suffix.'

Assert-Equal `
    -Actual (Get-TempProfileFixerBaseSid -SidOrKeyName $sid) `
    -Expected $sid `
    -Message 'Base SID leaves normal SID unchanged.'

$normalized = ConvertTo-TempProfileFixerNormalizedPath -Path 'C:/Users/Alice/'
Assert-Equal -Actual $normalized -Expected 'C:\USERS\ALICE' -Message 'Path normalization uppercases and trims slash.'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "TempProfileFixerTests-$PID"
try {
    $alicePath = Join-Path $testRoot 'Alice'
    $bobPath = Join-Path $testRoot 'Bob'
    New-Item -Path $alicePath -ItemType Directory -Force | Out-Null
    New-Item -Path $bobPath -ItemType Directory -Force | Out-Null

    $folders = @(
        (New-FolderMock -FolderName 'Alice' -Path $alicePath),
        (New-FolderMock -FolderName 'Bob' -Path $bobPath)
    )
    $entries = @(
        (New-EntryMock -KeyName $sid -ProfileImagePath $alicePath),
        (New-EntryMock -KeyName "$sid.bak" -ProfileImagePath $alicePath),
        (New-EntryMock -KeyName $sid2 -ProfileImagePath $bobPath)
    )
    $states = @(
        [pscustomobject]@{
            Sid            = $sid
            LocalPath      = $alicePath
            NormalizedPath = ConvertTo-TempProfileFixerNormalizedPath -Path $alicePath
            Loaded         = $false
            Special        = $false
        },
        [pscustomobject]@{
            Sid            = $sid2
            LocalPath      = $bobPath
            NormalizedPath = ConvertTo-TempProfileFixerNormalizedPath -Path $bobPath
            Loaded         = $true
            Special        = $false
        }
    )

    $inventory = @(New-TempProfileFixerInventory `
            -Folders $folders `
            -ProfileListEntries $entries `
            -UserProfileStates $states `
            -LocalAdministratorSids @() `
            -CurrentSid 'S-1-5-21-100-200-300-9999')

    $alice = $inventory | Where-Object FolderName -eq 'Alice'
    $bob = $inventory | Where-Object FolderName -eq 'Bob'

    Assert-Equal -Actual $alice.BaseSid -Expected $sid -Message 'Inventory relates folder to base SID.'
    Assert-True -Condition $alice.NormalKeyPresent -Message 'Inventory detects normal key.'
    Assert-True -Condition $alice.BakKeyPresent -Message 'Inventory detects .bak key.'
    Assert-False -Condition $alice.IsBlocked -Message 'Ready profile is not blocked.'

    Assert-True -Condition $bob.Loaded -Message 'Inventory reads loaded state.'
    Assert-True -Condition $bob.IsBlocked -Message 'Loaded profile is blocked.'

    $adminInventory = @(New-TempProfileFixerInventory `
            -Folders @($folders[0]) `
            -ProfileListEntries $entries `
            -UserProfileStates @($states[0]) `
            -LocalAdministratorSids @($sid) `
            -CurrentSid 'S-1-5-21-100-200-300-9999')
    Assert-True -Condition $adminInventory[0].IsBlocked -Message 'Local admin profile is blocked.'

    $currentInventory = @(New-TempProfileFixerInventory `
            -Folders @($folders[0]) `
            -ProfileListEntries $entries `
            -UserProfileStates @($states[0]) `
            -LocalAdministratorSids @() `
            -CurrentSid $sid)
    Assert-True -Condition $currentInventory[0].IsBlocked -Message 'Current user profile is blocked.'

    $ambiguousEntries = @(
        (New-EntryMock -KeyName $sid -ProfileImagePath $alicePath),
        (New-EntryMock -KeyName $sid2 -ProfileImagePath $alicePath)
    )
    $ambiguousInventory = @(New-TempProfileFixerInventory `
            -Folders @($folders[0]) `
            -ProfileListEntries $ambiguousEntries `
            -UserProfileStates @() `
            -LocalAdministratorSids @() `
            -CurrentSid 'S-1-5-21-100-200-300-9999')
    Assert-True -Condition $ambiguousInventory[0].IsBlocked -Message 'Ambiguous SID match is blocked.'

    $tempPath = Join-Path $testRoot 'Alice.Temp'
    New-Item -Path $tempPath -ItemType Directory -Force | Out-Null
    $existingBakEntries = @(
        (New-EntryMock -KeyName $sid -ProfileImagePath $tempPath),
        (New-EntryMock -KeyName "$sid.bak" -ProfileImagePath $alicePath)
    )
    $existingBakInventory = @(New-TempProfileFixerInventory `
            -Folders @($folders[0]) `
            -ProfileListEntries $existingBakEntries `
            -UserProfileStates @() `
            -LocalAdministratorSids @() `
            -CurrentSid 'S-1-5-21-100-200-300-9999')
    Assert-False -Condition $existingBakInventory[0].IsBlocked -Message 'Existing .bak profile state can be repaired from the original folder row.'
    Assert-True -Condition (@($existingBakInventory[0].Warnings) -contains 'Normal ProfileList key points to a different path') -Message 'Existing temp profile state warns when normal key points elsewhere.'

    $plan = New-TempProfileFixerRebuildPlan -Profile $alice -Now ([datetime]'2026-06-03T10:00:00')
    Assert-Equal -Actual $plan.ProfilePath -Expected $alicePath -Message 'Plan keeps source profile path.'
    Assert-Equal -Actual $plan.RegistryKeyNames.Count -Expected 2 -Message 'Plan includes normal and .bak keys.'
    Assert-False -Condition $plan.IsBlocked -Message 'Plan inherits unblocked state.'

    $plainOldPath = Get-TempProfileFixerUniqueOldPath -ProfilePath $bobPath -Now ([datetime]'2026-06-03T10:00:00')
    Assert-Equal -Actual $plainOldPath -Expected "$bobPath.old" -Message 'Unique old path uses .old when available.'

    New-Item -Path "$bobPath.old" -ItemType Directory -Force | Out-Null
    $timestampedOldPath = Get-TempProfileFixerUniqueOldPath -ProfilePath $bobPath -Now ([datetime]'2026-06-03T10:00:00')
    Assert-Equal -Actual $timestampedOldPath -Expected "$bobPath.old.20260603-100000" -Message 'Unique old path uses timestamp when .old exists.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host "Passed: $script:Passed"
Write-Host "Failed: $script:Failed"

if ($script:Failed -gt 0) {
    exit 1
}
