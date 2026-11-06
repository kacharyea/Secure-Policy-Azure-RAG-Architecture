"use strict";
const element = id => document.getElementById(id);
let config;
let explicitDocument = null;
let busy = false;
let revision = 0;
let msalClient;

async function headers() {
  const result = { "Content-Type": "application/json" };
  if (config.authenticationMode === "Synthetic") result["X-Demo-Profile"] = element("profile").value;
  else if (msalClient) {
    const account = msalClient.getActiveAccount();
    if (!account) throw new Error("Sign in with Microsoft before requesting documents.");
    try {
      const token = await msalClient.acquireTokenSilent({ scopes: [config.signIn.scope], account });
      result.Authorization = `Bearer ${token.accessToken}`;
    } catch (error) {
      clearIdentityView();
      throw new Error(`Token acquisition failed (${authCode(error)}). Sign in again; no fixture identity was used.`);
    }
  }
  else if (element("token").value.trim()) result.Authorization = `Bearer ${element("token").value.trim()}`;
  return result;
}

function status(message, error = false) {
  element("status").textContent = message;
  element("status").classList.toggle("error", error);
}

function evidence(value) {
  const target = element("evidence");
  target.replaceChildren();
  for (const [name, content] of Object.entries(value || {})) {
    const key = document.createElement("dt");
    const detail = document.createElement("dd");
    key.textContent = name;
    detail.textContent = Array.isArray(content) ? content.join(", ") || "(none)" : String(content);
    target.append(key, detail);
  }
}

async function request(path, options = {}) {
  const response = await fetch(path, { ...options, headers: await headers(), cache: "no-store" });
  let value;
  const text = await response.text();
  try { value = text ? JSON.parse(text) : {}; }
  catch { throw new Error(`The API returned an unreadable response (HTTP ${response.status}).`); }
  if (!response.ok) {
    if (response.status === 401) clearIdentityView();
    if (value.evidence) evidence(value.evidence);
    const error = new Error(`${value.title || response.statusText || "Request rejected"} (HTTP ${response.status})`);
    error.correlation = value.correlationId || response.headers.get("X-Correlation-ID");
    throw error;
  }
  return value;
}

function reset() {
  revision++;
  evidence({ run: "No request for this identity/input yet" });
}

function clearTranscript() {
  revision++;
  element("transcript").replaceChildren();
  const empty = document.createElement("p");
  empty.id = "transcript-empty"; empty.className = "chat-empty";
  empty.textContent = "What would you like to know about your policies? Ask a complete question to see an answer and its supporting sources.";
  element("transcript").append(empty);
  evidence({ run: "No request for this identity yet" });
}

function lock(value) {
  busy = value;
  for (const control of document.querySelectorAll("button, select, textarea, input")) control.disabled = value;
  if (!value) element("sign-out").disabled = !msalClient?.getActiveAccount();
}

