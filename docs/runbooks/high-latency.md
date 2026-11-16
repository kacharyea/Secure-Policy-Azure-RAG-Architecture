# policy-rag-high-latency runbook

**Class:** Create work  **Severity:** 3  **Owner / backup:** from the alert payload

## 1. Trigger and impact
Azure-mode p95 workflow latency exceeded the threshold with enough samples (defaults 15000 ms and 5 samples). The threshold is provisional and is not a production benchmark. Users wait longer; no failures are implied.

## 2. Confirm
1. Workbook "Observed workflow latency": check samples and p95.
2. A low sample count or a single slow request is noise; a sustained p95 over several windows is real.

## 3. Diagnose
1. "Stage duration and failures": which stage (embedding, retrieval, generation) has the high p95?
2. Open a slow `OperationId` and read its stage timeline.
3. Check model deployment capacity and quota, and Search service load.

## 4. Remediate
Open a work item. Adjust capacity or the slow stage; no rollback unless a deployment correlates with the change.

## 5. Verify
p95 under threshold for two consecutive windows.

## 6. Escalate and close
Assign the work item with a due date. Learning loop: is the threshold right for your traffic?
