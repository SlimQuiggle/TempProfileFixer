Set-StrictMode -Version 2.0

$script:ProfileListRegistryPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
$script:ProfileListRegExePath = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'

function Test-TempProfileFixerAdministrator {
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-TempProfileFixerCurrentSid {
    [CmdletBinding()]
    param()

    return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
}

function ConvertTo-TempProfileFixerNormalizedPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim())
    $expanded = $expanded -replace '/', '\'

    try {
        $expanded = [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        # Keep the expanded value; some registry paths can be malformed and should
        # still be displayed to the admin instead of crashing inventory.
    }

    return $expanded.TrimEnd('\').ToUpperInvariant()
}

function Get-TempProfileFixerBaseSid {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SidOrKeyName
    )

    if ($SidOrKeyName.EndsWith('.bak', [StringComparison]::OrdinalIgnoreCase)) {
        return $SidOrKeyName.Substring(0, $SidOrKeyName.Length - 4)
    }

    return $SidOrKeyName
}

function Get-TempProfileFixerObjectPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-TempProfileFixerDefaultExcludedFolderNames {
    [CmdletBinding()]
    param()

    return @(
        'All Users',
        'Default',
        'Default User',
        'defaultuser0',
        'LocalService',
        'NetworkService',
        'Public'
    )
}

function Get-TempProfileFixerProfileFolders {
    [CmdletBinding()]
    param(
        [string]$UsersRoot = "$env:SystemDrive\Users",
        [string[]]$ExcludedNames = (Get-TempProfileFixerDefaultExcludedFolderNames)
    )

    if (-not (Test-Path -LiteralPath $UsersRoot -PathType Container)) {
        throw "Users root '$UsersRoot' was not found."
    }

    $excluded = @{}
    foreach ($name in $ExcludedNames) {
        $excluded[$name.ToUpperInvariant()] = $true
    }

    Get-ChildItem -LiteralPath $UsersRoot -Directory -Force |
        Where-Object {
            -not $excluded.ContainsKey($_.Name.ToUpperInvariant()) -and
            $_.Name -notmatch '\.old(\.\d{8}-\d{6}(\.\d+)?)?$'
        } |
        Sort-Object Name |
        ForEach-Object {
            [pscustomobject]@{
                Name           = $_.Name
                FullName       = $_.FullName
                NormalizedPath = ConvertTo-TempProfileFixerNormalizedPath -Path $_.FullName
            }
        }
}

function Get-TempProfileFixerProfileListEntries {
    [CmdletBinding()]
    param(
        [string]$RegistryPath = $script:ProfileListRegistryPath
    )

    if (-not (Test-Path -LiteralPath $RegistryPath)) {
        throw "ProfileList registry path '$RegistryPath' was not found."
    }

    Get-ChildItem -LiteralPath $RegistryPath -ErrorAction Stop |
        Sort-Object PSChildName |
        ForEach-Object {
            $keyName = $_.PSChildName
            $props = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction Stop
            $profileImagePath = [string](Get-TempProfileFixerObjectPropertyValue -InputObject $props -Name 'ProfileImagePath')
            $isBak = $keyName.EndsWith('.bak', [StringComparison]::OrdinalIgnoreCase)
            $baseSid = Get-TempProfileFixerBaseSid -SidOrKeyName $keyName

            [pscustomobject]@{
                KeyName                  = $keyName
                BaseSid                  = $baseSid
                IsBak                    = $isBak
                RegistryPath             = Join-Path $RegistryPath $keyName
                RegExePath               = "$script:ProfileListRegExePath\$keyName"
                ProfileImagePath         = $profileImagePath
                NormalizedProfilePath    = ConvertTo-TempProfileFixerNormalizedPath -Path $profileImagePath
                State                    = Get-TempProfileFixerObjectPropertyValue -InputObject $props -Name 'State'
                RefCount                 = Get-TempProfileFixerObjectPropertyValue -InputObject $props -Name 'RefCount'
                ProfileLoadTimeLow       = Get-TempProfileFixerObjectPropertyValue -InputObject $props -Name 'ProfileLoadTimeLow'
                ProfileLoadTimeHigh      = Get-TempProfileFixerObjectPropertyValue -InputObject $props -Name 'ProfileLoadTimeHigh'
            }
        }
}

