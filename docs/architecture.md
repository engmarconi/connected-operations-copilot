# Architecture

Connected Operations Copilot is a layered ASP.NET Core + Next.js portfolio slice that shows how AI can assist operations without owning the control plane.

## Control flow

1. **Deterministic incident rules** open incidents from synthetic telemetry (latency rise, timeouts, heartbeat loss). Models never create or close incidents.
2. **Versioned runbook retrieval** ranks approved Markdown sections against structured incident signals. Free-text telemetry messages are untrusted and cannot steer ranking.
3. **Schema-constrained diagnosis** (mock, Gemini, or OpenAI) returns summary, causes, checks, citation keys, and confidence. The server validates citation keys against retrieved evidence and builds the final citations.
4. **Audited read-only tools** expose telemetry, fleet comparison, and incident history through a schema-validated registry with timeouts, allowlists, and an audit trail. Write tools are not registered.
5. **JWT Operator / Approver gate** protects the API. Operators run scenarios and tools; Approvers alone can approve notifications. `approvedBy` comes from claims, not the client body.
6. **OpenTelemetry observability** emits traces, metrics, and JSON structured logs for scenarios, diagnoses, approvals, and tool calls without capturing raw telemetry payloads or model prompts.

## Boundaries that matter

| Concern | Owner |
| --- | --- |
| Incident lifecycle | Deterministic rules + in-memory store |
| Evidence | Versioned runbooks + retrieval pipeline |
| Narrative diagnosis | Provider behind `IDiagnosticService` |
| Side effects | Simulated notification after Approver approval |
| Identity | Demo JWT roles (`Operator`, `Approver`) |

The dashboard is a thin client over the same policies. Failures from optional model providers return safe `502` responses; there is no silent fallback that invents a diagnosis.