function renderTurnResult(bubble, value) {
  bubble.className = "bubble-answer";
  bubble.replaceChildren();
  const speaker = document.createElement("span"); speaker.className = "speaker"; speaker.textContent = "POLICY ASSISTANT";
  const answer = document.createElement("p"); answer.className = "answer"; answer.textContent = value.answer;
  bubble.append(speaker, answer);
  if (value.evidenceGaps?.length) {
    const gapsHeading = document.createElement("strong"); gapsHeading.textContent = "Evidence gaps / limitations";
    const gaps = document.createElement("ul");
    gaps.replaceChildren(...value.evidenceGaps.map(gap => { const li = document.createElement("li"); li.textContent = gap; return li; }));
    bubble.append(gapsHeading, gaps);
  }
  if (value.citations?.length) {
    const citationsHeading = document.createElement("strong"); citationsHeading.textContent = `Sources (${value.citations.length}) - expand to verify`;
    bubble.append(citationsHeading);
    for (const [index, source] of value.citations.entries()) {
      const box = document.createElement("details"); box.className = "citation";
      const title = document.createElement("summary"); title.textContent = `[${index + 1}] ${source.title}`;
      const metadata = document.createElement("p"); metadata.className = "source-meta";
      metadata.textContent = `Document: ${source.documentId} · Version: ${source.version} · Chunk: ${source.chunkId}`;
      const quote = document.createElement("p"); quote.textContent = source.quote;
      box.append(title, metadata, quote); bubble.append(box);
    }
  }
  const evidenceLine = document.createElement("p"); evidenceLine.className = "turn-evidence";
  evidenceLine.textContent = `${value.status} · review=${value.reviewStatus} · mode=${value.evidence.mode} · authorization=${value.evidence.authorization} · calls(embed/gen/search)=${value.evidence.embeddingCalls}/${value.evidence.generationCalls}/${value.evidence.searchCalls}`;
  bubble.append(evidenceLine);
  evidence(value.evidence);
  status(`${config.mode === "Fixture" ? "Scripted fixture result; not evidence of live model behavior." : "Live dependency result; verify relevance and supporting evidence."}`);
  if (value.status === "answered" && value.reviewStatus === "Pending") {
    const reviewBox = document.createElement("div"); reviewBox.className = "turn-review";
    const accept = document.createElement("button"); accept.type = "button"; accept.textContent = "Accept for draft use";
    const reject = document.createElement("button"); reject.type = "button"; reject.className = "secondary"; reject.textContent = "Reject draft";
    reviewBox.append(accept, reject); bubble.append(reviewBox);
    const review = async decision => {
      if (busy) return;
      lock(true);
      try { renderTurnResult(bubble, await request(`/api/drafts/${value.id}/review`, { method: "POST", body: JSON.stringify({ decision }) })); }
      catch (error) { status(error.message, true); }
      finally { lock(false); }
    };
    accept.addEventListener("click", () => review("accept"));
    reject.addEventListener("click", () => review("reject"));
  }
}

function renderTurnError(bubble, error) {
  bubble.className = "bubble-answer error";
  bubble.textContent = `${error.message}${error.correlation ? `\nCorrelation: ${error.correlation}` : ""}`;
  status(error.message, true);
}

function scrollConversation() {
  const transcript = element("transcript");
  transcript.scrollTop = transcript.scrollHeight;
}

async function loadDocuments() {
  reset();
  explicitDocument = null;
  const current = revision;
  try {
    const documents = await request("/api/documents");
    if (current !== revision) return;
    const choices = documents.map(doc => {
      const option = document.createElement("option"); option.value = doc.id; option.textContent = doc.title; return option;
    });
    const all = document.createElement("option"); all.value = ""; all.textContent = "All authorized policies";
    element("document").replaceChildren(all, ...choices);
    status(`${documents.length} permitted policies. Choose a case or ask a question.`);
  } catch (error) {
    element("document").replaceChildren();
    status(error.message, true);
  }
}

element("ask-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (busy) return;
  const question = element("question").value.trim();
  if (!question) {
    status("Enter a policy question before sending.", true);
    element("question").focus();
    return;
  }
  const documentId = explicitDocument || element("document").value || null;
  lock(true);
  element("transcript-empty")?.remove();
  const turn = document.createElement("div"); turn.className = "turn";
  const questionBubble = document.createElement("div"); questionBubble.className = "bubble-question";
  const speaker = document.createElement("span"); speaker.className = "speaker"; speaker.textContent = "YOU";
  const meta = document.createElement("span"); meta.className = "meta";
  meta.textContent = documentId ? `Document: ${documentId}` : "Document: all authorized policies";
  const questionText = document.createElement("span"); questionText.textContent = question;
  questionBubble.append(speaker, meta, questionText);
  const answerBubble = document.createElement("div"); answerBubble.className = "bubble-answer pending";
  answerBubble.textContent = "Checking access and preparing a draft…";
  turn.append(questionBubble, answerBubble);
  element("transcript").append(turn);
  element("transcript").setAttribute("aria-busy", "true");
  scrollConversation();
  element("question").value = "";
  try {
    renderTurnResult(answerBubble, await request("/api/ask", {
      method: "POST", body: JSON.stringify({ question, documentId })
    }));
  } catch (error) { renderTurnError(answerBubble, error); }
  finally {
    lock(false);
    element("transcript").setAttribute("aria-busy", "false");
    scrollConversation();
    element("question").focus();
  }
});

