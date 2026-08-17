# Preview TLS: wildcard certs with lego

Operator recipe for putting real TLS on the HTTP preview frontend
(`Landbridge.Preview`, spec §8.4). The frontend terminates TLS itself on
`*.preview.<your-domain>` from a wildcard-cert PEM you point it at; this page
gets that PEM issued and renewed automatically with
[lego](https://go-acme.github.io/lego/), a single-binary ACME client.

## Why this shape

- **Wildcard, not per-label certs.** Preview subdomain labels are unguessable
  capability tokens (§8.4). Issuing a certificate *per label* would publish
  every label to public Certificate Transparency logs within minutes — a
  capability URL in a CT log is no longer secret. One wildcard cert keeps
  labels out of CT entirely. Wildcards require the ACME **DNS-01** challenge.
- **TLS stays in `Landbridge.Preview`, issuance stays outside it.** The frontend
  routes once per connection and byte-splices, so a TLS-terminating HTTP proxy
  in front (Caddy, nginx, an ALB) is *not* supported in the data path — backend
  connection pooling would interleave different labels onto one connection.
  Issuance is therefore an external, operator-side concern: any tool that
  leaves a PEM on disk works. lego is the recommended one because it is a
  single static binary with no daemon and ~90 DNS-provider integrations.
  (Running in plaintext behind a TCP/SNI-passthrough balancer is fine — the
  TLS bytes still end at the frontend.)

## Prerequisites

- A DNS zone for your domain at a provider lego supports
  ([provider list](https://go-acme.github.io/lego/dns/)) and an API token for
  it, scoped to DNS record edits on that zone.
- A wildcard DNS record pointing previews at the frontend host:

  ```
  *.preview.example.com.  A     <preview-host-ip>
  ```

- The `lego` binary on the preview host: download a release from
  [github.com/go-acme/lego](https://github.com/go-acme/lego/releases), or
  `brew install lego` / your distro's package.

The examples below use Cloudflare; swap the `--dns` value and the token
environment variable for your provider (each provider's page in the lego docs
names its variables).

## 1. First issuance

Put the DNS token in a root-owned env file (never on the command line):

```
# /etc/lego/dns.env   (chmod 600, owned by the user running lego)
CLOUDFLARE_DNS_API_TOKEN=...
```

Issue the wildcard (add `--server https://acme-staging-v02.api.letsencrypt.org/directory`
on your first attempts — the staging CA has no meaningful rate limits, the
production one does):

```
env $(cat /etc/lego/dns.env) lego \
  --accept-tos \
  --email ops@example.com \
  --dns cloudflare \
  --domains "*.preview.example.com" \
  --path /var/lib/lego \
  run
```

lego writes the pair (wildcard filenames use a leading underscore):

```
/var/lib/lego/certificates/_.preview.example.com.crt   # cert + chain, PEM
/var/lib/lego/certificates/_.preview.example.com.key   # private key, PEM
```

Keep the key readable only by the account `landbridge-preview` runs as
(`chmod 600`, `chown`).

## 2. Point `Landbridge.Preview` at the PEM

Config section `Preview` (appsettings/env of the preview host process):

```json
"Preview": {
  "ListenPort": 443,
  "Domain": "preview.example.com",
  "CertPemPath": "/var/lib/lego/certificates/_.preview.example.com.crt",
  "CertKeyPemPath": "/var/lib/lego/certificates/_.preview.example.com.key"
}
```

`Domain` is the base under which labels route (`{label}.preview.example.com`);
requests for any other host get a 404 before touching the control plane.

## 3. Automatic renewal

`lego renew` re-issues only when the cert is inside the expiry window, so it is
safe to run daily. A systemd service + timer pair:

```ini
# /etc/systemd/system/lego-preview-renew.service
[Unit]
Description=Renew the *.preview wildcard certificate

[Service]
Type=oneshot
EnvironmentFile=/etc/lego/dns.env
ExecStart=/usr/local/bin/lego --accept-tos --email ops@example.com \
  --dns cloudflare --domains "*.preview.example.com" --path /var/lib/lego \
  renew --days 30
```

```ini
# /etc/systemd/system/lego-preview-renew.timer
[Unit]
Description=Daily wildcard renewal check

[Timer]
OnCalendar=*-*-* 04:15:00
RandomizedDelaySec=1h
Persistent=true

[Install]
WantedBy=timers.target
```

```
systemctl enable --now lego-preview-renew.timer
```

**No restart needed.** `Landbridge.Preview` watches `CertPemPath`/`CertKeyPemPath`
and hot-reloads: when a renewal rewrites the pair, new TLS handshakes are served
off the new cert automatically, established preview tunnels are left untouched,
and there is no restart. `lego renew` only rewrites the files inside the expiry
window (~every 60 days), so most timer runs are a no-op. The frontend reloads
only when *both* files load and the key matches the cert, so it tolerates the
two files being rewritten non-atomically — a partially-written or mismatched
snapshot is ignored and the current cert keeps serving until the pair settles;
a failed reload never drops TLS. (A `--renew-hook "systemctl restart
landbridge-preview"` still works if you prefer an explicit restart, but it is no
longer required and does cost the in-flight tunnels.)

## Alternatives

- **Hosted product:** `landbridge-meta` already owns per-instance DNS and
  provisioning credentials (spec §3), so in the hosted deployment it — not the
  instance operator — is the natural ACME DNS-01 issuer, delivering the PEM
  alongside the instance's other provisioned material.
- **Manual:** for a single instance, renewing a purchased or manually-issued
  wildcard once a year and updating the two paths is entirely workable.
- **Not recommended:** in-process ACME libraries (the DNS API token would have
  to live inside the preview host — a credential-scope regression) and
  HTTP-01-only clients such as LettuceEncrypt (no DNS-01 → no wildcards → the
  per-label CT-log leak above).
