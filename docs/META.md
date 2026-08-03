# docket-meta — operator guide

`docket-meta` is Docket's provisioning control panel (spec §3). It is **human-only**:
a server-rendered web panel behind an operator passphrase, with **no MCP surface and
no agent access** — a separate credential class and, ideally, a separate network from
the Instances it creates. It owns Account labels, Instance lifecycle (create, suspend,
resume, destroy), Instance provisioning (network, database, plane, relay, edge routes),
and image rollout. It does **not** route work between Instances, aggregate a
cross-Instance view, or hold shared agent identity.

An **Instance** is a per-host recipe: a dedicated Docker network, a Postgres container
on a named volume, a `docket-mcp` container, and a `docket-relay` container, co-located
on one host and completely independent of every other Instance.

---

## 1. Prerequisites

### 1.1 A pool of Docker hosts

Meta drives one or more Docker Engine API endpoints through an `ISubstrate` seam:

- **Local (pool-of-one):** the unix socket `unix:///var/run/docker.sock`. Nothing to
  configure beyond meta being able to read the socket.
- **Remote:** the Engine API over TCP with **mutual TLS** (`https://host:2376`). Enable
  TLS on the remote daemon (`dockerd --tlsverify --tlscacert … --tlscert … --tlskey …`),
  then register the host in the panel with the **CA PEM**, **client cert PEM**, and
  **client key PEM**. Meta presents the client cert and pins server trust to the CA.

Each host also declares a **published-host address** (the address the edge reverse-proxies
published container ports on — `127.0.0.1` for the local pool-of-one) and a **host-port
range** meta allocates published ports from.

> Meta stores remote-host TLS material and per-Instance secrets in its own database.
> Protect the meta host and database accordingly; at-rest encryption of those columns is
> a tracked follow-up.

### 1.2 Wildcard DNS

Point wildcard DNS at your edge:

```
*.docket.<domain>   →   <edge public IP>
```

An Instance named `acme` is published at `acme.docket.<domain>` (the MCP/plane endpoint)
and `relay-acme.docket.<domain>` (the relay endpoint — `docketd` on customer machines
dials this directly, §8.3, so it needs its own public route).

### 1.3 An edge Caddy with the admin API

Meta manages **route entries** on a single edge Caddy via its admin API; you provide the
base config (an HTTPS server with automatic TLS and an initially-empty `routes` array).
Minimal base config (`caddy run --config base.json`):

```json
{
  "admin": { "listen": "0.0.0.0:2019" },
  "apps": { "http": { "servers": { "srv0": {
    "listen": [":443"],
    "routes": []
  } } } }
}
```

Caddy's own certificate automation issues certs for each `*.docket.<domain>` host as
routes appear. Set `Meta:CaddyAdminUrl` to the admin endpoint and `Meta:CaddyServerName`
to the server key (`srv0` above). Secure the admin endpoint — it is a control surface.

### 1.4 Published docket images

Meta **does not build images** — it pulls pinned tags and runs them. Build and publish
`docket-mcp` and `docket-relay` to a registry your hosts can pull from. The images are
**runtime-only** (they COPY a `dotnet publish` output), so the build is a two-step:

```
# from the repo root, for each of Docket.Mcp and Docket.Relay:
dotnet publish src/Docket.Mcp   -c Release -o out/mcp
dotnet publish src/Docket.Relay -c Release -o out/relay
docker build -f docker/Docket.Mcp.Dockerfile   -t <registry>/docket-mcp:<tag>   out/mcp
docker build -f docker/Docket.Relay.Dockerfile -t <registry>/docket-relay:<tag> out/relay
docker push <registry>/docket-mcp:<tag>
docker push <registry>/docket-relay:<tag>
```

Set `Meta:McpImageRepo` / `Meta:RelayImageRepo` to those repositories; an Instance pins a
single shared **tag** across both, and an upgrade moves both together.

---

## 2. Configuration

`docket-meta` reads its own config section and its own store connection string. It never
touches an Instance's database.

