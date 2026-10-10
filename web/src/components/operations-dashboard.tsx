"use client";

import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import {
  Activity,
  AlertTriangle,
  Bot,
  Check,
  ChevronRight,
  CircleDot,
  Clock3,
  FileSearch,
  Network,
  Play,
  RefreshCw,
  LogOut,
  ShieldCheck,
  Sparkles,
} from "lucide-react";

import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardAction,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";

const API_URL =
  process.env.NEXT_PUBLIC_API_URL ?? "http://127.0.0.1:5080/api";
const AUTH_STORAGE_KEY = "connectedops.auth";

type HealthState = "Healthy" | "Warning" | "Offline";
type IncidentStatus = "Open" | "ActionApproved" | "Resolved";

type Asset = {
  id: string;
  name: string;
  type: string;
  location: string;
  health: HealthState;
  lastSeenAt: string;
};

type Citation = {
  documentId: string;
  title: string;
  section: string;
  excerpt: string;
};

type ProposedAction = {
  id: string;
  kind: string;
  description: string;
  requiresApproval: boolean;
  isApproved: boolean;
  approvedBy?: string;
  approvedAt?: string;
  simulatedResult?: string;
};

type Incident = {
  id: string;
  assetId: string;
  title: string;
  severity: string;
  openedAt: string;
  status: IncidentStatus;
  diagnosis?: {
    summary: string;
    probableCauses: string[];
    recommendedSteps: string[];
    citations: Citation[];
    confidence: number;
    provider: string;
    generatedAt: string;
  };
  proposedAction?: ProposedAction;
};

type Dashboard = {
  summary: {
    totalAssets: number;
    healthyAssets: number;
    offlineAssets: number;
    openIncidents: number;
    diagnosedIncidents: number;
  };
  assets: Asset[];
  incidents: Incident[];
  generatedAt: string;
};

type ToolAuditRecord = {
  id: string;
  toolName: string;
  caller: string;
  startedAt: string;
  durationMilliseconds: number;
  succeeded: boolean;
  summary: string;
};

type DashboardView = Dashboard & {
  toolAudit: ToolAuditRecord[];
};

type AuthUser = {
  username: string;
  displayName: string;
  roles: string[];
};

type AuthSession = {
  accessToken: string;
  expiresAt: string;
  user: AuthUser;
};

const authListeners = new Set<() => void>();
let cachedAuthRaw: string | null = null;
let cachedAuthSession: AuthSession | null = null;

function emitAuthChange() {
  for (const listener of authListeners) {
    listener();
  }
}

function subscribeAuth(listener: () => void) {
  authListeners.add(listener);
  return () => {
    authListeners.delete(listener);
  };
}

function readStoredAuth(): AuthSession | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.sessionStorage.getItem(AUTH_STORAGE_KEY);
    if (raw === cachedAuthRaw) {
      return cachedAuthSession;
    }

    cachedAuthRaw = raw;
    if (!raw) {
      cachedAuthSession = null;
      return null;
    }

    const session = JSON.parse(raw) as AuthSession;
    if (
      !session.accessToken ||
      !session.user?.username ||
      Date.parse(session.expiresAt) <= Date.now()
    ) {
      window.sessionStorage.removeItem(AUTH_STORAGE_KEY);
      cachedAuthRaw = null;
      cachedAuthSession = null;
      return null;
    }

    cachedAuthSession = session;
    return cachedAuthSession;
  } catch {
    cachedAuthRaw = null;
    cachedAuthSession = null;
    return null;
  }
}

function writeStoredAuth(session: AuthSession | null) {
  if (session) {
    const raw = JSON.stringify(session);
    window.sessionStorage.setItem(AUTH_STORAGE_KEY, raw);
    cachedAuthRaw = raw;
    cachedAuthSession = session;
  } else {
    window.sessionStorage.removeItem(AUTH_STORAGE_KEY);
    cachedAuthRaw = null;
    cachedAuthSession = null;
  }
  emitAuthChange();
}

function timeLabel(value: string) {
  return new Intl.DateTimeFormat("en-GB", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).format(new Date(value));
}

