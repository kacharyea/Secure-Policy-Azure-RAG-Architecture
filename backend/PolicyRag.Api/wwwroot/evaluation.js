"use strict";
const status = document.getElementById("evaluation-status");
const results = document.getElementById("evaluation-results");
const comparison = document.getElementById("comparison");
function node(tag, value) {
  const result = document.createElement(tag);
  if (value !== undefined) result.textContent = String(value);
  return result;
}
function line(parent, value) { parent.append(node("p", value)); }
function validate(report) {
  if (!report || report.schemaVersion !== "1" || !["Fixture", "Azure"].includes(report.mode) ||
      !["baseline", "candidate"].includes(report.variant) ||
      !Array.isArray(report.results) || !report.results.length || report.results.length > 500 ||
      !Array.isArray(report.humanScores) || !Array.isArray(report.limitations) ||
      report.results.some(r => !r || !["PASS", "FAIL", "BLOCKED", "NOT RUN"].includes(r.outcome) ||
        typeof r.securityGatePassed !== "boolean" || !Number.isFinite(r.durationMs) ||
        !r.evidence || !r.evidence.usage || !Array.isArray(r.evidence.stages) ||
        !Array.isArray(r.failures) || !Array.isArray(r.citedSourceIds))) {
    throw new Error("Unsupported or malformed evaluation report.");
  }
}
function render(report) {
  const panel = node("section"); panel.className = "panel";
  panel.append(node("h2", `${report.variant} · ${report.mode} · ${report.decision}`));
  line(panel, `Run: ${report.runId} · Model: ${report.model} · Revision: ${report.revision} · Dirty: ${report.dirtyWorktree ?? "unknown"}`);
  line(panel, `Prompt hash: ${report.promptHash}`);
  line(panel, `Implementation hash: ${report.implementationHash || "not recorded"} · Runtime settings hash: ${report.settingsHash || "not recorded"}`);
  if (report.settings) panel.append(node("pre", JSON.stringify(report.settings, null, 2)));
  line(panel, `Dataset hash: ${report.datasetHash} · Corpus hash: ${report.corpusHash}`);
  for (const limit of report.limitations) line(panel, limit);
  const counts = ["PASS", "FAIL", "BLOCKED", "NOT RUN"].map(outcome =>
    `${outcome}: ${report.results.filter(r => r.outcome === outcome).length}`);
  line(panel, counts.join(" | "));
  line(panel, `Security gate failures: ${report.results.filter(r => !r.securityGatePassed).length}`);
  const durations = report.results.filter(r => r.outcome !== "NOT RUN").map(r => r.durationMs).sort((a, b) => a - b);
  if (durations.length)
    line(panel, `Observed latency, n=${durations.length}: p50=${durations[Math.ceil(durations.length * .5) - 1].toFixed(1)} ms; p95=${durations[Math.ceil(durations.length * .95) - 1].toFixed(1)} ms (nearest rank; includes denials/blockers; not a production benchmark).`);
  for (const result of report.results) {
    const detail = node("details");
    detail.append(node("summary", `${result.caseId} · ${result.outcome} · ${result.durationMs.toFixed(1)} ms`));
    line(detail, `${result.role} · scope: ${result.documentId ?? "all authorized"} · ${result.question}`);
    line(detail, `Expected: ${result.expected}; actual: ${result.actual}; security gate: ${result.outcome === "NOT RUN" ? "NOT EVALUATED" : result.securityGatePassed ? "PASS" : "FAIL"}`);
    line(detail, result.answer || "(no answer)");
    line(detail, `Cited source IDs: ${result.citedSourceIds.join(", ") || "none"}`);
    line(detail, `Trace ID: ${result.evidence.traceId || "not run"}; error code: ${result.errorCode ?? "none"}`);
    for (const failure of result.failures) line(detail, failure);
    const stages = node("ul");
    for (const stage of result.evidence.stages)
      stages.append(node("li", `${stage.name}: ${stage.outcome}, ${stage.durationMs} ms${stage.synthetic ? " (synthetic/local)" : ""}`));
    detail.append(stages);
    const usage = result.evidence.usage;
    line(detail, `Tokens (embedding / input / output): ${usage.embeddingTokens ?? "unknown"} / ${usage.inputTokens ?? "unknown"} / ${usage.outputTokens ?? "unknown"}`);
    const scores = report.humanScores.filter(s => s.caseId === result.caseId);
    for (const score of scores)
      line(detail, `Rater ${score.rater}: relevance=${score.relevance}, completeness=${score.completeness}, evidence support=${score.evidenceSupport}`);
    if (!scores.length) line(detail, "Human quality scoring: not recorded.");
    panel.append(detail);
  }
  results.append(panel);
}
document.getElementById("reports").addEventListener("change", async event => {
  results.replaceChildren(); comparison.replaceChildren();
  try {
    const files = Array.from(event.target.files);
    if (files.length < 1 || files.length > 2 || files.some(f => f.size > 5 * 1024 * 1024))
      throw new Error("Choose one or two reports, no larger than 5 MB each.");
    const reports = await Promise.all(files.map(async file => {
      const report = JSON.parse(await file.text()); validate(report); return report;
    }));
    if (reports.length === 2) {
      const [a, b] = reports;
      const sameCases = JSON.stringify(a.results.map(r => [r.caseId, r.split, r.role, r.question, r.documentId]).sort()) ===
        JSON.stringify(b.results.map(r => [r.caseId, r.split, r.role, r.question, r.documentId]).sort());
      const comparable = a.mode === b.mode && a.model === b.model && a.datasetHash === b.datasetHash &&
        a.corpusHash === b.corpusHash && a.variant !== b.variant && sameCases &&
        a.settingsHash && a.settingsHash === b.settingsHash &&
        a.implementationHash && a.implementationHash === b.implementationHash && a.revision === b.revision;
      line(comparison, comparable ? "Paired comparison: matching mode, model, cases, implementation, runtime settings and dataset/corpus hashes. Verify the same Azure service configuration was used." :
        "NOT A CONTROLLED COMPARISON: mode, model, cases, implementation, runtime settings, dataset/corpus or variants differ.");
      if (a.mode === "Fixture" || b.mode === "Fixture")
        line(comparison, "Fixture responses ignore prompt variants. Differences do not demonstrate model improvement.");
      if (comparable) {
        comparison.append(node("h2", "Paired case outcomes"));
        for (const left of a.results) {
          const right = b.results.find(r => r.caseId === left.caseId);
          line(comparison, `${left.caseId}: ${a.variant}=${left.outcome} (${left.durationMs.toFixed(1)} ms) | ${b.variant}=${right.outcome} (${right.durationMs.toFixed(1)} ms)`);
        }
      }
    }
    reports.forEach(render);
    status.textContent = "Loaded locally. Saved evidence is not proof of current Azure availability.";
  } catch (error) {
    results.replaceChildren(); comparison.replaceChildren();
    status.textContent = error instanceof SyntaxError ? "Invalid JSON report." : error.message;
  }
});
