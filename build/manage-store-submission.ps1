# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Status', 'Delete', 'Commit')] [string] $Action,
    [Parameter(Mandatory)] [ValidatePattern('^\d+$')] [string] $SubmissionId,
    [string] $AppId = '9PN6D8G1K9WZ'
)

$ErrorActionPreference = 'Stop'
foreach ($name in 'STORE_TENANT_ID', 'STORE_CLIENT_ID', 'STORE_CLIENT_SECRET') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set." }
}
$token = (Invoke-RestMethod -Method Post "https://login.microsoftonline.com/$env:STORE_TENANT_ID/oauth2/token" -Body @{
    grant_type = 'client_credentials'
    client_id = $env:STORE_CLIENT_ID
    client_secret = $env:STORE_CLIENT_SECRET
    resource = 'https://manage.devcenter.microsoft.com'
}).access_token
$headers = @{ Authorization = "Bearer $token" }
$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId"
$app = Invoke-RestMethod $api -Headers $headers
$status = Invoke-RestMethod "$api/submissions/$SubmissionId/status" -Headers $headers
Write-Host "Submission $SubmissionId status: $($status.status)"
Write-Host "Pending submission: $($app.pendingApplicationSubmission.id)"
Write-Host "Published submission: $($app.lastPublishedApplicationSubmission.id)"
if ($status.statusDetails) { $status.statusDetails | ConvertTo-Json -Depth 10 | Write-Host }
if ($Action -eq 'Status') { return }

# Commit again a pending submission whose commit failed (e.g. a transient 409 InvalidState), keeping its package.
if ($Action -eq 'Commit') {
    if ($app.pendingApplicationSubmission.id -ne $SubmissionId -or $status.status -notin 'PendingCommit', 'CommitFailed') {
        throw 'Only the current pending submission in PendingCommit or CommitFailed can be committed.'
    }
    Invoke-RestMethod -Method Post "$api/submissions/$SubmissionId/commit" -Headers $headers -ContentType 'application/json' -Body '' | Out-Null
    for ($i = 0; $i -lt 60; $i++) {
        $status = Invoke-RestMethod "$api/submissions/$SubmissionId/status" -Headers $headers
        if ($status.status -ne 'CommitStarted') { break }
        Start-Sleep -Seconds 10
    }
    Write-Host "Submission $SubmissionId status: $($status.status)"
    if ($status.status -like '*Failed') {
        $status.statusDetails | ConvertTo-Json -Depth 10 | Write-Host
        throw 'Store submission failed.'
    }
    return
}

# Delete only the exact pending submission explicitly named by the operator.
if ($app.lastPublishedApplicationSubmission.id -eq $SubmissionId -or $status.status -eq 'Published') {
    throw 'This submission is already published and cannot be withdrawn with this operation.'
}
if ($app.pendingApplicationSubmission.id -ne $SubmissionId) {
    throw 'The requested submission is not the current pending submission; nothing was deleted.'
}
Invoke-RestMethod -Method Delete "$api/submissions/$SubmissionId" -Headers $headers | Out-Null
for ($attempt = 0; $attempt -lt 6; $attempt++) {
    $app = Invoke-RestMethod $api -Headers $headers
    if ($app.pendingApplicationSubmission.id -ne $SubmissionId) {
        Write-Host "Submission $SubmissionId removed. Pending submission: $($app.pendingApplicationSubmission.id)"
        return
    }
    Start-Sleep -Seconds 5
}
throw 'The submission still appears pending after the deletion request; verify Partner Center before resubmitting.'