element("question").addEventListener("keydown", event => {
  if (event.key === "Enter" && !event.shiftKey && !event.isComposing && event.keyCode !== 229) {
    event.preventDefault();
    if (!busy) element("ask-form").requestSubmit();
  }
});
element("new-chat").addEventListener("click", () => {
  if (busy) return;
  clearTranscript();
  explicitDocument = null;
  element("question").value = "";
  status("New chat. Local conversation cleared; server drafts are unchanged. Identity and selected document scope are unchanged.");
  element("question").focus();
});
for (const button of document.querySelectorAll("[data-prompt]")) {
  button.addEventListener("click", () => {
    if (busy) return;
    explicitDocument = null;
    element("question").value = button.dataset.prompt;
    status("Suggested question ready. Your identity and selected document scope are unchanged. Press Send.");
    element("question").focus();
  });
}

element("profile").addEventListener("change", () => { clearTranscript(); loadDocuments(); });
element("token").addEventListener("input", clearIdentityView);
element("load-documents").addEventListener("click", loadDocuments);
element("document").addEventListener("change", () => { explicitDocument = null; });
for (const button of document.querySelectorAll("[data-case]")) {
  button.addEventListener("click", async () => {
    if (busy) return;
    lock(true);
    try {
      const name = button.dataset.case;
      const synthetic = config.authenticationMode === "Synthetic";
      if (synthetic) {
        const profile = name === "denied" || name === "implicit" ? "alex" : "sam";
        if (element("profile").value !== profile) clearTranscript();
        element("profile").value = profile;
      }
      await loadDocuments();
      if (name === "implicit") {
        element("question").value = "What are the privileged access review requirements?";
        status(!synthetic
          ? "Prepared: free-text question, no document selected. Your real account was not switched. Press Send."
          : "Prepared: Alex asks without naming a document; the permission filter excludes restricted chunks. Press Send.");
      } else {
        const id = !synthetic && name === "normal"
          ? element("document").options[1]?.value
          : { normal: "access-review", denied: "access-review", injection: "injection-policy", missing: "incomplete-review" }[name];
        if (!id) throw new Error("No permitted document is available. Sign in and check group membership.");
        explicitDocument = id;
        element("document").value = name === "denied" ? "" : id;
        element("question").value = name === "missing"
          ? "Does this privileged access review excerpt prove the control was performed?"
          : !synthetic && name === "normal" ? "What does this policy require?"
          : "What are the privileged access review requirements?";
        status(!synthetic
          ? `Prepared: ${name}, document ${id}. Your real account was not switched. Press Send.`
          : name === "denied"
          ? "Prepared: Alex explicitly requests restricted document access-review. Press Send."
          : `Prepared: ${name}. Press Send.`);
      }
    } catch (error) { status(error.message, true); }
    finally { lock(false); }
  });
}

function authCode(error) {
  return typeof error?.errorCode === "string" && /^[a-z0-9_]+$/i.test(error.errorCode)
    ? error.errorCode : "authentication_error";
}

function clearIdentityView() {
  explicitDocument = null;
  element("document").replaceChildren();
  clearTranscript();
}

