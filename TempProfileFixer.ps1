[CmdletBinding()]
param(
    [switch]$ListOnly,
    [switch]$DryRun,
    [string]$Path,
    [string]$UsersRoot = "$env:SystemDrive\Users"
)

Set-StrictMode -Version 2.0

$script:AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $script:AppRoot 'src\TempProfileFixer.Core.psm1') -Force

function Format-ProfileListOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Profiles
    )

    $Profiles |
        Select-Object FolderName, ProfilePath, BaseSid, NormalKeyPresent, BakKeyPresent, Loaded, Special, LocalAdministrator, Status |
        Format-Table -AutoSize
}

function Require-TempProfileFixerElevation {
    [CmdletBinding()]
    param()

    if (Test-TempProfileFixerAdministrator) {
        return $true
    }

    $message = 'Temp Profile Fixer must run as administrator to rename profile folders and edit HKLM ProfileList keys.'
    if ($ListOnly -or $DryRun) {
        Write-Warning $message
        return $false
    }

    try {
        $arguments = @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', "`"$PSCommandPath`""
        )
        Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs | Out-Null
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show(
            "Elevation was not granted. $message",
            'Temp Profile Fixer',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }

    return $false
}

if ($ListOnly) {
    Format-ProfileListOutput -Profiles @(Get-TempProfileFixerProfiles -UsersRoot $UsersRoot)
    return
}

if ($DryRun) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'DryRun requires -Path C:\Users\<name>.'
    }

    $profiles = @(Get-TempProfileFixerProfiles -UsersRoot $UsersRoot)
    $normalized = ConvertTo-TempProfileFixerNormalizedPath -Path $Path
    $matches = @($profiles | Where-Object { $_.NormalizedPath -eq $normalized })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one profile match for '$Path'; found $($matches.Count)."
    }

    New-TempProfileFixerRebuildPlan -Profile $matches[0] | Format-List
    return
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

if (-not (Require-TempProfileFixerElevation)) {
    return
}

$script:Profiles = @()

function New-ProfileGridColumn {
    param(
        [string]$Name,
        [string]$HeaderText,
        [int]$Width
    )

    $column = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $column.Name = $Name
    $column.HeaderText = $HeaderText
    $column.Width = $Width
    $column.ReadOnly = $true
    return $column
}

function Get-SelectedProfile {
    if ($grid.SelectedRows.Count -lt 1) {
        return $null
    }

    return $grid.SelectedRows[0].Tag
}

function Show-TextDialog {
    param(
        [string]$Title,
        [string]$Text
    )

    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = $Title
    $dialog.StartPosition = 'CenterParent'
    $dialog.Size = New-Object System.Drawing.Size(760, 420)
    $dialog.MinimizeBox = $false
    $dialog.MaximizeBox = $true

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.Multiline = $true
    $textBox.ReadOnly = $true
    $textBox.ScrollBars = 'Both'
    $textBox.WordWrap = $false
    $textBox.Dock = 'Fill'
    $textBox.Font = New-Object System.Drawing.Font('Consolas', 10)
    $textBox.Text = $Text

    $buttonPanel = New-Object System.Windows.Forms.Panel
    $buttonPanel.Dock = 'Bottom'
    $buttonPanel.Height = 44

    $closeButton = New-Object System.Windows.Forms.Button
    $closeButton.Text = 'Close'
    $closeButton.Width = 96
    $closeButton.Height = 28
    $closeButton.Left = 646
    $closeButton.Top = 8
    $closeButton.Anchor = 'Right,Top'
    $closeButton.Add_Click({ $dialog.Close() })

    $buttonPanel.Controls.Add($closeButton)
    $dialog.Controls.Add($textBox)
    $dialog.Controls.Add($buttonPanel)
    $dialog.ShowDialog($form) | Out-Null
}

function Convert-PlanToText {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Plan
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Folder: $($Plan.FolderName)")
    $lines.Add("Profile path: $($Plan.ProfilePath)")
    $lines.Add("Rename to: $($Plan.RenameTo)")
    $lines.Add("Base SID: $($Plan.BaseSid)")
    $lines.Add("Blocked: $($Plan.IsBlocked)")
    if (@($Plan.BlockReasons).Count -gt 0) {
        $lines.Add("Block reasons: $((@($Plan.BlockReasons)) -join '; ')")
    }
    if (@($Plan.Warnings).Count -gt 0) {
        $lines.Add("Warnings: $((@($Plan.Warnings)) -join '; ')")
    }
    $lines.Add("Registry keys to export and delete:")
    foreach ($keyName in @($Plan.RegistryKeyNames)) {
        $lines.Add("  HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$keyName")
    }
    $lines.Add('')
    $lines.Add($Plan.Summary)
    return ($lines -join [Environment]::NewLine)
}

