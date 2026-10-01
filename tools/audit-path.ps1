# audit-path.ps1
#
# Read-only audit of the system (HKLM) and user (HKCU) PATH: for every entry, can the account running this
# script - or any standard user - put a file in that folder, or create it if it's missing?
#
# It works from the ACLs (Get-Acl) and the current token's groups; it never writes a probe file. Run it from a
# NORMAL (non-elevated) shell: elevated, Administrators is an enabled group and everything looks writable.
#
# Columns:
#   Scope     System or User, and the entry's position in that value
#   Entry     the raw registry value (unexpanded), with your profile folder shown as %USERPROFILE%
#   Verdict   admin-only         only admins/SYSTEM/TrustedInstaller can add files
#             you                you can add files (your account or one of your groups is granted it)
#             any-user           a broad group (Everyone, Authenticated Users, Users, INTERACTIVE) can add files
#             owner:you          you own the folder, so you can grant yourself write (implicit WRITE_DAC)
#             replace:<who>      you can't write inside it, but its parent lets <who> rename it away and recreate it
#             missing:<who>      the folder doesn't exist and <who> can create it (the "phantom PATH entry" hole)
#             missing:admin-only the folder doesn't exist and only admins can create it
#             relative           not a fully qualified path: resolves against whatever the current folder is
#             unreadable         the ACL couldn't be read
#
# Limits: ignores mandatory integrity labels (rare on PATH folders) and file-level ACLs on existing exes (a
# folder you can't add to may still hold an exe you can overwrite). A "you" verdict on a System entry is a
# route from your account to SYSTEM; an "any-user" verdict is a route from every account to every other.
#
#   .\tools\audit-path.ps1            # table
#   .\tools\audit-path.ps1 -Json      # machine-readable
#
# ASCII only, no BOM.

[CmdletBinding()]
param([switch]$Json)

$ErrorActionPreference = 'Stop'

# ---- who am I ------------------------------------------------------------------------------------------

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if ($elevated) {
    Write-Warning 'This shell is elevated, so Administrators counts as yours and every folder will look writable. Re-run from a normal shell.'
}

# .Groups already drops deny-only groups (e.g. Administrators in a split token) and the logon SID.
$mySids = @($identity.User.Value) + @($identity.Groups | ForEach-Object { $_.Value })
$broadSids = @(
    'S-1-1-0',       # Everyone
    'S-1-5-11',      # Authenticated Users
    'S-1-5-32-545',  # BUILTIN\Users
    'S-1-5-4'        # INTERACTIVE
)
$ownerRightsSid = 'S-1-3-4'

# ---- rights --------------------------------------------------------------------------------------------

$CreateFiles = 0x2         # add a file to the folder: the plant
$CreateDirs = 0x4          # add a subfolder: enough to create a missing PATH folder under this one
$DeleteChild = 0x40        # delete/rename children regardless of their own ACL
$WriteDac = 0x40000
$WriteOwner = 0x80000
$GenericAll = 0x10000000
$GenericWrite = 0x40000000

function Get-Mask([System.Security.AccessControl.FileSystemSecurity]$acl, [string[]]$sids) {
    $allow = 0; $deny = 0
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($sids -notcontains $rule.IdentityReference.Value) { continue }
        # Inherit-only ACEs (e.g. CREATOR OWNER) apply to children, not to this folder.
        if ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        $m = [int]$rule.FileSystemRights
        if ($m -band $GenericAll) { $m = $m -bor $CreateFiles -bor $CreateDirs -bor $DeleteChild -bor $WriteDac -bor $WriteOwner }
        if ($m -band $GenericWrite) { $m = $m -bor $CreateFiles -bor $CreateDirs }
        if ($rule.AccessControlType -eq 'Deny') { $deny = $deny -bor $m } else { $allow = $allow -bor $m }
    }
    # Canonical ACLs put deny before allow, so subtracting the deny bits is the effective result.
    return $allow -band (-bnot $deny)
}

function Test-Bit([int]$mask, [int]$bits) { return ($mask -band $bits) -ne 0 }

