# Agent-first comparison (synthetic data only)

An isolated Python comparison with the existing app-controlled RAG. No existing
application, configuration, Search index, or documents are changed.

## Current verification status

Live setup succeeded on November 4, 2026, and a second run reused both resources:

- Created an **Entra/AAD** project connection named `policy-agent-first-search`.
- Granted only the **project managed identity** **Search Index Data Reader** on
  the existing Search service. No Contributor roles or keys were added.
- Created/reused `policy-agent-first-demo`, and verified version-only deletion.
- **Not presentation-ready:** Q1 returned an intention to search, without an
  observed Search call or citations. Q2 then failed with **HTTP 400**,
  **`tool_user_error`**: "Access denied. Check your permissions or managed identity
  access to the search service."
- Stopped without relaxing access or networking. No grounded answers or successful
  reviewer-content retrieval have been demonstrated.

Role assignments can take **5-15 minutes** to propagate. There is also an important
identity mismatch: current [Microsoft Learn guidance][search-tool] describes the
**Foundry account system identity** for keyless Search and documents Contributor
roles. This demo deliberately grants only the **project identity's Reader role**,
as requested. That narrower grant is not proof the tool uses that identity.
Foundry also has outbound network restrictions enabled. The service error does
not identify which condition caused the denial; do not claim a confirmed root
cause or add broader roles as a workaround.

## Existing resources and schema

| Resource | Existing name |
|---|---|
| Resource group | `ailab-demo-eastus2` |
| Foundry account / project | `ai-policy-xqztycjhejs32` / `secure-policy-rag` |
| Model deployment | `gpt-5-mini` |
| Search service / index | `srch-policy-xqztycjhejs32` / `policy-chunks` |

The project endpoint is discovered from Azure, not constructed from identifiers.
The live index has **7 chunks**, **no vectorizer**, and **no semantic
configuration**. The tool therefore uses **SIMPLE**, top **5**, without a filter.
Setup selects SEMANTIC only if an existing semantic configuration is present.
It never enables semantic search, creates a vectorizer, or embeds queries.

| Field | Type | Purpose |
|---|---|---|
| `id` | String, key | Chunk/source ID |
| `documentId` | String | Parent document ID |
| `title` | String | Source title |
| `version` | String | Source version |
| `content` | String | Evidence passage |
| `kind` | String | Document classification |
| `allowedGroups` | String collection | App permission filter |
| `vector` | Single collection, 1536 dimensions | Existing app embeddings; not used here |

All fields except `vector` are retrievable. There is **no URL field**. The CLI
prints native citation annotations when returned and separately labels
model-reported source IDs as **not server-validated**. It does not invent links
or treat generated source labels as verified evidence.

## Prerequisites

- Python 3.10+ and PowerShell 7; Azure CLI signed in with `az login` as an
  approved user in the correct subscription.
- Existing caller permissions to inspect the index/schema, manage project
  connections and agents, use the model, and assign the requested Search Reader
  role. The script does not grant caller roles.
- Existing project managed identity and populated index. No new Azure services.
- `DefaultAzureCredential` uses your available developer credential. For this
  demo, remove unintended `AZURE_*` service-principal credentials so its selected
  credential matches your approved CLI user. Do not paste credentials into files.

Run from this directory. Dependencies match the reference workshop's direct pins:
`azure-ai-projects==2.2.0`, `openai==2.44.0`, `azure-identity==1.25.3`.

```powershell
python -m venv .venv
& .\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
.\setup.ps1
& .\.venv\Scripts\python.exe .\agent_first_demo.py
```

On this machine, the `python` command is a Store alias. Dependencies have already
been installed into the ignored `.packages` directory using Azure CLI's bundled
Python, without changing its global packages. The ready-to-run alternative is:

```powershell
$python = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\python.exe'
# Only needed if this folder's .packages directory is missing:
& $python -m pip install --target .\.packages -r .\requirements.txt
.\setup.ps1
& $python .\agent_first_demo.py
```

Setup prints names/schema only and writes ignored `config.local.json` without
subscription IDs, client/principal IDs, keys, or tokens. Its only cloud writes are
the Entra connection and the project identity's Search Reader assignment.
Do not enable SDK HTTP/debug logging during the customer demo.

## Questions and teaching point

Omit the question argument to run both independently. Each gets a new
conversation; execution stops on an unsuccessful run.

```powershell
& $python .\agent_first_demo.py 'What are the privileged access review requirements?'
& $python .\agent_first_demo.py 'How long must privileged access review evidence be retained, and who approves access review exceptions?'
```

- **Q1** is exactly the app's Case 1 question. That scenario uses **Sam, the
  security reviewer**, not Alex the employee. Its reviewer-authorized evidence
  includes review every 90 days and an independent reviewer (`access-review-1`).
- **Q2** targets the reviewer-only retention/exception passage
  (`access-review-2`): retain evidence for **12 months**, with exceptions approved
  by the **control owner**. Alex has only `employees`; an explicit request for
  this document is denied by the app before downstream AI calls.

If service-side Search access succeeds, the agent can retrieve reviewer-only
content regardless of whether the presenter is acting as Alex. **The employee's
identity is not forwarded**, and `allowedGroups` is not filtered. The intended
contrast is service-identity access versus the app's code-enforced per-user
authorization, not a bypass of Azure's service-level RBAC. Current platform
documentation's account/project identity distinction is noted above.

Tool selection is automatic; no `tool_choice` override or pre-retrieval is used.
The agent instructions require evidence for policy questions, but announcing
"I will search" is not a tool invocation. The CLI inspects Responses output for
`azure_ai_search_call`, prints its status, answers, and citation/source IDs, and
exits nonzero on missing retrieval, failed responses, or uncited substantive
answers. This Responses-based implementation does not invent legacy run steps.

## Cleanup and local checks

```powershell
& $python -m unittest discover -s . -p 'test_agent_first_demo.py' -v
& $python .\agent_first_demo.py --cleanup
```

Cleanup deletes **only the version recorded in ignored `agent-state.local.json`**
after verifying the project, demo ownership marker, and definition hash.
It never deletes the index, documents, project connection, Reader grant, or
conversations/threads. It does not delete other agent versions. Keep the local
state until cleanup; a subsequent run creates or reuses the demo again.

## Limitations

- **No per-user permission trimming:** service identity only. Intentional.
- **Direct Foundry model calls, not APIM:** gateway logging/policies do not apply.
- **No server-side exact-quote validation or Pending review status:** those are
  application controls, not capabilities demonstrated by this agent.
- **Synthetic data only.** Never use customer/restricted production content.
- Prompt instructions are not an authorization boundary or a demonstrated
  prompt-injection defense. Source IDs remain unvalidated model output unless
  native annotations are separately present; even those do not prove exact quotes.
- Live retrieval remains blocked as described above. No fixture, key, networking,
  index-edit, or broader-role fallback is implemented.

API pattern: [reference notebook][notebook], section 2 agent creation,
`responses.create` with `agent_reference`, and agent `delete_version` only.
Its index creation, upload, embedding, API-key connection, and index cleanup cells
are deliberately excluded. See also [agent version APIs][versions].

[search-tool]: https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/ai-search
[notebook]: https://github.com/dhangerkapil/agentic-ai-immersion/blob/main/azure-ai-agents/5-agents-aisearch.ipynb
[versions]: https://learn.microsoft.com/en-us/python/api/azure-ai-projects/azure.ai.projects.operations.agentsoperations?view=azure-python