function Refresh-ProfileGrid {
    try {
        $statusLabel.Text = 'Refreshing profiles...'
        $form.Refresh()
        $script:Profiles = @(Get-TempProfileFixerProfiles -UsersRoot $UsersRoot)
        $grid.Rows.Clear()

        foreach ($profile in $script:Profiles) {
            $rowIndex = $grid.Rows.Add(
                $profile.FolderName,
                $profile.ProfilePath,
                $profile.BaseSid,
                $(if ($profile.NormalKeyPresent) { 'Yes' } else { 'No' }),
                $(if ($profile.BakKeyPresent) { 'Yes' } else { 'No' }),
                $(if ($profile.Loaded) { 'Yes' } else { 'No' }),
                $(if ($profile.Special) { 'Yes' } else { 'No' }),
                $(if ($profile.LocalAdministrator) { 'Yes' } else { 'No' }),
                $profile.Status
            )
            $row = $grid.Rows[$rowIndex]
            $row.Tag = $profile
            if ($profile.IsBlocked) {
                $row.DefaultCellStyle.BackColor = [System.Drawing.Color]::FromArgb(255, 235, 238)
            }
            elseif (@($profile.Warnings).Count -gt 0) {
                $row.DefaultCellStyle.BackColor = [System.Drawing.Color]::FromArgb(255, 249, 196)
            }
        }

        $statusLabel.Text = "Loaded $($script:Profiles.Count) profile folder(s) from $UsersRoot."
    }
    catch {
        $statusLabel.Text = 'Refresh failed.'
        [System.Windows.Forms.MessageBox]::Show(
            $_.Exception.Message,
            'Refresh failed',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Temp Profile Fixer'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(1180, 680)
$form.MinimumSize = New-Object System.Drawing.Size(980, 560)

$topPanel = New-Object System.Windows.Forms.Panel
$topPanel.Dock = 'Top'
$topPanel.Height = 66
$topPanel.Padding = New-Object System.Windows.Forms.Padding(12, 10, 12, 8)

$titleLabel = New-Object System.Windows.Forms.Label
$titleLabel.Text = 'Temp Profile Fixer'
$titleLabel.Font = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Bold)
$titleLabel.AutoSize = $true
$titleLabel.Location = New-Object System.Drawing.Point(12, 10)

$adminLabel = New-Object System.Windows.Forms.Label
$adminLabel.Text = 'Running as administrator'
$adminLabel.AutoSize = $true
$adminLabel.Location = New-Object System.Drawing.Point(14, 39)

$refreshButton = New-Object System.Windows.Forms.Button
$refreshButton.Text = 'Refresh'
$refreshButton.Width = 96
$refreshButton.Height = 30
$refreshButton.Anchor = 'Top,Right'
$refreshButton.Location = New-Object System.Drawing.Point(760, 18)
$refreshButton.Add_Click({ Refresh-ProfileGrid })

$dryRunButton = New-Object System.Windows.Forms.Button
$dryRunButton.Text = 'Dry Run'
$dryRunButton.Width = 96
$dryRunButton.Height = 30
$dryRunButton.Anchor = 'Top,Right'
$dryRunButton.Location = New-Object System.Drawing.Point(864, 18)
$dryRunButton.Add_Click({
    $selected = Get-SelectedProfile
    if ($null -eq $selected) {
        [System.Windows.Forms.MessageBox]::Show('Select a profile first.', 'Dry Run') | Out-Null
        return
    }

    $plan = New-TempProfileFixerRebuildPlan -Profile $selected
    Show-TextDialog -Title 'Dry Run' -Text (Convert-PlanToText -Plan $plan)
})

$rebuildButton = New-Object System.Windows.Forms.Button
$rebuildButton.Text = 'Rebuild Selected'
$rebuildButton.Width = 132
$rebuildButton.Height = 30
$rebuildButton.Anchor = 'Top,Right'
$rebuildButton.Location = New-Object System.Drawing.Point(968, 18)
$rebuildButton.Add_Click({
    $selected = Get-SelectedProfile
    if ($null -eq $selected) {
        [System.Windows.Forms.MessageBox]::Show('Select a profile first.', 'Rebuild Selected') | Out-Null
        return
    }

    $plan = New-TempProfileFixerRebuildPlan -Profile $selected
    if ($plan.IsBlocked) {
        [System.Windows.Forms.MessageBox]::Show(
            ($plan.BlockReasons -join [Environment]::NewLine),
            'Profile rebuild is blocked',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $confirmText = @(
        "Rebuild profile '$($plan.FolderName)'?",
        '',
        "Rename:",
        "  $($plan.ProfilePath)",
        "to:",
        "  $($plan.RenameTo)",
        '',
        'Export and delete ProfileList keys:',
        ($plan.RegistryKeyNames | ForEach-Object { "  $_" }),
        '',
        'The target user should sign in once after this completes so Windows creates a fresh profile.'
    ) -join [Environment]::NewLine

    $confirmation = [System.Windows.Forms.MessageBox]::Show(
        $confirmText,
        'Confirm profile rebuild',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2
    )

    if ($confirmation -ne [System.Windows.Forms.DialogResult]::Yes) {
        return
    }

    try {
        $statusLabel.Text = "Rebuilding $($selected.FolderName)..."
        $form.Refresh()
        $result = Invoke-TempProfileFixerRebuild `
            -ProfilePath $selected.ProfilePath `
            -UsersRoot $UsersRoot `
            -BackupRoot (Join-Path $script:AppRoot 'backups') `
            -LogRoot (Join-Path $script:AppRoot 'logs')

        $message = @(
            'Profile rebuild completed.',
            '',
            "Renamed to: $($result.RenamedTo)",
            "Backup folder: $($result.BackupDirectory)",
            "Log: $($result.LogPath)",
            '',
            'Have the target user sign in once to create the fresh profile.'
        ) -join [Environment]::NewLine

        [System.Windows.Forms.MessageBox]::Show(
            $message,
            'Rebuild completed',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
        Refresh-ProfileGrid
    }
    catch {
        $statusLabel.Text = 'Rebuild failed.'
        [System.Windows.Forms.MessageBox]::Show(
            $_.Exception.Message,
            'Rebuild failed',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
})

$topPanel.Controls.Add($titleLabel)
$topPanel.Controls.Add($adminLabel)
$topPanel.Controls.Add($refreshButton)
$topPanel.Controls.Add($dryRunButton)
$topPanel.Controls.Add($rebuildButton)

$grid = New-Object System.Windows.Forms.DataGridView
$grid.Dock = 'Fill'
$grid.AllowUserToAddRows = $false
$grid.AllowUserToDeleteRows = $false
$grid.AllowUserToResizeRows = $false
$grid.MultiSelect = $false
$grid.SelectionMode = 'FullRowSelect'
$grid.ReadOnly = $true
$grid.RowHeadersVisible = $false
$grid.AutoSizeColumnsMode = 'None'
$grid.BackgroundColor = [System.Drawing.Color]::White
$grid.Columns.Add((New-ProfileGridColumn -Name 'FolderName' -HeaderText 'Folder' -Width 120)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'ProfilePath' -HeaderText 'Path' -Width 230)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'BaseSid' -HeaderText 'Base SID' -Width 250)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'NormalKey' -HeaderText 'Normal Key' -Width 86)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'BakKey' -HeaderText '.bak Key' -Width 76)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'Loaded' -HeaderText 'Loaded' -Width 70)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'Special' -HeaderText 'Special' -Width 70)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'LocalAdmin' -HeaderText 'Local Admin' -Width 88)) | Out-Null
$grid.Columns.Add((New-ProfileGridColumn -Name 'Status' -HeaderText 'Status' -Width 210)) | Out-Null

$statusPanel = New-Object System.Windows.Forms.Panel
$statusPanel.Dock = 'Bottom'
$statusPanel.Height = 34
$statusPanel.Padding = New-Object System.Windows.Forms.Padding(10, 8, 10, 6)

$statusLabel = New-Object System.Windows.Forms.Label
$statusLabel.Dock = 'Fill'
$statusLabel.Text = 'Ready.'

$statusPanel.Controls.Add($statusLabel)

$form.Controls.Add($grid)
$form.Controls.Add($topPanel)
$form.Controls.Add($statusPanel)
$form.Add_Shown({ Refresh-ProfileGrid })

[System.Windows.Forms.Application]::Run($form)
