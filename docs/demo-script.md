# Interviewer demo script

Use this script to walk through role separation in about five minutes. API base: `http://127.0.0.1:5080`. Dashboard: `http://127.0.0.1:43123`.

## Accounts

| Username | Password | Roles |
| --- | --- | --- |
| `operator` | `operate-demo` | Operator |
| `approver` | `approve-demo` | Approver |
| `admin` | `admin-demo` | Operator + Approver |

## Happy path (admin)

1. Sign in as `admin` / `admin-demo`.
2. Click **Run offline scenario**.
3. Confirm Vehicle 12 is Offline, citations appear, and **Approve notification** is enabled.
4. Click **Approve notification**. Status moves to approved; `approvedBy` shows **Sam Admin**.

## Operator vs Approver (API)

Obtain tokens:

```bash
OP=$(curl -s -X POST http://127.0.0.1:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"operator","password":"operate-demo"}' | jq -r .accessToken)

AP=$(curl -s -X POST http://127.0.0.1:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"approver","password":"approve-demo"}' | jq -r .accessToken)
```

| Step | Call | Expected |
| --- | --- | --- |
| Anonymous dashboard | `GET /api/operations/dashboard` (no token) | **401** |
| Operator reset | `POST /api/simulation/reset` with `OP` | **200** |
| Operator scenario | `POST /api/simulation/offline/{vehicle12Id}` with `OP` | **200** (capture `incidentId`) |
| Operator approve | `POST /api/incidents/{incidentId}/approve-notification` with `OP` | **403** |
| Approver approve | same approve URL with `AP` | **200**; `proposedAction.approvedBy` is **Jordan Approver** |
| Approver scenario | `POST /api/simulation/offline/{id}` with `AP` | **403** |

## Dashboard role check

1. Sign in as `operator` / `operate-demo` (or sign out from admin first).
2. Run the offline scenario if needed — **Run offline scenario** works.
3. Confirm **Approve notification** is disabled / blocked (**403** if forced).
4. Sign in as `approver` / `approve-demo`.
5. Confirm run/reset are blocked; approve succeeds when an open incident exists.

## Talking points

- Rules open incidents; the model only narrates retrieved evidence.
- Citation keys are server-validated; fabricated evidence fails closed.
- Approval is a role policy, not a UI convention.
- Actions remain simulated — no device writes.
