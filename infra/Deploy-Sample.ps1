#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$ConfigurationPath,
    [string]$BackendPublicCertificatePath,
    [string]$IndexerPublicCertificatePath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$config = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
$required = @('subscriptionId', 'tenantId', 'resourceGroupName', 'location', 'searchLocation',
    'publisherEmail', 'ownerPrincipalId', 'operatorPrincipalId', 'employeeMemberIds',
    'reviewerMemberIds', 'appServiceName')
foreach ($key in $required) {
    if ($key -notin $config.PSObject.Properties.Name) { throw "Missing configuration property: $key" }
}
foreach ($id in @($config.subscriptionId, $config.tenantId, $config.ownerPrincipalId,
        $config.operatorPrincipalId) + @($config.employeeMemberIds) + @($config.reviewerMemberIds)) {
    $guid = [Guid]::Empty
    if (-not [Guid]::TryParse($id, [ref]$guid) -or $guid -eq [Guid]::Empty) {
        throw 'Replace all example IDs with approved nonempty GUIDs before running this script.'
    }
}
if ($config.resourceGroupName -notmatch '^[a-zA-Z0-9_-]{1,80}$' -or
    $config.location -notmatch '^[a-z0-9]+$' -or $config.searchLocation -notmatch '^[a-z0-9]+$' -or
    $config.publisherEmail -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$' -or
    $config.publisherEmail -like '*@example.com' -or
    ($config.appServiceName -and $config.appServiceName -notmatch '^[a-z0-9][a-z0-9-]{0,58}[a-z0-9]$')) {
    throw 'Invalid resource group, location, publisher email, or globally unique App Service name.'
}
$certificate = $null
$indexerCertificate = $null
function Read-PublicCertificate([string]$Path) {
    if ([IO.Path]::GetExtension($Path) -ne '.cer') {
        throw 'Supply a DER public .cer certificate, never a PEM, PFX, or private key.'
    }
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $Path).Path)
    if ($cert.HasPrivateKey -or $cert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or
        $cert.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow) {
        throw 'The public certificate must be currently valid and contain no private key.'
    }
    return $cert
}
if ($BackendPublicCertificatePath) { $certificate = Read-PublicCertificate $BackendPublicCertificatePath }
if ($IndexerPublicCertificatePath) { $indexerCertificate = Read-PublicCertificate $IndexerPublicCertificatePath }
if ($certificate -and $indexerCertificate -and $certificate.Thumbprint -eq $indexerCertificate.Thumbprint) {
    throw 'Runtime and indexer must use separate certificates.'
}
function Invoke-AzJson([string[]]$Arguments) {
    # Windows az.cmd otherwise interprets shell characters in repository paths.
    $cliArguments = if ($IsWindows -and (Get-Command az).Source -like '*.cmd') {
        @($Arguments | ForEach-Object { if ($_ -match '[&|<>^()]') { '"' + $_ + '"' } else { $_ } })
    } else { $Arguments }
    $text = & az @cliArguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed during $($Arguments[0..1] -join ' '). Deployment stopped." }
    if ($text) { return ($text | Out-String | ConvertFrom-Json) }
}
function Deploy-Group([string]$Name, [string]$Template, [hashtable]$Parameters) {
    $path = Join-Path $temp "$Name.parameters.json"
    $wrapped = @{}
    foreach ($entry in $Parameters.GetEnumerator()) { $wrapped[$entry.Key] = @{ value = $entry.Value } }
    @{ parameters = $wrapped } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path
    $deployment = Invoke-AzJson @('deployment', 'group', 'create', '--subscription', $config.subscriptionId,
        '--resource-group', $config.resourceGroupName, '--name', $Name,
        '--template-file', (Join-Path $PSScriptRoot $Template), '--parameters', "@$path")
    return $deployment.properties.outputs
}

