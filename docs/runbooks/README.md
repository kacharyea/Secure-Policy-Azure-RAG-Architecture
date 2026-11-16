# Policy RAG runbooks

Same six-section format as the Hosted Agent runbooks: **1 Trigger and impact, 2 Confirm, 3 Diagnose, 4 Remediate, 5 Verify, 6 Escalate and close.** Ownership and escalation are draft examples; replace them with your organization's.

| Alert | Class | Severity | Runbook |
|---|---|---|---|
| `policy-rag-unexpected-errors` | Page | 2 | [unexpected-errors](unexpected-errors.md) |
| `policy-rag-high-latency` | Create work | 3 | [high-latency](high-latency.md) |
| `policy-rag-evaluation-regression` | Create work | 3 | [evaluation-regression](evaluation-regression.md) |

Expected access denials (`rag.authorization == Denied`) are not outages and are excluded. Alerts watch Azure-mode traffic only.

Workbook: "Policy RAG - monitoring and tracing". Every runbook pivots on `OperationId` from the "Recent workflow trace IDs" panel.

## Demo trick: make the latency alert fire on demand

Redeploy `infra/monitoring.bicep` with a very low `latencyThresholdMs` (for example 100) and `minimumLatencySamples` 1, send a few Azure-mode questions, and the high-latency alert fires on the next evaluation (about 5 to 10 minutes including ingestion). Restore the defaults (15000 and 5) afterwards.
