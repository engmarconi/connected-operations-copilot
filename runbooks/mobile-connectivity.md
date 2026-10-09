# Mobile Connectivity Runbook

Document ID: RB-CONN-001
Keywords: connectivity, mobile, network, latency, timeout, offline, heartbeat

## 2.1 — Rising latency and heartbeat loss

Repeated latency growth followed by request timeouts and heartbeat loss usually indicates a degraded or lost mobile network path. Confirm whether nearby assets remain online before treating the event as a platform-wide outage.

## 2.2 — Connectivity verification

Confirm that the driver phone has an active data connection. Check the most recent successful heartbeat and compare it with timeout telemetry before escalating to device inspection.
