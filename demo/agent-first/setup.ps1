#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'ailab-demo-eastus2',
    [string]$FoundryName = 'ai-policy-xqztycjhejs32',
    [string]$ProjectName = 'secure-policy-rag',
    [string]$SearchServiceName,
    [string]$IndexName = 'policy-chunks'
)

$ErrorActionPreference = 'Stop'
Get-Command az -ErrorAction Stop | Out-Null

function Invoke-AzJson([string[]]$Arguments) {
    $cliArguments = if ($IsWindows -and (Get-Command az).Source -like '*.cmd') {
        @($Arguments | ForEach-Object { if ($_ -match '[&|<>^()]') { '"' + $_ + '"' } else { $_ } })
    } else { $Arguments }
    $text = & az @cliArguments --only-show-errors --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        $safe = ($text -join "`n") -replace '[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}', '[redacted-id]'
        $code = [regex]::Match($safe, '\b(AuthorizationFailed|Forbidden|ResourceNotFound|AADSTS\d+|InvalidAuthenticationToken|AuthenticationFailed)\b').Value
        throw "Azure CLI failed during $($Arguments[0..1] -join ' ') (exit $LASTEXITCODE; $code). No broader role or key fallback will be attempted."
    }
    if ($text) { return ($text -join "`n" | ConvertFrom-Json) }
}

$account = Invoke-AzJson @('account', 'show')
if ($account.user.type -ne 'user') { throw 'Sign in with az login as an approved user, not a service principal.' }
$resources = @(Invoke-AzJson @('resource', 'list', '--resource-group', $ResourceGroup))
$searches = @($resources | Where-Object {
    $_.type -eq 'Microsoft.Search/searchServices' -and (!$SearchServiceName -or $_.name -eq $SearchServiceName)
})
if ($searches.Count -ne 1) { throw 'Search selection is ambiguous or missing. Supply -SearchServiceName.' }
$search = $searches[0]
$foundry = @($resources | Where-Object {
    $_.type -eq 'Microsoft.CognitiveServices/accounts' -and $_.name -eq $FoundryName
})
if ($foundry.Count -ne 1) { throw 'The specified existing Foundry account was not found.' }
$projectId = $foundry[0].id + '/projects/' + $ProjectName
$projectUrl = 'https://management.azure.com' + $projectId
$project = Invoke-AzJson @('rest', '--method', 'get', '--url', ($projectUrl + '?api-version=2025-06-01'))
if (!$project.identity.principalId) { throw 'The existing project has no managed identity. Setup will not change its identity.' }
$endpoints = @($project.properties.endpoints.PSObject.Properties |
    Where-Object { $_.Value -match '^https://[^/]+/api/projects/[^/]+/?$' })
if ($endpoints.Count -ne 1) { throw 'Cannot discover a unique Foundry project endpoint. Check Project details in the portal.' }
$projectEndpoint = $endpoints[0].Value.TrimEnd('/')
$searchEndpoint = "https://$($search.name).search.windows.net"

