# policy-rag-unexpected-errors runbook

**Class:** Page  **Severity:** 2  **Owner / backup:** from the alert payload

## 1. Trigger and impact
At least one Azure-mode `policy.workflow` failed in five minutes, excluding expected access denials. A user's question got an error instead of an answer.

## 2. Confirm
1. Workbook "Unexpected error percentage": is it one isolated failure or a sustained rate?
2. Confirm the row is not a denial (`rag.authorization` must not be `Denied`).
3. Declare if the rate is sustained or more than one user is affected.

## 3. Diagnose
1. Copy the `OperationId` from "Recent workflow trace IDs".
2. Use "Stage duration and failures" to find the stage with non-completed outcomes (retrieval, embedding, generation).
3. Read that operation in Logs (`AppRequests`, `AppDependencies`, `AppExceptions`). Check Search and model availability, quota, and recent deployments.

## 4. Remediate
Fix the failing dependency (quota, configuration, permissions). If a recent app deployment caused it, redeploy the previous build with approval. Stop and escalate if the previous build also fails.

## 5. Verify
Error percentage returns to 0 on new Azure-mode requests and a test question returns an answered status.

## 6. Escalate and close
Escalate to the service owner for a sustained Page. Record timeline, action, approver and follow-up owner. Learning loop: should this failure type become a clearer user-facing message or a separate alert?
