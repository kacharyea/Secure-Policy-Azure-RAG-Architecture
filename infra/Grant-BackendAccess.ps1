#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][Guid]$TenantId,
    [Parameter(Mandatory = $true)][Guid]$BackendServicePrincipalId,
    [Parameter(Mandatory = $true)][Guid]$GatewayServicePrincipalId,
    [Parameter(Mandatory = $true)][ValidatePattern('^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.Search/searchServices/[^/]+$')]
    [string]$SearchResourceId,
    [switch]$GatewayOnly,
    [switch]$Indexer
)

$ErrorActionPreference = 'Stop'
if ($GatewayOnly -and $Indexer) { throw 'Choose either GatewayOnly or Indexer, not both.' }
function Invoke-AzJson([string[]]$Arguments) {
    $cliArguments = if ($IsWindows -and (Get-Command az).Source -like '*.cmd') {
        @($Arguments | ForEach-Object { if ($_ -match '[&|<>^()]') { '"' + $_ + '"' } else { $_ } })
    } else { $Arguments }
    $result = & az @cliArguments
    if ($LASTEXITCODE -ne 0) { throw 'Azure CLI operation failed. No successful provisioning is claimed.' }
    return ($result | Out-String | ConvertFrom-Json)
}

$account = Invoke-AzJson @('account', 'show', '--output', 'json')
if ($account.tenantId -ne $TenantId.ToString() -or
    $TenantId -eq [Guid]::Empty -or $BackendServicePrincipalId -eq [Guid]::Empty -or
    $GatewayServicePrincipalId -eq [Guid]::Empty -or
    $BackendServicePrincipalId -eq $GatewayServicePrincipalId -or
    ($SearchResourceId -split '/')[2] -ne $account.id) {
    throw 'Select the approved tenant/subscription and provide distinct nonempty service-principal IDs.'
}
$backend = Invoke-AzJson @('rest', '--method', 'GET', '--url',
    "https://graph.microsoft.com/v1.0/servicePrincipals/$BackendServicePrincipalId")
$gateway = Invoke-AzJson @('rest', '--method', 'GET', '--url',
    "https://graph.microsoft.com/v1.0/servicePrincipals/$GatewayServicePrincipalId")
if (-not $backend.accountEnabled -or -not $gateway.accountEnabled) {
    throw 'Both service principals must be enabled.'
}
$roles = @($gateway.appRoles | Where-Object {
    $_.value -ceq 'ModelGateway.Invoke' -and $_.isEnabled -and 'Application' -in $_.allowedMemberTypes
})
if ($roles.Count -ne 1) { throw 'Deploy the gateway application role before assigning backend access.' }
$assignments = Invoke-AzJson @('rest', '--method', 'GET', '--url',
    "https://graph.microsoft.com/v1.0/servicePrincipals/$BackendServicePrincipalId/appRoleAssignments")
if ($assignments.PSObject.Properties['@odata.nextLink']) { throw 'Paginated assignments require explicit review before modifying this identity.' }
$existing = @($assignments.value | Where-Object {
    $_.resourceId -eq $GatewayServicePrincipalId.ToString() -and $_.appRoleId -eq $roles[0].id
})
if ($existing.Count -gt 1) { throw 'Duplicate gateway role assignments require investigation.' }
if ($existing.Count -eq 0 -and $PSCmdlet.ShouldProcess($BackendServicePrincipalId, 'Assign ModelGateway.Invoke application role')) {
    $body = @{
        principalId = $BackendServicePrincipalId.ToString()
        resourceId = $GatewayServicePrincipalId.ToString()
        appRoleId = $roles[0].id
    } | ConvertTo-Json -Compress
    $bodyPath = [IO.Path]::GetTempFileName()
    try {
        # A file avoids shell-dependent quoting of the Graph JSON body.
        Set-Content -LiteralPath $bodyPath -Value $body
        $null = Invoke-AzJson @('rest', '--method', 'POST', '--url',
            "https://graph.microsoft.com/v1.0/servicePrincipals/$BackendServicePrincipalId/appRoleAssignments",
            '--body', "@$bodyPath")
    } finally { Remove-Item -LiteralPath $bodyPath -Force }
}
if ($GatewayOnly) {
    Write-Output 'Gateway assignment completed (or previewed). Search permissions were not changed.'
    return
}
$roleIds = if ($Indexer) {
    @('7ca78c08-252a-4471-8644-bb5ff32d4ba0', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
} else { @('1407120a-92aa-4202-b7e9-c0e197c71c8f') }
$search = Invoke-AzJson @('role', 'assignment', 'list', '--scope', $SearchResourceId,
    '--all', '--output', 'json')
foreach ($readerRole in $roleIds) {
    $readerAssignments = @($search | Where-Object {
        $_.principalId -eq $BackendServicePrincipalId.ToString() -and
        $_.scope -eq $SearchResourceId -and $_.roleDefinitionId -like "*/$readerRole"
    })
    if ($readerAssignments.Count -eq 0 -and $PSCmdlet.ShouldProcess($SearchResourceId, "Assign Search role $readerRole to $BackendServicePrincipalId")) {
        $null = Invoke-AzJson @('role', 'assignment', 'create',
            '--assignee-object-id', $BackendServicePrincipalId.ToString(),
            '--assignee-principal-type', 'ServicePrincipal', '--role', $readerRole,
            '--scope', $SearchResourceId, '--output', 'json')
    }
}
Write-Output "Access operations completed (or previewed). Indexer write access requested: $([bool]$Indexer). No model or directory roles granted."