function Get-TempProfileFixerUserProfileStates {
    [CmdletBinding()]
    param()

    try {
        $profiles = Get-CimInstance -ClassName Win32_UserProfile -ErrorAction Stop
    }
    catch {
        $profiles = Get-WmiObject -Class Win32_UserProfile -ErrorAction Stop
    }

    $profiles | ForEach-Object {
        [pscustomobject]@{
            Sid            = [string]$_.SID
            LocalPath      = [string]$_.LocalPath
            NormalizedPath = ConvertTo-TempProfileFixerNormalizedPath -Path ([string]$_.LocalPath)
            Loaded         = [bool]$_.Loaded
            Special        = [bool]$_.Special
        }
    }
}

function Get-TempProfileFixerLocalAdministratorSids {
    [CmdletBinding()]
    param()

    $sids = New-Object System.Collections.Generic.List[string]

    try {
        $group = [ADSI]'WinNT://./Administrators,group'
        $members = @($group.psbase.Invoke('Members'))
        foreach ($member in $members) {
            $memberDirectoryEntry = [ADSI]$member
            $adsPath = [string]$memberDirectoryEntry.psbase.Path
            $parts = $adsPath -replace '^WinNT://', ''
            $parts = $parts -split '/'
            if ($parts.Count -lt 2) {
                continue
            }

            $accountName = '{0}\{1}' -f $parts[$parts.Count - 2], $parts[$parts.Count - 1]
            try {
                $sid = (New-Object Security.Principal.NTAccount($accountName)).
                    Translate([Security.Principal.SecurityIdentifier]).Value
                if (-not $sids.Contains($sid)) {
                    $sids.Add($sid)
                }
            }
            catch {
                # Domain or nested group entries might not translate while offline.
            }
        }
    }
    catch {
        # Membership detection is best-effort. The UI still blocks current and
        # loaded profiles even when local admin detection is unavailable.
    }

    return @($sids)
}

