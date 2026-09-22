# Static inspection only. Does not create a D3D12 device or execute GPU work.
[CmdletBinding()]
param([string] $OutputPath = '')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ProductionHealth.psm1') -Force
$root = Split-Path -Parent $PSScriptRoot
$binaryDirectory = Join-Path $root 'tests\RendererChecks\bin\Release\net8.0-windows'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $binaryDirectory 'ComputeSharp.Core.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binaryDirectory 'ComputeSharp.dll'))
$deviceType = $assembly.GetType('ComputeSharp.GraphicsDevice', $true)
$opcodes = @{}
foreach ($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
    $opcode = $field.GetValue($null)
    $opcodes[([int]$opcode.Value -band 0xffff)] = $opcode
}

function Get-MethodCalls {
    param([Reflection.MethodInfo] $Method)
    $il = $Method.GetMethodBody().GetILAsByteArray()
    $offset = 0
    while ($offset -lt $il.Length) {
        $value = [int]$il[$offset++]
        if ($value -eq 0xfe) { $value = 0xfe00 + [int]$il[$offset++] }
        $opcode = $opcodes[$value]
        if ($null -eq $opcode) { throw "Unknown IL opcode $value in $($Method.Name)." }
        $size = switch ($opcode.OperandType.ToString()) {
            'InlineNone' { 0 }
            { $_ -in 'ShortInlineBrTarget', 'ShortInlineI', 'ShortInlineVar' } { 1 }
            'InlineVar' { 2 }
            { $_ -in 'InlineI8', 'InlineR' } { 8 }
            'InlineSwitch' { 4 + 4 * [BitConverter]::ToInt32($il, $offset) }
            { $_ -in 'InlineBrTarget', 'InlineField', 'InlineI', 'InlineMethod', 'InlineSig', 'InlineString',
                'InlineTok', 'InlineType', 'ShortInlineR' } { 4 }
            default { throw "Unsupported IL operand $($opcode.OperandType)." }
        }
        if ($opcode.OperandType -eq [Reflection.Emit.OperandType]::InlineMethod) {
            $token = [BitConverter]::ToInt32($il, $offset)
            $target = $Method.Module.ResolveMethod($token)
            $target.DeclaringType.FullName + '.' + $target.Name
        }
        $offset += $size
    }
}

$records = @()
foreach ($name in @('WaitForFenceAsync', 'WaitForSingleObjectCallbackForWaitForFenceAsync', 'CompleteFenceWait')) {
    $method = $deviceType.GetMethod($name, [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $method -and $name -eq 'CompleteFenceWait') { continue }
    if ($null -eq $method) { throw "Pinned-library method $name not found; audit this version manually." }
    $records += [pscustomobject]@{ method = $name; calls = @(Get-MethodCalls $method) }
}
$calls = @($records | ForEach-Object { $_.calls })
$register = @($calls | Where-Object { $_ -match '\.RegisterWaitForSingleObject$' }).Count -gt 0
$unregister = @($calls | Where-Object { $_ -match '\.UnregisterWait(Ex)?$' }).Count -gt 0
$record = @{ utc = [DateTime]::UtcNow.ToString('o'); binary = $assembly.Location;
    assemblyVersion = $assembly.GetName().Version.ToString(); sha256 = (Get-FileHash -LiteralPath $assembly.Location -Algorithm SHA256).Hash;
    methods = $records; registerWaitCall = $register; unregisterWaitCall = $unregister;
    passedNecessaryCheck = ($register -and $unregister);
    limitation = 'Presence is not sufficient proof of race-safe cleanup. Absence confirms this known native-wait lifetime defect in the inspected path.' }
if ($OutputPath) { Write-ProductionRecord $OutputPath $record }
$records | Format-List | Out-String | Write-Output
if (-not $register -or -not $unregister) {
    Write-Output 'ASYNC FENCE AUDIT FAILED: native wait registration has no matching unregister call in the inspected path. Do not advance the asynchronous stability runs.'
    exit 1
}
Write-Output 'Necessary unregister call observed; source-level race/failure-path review is still required.'
