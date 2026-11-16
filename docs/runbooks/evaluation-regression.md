# policy-rag-evaluation-regression runbook

**Class:** Create work  **Severity:** 3  **Owner / backup:** from the alert payload

## 1. Trigger and impact
A completed Azure-mode deterministic evaluation batch has failed checks or security failures. These are deterministic checks, not semantic quality or harmful-content scores. Users are not directly affected yet; a release or prompt change may be.

## 2. Confirm
1. Workbook "Deterministic batch checks": read Decision, Failed, SecurityFailures and Variant.
2. Check whether the experiment was an intentional change (a new prompt variant) or a regression.

## 3. Diagnose
1. Compare the failing run with the last passing run for the same Variant.
2. Identify the failed cases and whether any are security failures; treat security failures as higher priority.
3. Correlate with recent prompt, retrieval or model changes.

## 4. Remediate
Block promotion of the failing variant. Fix the prompt or retrieval change, or revert it, then rerun the batch. Security failures need the service owner before any release.

## 5. Verify
A rerun completes with zero failed and zero security failures.

## 6. Escalate and close
Record the cause and owner. Learning loop: do the checks cover the failure that was found, or is a new case needed?
