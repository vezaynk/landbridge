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

Meta **does not build images** — it pulls pinned tags and runs them. Both images come from
the repo's **Publish images** workflow (`.github/workflows/publish-images.yml`), which
publishes to GHCR:

```
ghcr.io/vezaynk/docket-mcp
ghcr.io/vezaynk/docket-relay
```

**Publishing.** The workflow is deliberate, never per-push. Either push a version tag
(`git tag v0.4.0 && git push origin v0.4.0`) or dispatch it from the Actions tab with the
tag to publish. It `dotnet publish`es both hosts and builds the runtime-only Dockerfiles
against that output for **`linux/amd64` and `linux/arm64`**, so an Apple-silicon Docker host
pulls a native image instead of failing at run time with an exec-format error.

**Tag scheme.** Every run publishes two tags per image:

| Tag | Meaning |
|---|---|
| `v0.4.0` | The version tag pushed, or the dispatch input. Moves if you move the git tag. |
| `sha-<12-char commit>` | The commit the images were built from. Never moves — **pin this**. |

Nothing publishes `latest`, deliberately: meta skips the pull when the image is already on
the host (`EnsureImageAsync`), so a mutable tag would silently keep running a stale image.
For the same reason `Meta:DefaultImageTag` has **no default** — set it to a published tag
(prefer a `sha-` one) if you want creates to be able to omit a tag, or leave it unset and
name a tag on every create. A create with neither is rejected outright rather than pinning
something unpullable.

**Pinning.** Set `Meta:McpImageRepo` / `Meta:RelayImageRepo` to the two repositories above
(no tag). An Instance records **one shared tag** across both images — chosen at create time
and re-pinned by **Upgrade** (§4), which recreates the mcp + relay containers on the new tag
and leaves Postgres and its volume alone. Because the pair moves together, a tag is only
usable once its whole workflow run is green.

**Make the packages public — this is the supported setup.** A GHCR package the workflow
creates starts out **private**, and meta pulls **anonymously**: it sends no registry
credentials, so it cannot pull a private package at all. Publishing the first tag is
therefore two steps — run the workflow, then GitHub → *Packages* → the package → *Package
settings* → *Change visibility* → *Public*, once per package. After that there is nothing to
configure and every host can pull.

**If a package has to stay private** (an air-gapped or otherwise closed deployment) the only
option today is to pre-pull on every Docker host: `docker login ghcr.io` with a PAT carrying
`read:packages`, then `docker pull ghcr.io/vezaynk/docket-mcp:<tag>` and the same for the
relay, so meta's present-locally check short-circuits before it would pull. Two caveats
worth knowing before you rely on it: a host-side `docker login` alone is **not** enough — the
daemon does not use the CLI's credentials for meta's API-driven pull — and the pre-pull must
be repeated on every host for every new tag, so an **Upgrade** (§4) stalls on any host where
the new tag has not been fetched yet. Registry credentials meta could use itself are a
deliberately deferred follow-up, recorded against this gap; there is nothing to configure
today.

Either way you will not be guessing: a pull meta cannot satisfy fails the `PullImages` step
with the image reference and the registry's own words, and says which of the two causes
applies — see the step log on the Instance detail page (§4).

**Building by hand** (a local registry, an air-gapped host, a one-off): the images are
**runtime-only** — they COPY a `dotnet publish` output — so the build is a two-step:

```
# from the repo root, for each of Docket.Mcp and Docket.Relay:
dotnet publish src/Docket.Mcp   -c Release -o out/mcp
dotnet publish src/Docket.Relay -c Release -o out/relay
docker build -f docker/Docket.Mcp.Dockerfile   -t <registry>/docket-mcp:<tag>   out/mcp
docker build -f docker/Docket.Relay.Dockerfile -t <registry>/docket-relay:<tag> out/relay
docker push <registry>/docket-mcp:<tag>
docker push <registry>/docket-relay:<tag>
```

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
| `Meta:DefaultImageTag` | Tag a create pins when it names none. **No default** — a create with neither is rejected (§1.4). |
| `Meta:PostgresImage` | Postgres image for the per-Instance database container. |
| `Meta:CaddyAdminUrl` / `Meta:CaddyServerName` | The edge Caddy admin API and server key. |
| `Meta:MigrateOnStartup` | Apply meta's own migration to its own store at startup (dev convenience). |
| `Meta:Secrets:Keys` | **Required.** Ordered AES-256 master keys (base64, 32 bytes). Element 0 seals new values; the rest are retired keys kept so existing rows still decrypt. Meta refuses to start when empty. See §2.1. |

Run migrations for meta's store out of band in production (`Meta:MigrateOnStartup=true` is
the dev shortcut). Each **Instance** container self-migrates on boot — meta sets
`Docket:MigrateOnStartup=true` in the plane container's env, and image upgrades rely on it.

### 2.1 Key management

Meta must **retain** some Instance secrets to re-inject them when it recreates a container
on resume or upgrade: the Instance's Postgres password, the relay shared bearer, and — for a
remote host — that host's mTLS **client private key**. Its own Postgres therefore holds live
credentials, and those three columns are encrypted at rest with AES-256-GCM under a master
key you supply.

Generate a key and put it in config or the environment:

```
openssl rand -base64 32
```

```
Meta__Secrets__Keys__0=<the base64 key>
```

**Meta will not start without it.** There is no plaintext fallback: a meta that cannot
protect these values refuses to run rather than quietly writing credentials in the clear.
Startup also fails on a key that is not 32 bytes of valid base64.

What is *not* encrypted, deliberately: the operator passphrase **hash** (already a one-way
digest of a value meta never keeps) and a host's CA and client **certificates** (public
material — leaving them legible lets you audit which host a row points at without holding
the key).

#### Back up the key AND the database

They are useless apart, and losing **either** one strands every Instance:

- Lose the key, keep the database → meta can no longer decrypt the retained secrets. The
  running containers keep running, but resume, upgrade, and any recreate stop working.
  Recovery means rebuilding each Instance's credentials by hand.
- Lose the database, keep the key → the Instance records are gone regardless.

Back the key up somewhere separate from the database dump (a password manager or your
organisation's secret store). A database dump alone is not enough to restore meta, and — the
other half of the same property — a leaked database dump alone does not disclose the
secrets.

#### Rotating the key

Rotation is deliberately *completable*: you can finish it and drop the old key, rather than
carrying every historical key forever.

1. Generate a new key: `openssl rand -base64 32`.
2. Put the **new key first** and keep the old one after it:
   ```
   Meta__Secrets__Keys__0=<new key>
   Meta__Secrets__Keys__1=<old key>
   ```
3. Restart meta. On startup the rewrap sweep re-seals every live Instance's retained
   secrets **and every host's mTLS client key** under the new key, logging
   `rewrapped secrets for N row(s) under key <fp>`.
4. Confirm that log line, then **remove the old key** and restart again.

Each stored value names the key that sealed it by fingerprint, so during step 3 both keys
are in use and nothing is unreadable. If you remove a key that some row still needs, meta
tells you exactly which fingerprint is missing instead of failing obscurely — put it back
and repeat step 3.

Destroying an Instance shreds its retained secrets (the columns are blanked, not just
tombstoned), so a destroyed Instance holds nothing a future key rotation needs.

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
upgrade), encrypted at rest under the master key from §2.1. Only the operator passphrase is
shown-once-and-discarded.

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
