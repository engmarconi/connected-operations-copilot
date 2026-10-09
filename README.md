# Connected Operations Copilot

[![CI](https://github.com/engmarconi/connected-operations-copilot/actions/workflows/ci.yml/badge.svg)](https://github.com/engmarconi/connected-operations-copilot/actions/workflows/ci.yml)

Evidence-based incident diagnostics for fleet and connected-device operations.

This public portfolio project demonstrates how deterministic operational systems can use AI safely: rules open incidents, versioned runbooks are retrieved as evidence, and a person must approve every proposed action.

The repository uses synthetic data only. It contains no DrivoX, McShippers, iSmart Home, employer, or customer data.

## See it in action

![Connected Operations Copilot showing retrieved runbook evidence and a human approval gate](docs/assets/retrieved-runbook-evidence.png)

## Working vertical slice

1. Six synthetic connected assets start healthy.
2. An operator runs the **Vehicle 12 offline** scenario.
3. The deterministic simulator records rising latency, timeouts, and heartbeat loss.
4. The incident engine opens a high-severity incident.
5. A local retrieval pipeline ranks versioned runbook sections against the incident telemetry.
6. A deterministic mock diagnostic provider returns:
   - an evidence summary;
   - probable causes;
   - recommended checks;
   - citations built from the retrieved runbook sections;
   - a confidence score.
7. The provider proposes an operator notification.
8. The notification is only simulated after explicit human approval.

The mock generator keeps the demo runnable without credentials. Retrieval is real but deliberately local and deterministic: Markdown runbooks are ingested from the repository, split by section, and ranked with weighted lexical relevance. Vector storage, model providers, and AI evaluation suites remain roadmap items rather than implied current capabilities.

## Architecture

```mermaid
flowchart LR
    Simulator[Synthetic asset simulator] --> API[ASP.NET Core API]
    API --> Rules[Deterministic incident rules]
    Rules --> Store[In-memory operations store]
    Store --> Diagnostic[Mock diagnostic generator]
    Runbooks[Versioned Markdown runbooks] --> Retrieval[Local retrieval index]
    Retrieval --> Diagnostic
    Diagnostic --> Review[Human review]
    Review --> Action[Simulated notification]
    API --> Web[Next.js operations dashboard]
```

## Technology

- ASP.NET Core / .NET 10
- Layered Domain, Application, Infrastructure, and API projects
- Next.js, TypeScript, Tailwind CSS, and shadcn/ui
- xUnit
- In-memory synthetic operations store
- Versioned Markdown runbook corpus
- Deterministic ingestion and weighted lexical retrieval
- Mock diagnostic generator with retrieved citations

## Retrieval pipeline

Runbooks under `runbooks/` are embedded into the backend at build time. On startup, the retrieval layer validates document metadata, chunks each document by section, and builds an in-memory index. Incident titles and telemetry become the retrieval query. Results are ranked by weighted matches across document keywords, titles, section names, and content, with at most one section returned per document.

`IRunbookRetriever` keeps retrieval independent from diagnosis generation. The current local implementation can therefore be replaced by PostgreSQL and pgvector without changing the incident workflow or its human approval boundary.

## Run locally

Requirements:

- .NET SDK 10
- Node.js 22+

Start the API:

```bash
dotnet run --project src/ConnectedOps.Api --urls http://127.0.0.1:5080
```

In a second terminal, start the dashboard:

```bash
cd web
cp .env.example .env.local
npm install
npm run dev -- --hostname 0.0.0.0 --port 43123
```

Open <http://127.0.0.1:43123>.

## Verify

```bash
dotnet build ConnectedOps.slnx
dotnet test ConnectedOps.slnx

cd web
npm run lint
npm run build
```

## Safety properties in the first slice

- Incident creation is deterministic, not model-controlled.
- The diagnostic provider has no direct database or device access.
- Diagnosis citations are created from retrieved, versioned runbook sections.
- Proposed actions require explicit approval.
- Actions are simulated and auditable.
- The application runs without model credentials.

## Roadmap

- PostgreSQL and pgvector persistence for semantic retrieval
- Schema-constrained Gemini and OpenAI providers
- Read-only tool calling
- Prompt-injection and missing-evidence tests
- Repeatable AI evaluation dataset
- OpenTelemetry traces, metrics, and structured logs
- Authentication and role-based approvals

## License

MIT