function New-TempProfileFixerInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Folders,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$ProfileListEntries,

        [object[]]$UserProfileStates = @(),

        [string[]]$LocalAdministratorSids = @(),

        [string]$CurrentSid = (Get-TempProfileFixerCurrentSid)
    )

    $statesBySid = @{}
    foreach ($state in $UserProfileStates) {
        if (-not [string]::IsNullOrWhiteSpace([string]$state.Sid)) {
            $statesBySid[[string]$state.Sid] = $state
        }
    }

    $statesByPath = @{}
    foreach ($state in $UserProfileStates) {
        $normalized = [string]$state.NormalizedPath
        if (-not [string]::IsNullOrWhiteSpace($normalized)) {
            if (-not $statesByPath.ContainsKey($normalized)) {
                $statesByPath[$normalized] = @()
            }
            $statesByPath[$normalized] = @($statesByPath[$normalized]) + $state
        }
    }

    $adminSidLookup = @{}
    foreach ($sid in $LocalAdministratorSids) {
        if (-not [string]::IsNullOrWhiteSpace($sid)) {
            $adminSidLookup[$sid] = $true
        }
    }

    foreach ($folder in $Folders) {
        $folderPath = [string]$folder.FullName
        $normalizedPath = ConvertTo-TempProfileFixerNormalizedPath -Path $folderPath
        $matchingEntries = @($ProfileListEntries | Where-Object {
                [string]$_.NormalizedProfilePath -eq $normalizedPath
            })

        $baseSids = @($matchingEntries | Select-Object -ExpandProperty BaseSid -Unique)
        $baseSid = ''
        if ($baseSids.Count -eq 1) {
            $baseSid = [string]$baseSids[0]
        }

        $normalEntries = @()
        $bakEntries = @()
        if (-not [string]::IsNullOrWhiteSpace($baseSid)) {
            $normalEntries = @($ProfileListEntries | Where-Object {
                    $_.BaseSid -eq $baseSid -and -not $_.IsBak
                })
            $bakEntries = @($ProfileListEntries | Where-Object {
                    $_.BaseSid -eq $baseSid -and $_.IsBak
                })
        }

        $state = $null
        if (-not [string]::IsNullOrWhiteSpace($baseSid) -and $statesBySid.ContainsKey($baseSid)) {
            $state = $statesBySid[$baseSid]
        }
        elseif ($statesByPath.ContainsKey($normalizedPath) -and @($statesByPath[$normalizedPath]).Count -eq 1) {
            $state = @($statesByPath[$normalizedPath])[0]
        }

        $loaded = $false
        $special = $false
        if ($null -ne $state) {
            $loaded = [bool]$state.Loaded
            $special = [bool]$state.Special
        }

        $reasons = New-Object System.Collections.Generic.List[string]
        $warnings = New-Object System.Collections.Generic.List[string]

        if (-not (Test-Path -LiteralPath $folderPath -PathType Container)) {
            $reasons.Add('Folder missing')
        }
        if ($baseSids.Count -eq 0) {
            $reasons.Add('No matching ProfileList SID')
        }
        elseif ($baseSids.Count -gt 1) {
            $reasons.Add('Multiple SIDs match this folder')
        }
        if (-not [string]::IsNullOrWhiteSpace($baseSid) -and $baseSid -eq $CurrentSid) {
            $reasons.Add('Current admin profile')
        }
        if ($loaded) {
            $reasons.Add('Profile is loaded')
        }
        if ($special) {
            $reasons.Add('Special/system profile')
        }
        if (-not [string]::IsNullOrWhiteSpace($baseSid) -and $adminSidLookup.ContainsKey($baseSid)) {
            $reasons.Add('Local administrator profile')
        }
        if ($normalEntries.Count -gt 1) {
            $reasons.Add('Multiple normal ProfileList keys')
        }
        if ($bakEntries.Count -gt 1) {
            $warnings.Add('Multiple .bak ProfileList keys will be removed')
        }
        if ($normalEntries.Count -eq 0 -and $bakEntries.Count -gt 0) {
            $warnings.Add('Only .bak key is present')
        }
        if ($normalEntries.Count -gt 0) {
            $normalPathMatches = @($normalEntries | Where-Object {
                    [string]$_.NormalizedProfilePath -eq $normalizedPath
                })
            if ($normalPathMatches.Count -eq 0) {
                $warnings.Add('Normal ProfileList key points to a different path')
            }
        }
        if ($bakEntries.Count -gt 0) {
            $bakPathMatches = @($bakEntries | Where-Object {
                    [string]$_.NormalizedProfilePath -eq $normalizedPath
                })
            if ($bakPathMatches.Count -eq 0) {
                $warnings.Add('.bak ProfileList key points to a different path')
            }
        }

        $status = 'Ready'
        if ($reasons.Count -gt 0) {
            $status = 'Blocked: ' + ($reasons -join '; ')
        }
        elseif ($warnings.Count -gt 0) {
            $status = 'Warning: ' + ($warnings -join '; ')
        }

        [pscustomobject]@{
            FolderName         = [string]$folder.Name
            ProfilePath        = $folderPath
            NormalizedPath     = $normalizedPath
            BaseSid            = $baseSid
            MatchingSidCount   = $baseSids.Count
            NormalKeyNames     = @($normalEntries | Select-Object -ExpandProperty KeyName)
            BakKeyNames        = @($bakEntries | Select-Object -ExpandProperty KeyName)
            NormalKeyPresent   = $normalEntries.Count -gt 0
            BakKeyPresent      = $bakEntries.Count -gt 0
            Loaded             = $loaded
            Special            = $special
            LocalAdministrator = (-not [string]::IsNullOrWhiteSpace($baseSid) -and $adminSidLookup.ContainsKey($baseSid))
            IsBlocked          = $reasons.Count -gt 0
            BlockReasons       = @($reasons)
            Warnings           = @($warnings)
            Status             = $status
        }
    }
}