function providerLabel(provider?: string) {
  if (!provider) {
    return "Retrieved evidence · validated citations";
  }

  if (provider.startsWith("gemini/") || provider.startsWith("openai/")) {
    const model = provider.split("/")[1] ?? "model";
    const displayModel = model
      .split("-")
      .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
      .join(" ");
    return `${displayModel} · validated citations`;
  }

  if (provider.startsWith("mock-generator/")) {
    return "Mock provider · cited runbooks";
  }

  return "Evidence provider · validated citations";
}

function providerFooterLabel(provider?: string) {
  if (provider?.startsWith("gemini/")) {
    return "Gemini AI provider";
  }

  if (provider?.startsWith("openai/")) {
    return "OpenAI provider";
  }

  if (provider?.startsWith("mock-generator/")) {
    return "Mock AI provider";
  }

  return "Configured diagnostic provider";
}

function statusBadge(health: HealthState) {
  if (health === "Offline") {
    return (
      <Badge className="border-red-200 bg-red-50 text-red-700">
        <CircleDot className="size-3 fill-red-500 text-red-500" />
        Offline
      </Badge>
    );
  }

  if (health === "Warning") {
    return (
      <Badge className="border-amber-200 bg-amber-50 text-amber-700">
        <CircleDot className="size-3 fill-amber-500 text-amber-500" />
        Warning
      </Badge>
    );
  }

  return (
    <Badge className="border-emerald-200 bg-emerald-50 text-emerald-700">
      <CircleDot className="size-3 fill-emerald-500 text-emerald-500" />
      Healthy
    </Badge>
  );
}

function DashboardSkeleton() {
  return (
    <div className="space-y-6">
      <div className="grid gap-4 md:grid-cols-3">
        {[0, 1, 2].map((item) => (
          <Skeleton key={item} className="h-32 rounded-2xl" />
        ))}
      </div>
      <div className="grid gap-6 xl:grid-cols-[0.85fr_1.15fr]">
        <Skeleton className="h-[460px] rounded-2xl" />
        <Skeleton className="h-[460px] rounded-2xl" />
      </div>
    </div>
  );
}

