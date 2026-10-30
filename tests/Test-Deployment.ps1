#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
$null = New-Item -ItemType Directory -Path $temp
$calls = [Collections.Generic.List[object]]::new()
$fixtureGraphGrants = [Collections.Generic.List[object]]::new()
$fixtureSearchGrants = [Collections.Generic.List[object]]::new()
$fixtureParameters = @{}
$fixtureFailBuild = $false
$tenant = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
$subscription = 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff'
$runtime = 'cccccccc-dddd-eeee-ffff-000000000001'
$indexer = 'cccccccc-dddd-eeee-ffff-000000000002'
$gateway = 'cccccccc-dddd-eeee-ffff-000000000003'
$localBackend = 'cccccccc-dddd-eeee-ffff-000000000004'
$fixtureLocalIndexerId = 'cccccccc-dddd-eeee-ffff-000000000005'
$role = 'dddddddd-eeee-ffff-0000-000000000001'
$search = "/subscriptions/$subscription/resourceGroups/rg-test/providers/Microsoft.Search/searchServices/test-search"
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function az {
    $a = @($args)
    $calls.Add($a)
    $global:LASTEXITCODE = 0
    switch ("$($a[0]) $($a[1])") {
        'bicep build' {
            if ($fixtureFailBuild) { $global:LASTEXITCODE = 1 }
            return
        }
        'account show' { return (@{ tenantId = $tenant; id = $subscription } | ConvertTo-Json) }
        'group create' { return '{}' }
        'role assignment' {
            if ($a[2] -eq 'list') { return (ConvertTo-Json -InputObject @($fixtureSearchGrants.ToArray())) }
            if ($a[2] -eq 'create') {
                $fixtureSearchGrants.Add(@{
                    principalId = $a[[Array]::IndexOf($a, '--assignee-object-id') + 1]
                    scope = $a[[Array]::IndexOf($a, '--scope') + 1]
                    roleDefinitionId = '/roles/' + $a[[Array]::IndexOf($a, '--role') + 1]
                })
                return '{}'
            }
            throw 'Unexpected role operation'
        }
        'rest --method' {
            $method = $a[2]
            $url = $a[[Array]::IndexOf($a, '--url') + 1]
            if ($method -eq 'GET' -and $url -like '*/appRoleAssignments') {
                $principal = ($url -split '/')[-2]
                return (@{ value = @($fixtureGraphGrants | Where-Object principalId -eq $principal) } | ConvertTo-Json -Depth 8)
            }
            if ($method -eq 'POST' -and $url -like '*/appRoleAssignments') {
                $bodyFile = $a[[Array]::IndexOf($a, '--body') + 1].Substring(1)
                $fixtureGraphGrants.Add((Get-Content -LiteralPath $bodyFile -Raw | ConvertFrom-Json))
                return '{}'
            }
            if ($method -eq 'GET' -and $url -like '*/servicePrincipals/*') {
                return (@{ accountEnabled = $true; appRoles = @(@{
                    value = 'ModelGateway.Invoke'; isEnabled = $true
                    allowedMemberTypes = @('Application'); id = $role
                }) } | ConvertTo-Json -Depth 8)
            }
            throw "Unexpected Graph request: $method $url"
        }
        'deployment group' {
            $name = $a[[Array]::IndexOf($a, '--name') + 1]
            $parameterFile = $a[[Array]::IndexOf($a, '--parameters') + 1].Substring(1)
            $fixtureParameters[$name] = (Get-Content -LiteralPath $parameterFile -Raw | ConvertFrom-Json).parameters
            $outputs = switch ($name) {
                'policy-rag-gateway-identity' { @{ configuration = @{ value = @{ gateway = @{
                    audience = $gateway; servicePrincipalId = $gateway; delegatedScope = "api://$gateway/.default"
                } } } } }
                'policy-rag-user-identity' { @{ configuration = @{ value = @{
                    api = @{ audience = $runtime }
                    browser = @{ clientId = $indexer; scope = "api://$runtime/PolicyReview.Invoke" }
                    groups = @{ employees = $runtime; securityReviewers = $indexer }
                } } } }
                'policy-rag-backend-identity' { @{
                    clientId = @{ value = $localBackend }; servicePrincipalId = @{ value = $localBackend }
                } }
                'policy-rag-indexer-identity' { @{
                    clientId = @{ value = $fixtureLocalIndexerId }; servicePrincipalId = @{ value = $fixtureLocalIndexerId }
                } }
                'policy-rag-app-service' { @{ url = @{ value = 'https://test-host.azurewebsites.net' } } }
                default { throw "Unexpected deployment $name" }
            }
            return (@{ properties = @{ outputs = $outputs } } | ConvertTo-Json -Depth 20)
        }
        'deployment sub' {
            return (@{ properties = @{ outputs = @{
                services = @{ value = @{
                    runtimeIdentity = @{ principalId = $runtime; clientId = $runtime; resourceId = '/identities/runtime' }
                    indexerIdentity = @{ principalId = $indexer }
                    search = @{ resourceId = $search; endpoint = 'https://test.search.windows.net'; indexName = 'policy-chunks' }
                    gateway = @{ endpoint = 'https://test.azure-api.net/' }
                    foundry = @{ embeddingDeployment = 'text-embedding-3-small'; generationDeployment = 'gpt-5-mini'; embeddingDimensions = 1536 }
                } }
                sourceStorage = @{ value = @{ endpoint = 'https://test.blob.core.windows.net'; container = 'policy-documents' } }
            } } } | ConvertTo-Json -Depth 20)
        }
        default { throw "Unexpected CLI operation: $($a -join ' ')" }
    }
}
try {
    $config = @{
        tenantId = $tenant; subscriptionId = $subscription; resourceGroupName = 'rg-test'
        location = 'eastus2'; searchLocation = 'eastus'; publisherEmail = 'admin@example.org'
        ownerPrincipalId = $runtime; operatorPrincipalId = $runtime
        employeeMemberIds = @($runtime); reviewerMemberIds = @($indexer); appServiceName = 'test-host'
    }
    $path = Join-Path $temp 'environment.local.json'
    $config | ConvertTo-Json | Set-Content -LiteralPath $path
    $script = Join-Path $root 'infra\Deploy-Sample.ps1'
    & $script -ConfigurationPath $path -ValidateOnly
    Assert (@($calls | Where-Object { $_[0] -ne 'bicep' }).Count -eq 0) 'ValidateOnly called a cloud API.'
    $calls.Clear()
    & $script -ConfigurationPath $path -WhatIf
    Assert (@($calls | Where-Object { $_[0] -notin @('bicep', 'account') }).Count -eq 0) 'WhatIf performed a write.'
    $calls.Clear()
    foreach ($name in @('runtime', 'indexer')) {
        $key = [Security.Cryptography.RSA]::Create(2048)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
                "CN=$name-test", $key, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
            try { [IO.File]::WriteAllBytes((Join-Path $temp "$name.cer"), $cert.RawData) } finally { $cert.Dispose() }
        } finally { $key.Dispose() }
    }
    & $script -ConfigurationPath $path -BackendPublicCertificatePath (Join-Path $temp 'runtime.cer') `
        -IndexerPublicCertificatePath (Join-Path $temp 'indexer.cer') -Confirm:$false
    $output = Get-Content -LiteralPath (Join-Path $temp 'deployment.local.json') -Raw | ConvertFrom-Json
    Assert ($output.applicationSettings.'Rag:Azure:SearchEndpoint' -eq 'https://test.search.windows.net') 'Missing application settings.'
    Assert ($output.backend.clientId.value -eq $localBackend) 'Local backend output missing.'
    Assert ($output.localIndexer.clientId.value -eq $fixtureLocalIndexerId) 'Separate indexer output missing.'
    $writes = @($calls | Where-Object { $_[0] -eq 'role' -and $_[2] -eq 'create' })
    Assert ($writes.Count -eq 4) 'Expected two reader grants and two local indexer grants.'
    foreach ($write in $writes) {
        $principal = $write[[Array]::IndexOf($write, '--assignee-object-id') + 1]
        $assignedRole = $write[[Array]::IndexOf($write, '--role') + 1]
        if ($principal -in @($runtime, $localBackend)) {
            Assert ($assignedRole -eq '1407120a-92aa-4202-b7e9-c0e197c71c8f') 'Runtime received a write permission.'
        }
        Assert ($write[[Array]::IndexOf($write, '--scope') + 1] -eq $search) 'Role assignment escaped Search scope.'
    }
    Assert (@($calls | Where-Object { $_[0] -eq 'rest' -and $_[2] -eq 'POST' }).Count -eq 4) 'Expected four explicit gateway role assignments.'
    $hostSettings = $fixtureParameters['policy-rag-app-service'].applicationSettings.value
    Assert ($hostSettings.AllowedHosts -eq 'test-host.azurewebsites.net') 'Hosted AllowedHosts is not exact.'
    Assert ($hostSettings.Rag__Azure__ManagedIdentityClientId -eq $runtime) 'Hosted managed identity is missing.'
    Assert ($hostSettings.BrowserSignIn__RedirectUri -eq 'https://test-host.azurewebsites.net/auth-callback.html') 'Hosted redirect is not HTTPS.'
    Assert ($fixtureParameters['policy-rag-user-identity'].browserRedirectUris.value.Count -eq 2) 'Local and hosted callbacks were not registered.'
    Assert (@($hostSettings.PSObject.Properties.Name | Where-Object { $_ -match 'SECRET|PASSWORD|CERTIFICATE' }).Count -eq 0) 'Hosted settings contain credentials.'
    $calls.Clear()
    & $script -ConfigurationPath $path -BackendPublicCertificatePath (Join-Path $temp 'runtime.cer') `
        -IndexerPublicCertificatePath (Join-Path $temp 'indexer.cer') -Confirm:$false
    Assert (@($calls | Where-Object { ($_[0] -eq 'rest' -and $_[2] -eq 'POST') -or
                ($_[0] -eq 'role' -and $_[2] -eq 'create') }).Count -eq 0) 'Rerun duplicated assignments.'
    $calls.Clear()
    $fixtureFailBuild = $true
    $stopped = $false
    try { & $script -ConfigurationPath $path -Confirm:$false } catch {
        if ($_.Exception.Message -notlike '*Azure CLI failed*') { throw }
        $stopped = $true
    }
    Assert $stopped 'A failed CLI command was ignored.'
    Assert ($calls.Count -eq 1) 'Execution continued after compilation failure.'
    $fixtureFailBuild = $false
    $calls.Clear()
    $config.tenantId = '<tenant-guid>'
    $config | ConvertTo-Json | Set-Content -LiteralPath $path
    $rejected = $false
    try { & $script -ConfigurationPath $path -ValidateOnly } catch {
        if ($_.Exception.Message -notlike '*nonempty GUIDs*') { throw }
        $rejected = $true
    }
    Assert $rejected 'Placeholder tenant ID was accepted.'
    Assert ($calls.Count -eq 0) 'Invalid configuration reached the CLI.'
    Write-Output 'Deployment script tests passed; all Azure CLI calls were mocked.'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