$token = Invoke-AzJson @('account', 'get-access-token', '--resource', 'https://search.azure.com')
$headers = @{ Authorization = 'Bearer ' + $token.accessToken }
try {
    $schema = Invoke-RestMethod -Uri "$searchEndpoint/indexes/$IndexName`?api-version=2024-07-01" `
        -Headers $headers -TimeoutSec 30
    $count = Invoke-RestMethod -Uri "$searchEndpoint/indexes/$IndexName/docs/`$count?api-version=2024-07-01" `
        -Headers $headers -TimeoutSec 30
} catch [Microsoft.PowerShell.Commands.HttpResponseException] {
    throw "Read-only Search inspection failed (HTTP $([int]$_.Exception.Response.StatusCode)). The signed-in user needs existing index-schema/read access; setup will not grant it."
} finally {
    $headers.Clear()
    $token = $null
}
$semantic = @($schema.semantic.configurations | Where-Object { $_ })
$vectorizers = @($schema.vectorSearch.vectorizers | Where-Object { $_ })
$queryType = if ($semantic.Count -gt 0) { 'semantic' } else { 'simple' }
$required = @('id', 'documentId', 'title', 'content', 'allowedGroups')
foreach ($name in $required) {
    if (!($schema.fields | Where-Object { $_.name -eq $name -and $_.retrievable -ne $false })) {
        throw "Required retrievable field '$name' is absent. The existing index will not be changed."
    }
}
Write-Host "Found existing Search: $($search.name); index: $IndexName; chunks: $count"
$schema.fields | Select-Object name, type, searchable, retrievable, filterable | Format-Table | Out-Host
Write-Host "Vectorizers: $($vectorizers.Count); semantic configurations: $($semantic.Count); selected query: $queryType"
Write-Host "Found project endpoint: $projectEndpoint"
if ([int]$count -eq 0) { throw 'The existing index is empty. This demo will not upload documents.' }

$reader = 'Search Index Data Reader'
$roles = @(Invoke-AzJson @('role', 'assignment', 'list', '--scope', $search.id, '--include-inherited'))
$existing = @($roles | Where-Object {
    $_.principalId -eq $project.identity.principalId -and $_.roleDefinitionName -eq $reader
})
if ($existing.Count -gt 0) {
    Write-Host "Found project managed identity role: $reader."
} else {
    Invoke-AzJson @('role', 'assignment', 'create', '--assignee-object-id', $project.identity.principalId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', $reader, '--scope', $search.id) | Out-Null
    Write-Host "Created project managed identity role: $reader on the existing Search service."
}

$connections = Invoke-AzJson @('rest', '--method', 'get', '--url', ($projectUrl + '/connections?api-version=2025-06-01'))
$matching = @($connections.value | Where-Object {
    $_.properties.category -eq 'CognitiveSearch' -and $_.properties.authType -eq 'AAD' -and
    $_.properties.target.TrimEnd('/') -eq $searchEndpoint
} | Sort-Object name)
if ($matching.Count -gt 0) {
    $connectionName = $matching[0].name.Split('/')[-1]
    Write-Host "Found Entra Search connection: $connectionName."
} else {
    $connectionName = 'policy-agent-first-search'
    if ($connections.value | Where-Object { $_.name.Split('/')[-1] -eq $connectionName }) {
        throw 'The demo connection name is already used with a different target/authentication. Nothing will be overwritten.'
    }
    $bodyPath = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName() + '.json')
    try {
        @{ properties = @{ category = 'CognitiveSearch'; target = $searchEndpoint; authType = 'AAD' } } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $bodyPath -Encoding utf8NoBOM
        Invoke-AzJson @('rest', '--method', 'put', '--url',
            ($projectUrl + "/connections/$connectionName`?api-version=2025-06-01"),
            '--body', "@$bodyPath") | Out-Null
    } finally { Remove-Item -LiteralPath $bodyPath -Force }
    Write-Host "Created Entra Search connection: $connectionName (no keys)."
}

$schemaJson = $schema | ConvertTo-Json -Depth 30 -Compress
$schemaHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($schemaJson)))
@{
    projectEndpoint = $projectEndpoint
    connectionName = $connectionName
    searchEndpoint = $searchEndpoint
    indexName = $IndexName
    queryType = $queryType
    model = 'gpt-5-mini'
    indexSchemaSha256 = $schemaHash
    indexedChunkCount = [int]$count
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'config.local.json') -Encoding utf8NoBOM
Write-Host 'Saved ignored config.local.json with endpoint/names only; no subscription, principal, client IDs, or credentials.'
Write-Host 'Role assignments can take 5-15 minutes to propagate. Rerunning setup is safe.'
Write-Warning 'Only the project identity received Search Index Data Reader. Current Learn guidance also describes account-identity/Contributor access. This demo will STOP if the service requires that; it will not grant broader roles, change networking, or enable keys.'