foreach ($template in @('main.bicep', 'identity.bicep', 'gateway-identity.bicep',
        'backend-identity.bicep', 'app-service.bicep')) {
    Invoke-AzJson @('bicep', 'build', '--file', (Join-Path $PSScriptRoot $template), '--stdout') | Out-Null
}
if ($ValidateOnly) {
    if ($certificate) { $certificate.Dispose() }
    if ($indexerCertificate) { $indexerCertificate.Dispose() }
    Write-Output 'Configuration and Bicep compilation passed. No Azure resources were read or changed.'
    return
}
$account = Invoke-AzJson @('account', 'show')
if ($account.tenantId -ne $config.tenantId -or $account.id -ne $config.subscriptionId) {
    throw 'Run az login and az account set for the exact tenant/subscription in your configuration.'
}
Write-Warning 'This creates billable services and Entra objects and grants explicit application/RBAC access.'
Write-Warning 'Graph extension resources do not support ARM what-if. -WhatIf here previews stages, not directory diffs.'
if (-not $PSCmdlet.ShouldProcess("$($config.tenantId) / $($config.subscriptionId) / $($config.resourceGroupName)",
        'Deploy sample identities, services, access assignments, and optional App Service')) {
    if ($certificate) { $certificate.Dispose() }
    if ($indexerCertificate) { $indexerCertificate.Dispose() }
    return
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
$null = New-Item -ItemType Directory -Path $temp
try {
    $null = Invoke-AzJson @('group', 'create', '--subscription', $config.subscriptionId,
        '--name', $config.resourceGroupName, '--location', $config.location)
    $gateway = (Deploy-Group 'policy-rag-gateway-identity' 'gateway-identity.bicep' @{
        tenantId = $config.tenantId; ownerPrincipalId = $config.ownerPrincipalId
    }).configuration.value
    $redirects = @('http://localhost:5280/auth-callback.html')
    if ($config.appServiceName) { $redirects += "https://$($config.appServiceName).azurewebsites.net/auth-callback.html" }
    $identity = (Deploy-Group 'policy-rag-user-identity' 'identity.bicep' @{
        tenantId = $config.tenantId; ownerPrincipalId = $config.ownerPrincipalId
        enableBrowserSignIn = $true; browserRedirectUris = $redirects
        employeeMemberIds = @($config.employeeMemberIds); reviewerMemberIds = @($config.reviewerMemberIds)
    }).configuration.value
    $parameters = @{
        resourceGroupName = $config.resourceGroupName; location = $config.location
        searchLocation = $config.searchLocation; publisherEmail = $config.publisherEmail
        operatorPrincipalId = $config.operatorPrincipalId; modelGatewayTenantId = $config.tenantId
        modelGatewayAudience = $gateway.gateway.audience
    }
    $wrapped = @{}
    foreach ($entry in $parameters.GetEnumerator()) { $wrapped[$entry.Key] = @{ value = $entry.Value } }
    $path = Join-Path $temp 'services.parameters.json'
    @{ parameters = $wrapped } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path
    $deployment = Invoke-AzJson @('deployment', 'sub', 'create', '--subscription', $config.subscriptionId,
        '--name', 'policy-rag-services', '--location', $config.location,
        '--template-file', (Join-Path $PSScriptRoot 'main.bicep'), '--parameters', "@$path")
    $services = $deployment.properties.outputs.services.value
    $storage = $deployment.properties.outputs.sourceStorage.value
    & (Join-Path $PSScriptRoot 'Grant-BackendAccess.ps1') -TenantId $config.tenantId `
        -BackendServicePrincipalId $services.runtimeIdentity.principalId `
        -GatewayServicePrincipalId $gateway.gateway.servicePrincipalId `
        -SearchResourceId $services.search.resourceId -Confirm:$false
    & (Join-Path $PSScriptRoot 'Grant-BackendAccess.ps1') -TenantId $config.tenantId `
        -BackendServicePrincipalId $services.indexerIdentity.principalId `
        -GatewayServicePrincipalId $gateway.gateway.servicePrincipalId `
        -SearchResourceId $services.search.resourceId -GatewayOnly -Confirm:$false

    $settings = @{
        'Rag:Azure:TenantId' = $config.tenantId; 'Rag:Azure:Audience' = $identity.api.audience
        'Rag:Azure:SearchEndpoint' = $services.search.endpoint; 'Rag:Azure:SearchIndex' = $services.search.indexName
        'Rag:Azure:ApimEndpoint' = $services.gateway.endpoint; 'Rag:Azure:ApimScope' = $gateway.gateway.delegatedScope
        'Rag:Azure:EmbeddingModel' = $services.foundry.embeddingDeployment
        'Rag:Azure:AnswerModel' = $services.foundry.generationDeployment
        'Rag:Azure:EmbeddingDimensions' = [string]$services.foundry.embeddingDimensions
        'Rag:Azure:GenerationProtocol' = 'OpenAI'
        'BrowserSignIn:ClientId' = $identity.browser.clientId
        'BrowserSignIn:Scope' = $identity.browser.scope
        'BrowserSignIn:RedirectUri' = 'http://localhost:5280/auth-callback.html'
        "Rag:Azure:GroupMappings:$($identity.groups.employees):0" = 'employees'
        "Rag:Azure:GroupMappings:$($identity.groups.securityReviewers):0" = 'security-reviewers'
    }
    $backend = $null
    if ($certificate) {
        $backend = Deploy-Group 'policy-rag-backend-identity' 'backend-identity.bicep' @{
            tenantId = $config.tenantId; ownerPrincipalId = $config.ownerPrincipalId
            certificatePublicKey = [Convert]::ToBase64String($certificate.RawData)
            certificateStartDateTime = $certificate.NotBefore.ToUniversalTime().ToString('o')
            certificateEndDateTime = $certificate.NotAfter.ToUniversalTime().ToString('o')
        }
        & (Join-Path $PSScriptRoot 'Grant-BackendAccess.ps1') -TenantId $config.tenantId `
            -BackendServicePrincipalId $backend.servicePrincipalId.value `
            -GatewayServicePrincipalId $gateway.gateway.servicePrincipalId `
            -SearchResourceId $services.search.resourceId -Confirm:$false
    }
    $localIndexer = $null
    if ($indexerCertificate) {
        $localIndexer = Deploy-Group 'policy-rag-indexer-identity' 'backend-identity.bicep' @{
            tenantId = $config.tenantId; ownerPrincipalId = $config.ownerPrincipalId; purpose = 'indexer'
            certificatePublicKey = [Convert]::ToBase64String($indexerCertificate.RawData)
            certificateStartDateTime = $indexerCertificate.NotBefore.ToUniversalTime().ToString('o')
            certificateEndDateTime = $indexerCertificate.NotAfter.ToUniversalTime().ToString('o')
        }
        & (Join-Path $PSScriptRoot 'Grant-BackendAccess.ps1') -TenantId $config.tenantId `
            -BackendServicePrincipalId $localIndexer.servicePrincipalId.value `
            -GatewayServicePrincipalId $gateway.gateway.servicePrincipalId `
            -SearchResourceId $services.search.resourceId -Indexer -Confirm:$false
    }
    $hosted = $null
    if ($config.appServiceName) {
        $hostedSettings = @{
            ASPNETCORE_ENVIRONMENT = 'Production'
            Rag__Mode = 'Azure'
            BrowserSignIn__Enabled = 'true'
            Rag__Azure__ManagedIdentityClientId = $services.runtimeIdentity.clientId
            AllowedHosts = "$($config.appServiceName).azurewebsites.net"
        }
        foreach ($entry in $settings.GetEnumerator()) { $hostedSettings[$entry.Key.Replace(':', '__')] = $entry.Value }
        $hostedSettings.BrowserSignIn__RedirectUri = "https://$($config.appServiceName).azurewebsites.net/auth-callback.html"
        $hosted = Deploy-Group 'policy-rag-app-service' 'app-service.bicep' @{
            location = $config.location; appName = $config.appServiceName
            runtimeIdentityResourceId = $services.runtimeIdentity.resourceId; applicationSettings = $hostedSettings
        }
    }
    $outputPath = Join-Path (Split-Path (Resolve-Path -LiteralPath $ConfigurationPath).Path -Parent) 'deployment.local.json'
    @{
        applicationSettings = $settings; services = $services; storage = $storage
        identity = $identity; gateway = $gateway; backend = $backend; localIndexer = $localIndexer; hosted = $hosted
    } | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $outputPath
    Write-Output "Deployment outputs saved to $outputPath (no private credentials)."
    Write-Warning 'Complete approved browser consent and ingestion before testing. App Service infrastructure does not publish application code.'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
    if ($certificate) { $certificate.Dispose() }
    if ($indexerCertificate) { $indexerCertificate.Dispose() }
}
