#Requires -Modules Az.Accounts
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][Guid]$TenantId,
    [Parameter(Mandatory = $true)][Guid]$BrowserServicePrincipalId,
    [Parameter(Mandatory = $true)][Guid]$ApiServicePrincipalId,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][Guid[]]$PrincipalIds
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$context = Get-AzContext
if (-not $context -or $context.Tenant.Id -ne $TenantId.ToString('D')) {
    throw 'Select the approved tenant with Set-AzContext before provisioning consent.'
}
$approvedIds = @($PrincipalIds | ForEach-Object { $_.ToString('D') } | Sort-Object -Unique)
if ($TenantId -eq [Guid]::Empty -or $BrowserServicePrincipalId -eq [Guid]::Empty -or
    $ApiServicePrincipalId -eq [Guid]::Empty -or [Guid]::Empty.ToString('D') -in $approvedIds) {
    throw 'Tenant, service-principal and approved user IDs must be nonempty GUIDs.'
}

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$http = [Net.Http.HttpClient]::new($handler)
function Invoke-Graph([string]$Method, [string]$Path, $Body = $null) {
    $uri = if ($Path.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        $Path
    } else { "https://graph.microsoft.com/v1.0/$Path" }
    if (-not $uri.StartsWith('https://graph.microsoft.com/v1.0/', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing a Graph request outside the approved HTTPS API origin.'
    }
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $uri)
    $response = $null
    try {
        if ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(
                ($Body | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Graph $Method failed with HTTP $([int]$response.StatusCode). Inspect actual grants before retrying."
        }
        return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }
}
function Get-BrowserGrants {
    $items = [Collections.Generic.List[object]]::new()
    $path = "oauth2PermissionGrants?`$filter=clientId eq '$BrowserServicePrincipalId'"
    do {
        $page = Invoke-Graph 'GET' $path
        foreach ($item in $page.value) { $items.Add($item) }
        $path = $page.'@odata.nextLink'
    } while ($path)
    return ,@($items.ToArray())
}
function Assert-ApprovedGrants([object[]]$Grants) {
    foreach ($grant in $Grants) {
        if ($grant.clientId -ne $BrowserServicePrincipalId.ToString('D') -or
            $grant.resourceId -ne $ApiServicePrincipalId.ToString('D') -or
            $grant.consentType -cne 'Principal' -or $grant.principalId -notin $approvedIds -or
            $grant.scope -cne 'PolicyReview.Invoke') {
            throw 'Unexpected browser grant. Review it explicitly; no grant will be replaced or broadened.'
        }
    }
    if (@($Grants | Group-Object principalId | Where-Object Count -gt 1).Count -gt 0) {
        throw 'Duplicate Principal grants require explicit investigation; no grants will be changed.'
    }
}

try {
    $token = Get-AzAccessToken -ResourceUrl 'https://graph.microsoft.com/' -TenantId $TenantId.ToString('D')
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($token.Token)
    try {
        $http.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new(
            'Bearer', [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer))
    }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }

    $api = Invoke-Graph 'GET' "servicePrincipals/$ApiServicePrincipalId"
    $null = Invoke-Graph 'GET' "servicePrincipals/$BrowserServicePrincipalId"
    if (@($api.oauth2PermissionScopes | Where-Object {
        $_.value -ceq 'PolicyReview.Invoke' -and $_.isEnabled
    }).Count -ne 1) {
        throw 'The target API must expose exactly one enabled PolicyReview.Invoke scope.'
    }
    foreach ($principalId in $approvedIds) {
        $user = Invoke-Graph 'GET' ("users/$principalId" + '?$select=id,accountEnabled,userType')
        if ($user.id -ne $principalId -or -not $user.accountEnabled -or $user.userType -ne 'Member') {
            throw "Approved principal $principalId is not an enabled Member user."
        }
    }
    $grants = Get-BrowserGrants
    Assert-ApprovedGrants $grants
    foreach ($principalId in $approvedIds) {
        if (@($grants | Where-Object principalId -eq $principalId).Count -eq 0 -and
            $PSCmdlet.ShouldProcess($principalId, 'Create Principal consent for PolicyReview.Invoke only')) {
            $null = Invoke-Graph 'POST' 'oauth2PermissionGrants' @{
                clientId = $BrowserServicePrincipalId.ToString('D')
                resourceId = $ApiServicePrincipalId.ToString('D')
                consentType = 'Principal'
                principalId = $principalId
                scope = 'PolicyReview.Invoke'
            }
        }
    }
    if ($WhatIfPreference) {
        Write-Output 'Consent dry run complete; no grants written.'
        return
    }
    $verified = Get-BrowserGrants
    Assert-ApprovedGrants $verified
    if ($verified.Count -ne $approvedIds.Count) {
        throw 'Consent is incomplete. Inspect actual grants before retrying.'
    }
    foreach ($grant in $verified) {
        Write-Output "Verified Principal consent: $($grant.principalId), grant $($grant.id)."
    }
}
finally { $http.Dispose() }