async function initializeSignIn() {
  if (new URL(config.signIn.redirectUri).origin !== location.origin)
    throw new Error(`Open the application at the registered origin: ${new URL(config.signIn.redirectUri).origin}/.`);
  await new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = "/vendor/msal/lib/msal-browser.min.js";
    script.integrity = "sha256-K1gBZQZKak7AQevUSDIpkXJoiiOkLDuB1lMUeZ26BOs=";
    script.onload = resolve;
    script.onerror = () => reject(new Error("The pinned MSAL asset could not be loaded or verified."));
    document.head.append(script);
  });
  msalClient = new msal.PublicClientApplication({
    auth: {
      clientId: config.signIn.clientId,
      authority: config.signIn.authority,
      redirectUri: config.signIn.redirectUri,
      postLogoutRedirectUri: config.signIn.redirectUri
    },
    cache: { cacheLocation: "memoryStorage", temporaryCacheLocation: "sessionStorage" }
  });
  await msalClient.initialize();
  await msalClient.handleRedirectPromise();
  status("Sign in with Microsoft to load your permitted policies. Refreshing this page clears the in-memory token cache.");
}

element("sign-in").addEventListener("click", async () => {
  if (busy || !msalClient) return;
  clearIdentityView();
  msalClient.setActiveAccount(null);
  element("signed-in").textContent = "Not signed in.";
  lock(true);
  try {
    await msalClient.clearCache();
    const result = await msalClient.loginPopup({ scopes: [config.signIn.scope], prompt: "select_account" });
    msalClient.setActiveAccount(result.account);
    element("signed-in").textContent = `Signed in: ${result.account.username}`;
    await loadDocuments();
  } catch (error) {
    const code = authCode(error);
    status(code === "popup_window_error" || code === "empty_window_error"
      ? "The sign-in popup was blocked. Open the application in Edge or Chrome and allow popups, then sign in again. No synthetic identity was used."
      : `Sign-in failed (${code}). No synthetic identity was used.`, true);
  }
  finally { lock(false); }
});

element("sign-out").addEventListener("click", async () => {
  if (busy || !msalClient) return;
  const account = msalClient.getActiveAccount();
  clearIdentityView();
  element("signed-in").textContent = "Not signed in.";
  lock(true);
  try {
    await msalClient.logoutPopup({ account });
    status("Signed out. Previous document and draft results were cleared.");
  } catch (error) { status(`Sign-out failed (${authCode(error)}). Close this tab to discard its memory cache.`, true); }
  finally {
    try { await msalClient.clearCache({ account }); }
    catch (error) { status(`Local sign-out failed (${authCode(error)}). Close this tab to discard its memory cache.`, true); }
    msalClient.setActiveAccount(null);
    lock(false);
  }
});

async function initialize() {
  try {
    const response = await fetch("/api/config", { cache: "no-store" });
    if (!response.ok) throw new Error(`Configuration failed (HTTP ${response.status}).`);
    config = await response.json();
    config.authenticationMode ||= config.mode === "Fixture" ? "Synthetic" : "Entra";
    element("mode").textContent = config.mode === "Fixture" && config.authenticationMode === "Entra"
      ? "FIXTURE DATA / REAL ENTRA IDENTITY" : `${config.mode.toUpperCase()} MODE`;
    element("notice").textContent = config.notice;
    element("evidence-mode-hint").textContent = config.mode === "Fixture"
      ? "In fixture mode actual embedding/generation calls remain zero. Keyword searches are local fixture operations, not Azure AI Search."
      : "Azure mode: embedding/generation/search calls are real requests through the model gateway and Search, not scripted fixtures.";
    element("fixture-identity").hidden = config.authenticationMode !== "Synthetic";
    element("azure-identity").hidden = config.authenticationMode === "Synthetic";
    element("browser-sign-in").hidden = !config.signIn;
    element("manual-token").hidden = !!config.signIn;
    if (config.authenticationMode === "Entra")
      element("case-hint").textContent = "Case buttons use your current real identity; they never switch accounts. The restricted case requests access-review explicitly and should be denied to employee-only users.";
    for (const profile of config.profiles) {
      const option = document.createElement("option"); option.value = profile.id;
      option.textContent = profile.displayName; element("profile").append(option);
    }
    if (config.signIn) await initializeSignIn();
    if (config.authenticationMode === "Synthetic") await loadDocuments();
    lock(false);
  } catch (error) { status(error.message, true); lock(true); }
}
lock(true);
initialize();