| Key | Meaning |
|---|---|
| `ConnectionStrings:Meta` / `DOCKET_META_DB` | Meta's own Postgres. |
| `Meta:Operator:PassphraseHash` | SHA-256 **hex** of the operator passphrase. Fail-closed (503 login) when unset. Generate: `printf '%s' 'your-passphrase' \| shasum -a 256`. |
| `Meta:Domain` | Base domain for the two routes per Instance. |
| `Meta:McpImageRepo` / `Meta:RelayImageRepo` | Image repositories (no tag). |
| `Meta:DefaultImageTag` | Default tag a new Instance pins. |
| `Meta:PostgresImage` | Postgres image for the per-Instance database container. |
| `Meta:CaddyAdminUrl` / `Meta:CaddyServerName` | The edge Caddy admin API and server key. |
| `Meta:MigrateOnStartup` | Apply meta's own migration to its own store at startup (dev convenience). |

Run migrations for meta's store out of band in production (`Meta:MigrateOnStartup=true` is
the dev shortcut). Each **Instance** container self-migrates on boot — meta sets
`Docket:MigrateOnStartup=true` in the plane container's env, and image upgrades rely on it.

---

## 3. What meta injects into an Instance (the security-sensitive part)

At create, before any container exists, meta generates the Instance's secrets and records
a `provisioning` row. It then injects:

- `Docket:Operator:PassphraseHash` — the **hash** of a freshly generated operator
  passphrase. The **plaintext is shown once** on the create page and never stored; copy it
  and hand it to the Instance's operator.
- `ConnectionStrings:Docket` — the private Postgres, reachable by container name on the
  Instance's network.
- `Docket:PublicMcpUrl` — `https://<name>.docket.<domain>` (also the OAuth issuer/resource id).
- `Docket:RelayUrl` — `https://relay-<name>.docket.<domain>`.
- `Docket:RelayValidation:Bearer` — a shared secret, also set as `Relay:ControlPlane:Bearer`
  on the relay container, so the relay validates grants against the plane.
- `Docket:MigrateOnStartup=true`.

Meta **never** sets the dev-only gates (`Docket:DevSeed:TokenFile`,
`Docket:Oauth:AllowInsecureClientMetadata`): a production Instance is passphrase-gated with
real OAuth and no seeded identities. Enroll machines and provision a verifier through the
Instance's own §5 flows after creation.

Meta **retains** the DB password and relay bearer (it must re-inject them on resume and
upgrade). Only the operator passphrase is shown-once-and-discarded.

> **No signing key.** Docket's tokens are opaque and hashed at rest (spec §5, §13) — there
> is no signing/HMAC key for an Instance to hold today, so meta generates and injects none.
> The concept is reserved for a future consumer.

---

## 4. Panel walkthrough

1. **Sign in** with the operator passphrase.
2. **Hosts** → add a host (local socket, or a remote endpoint with its mTLS PEMs, a
   published-host address, and a port range). Remove is blocked while a host has live
   Instances.
3. **Instances → Create** → name (a DNS label), optional account label, image tag, and a
   host (or *Auto*, which picks the least-loaded host). Submitting shows the **operator
   passphrase once** and starts provisioning in the background.
4. **Instance detail** shows live health (Postgres / plane / relay), the two published
   ports and public URL, and the **provisioning step log** (the saga checkpoints). Actions:
   - **Suspend** — stop the containers and drop the edge routes; the volume and config are
     kept.
   - **Resume** — start the containers back up and restore the routes.
   - **Upgrade** — recreate the mcp + relay containers on a new tag; Postgres and its volume
     are untouched (the plane re-migrates on boot).
   - **Destroy** — remove containers, network, **and volume** (data is lost); requires
     retyping the Instance name. Destroyed ≠ suspended.
   - **Retry** — re-run a stalled (`failed`) provision from its last good step.

Provisioning is a resumable saga: every step is idempotent and checkpointed, so a meta
restart mid-provision resumes on startup, and a stalled Instance is retried from where it
stopped. A destroy or suspend tolerates an unreachable edge — a dangling route resolves to
a dead upstream until the next reconcile clears it, rather than blocking teardown.