export function OperationsDashboard() {
  const auth = useSyncExternalStore(
    subscribeAuth,
    readStoredAuth,
    () => null,
  );
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("admin-demo");
  const [dashboard, setDashboard] = useState<DashboardView | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [isActing, setIsActing] = useState(false);
  const [isLoggingIn, setIsLoggingIn] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const actionInFlight = useRef(false);

  const canOperate = !!auth?.user.roles.includes("Operator");
  const canApprove = !!auth?.user.roles.includes("Approver");

  const authHeaders = useCallback(
    (extra?: HeadersInit): HeadersInit => {
      const headers = new Headers(extra);
      if (auth?.accessToken) {
        headers.set("Authorization", `Bearer ${auth.accessToken}`);
      }
      return headers;
    },
    [auth],
  );

  const clearAuth = useCallback(() => {
    writeStoredAuth(null);
    setDashboard(null);
    setIsLoading(false);
  }, []);

  const fetchDashboard = useCallback(async () => {
    if (!auth?.accessToken) {
      throw new Error("Sign in required.");
    }
    const [dashboardResponse, auditResponse] = await Promise.all([
      fetch(`${API_URL}/operations/dashboard`, {
        cache: "no-store",
        headers: authHeaders(),
      }),
      fetch(`${API_URL}/tools/audit?limit=5`, {
        cache: "no-store",
        headers: authHeaders(),
      }),
    ]);
    if (dashboardResponse.status === 401 || auditResponse.status === 401) {
      clearAuth();
      throw new Error("Your session expired. Sign in again.");
    }
    if (!dashboardResponse.ok || !auditResponse.ok) {
      throw new Error("The operations API did not return a dashboard.");
    }
    const snapshot = (await dashboardResponse.json()) as Dashboard;
    const audit = (await auditResponse.json()) as {
      records: ToolAuditRecord[];
    };
    return {
      ...snapshot,
      toolAudit: audit.records,
    };
  }, [auth, authHeaders, clearAuth]);

  useEffect(() => {
    if (!auth) {
      return;
    }

    let cancelled = false;

    void (async () => {
      await Promise.resolve();
      if (cancelled) {
        return;
      }

      setIsLoading(true);
      try {
        const snapshot = await fetchDashboard();
        if (!cancelled) {
          setDashboard(snapshot);
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(
            loadError instanceof Error
              ? loadError.message
              : "The dashboard could not reach the .NET API. Start the API on port 5080 and try again.",
          );
        }
      } finally {
        if (!cancelled) {
          setIsLoading(false);
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [auth, fetchDashboard]);

  const primaryIncident = dashboard?.incidents[0];
  const primaryAsset = useMemo(
    () =>
      dashboard?.assets.find((asset) => asset.id === primaryIncident?.assetId),
    [dashboard, primaryIncident],
  );

  async function login() {
    setIsLoggingIn(true);
    setError(null);
    try {
      const response = await fetch(`${API_URL}/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password }),
      });
      if (!response.ok) {
        throw new Error("Invalid demo username or password.");
      }
      const session = (await response.json()) as AuthSession;
      writeStoredAuth(session);
    } catch (loginError) {
      setError(
        loginError instanceof Error
          ? loginError.message
          : "Sign-in failed.",
      );
    } finally {
      setIsLoggingIn(false);
    }
  }

  async function runAction(action: () => Promise<Response>) {
    if (actionInFlight.current) return;
    actionInFlight.current = true;
    setIsActing(true);
    setError(null);
    try {
      const response = await action();
      if (response.status === 401) {
        clearAuth();
        throw new Error("Your session expired. Sign in again.");
      }
      if (response.status === 403) {
        throw new Error(
          "Your account does not have permission for that action.",
        );
      }
      if (!response.ok) {
        const problem = (await response.json().catch(() => null)) as {
          detail?: string;
        } | null;
        throw new Error(
          problem?.detail ?? "The requested operation was not completed.",
        );
      }
      setDashboard(await fetchDashboard());
    } catch (actionError) {
      setError(
        actionError instanceof Error
          ? actionError.message
          : "The requested operation was not completed.",
      );
    } finally {
      actionInFlight.current = false;
      setIsActing(false);
    }
  }

  function runOfflineScenario() {
    const vehicle = dashboard?.assets.find(
      (asset) => asset.name === "Vehicle 12",
    );
    if (!vehicle) return;

    void runAction(() =>
      fetch(`${API_URL}/simulation/offline/${vehicle.id}`, {
        method: "POST",
        headers: authHeaders(),
      }),
    );
  }

  function resetDemo() {
    void runAction(() =>
      fetch(`${API_URL}/simulation/reset`, {
        method: "POST",
        headers: authHeaders(),
      }),
    );
  }

  function approveNotification(incidentId: string) {
    void runAction(() =>
      fetch(`${API_URL}/incidents/${incidentId}/approve-notification`, {
        method: "POST",
        headers: authHeaders({ "Content-Type": "application/json" }),
        body: "{}",
      }),
    );
  }

  if (!auth) {
    return (
      <main className="min-h-screen bg-[#f4f6f3] text-[#17211b]">
        <div className="border-b border-black/5 bg-[#102a20] text-white">
          <div className="mx-auto flex max-w-[1480px] items-center justify-between px-5 py-3 sm:px-8">
            <div className="flex items-center gap-3">
              <div className="grid size-9 place-items-center rounded-xl bg-[#b9f66e] text-[#102a20]">
                <Network className="size-5" strokeWidth={2.4} />
              </div>
              <div>
                <div className="text-sm font-semibold tracking-tight">
                  ConnectedOps
                </div>
                <div className="text-[11px] text-white/55">
                  Role-gated incident response
                </div>
              </div>
            </div>
            <Badge className="border-white/10 bg-white/7 text-white/80">
              <span className="size-1.5 rounded-full bg-[#b9f66e]" />
              Demo environment
            </Badge>
          </div>
        </div>
        <div className="mx-auto flex max-w-lg flex-col gap-5 px-5 py-16 sm:px-8">
          <Card className="rounded-2xl border-black/5 bg-white shadow-sm">
            <CardHeader>
              <CardTitle className="text-2xl tracking-tight">
                Sign in to operate
              </CardTitle>
              <CardDescription>
                Demo JWT accounts separate operators who run scenarios from
                approvers who can release notifications.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <label className="block space-y-1.5 text-sm">
                <span className="font-medium text-[#31453a]">Username</span>
                <input
                  className="w-full rounded-xl border border-black/10 bg-[#f7f9f6] px-3 py-2 outline-none ring-[#173f2e] focus:ring-2"
                  value={username}
                  onChange={(event) => setUsername(event.target.value)}
                  autoComplete="username"
                />
              </label>
              <label className="block space-y-1.5 text-sm">
                <span className="font-medium text-[#31453a]">Password</span>
                <input
                  type="password"
                  className="w-full rounded-xl border border-black/10 bg-[#f7f9f6] px-3 py-2 outline-none ring-[#173f2e] focus:ring-2"
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  autoComplete="current-password"
                />
              </label>
              {error && (
                <Alert className="border-red-200 bg-red-50 text-red-900">
                  <AlertTriangle />
                  <AlertTitle>Sign-in failed</AlertTitle>
                  <AlertDescription>{error}</AlertDescription>
                </Alert>
              )}
              <Button
                className="w-full bg-[#173f2e] text-white hover:bg-[#102a20]"
                onClick={() => void login()}
                disabled={isLoggingIn || !username || !password}
              >
                {isLoggingIn ? "Signing in…" : "Sign in"}
              </Button>
              <div className="rounded-xl bg-[#f1f9e8] p-3 text-xs leading-5 text-[#456039]">
                <div className="font-semibold">Demo accounts</div>
                <div>operator / operate-demo · Operator</div>
                <div>approver / approve-demo · Approver</div>
                <div>admin / admin-demo · Operator + Approver</div>
              </div>
            </CardContent>
          </Card>
        </div>
      </main>
    );
  }

  return (
    <main className="min-h-screen bg-[#f4f6f3] text-[#17211b]">
      <div className="border-b border-black/5 bg-[#102a20] text-white">
        <div className="mx-auto flex max-w-[1480px] items-center justify-between gap-3 px-5 py-3 sm:px-8">
          <div className="flex items-center gap-3">
            <div className="grid size-9 place-items-center rounded-xl bg-[#b9f66e] text-[#102a20]">
              <Network className="size-5" strokeWidth={2.4} />
            </div>
            <div>
              <div className="text-sm font-semibold tracking-tight">
                ConnectedOps
              </div>
              <div className="text-[11px] text-white/55">
                Evidence-led incident response
              </div>
            </div>
          </div>
          <div className="flex flex-wrap items-center justify-end gap-2">
            <Badge className="border-white/10 bg-white/7 text-white/80">
              {auth.user.displayName}
            </Badge>
            {auth.user.roles.map((role) => (
              <Badge
                key={role}
                className="border-white/10 bg-white/7 text-white/80"
              >
                {role}
              </Badge>
            ))}
            <Button
              variant="outline"
              size="sm"
              className="border-white/15 bg-transparent text-white hover:bg-white/10"
              onClick={clearAuth}
            >
              <LogOut className="size-3.5" />
              Sign out
            </Button>
          </div>
        </div>
      </div>

      <div className="mx-auto max-w-[1480px] px-5 py-8 sm:px-8 lg:py-10">
        <header className="mb-8 flex flex-col gap-5 lg:flex-row lg:items-end lg:justify-between">
          <div className="max-w-3xl">
            <div className="mb-3 flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.18em] text-[#52705f]">
              <Activity className="size-3.5" />
              Live operations
            </div>
            <h1 className="text-balance text-3xl font-semibold tracking-[-0.035em] sm:text-4xl">
              Diagnose connected-asset incidents with evidence, not guesses.
            </h1>
            <p className="mt-3 max-w-2xl text-sm leading-6 text-[#617067] sm:text-base">
              Deterministic rules open incidents. The diagnostic copilot reads
              telemetry and approved runbooks, then proposes a human-reviewed
              response. Approvals require the Approver role.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button
              variant="outline"
              className="border-black/10 bg-white"
              onClick={resetDemo}
              disabled={isActing || isLoading || !canOperate}
            >
              <RefreshCw className={isActing ? "animate-spin" : ""} />
              Reset demo
            </Button>
            <Button
              className="bg-[#173f2e] text-white hover:bg-[#102a20]"
              onClick={runOfflineScenario}
              disabled={
                isActing || isLoading || !!primaryIncident || !canOperate
              }
            >
              <Play className="fill-current" />
              Run offline scenario
            </Button>
          </div>
        </header>

        {error && (
          <Alert className="mb-6 border-red-200 bg-red-50 text-red-900">
            <AlertTriangle />
            <AlertTitle>Connection problem</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}

        {isLoading ? (
          <DashboardSkeleton />
        ) : dashboard ? (
          <div className="space-y-6">
            <section className="grid gap-4 md:grid-cols-3">
              <Card className="rounded-2xl border-black/5 bg-white shadow-sm">
                <CardHeader>
                  <CardDescription>Connected assets</CardDescription>
                  <CardAction>
                    <Network className="size-5 text-[#52705f]" />
                  </CardAction>
                  <CardTitle className="text-3xl tracking-tight">
                    {dashboard.summary.totalAssets}
                  </CardTitle>
                </CardHeader>
                <CardContent className="text-sm text-[#617067]">
                  {dashboard.summary.healthyAssets} reporting normally
                </CardContent>
              </Card>
              <Card className="rounded-2xl border-black/5 bg-white shadow-sm">
                <CardHeader>
                  <CardDescription>Open incidents</CardDescription>
                  <CardAction>
                    <AlertTriangle className="size-5 text-[#52705f]" />
                  </CardAction>
                  <CardTitle className="text-3xl tracking-tight">
                    {dashboard.summary.openIncidents}
                  </CardTitle>
                </CardHeader>
                <CardContent className="text-sm text-[#617067]">
                  {dashboard.summary.offlineAssets} asset offline
                </CardContent>
              </Card>
              <Card className="rounded-2xl border-black/5 bg-[#e5f8cf] shadow-sm">
                <CardHeader>
                  <CardDescription className="text-[#456039]">
                    Evidence-backed analyses
                  </CardDescription>
                  <CardAction>
                    <Sparkles className="size-5 text-[#31531e]" />
                  </CardAction>
                  <CardTitle className="text-3xl tracking-tight text-[#173f2e]">
                    {dashboard.summary.diagnosedIncidents}
                  </CardTitle>
                </CardHeader>
                <CardContent className="text-sm text-[#456039]">
                  {providerLabel(primaryIncident?.diagnosis?.provider)}
                </CardContent>
              </Card>
            </section>

            <Card className="rounded-2xl border-black/5 bg-white shadow-sm">
              <CardHeader>
                <CardDescription>Audited execution</CardDescription>
                <CardAction>
                  <Badge
                    variant="outline"
                    className="border-black/10 bg-[#f4f6f3] text-[#52705f]"
                  >
                    {dashboard.toolAudit.length} recent
                  </Badge>
                </CardAction>
                <CardTitle className="flex items-center gap-2">
                  <ShieldCheck className="size-5 text-[#31531e]" />
                  Read-only tool activity
                </CardTitle>
                <CardDescription>
                  Server-controlled caller labels, bounded results and no write
                  capabilities
                </CardDescription>
              </CardHeader>
              <CardContent>
                {dashboard.toolAudit.length === 0 ? (
                  <div className="rounded-xl border border-dashed border-black/10 bg-[#f8faf7] px-4 py-5 text-sm text-[#617067]">
                    No tool calls recorded. Gemini or an operator may request
                    recent telemetry after an incident opens.
                  </div>
                ) : (
                  <div className="divide-y divide-black/5 rounded-xl border border-black/5">
                    {dashboard.toolAudit.map((record) => (
                      <div
                        key={record.id}
                        className="grid min-w-0 max-w-full gap-3 overflow-hidden px-4 py-4 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center"
                      >
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <Badge
                              className={
                                record.succeeded
                                  ? "border-emerald-200 bg-emerald-50 text-emerald-700"
                                  : "border-red-200 bg-red-50 text-red-700"
                              }
                            >
                              {record.succeeded ? (
                                <Check className="size-3" />
                              ) : (
                                <AlertTriangle className="size-3" />
                              )}
                              {record.succeeded ? "Succeeded" : "Blocked"}
                            </Badge>
                            <span className="truncate font-mono text-xs font-semibold text-[#25451b]">
                              {record.toolName}
                            </span>
                            <span className="text-xs text-[#738078]">
                              {record.durationMilliseconds} ms
                            </span>
                          </div>
                          <p className="mt-2 min-w-0 max-w-full break-all whitespace-normal text-sm leading-5 text-[#526159]">
                            {record.summary}
                          </p>
                        </div>
                        <div className="min-w-0 text-left text-xs text-[#738078] sm:text-right">
                          <div className="max-w-full break-all whitespace-normal font-medium text-[#52705f]">
                            {record.caller}
                          </div>
                          <div className="mt-1">
                            {timeLabel(record.startedAt)}
                          </div>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </CardContent>
            </Card>

            <section className="grid gap-6 xl:grid-cols-[0.82fr_1.18fr]">
              <Card className="rounded-2xl border-black/5 bg-white shadow-sm">
                <CardHeader>
                  <CardTitle>Asset health</CardTitle>
                  <CardDescription>
                    Synthetic fleet and connected-device telemetry
                  </CardDescription>
                </CardHeader>
                <CardContent className="space-y-1">
                  {dashboard.assets.map((asset, index) => (
                    <div key={asset.id}>
                      <div className="flex items-center justify-between gap-4 py-3">
                        <div className="min-w-0">
                          <div className="truncate text-sm font-medium">
                            {asset.name}
                          </div>
                          <div className="mt-1 flex items-center gap-2 text-xs text-[#738078]">
                            <span>{asset.type}</span>
                            <span>·</span>
                            <span>{asset.location}</span>
                          </div>
                        </div>
                        {statusBadge(asset.health)}
                      </div>
                      {index < dashboard.assets.length - 1 && <Separator />}
                    </div>
                  ))}
                </CardContent>
              </Card>

              {primaryIncident?.diagnosis ? (
                <Card className="overflow-hidden rounded-2xl border-black/5 bg-white shadow-sm">
                  <div className="border-b border-black/5 bg-[#102a20] px-6 py-5 text-white">
                    <div className="flex flex-wrap items-start justify-between gap-4">
                      <div>
                        <div className="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-[0.15em] text-[#b9f66e]">
                          <Bot className="size-4" />
                          Copilot analysis
                        </div>
                        <h2 className="text-xl font-semibold tracking-tight">
                          {primaryIncident.title}
                        </h2>
                        <div className="mt-2 flex items-center gap-2 text-xs text-white/55">
                          <Clock3 className="size-3.5" />
                          Opened at {timeLabel(primaryIncident.openedAt)}
                          {primaryAsset && ` · ${primaryAsset.location}`}
                        </div>
                      </div>
                      <Badge className="border-white/10 bg-white/10 text-white">
                        {Math.round(
                          primaryIncident.diagnosis.confidence * 100,
                        )}
                        % confidence
                      </Badge>
                    </div>
                  </div>

                  <CardContent className="space-y-6 p-6">
                    <div>
                      <div className="mb-2 text-xs font-semibold uppercase tracking-[0.14em] text-[#738078]">
                        Evidence summary
                      </div>
                      <p className="text-sm leading-6 text-[#33433a]">
                        {primaryIncident.diagnosis.summary}
                      </p>
                    </div>

                    <div className="grid gap-5 md:grid-cols-2">
                      <div>
                        <div className="mb-3 flex items-center gap-2 text-sm font-semibold">
                          <AlertTriangle className="size-4 text-amber-600" />
                          Probable causes
                        </div>
                        <ol className="space-y-2">
                          {primaryIncident.diagnosis.probableCauses.map(
                            (cause, index) => (
                              <li
                                key={cause}
                                className="flex gap-3 text-sm leading-5 text-[#526158]"
                              >
                                <span className="grid size-5 shrink-0 place-items-center rounded-full bg-amber-50 text-[10px] font-semibold text-amber-700">
                                  {index + 1}
                                </span>
                                {cause}
                              </li>
                            ),
                          )}
                        </ol>
                      </div>
                      <div>
                        <div className="mb-3 flex items-center gap-2 text-sm font-semibold">
                          <ShieldCheck className="size-4 text-emerald-700" />
                          Recommended checks
                        </div>
                        <ol className="space-y-2">
                          {primaryIncident.diagnosis.recommendedSteps.map(
                            (step, index) => (
                              <li
                                key={step}
                                className="flex gap-3 text-sm leading-5 text-[#526158]"
                              >
                                <span className="grid size-5 shrink-0 place-items-center rounded-full bg-emerald-50 text-[10px] font-semibold text-emerald-700">
                                  {index + 1}
                                </span>
                                {step}
                              </li>
                            ),
                          )}
                        </ol>
                      </div>
                    </div>

                    <Separator />

                    <div>
                      <div className="mb-3 flex items-center gap-2 text-sm font-semibold">
                        <FileSearch className="size-4 text-[#52705f]" />
                        Supporting runbooks
                      </div>
                      <div className="grid gap-3 md:grid-cols-2">
                        {primaryIncident.diagnosis.citations.map((citation) => (
                          <div
                            key={citation.documentId}
                            className="rounded-xl border border-black/7 bg-[#f7f8f5] p-4"
                          >
                            <div className="flex items-center justify-between gap-3">
                              <span className="text-sm font-medium">
                                {citation.title}
                              </span>
                              <Badge variant="outline" className="bg-white">
                                {citation.documentId}
                              </Badge>
                            </div>
                            <div className="mt-2 text-xs font-medium text-[#52705f]">
                              {citation.section}
                            </div>
                            <p className="mt-2 text-xs leading-5 text-[#738078]">
                              “{citation.excerpt}”
                            </p>
                          </div>
                        ))}
                      </div>
                    </div>

                    {primaryIncident.proposedAction && (
                      <div className="rounded-xl border border-[#cdddbf] bg-[#f1f9e8] p-4">
                        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
                          <div>
                            <div className="flex items-center gap-2 text-sm font-semibold text-[#25451b]">
                              {primaryIncident.proposedAction.isApproved ? (
                                <Check className="size-4" />
                              ) : (
                                <ShieldCheck className="size-4" />
                              )}
                              {primaryIncident.proposedAction.isApproved
                                ? "Human-approved action"
                                : "Approval required"}
                            </div>
                            <p className="mt-1 max-w-xl text-xs leading-5 text-[#527047]">
                              {primaryIncident.proposedAction.isApproved
                                ? primaryIncident.proposedAction.simulatedResult
                                : primaryIncident.proposedAction.description}
                            </p>
                          </div>
                          {!primaryIncident.proposedAction.isApproved && (
                            <Button
                              className="shrink-0 bg-[#173f2e] text-white hover:bg-[#102a20]"
                              onClick={() =>
                                approveNotification(primaryIncident.id)
                              }
                              disabled={isActing || !canApprove}
                              title={
                                canApprove
                                  ? undefined
                                  : "Approver role required"
                              }
                            >
                              Approve notification
                              <ChevronRight />
                            </Button>
                          )}
                        </div>
                      </div>
                    )}
                  </CardContent>
                </Card>
              ) : (
                <Card className="grid min-h-[460px] place-items-center rounded-2xl border-dashed border-black/10 bg-white/55 shadow-none">
                  <CardContent className="max-w-md px-6 text-center">
                    <div className="mx-auto mb-5 grid size-14 place-items-center rounded-2xl bg-[#e5f8cf] text-[#31531e]">
                      <Bot className="size-7" />
                    </div>
                    <h2 className="text-xl font-semibold tracking-tight">
                      No incident to analyse
                    </h2>
                    <p className="mt-2 text-sm leading-6 text-[#617067]">
                      Run the offline scenario to generate synthetic telemetry,
                      open an incident and request a cited diagnosis.
                    </p>
                    <Button
                      className="mt-5 bg-[#173f2e] text-white hover:bg-[#102a20]"
                      onClick={runOfflineScenario}
                      disabled={isActing || !canOperate}
                      title={
                        canOperate ? undefined : "Operator role required"
                      }
                    >
                      <Play className="fill-current" />
                      Run offline scenario
                    </Button>
                  </CardContent>
                </Card>
              )}
            </section>

            <div className="flex flex-col gap-2 border-t border-black/5 pt-4 text-xs text-[#738078] sm:flex-row sm:items-center sm:justify-between">
              <span>
                Synthetic data only · No autonomous write actions ·{" "}
                {providerFooterLabel(primaryIncident?.diagnosis?.provider)}
              </span>
              <span>Snapshot {timeLabel(dashboard.generatedAt)}</span>
            </div>
          </div>
        ) : null}
      </div>
    </main>
  );
}
