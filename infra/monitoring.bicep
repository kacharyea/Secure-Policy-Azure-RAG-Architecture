targetScope = 'resourceGroup'

@description('Existing Application Insights. Shared authentication and retention policies are not changed.')
param insightsName string
param workspaceName string
param location string = resourceGroup().location

@description('Optional approved telemetry publishing user/service-principal object ID. Not persisted in parameters files.')
param telemetryPublisherObjectId string = ''
@allowed(['User', 'ServicePrincipal'])
param telemetryPublisherPrincipalType string = 'User'

@description('Portal-visible alerts by default; supply approved action groups for notification delivery.')
param actionGroupResourceIds array = []
param enableAlerts bool = true
@minValue(1)
param latencyThresholdMs int = 15000
@minValue(1)
param minimumLatencySamples int = 5

resource insights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: insightsName
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(telemetryPublisherObjectId)) {
  name: guid(insights.id, telemetryPublisherObjectId, 'telemetry-publisher')
  scope: insights
  properties: {
    principalId: telemetryPublisherObjectId
    principalType: telemetryPublisherPrincipalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

var requests = 'AppRequests\n| where _ResourceId =~ "${insights.id}"\n| where Name == "policy.workflow"'
var dependencies = 'AppDependencies\n| where _ResourceId =~ "${insights.id}"'
var panels = [
  {
    title: 'Workflow outcomes - fixture and live are separate'
    visualization: 'table'
    query: concat(requests, '''

| where TimeGenerated > ago(1h)
| extend Mode=tostring(Properties["rag.mode"]), Auth=tostring(Properties["rag.authorization"])
| summarize Requests=count(), ExpectedDenials=countif(Auth == "Denied"),
    UnexpectedErrors=countif(Success == false and Auth != "Denied"),
    Answered=countif(tostring(Properties["rag.answer_status"]) == "answered"),
    InsufficientEvidence=countif(tostring(Properties["rag.answer_status"]) == "insufficient_evidence") by Mode
''')
  }
  {
    title: 'Request volume'
    visualization: 'timechart'
    query: concat(requests, '''

| where TimeGenerated > ago(1h)
| summarize Requests=count() by bin(TimeGenerated, 5m), Mode=tostring(Properties["rag.mode"])
''')
  }
  {
    title: 'Unexpected error percentage - expected denials excluded'
    visualization: 'timechart'
    query: concat(requests, '''

| where TimeGenerated > ago(1h)
| where tostring(Properties["rag.authorization"]) != "Denied"
| summarize Requests=count(), Errors=countif(Success == false) by bin(TimeGenerated, 5m), Mode=tostring(Properties["rag.mode"])
| extend ErrorPercent=100.0 * Errors / Requests
| project TimeGenerated, Mode, ErrorPercent
''')
  }
  {
    title: 'Observed workflow latency - denials excluded, not a production benchmark'
    visualization: 'table'
    query: concat(requests, '''

| where TimeGenerated > ago(1h)
| where tostring(Properties["rag.authorization"]) != "Denied"
| summarize Samples=count(), P50Ms=percentile(DurationMs, 50), P95Ms=percentile(DurationMs, 95) by Mode=tostring(Properties["rag.mode"])
''')
  }
  {
    title: 'Stage duration and failures'
    visualization: 'table'
    query: concat(dependencies, '''

| where TimeGenerated > ago(1h)
| where Name startswith "policy." and Name != "policy.evaluation_run"
| summarize Samples=count(), P50Ms=percentile(DurationMs, 50), P95Ms=percentile(DurationMs, 95),
    NonCompleted=countif(tostring(Properties["rag.outcome"]) != "Completed")
    by Mode=tostring(Properties["rag.mode"]), Stage=tostring(Properties["rag.stage"])
''')
  }
  {
    title: 'Reported provider tokens only - missing usage is unknown, not zero'
    visualization: 'table'
    query: concat(dependencies, '''

| where TimeGenerated > ago(1h)
| where Name in ("policy.embedding", "policy.generation")
| extend Input=tolong(Properties["gen_ai.usage.input_tokens"]), Output=tolong(Properties["gen_ai.usage.output_tokens"])
| where isnotnull(Input) or isnotnull(Output)
| summarize InputReports=countif(isnotnull(Input)), ReportedInput=sum(Input),
    OutputReports=countif(isnotnull(Output)), ReportedOutput=sum(Output)
    by Mode=tostring(Properties["rag.mode"]), Stage=tostring(Properties["rag.stage"])
| extend InputTokens=iff(InputReports == 0, long(null), ReportedInput),
    OutputTokens=iff(OutputReports == 0, long(null), ReportedOutput)
| project Mode, Stage, InputReports, InputTokens, OutputReports, OutputTokens
''')
  }
  {
    title: 'Deterministic batch checks - NOT semantic or harmful-content scores'
    visualization: 'table'
    query: concat(dependencies, '''

| where TimeGenerated > ago(24h)
| where Name == "policy.evaluation_run"
| project TimeGenerated, Mode=tostring(Properties["rag.mode"]), Variant=tostring(Properties["rag.prompt_variant"]),
    Experiment=tostring(Properties["rag.experiment_id"]), Decision=tostring(Properties["rag.evaluation.decision"]),
    Total=tolong(Properties["rag.evaluation.total"]), Passed=tolong(Properties["rag.evaluation.pass"]),
    Failed=tolong(Properties["rag.evaluation.fail"]), Blocked=tolong(Properties["rag.evaluation.blocked"]),
    NotRun=tolong(Properties["rag.evaluation.not_run"]), SecurityFailures=tolong(Properties["rag.evaluation.security_failures"])
| order by TimeGenerated desc
''')
  }
  {
    title: 'Recent workflow trace IDs - copy OperationId into Logs or transaction search'
    visualization: 'table'
    query: concat(requests, '''

| where TimeGenerated > ago(1h)
| project TimeGenerated, OperationId, Mode=tostring(Properties["rag.mode"]),
    Authorization=tostring(Properties["rag.authorization"]), DurationMs, Success,
    AnswerStatus=tostring(Properties["rag.answer_status"]), Validation=tostring(Properties["rag.validation"])
| top 50 by TimeGenerated desc
''')
  }
]

var queryItems = [for (panel, index) in panels: {
  type: 3
  name: 'policy-query-${index}'
  content: {
    version: 'KqlItem/1.0'
    title: panel.title
    query: panel.query
    queryType: 0
    resourceType: 'microsoft.operationalinsights/workspaces'
    crossComponentResources: [workspace.id]
    visualization: panel.visualization
    size: 0
  }
}]

resource workbook 'Microsoft.Insights/workbooks@2022-04-01' = {
  name: guid(insights.id, 'policy-rag-monitoring')
  location: location
  kind: 'shared'
  properties: {
    displayName: 'Policy RAG - monitoring and tracing'
    category: 'workbook'
    sourceId: insights.id
    version: '1.0'
    serializedData: string({
      version: 'Notebook/1.0'
      isLocked: false
      fallbackResourceIds: [workspace.id]
      items: concat([
        {
          type: 1
          name: 'scope-and-limitations'
          content: {
            json: '# Policy RAG monitoring and tracing\nScoped to this Application Insights component and policy.* workflow spans. Most views show the last hour; evaluation summaries show 24 hours. Fixture stages are local/scripted even when telemetry is really ingested in Azure. Missing provider usage is unknown. Deterministic evaluations are not semantic quality or harmful-content assessments. No prompts, policy text, users or groups are exported. Expected access denials are not outages. Alert rules watch Azure-mode traffic only; notification delivery requires an action group.'
          }
        }
      ], queryItems)
    })
  }
}

var alerts = [
  {
    name: 'policy-rag-unexpected-errors'
    description: 'At least one failed Azure workflow in five minutes; expected access denials excluded.'
    severity: 2
    query: concat(requests, '''

| where TimeGenerated > ago(5m)
| where tostring(Properties["rag.mode"]) == "Azure"
| where tostring(Properties["rag.authorization"]) != "Denied" and Success == false
''')
  }
  {
    name: 'policy-rag-high-latency'
    description: 'Azure p95 workflow latency exceeds the provisional demo threshold, with a minimum sample count.'
    severity: 3
    query: concat(requests, '\n| where TimeGenerated > ago(5m)\n| where tostring(Properties["rag.mode"]) == "Azure"\n| where tostring(Properties["rag.authorization"]) != "Denied"\n| summarize Samples=count(), P95Ms=percentile(DurationMs, 95)\n| where Samples >= ${minimumLatencySamples} and P95Ms > ${latencyThresholdMs}')
  }
  {
    name: 'policy-rag-evaluation-regression'
    description: 'A completed Azure-mode deterministic batch has failed checks. Not an LLM quality/safety judge.'
    severity: 3
    query: concat(dependencies, '''

| where TimeGenerated > ago(5m)
| where Name == "policy.evaluation_run" and tostring(Properties["rag.mode"]) == "Azure"
| where tolong(Properties["rag.evaluation.fail"]) > 0 or tolong(Properties["rag.evaluation.security_failures"]) > 0
''')
  }
]

resource rules 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for alert in alerts: {
  name: alert.name
  location: location
  kind: 'LogAlert'
  properties: {
    displayName: alert.name
    description: alert.description
    enabled: enableAlerts
    severity: alert.severity
    scopes: [workspace.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      allOf: [{
        query: alert.query
        timeAggregation: 'Count'
        operator: 'GreaterThan'
        threshold: 0
        failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
      }]
    }
    actions: { actionGroups: actionGroupResourceIds }
  }
}]

output workbookDisplayName string = workbook.properties.displayName
output alertNames array = [for alert in alerts: alert.name]