function Get-TempProfileFixerProfiles {
    [CmdletBinding()]
    param(
        [string]$UsersRoot = "$env:SystemDrive\Users"
    )

    $folders = @(Get-TempProfileFixerProfileFolders -UsersRoot $UsersRoot)
    $entries = @(Get-TempProfileFixerProfileListEntries)
    $states = @(Get-TempProfileFixerUserProfileStates)
    $adminSids = @(Get-TempProfileFixerLocalAdministratorSids)
    $currentSid = Get-TempProfileFixerCurrentSid

    New-TempProfileFixerInventory `
        -Folders $folders `
        -ProfileListEntries $entries `
        -UserProfileStates $states `
        -LocalAdministratorSids $adminSids `
        -CurrentSid $currentSid
}

function Get-TempProfileFixerUniqueOldPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProfilePath,

        [datetime]$Now = (Get-Date)
    )

    $baseOldPath = "$ProfilePath.old"
    if (-not (Test-Path -LiteralPath $baseOldPath)) {
        return $baseOldPath
    }

    $timestamp = $Now.ToString('yyyyMMdd-HHmmss')
    $timestampedPath = "$ProfilePath.old.$timestamp"
    if (-not (Test-Path -LiteralPath $timestampedPath)) {
        return $timestampedPath
    }

    for ($i = 2; $i -lt 1000; $i++) {
        $candidate = "$timestampedPath.$i"
        if (-not (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Could not find an available .old path for '$ProfilePath'."
}

function New-TempProfileFixerRebuildPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Profile,

        [datetime]$Now = (Get-Date)
    )

    $renameTo = Get-TempProfileFixerUniqueOldPath -ProfilePath ([string]$Profile.ProfilePath) -Now $Now
    $keysToRemove = @()
    foreach ($key in @(@($Profile.NormalKeyNames) + @($Profile.BakKeyNames))) {
        if (-not [string]::IsNullOrWhiteSpace([string]$key)) {
            $keysToRemove += [string]$key
        }
    }

    [pscustomobject]@{
        FolderName       = [string]$Profile.FolderName
        ProfilePath      = [string]$Profile.ProfilePath
        RenameTo         = $renameTo
        BaseSid          = [string]$Profile.BaseSid
        RegistryKeyNames = @($keysToRemove | Select-Object -Unique)
        IsBlocked        = [bool]$Profile.IsBlocked
        BlockReasons     = @($Profile.BlockReasons)
        Warnings         = @($Profile.Warnings)
        Summary          = if ([bool]$Profile.IsBlocked) {
            'Blocked: ' + (@($Profile.BlockReasons) -join '; ')
        }
        else {
            "Rename '$($Profile.ProfilePath)' to '$renameTo' and remove ProfileList keys: $((@($keysToRemove | Select-Object -Unique)) -join ', ')"
        }
    }
}

function Write-TempProfileFixerLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$LogPath,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $timestamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Add-Content -LiteralPath $LogPath -Value "[$timestamp] $Message" -Encoding UTF8
}

function Export-TempProfileFixerRegistryKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$KeyName,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $regExePath = "$script:ProfileListRegExePath\$KeyName"
    $arguments = @('export', $regExePath, $DestinationPath, '/y')
    $process = Start-Process -FilePath "$env:SystemRoot\System32\reg.exe" -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "reg.exe export failed for '$regExePath' with exit code $($process.ExitCode)."
    }
}

function Remove-TempProfileFixerRegistryKey {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$KeyName
    )

    $path = Join-Path $script:ProfileListRegistryPath $KeyName
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
    }
}

function Invoke-TempProfileFixerRebuild {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProfilePath,

        [string]$UsersRoot = "$env:SystemDrive\Users",

        [string]$BackupRoot = (Join-Path (Get-Location) 'backups'),

        [string]$LogRoot = (Join-Path (Get-Location) 'logs')
    )

    $normalizedTarget = ConvertTo-TempProfileFixerNormalizedPath -Path $ProfilePath
    $profiles = @(Get-TempProfileFixerProfiles -UsersRoot $UsersRoot)
    $matches = @($profiles | Where-Object { $_.NormalizedPath -eq $normalizedTarget })

    if ($matches.Count -ne 1) {
        throw "Expected exactly one profile match for '$ProfilePath'; found $($matches.Count)."
    }

    $profile = $matches[0]
    $plan = New-TempProfileFixerRebuildPlan -Profile $profile

    if ($plan.IsBlocked) {
        throw "Profile rebuild is blocked: $($plan.BlockReasons -join '; ')"
    }

    New-Item -Path $BackupRoot -ItemType Directory -Force | Out-Null
    New-Item -Path $LogRoot -ItemType Directory -Force | Out-Null

    $runId = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $safeFolderName = $profile.FolderName -replace '[^A-Za-z0-9_.-]', '_'
    $backupDirectory = Join-Path $BackupRoot "$runId-$safeFolderName"
    New-Item -Path $backupDirectory -ItemType Directory -Force | Out-Null

    $logPath = Join-Path $LogRoot "$runId-$safeFolderName.log"
    Write-TempProfileFixerLog -LogPath $logPath -Message "Starting rebuild for $($profile.ProfilePath)"
    Write-TempProfileFixerLog -LogPath $logPath -Message "Base SID: $($profile.BaseSid)"
    Write-TempProfileFixerLog -LogPath $logPath -Message "Rename target: $($plan.RenameTo)"

    try {
        foreach ($keyName in $plan.RegistryKeyNames) {
            $destination = Join-Path $backupDirectory "$keyName.reg"
            Write-TempProfileFixerLog -LogPath $logPath -Message "Exporting ProfileList key $keyName to $destination"
            if ($PSCmdlet.ShouldProcess($keyName, 'Export registry key')) {
                Export-TempProfileFixerRegistryKey -KeyName $keyName -DestinationPath $destination
            }
        }

        Write-TempProfileFixerLog -LogPath $logPath -Message "Renaming $($plan.ProfilePath) to $($plan.RenameTo)"
        if ($PSCmdlet.ShouldProcess($plan.ProfilePath, "Rename to $($plan.RenameTo)")) {
            Rename-Item -LiteralPath $plan.ProfilePath -NewName (Split-Path -Leaf $plan.RenameTo) -ErrorAction Stop
        }

        foreach ($keyName in $plan.RegistryKeyNames) {
            Write-TempProfileFixerLog -LogPath $logPath -Message "Removing ProfileList key $keyName"
            if ($PSCmdlet.ShouldProcess($keyName, 'Remove registry key')) {
                Remove-TempProfileFixerRegistryKey -KeyName $keyName
            }
        }

        Write-TempProfileFixerLog -LogPath $logPath -Message 'Rebuild completed successfully.'

        [pscustomobject]@{
            ProfilePath     = $plan.ProfilePath
            RenamedTo       = $plan.RenameTo
            RemovedKeys     = @($plan.RegistryKeyNames)
            BackupDirectory = $backupDirectory
            LogPath         = $logPath
            Success         = $true
        }
    }
    catch {
        Write-TempProfileFixerLog -LogPath $logPath -Message "FAILED: $($_.Exception.Message)"
        throw
    }
}

Export-ModuleMember -Function @(
    'Test-TempProfileFixerAdministrator',
    'Get-TempProfileFixerCurrentSid',
    'ConvertTo-TempProfileFixerNormalizedPath',
    'Get-TempProfileFixerBaseSid',
    'Get-TempProfileFixerDefaultExcludedFolderNames',
    'Get-TempProfileFixerProfileFolders',
    'Get-TempProfileFixerProfileListEntries',
    'Get-TempProfileFixerUserProfileStates',
    'Get-TempProfileFixerLocalAdministratorSids',
    'New-TempProfileFixerInventory',
    'Get-TempProfileFixerProfiles',
    'Get-TempProfileFixerUniqueOldPath',
    'New-TempProfileFixerRebuildPlan',
    'Invoke-TempProfileFixerRebuild'
)