# Who can add files to $dir: 'any-user', 'you', 'owner:you' or $null.
function Get-Writer([string]$dir) {
    $acl = Get-Acl -LiteralPath $dir
    $broad = Get-Mask $acl $broadSids
    $mine = Get-Mask $acl $mySids
    if (Test-Bit $broad ($CreateFiles -bor $WriteDac -bor $WriteOwner)) { return 'any-user' }
    if (Test-Bit $mine ($CreateFiles -bor $WriteDac -bor $WriteOwner)) { return 'you' }
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    $ownerRights = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.IdentityReference.Value -eq $ownerRightsSid })
    if ($ownerRights.Count -eq 0 -and $mySids -contains $owner) { return 'owner:you' }
    return $null
}

# Who can rename $dir away and create a new one in its place (delete-child + create-subfolder on the parent).
function Get-Replacer([string]$dir) {
    $parent = Split-Path -Parent $dir
    if (-not $parent) { return $null }
    $acl = Get-Acl -LiteralPath $parent
    $both = $DeleteChild -bor $CreateDirs
    if (((Get-Mask $acl $broadSids) -band $both) -eq $both) { return 'any-user' }
    if (((Get-Mask $acl $mySids) -band $both) -eq $both) { return 'you' }
    return $null
}

# Who can create the missing folder $dir: walk up to the nearest folder that exists.
function Get-Creator([string]$dir) {
    $p = $dir
    while ($p -and -not (Test-Path -LiteralPath $p -PathType Container)) { $p = Split-Path -Parent $p }
    if (-not $p) { return 'admin-only' }
    $acl = Get-Acl -LiteralPath $p
    if (Test-Bit (Get-Mask $acl $broadSids) $CreateDirs) { return 'any-user' }
    if (Test-Bit (Get-Mask $acl $mySids) $CreateDirs) { return 'you' }
    return 'admin-only'
}

# ---- read PATH -----------------------------------------------------------------------------------------

function Get-RawPath([Microsoft.Win32.RegistryKey]$hive, [string]$subKey) {
    $key = $hive.OpenSubKey($subKey, $false)   # read-only
    if (-not $key) { return '' }
    try { return [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Close() }
}

$sources = @(
    @{ Scope = 'System'; Raw = Get-RawPath ([Microsoft.Win32.Registry]::LocalMachine) 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment' },
    @{ Scope = 'User'; Raw = Get-RawPath ([Microsoft.Win32.Registry]::CurrentUser) 'Environment' }
)

$profileDir = $env:USERPROFILE.TrimEnd('\')
function Hide-Profile([string]$s) {
    if (-not $s) { return $s }
    return [regex]::Replace($s, [regex]::Escape($profileDir), '%USERPROFILE%', 'IgnoreCase')
}

# ---- audit ---------------------------------------------------------------------------------------------

$rows = foreach ($src in $sources) {
    $i = 0
    foreach ($entry in ($src.Raw -split ';')) {
        if (-not $entry.Trim()) { continue }
        $i++
        $expanded = [Environment]::ExpandEnvironmentVariables($entry.Trim()).TrimEnd('\')
        $verdict = $null
        try {
            # Relative, drive-relative (C:foo), current-drive-rooted (\foo), or a %VAR% that didn't expand.
            if (-not [IO.Path]::IsPathRooted($expanded) -or $expanded -match '^[A-Za-z]:[^\\]' -or
                $expanded -match '^\\[^\\]' -or $expanded -match '%') {
                $verdict = 'relative'
            }
            elseif (-not (Test-Path -LiteralPath $expanded -PathType Container)) {
                $verdict = 'missing:' + (Get-Creator $expanded)
            }
            else {
                $writer = Get-Writer $expanded
                if ($writer) { $verdict = $writer }
                else {
                    $replacer = Get-Replacer $expanded
                    if ($replacer) { $verdict = 'replace:' + $replacer } else { $verdict = 'admin-only' }
                }
            }
        }
        catch { $verdict = 'unreadable' }

        [pscustomobject]@{
            Scope   = '{0} {1,2}' -f $src.Scope, $i
            Verdict = $verdict
            Entry   = Hide-Profile $entry.Trim()
        }
    }
}

if ($Json) {
    [pscustomobject]@{ Elevated = $elevated; Entries = @($rows) } | ConvertTo-Json -Depth 4
}
else {
    $rows | Format-Table -AutoSize -Wrap
    $risky = @($rows | Where-Object { $_.Scope -like 'System*' -and $_.Verdict -ne 'admin-only' })
    "System PATH entries a non-admin can influence: $($risky.Count)"
}
