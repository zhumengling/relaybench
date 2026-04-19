# NetTest Suite

NetTest Suite is a Windows desktop diagnostics workbench for:

- OpenAI-compatible proxy stability checks
- ChatGPT trace and region checks
- NAT and STUN observation
- Route map, MTR, speed-test, split-routing, and local port-scan modules

## Implemented in the current build

- WPF desktop shell with tabbed diagnostics pages
- One-click structured report bundle export into `data/reports`
  - exports `report.txt`, `sections.json`, `report.json`, and `manifest.json`
  - bundles raw outputs and route-map screenshots into a `.zip`
  - shows recent report archives directly in the History page
- Baseline network check
  - host name
  - active adapters and addresses
  - DNS summary
  - baseline ping against `1.1.1.1`, `8.8.8.8`, and `chatgpt.com`
  - public IP capture through `cloudflare.com/cdn-cgi/trace`
- ChatGPT trace check
  - requests `https://chatgpt.com/cdn-cgi/trace`
  - parses `ip`, `loc`, and `colo`
  - compares `loc` against the embedded supported-region snapshot
  - runs an extended unlock catalog across OpenAI and other common AI-service endpoints
- STUN baseline probe
  - defaults to `stun.cloudflare.com`
  - parses `MAPPED-ADDRESS`, `XOR-MAPPED-ADDRESS`, and `OTHER-ADDRESS`
  - performs classic follow-up tests with `CHANGE-REQUEST` and alternate addresses when available
  - provides best-effort NAT classification such as open internet, full cone, restricted cone, port-restricted cone, and symmetric NAT
- Proxy diagnostic MVP
  - tests `GET /models`
  - sends one tiny non-stream chat request
  - sends one stream chat request to capture a first-token timing signal
- Proxy stability series
  - repeats the proxy probe for multiple rounds
  - computes success rates, average latency, average TTFT, and max consecutive failures
  - outputs a simple health score and label
- Proxy batch comparison
  - accepts a small batch list of relay endpoints
  - reuses the current relay settings or per-line overrides
  - ranks candidates with a simple score based on models, chat, stream, latency, and TTFT
  - outputs a recommended relay plus per-entry details in the GUI
- Proxy timeline and history view
  - stores recent single-run, stability, and batch-comparison results locally
  - summarizes success rate, average latency, TTFT, health score, batch score, and volatility per relay
- Cloudflare-style speed test
  - uses `https://speed.cloudflare.com/__down` and `https://speed.cloudflare.com/__up`
  - supports `Quick`, `Balanced`, and `Extended` profiles
  - measures idle latency, idle jitter, download, upload, download loaded latency, and upload loaded latency
  - adds an ICMP-based packet-loss estimator against `speed.cloudflare.com`
  - computes a desktop-oriented GPT impact score and label
- Route and MTR baseline
  - runs `tracert` against a target host or IP
  - parses each hop into a route table
  - performs MTR-like ICMP sampling per responsive hop
  - reports per-hop packet loss plus best, average, and worst latency
  - geolocates public hops and renders them on real OpenStreetMap tiles
- IP and split-routing diagnostics
  - shows active adapters, local IPs, and configured DNS servers
  - compares public exit views from `https://chatgpt.com/cdn-cgi/trace`
  - compares public exit views from `https://www.cloudflare.com/cdn-cgi/trace`
  - compares public exit hints from `https://speed.cloudflare.com/__down?bytes=0`
  - contrasts system DNS answers with Cloudflare DoH and Google DoH
  - checks HTTPS reachability for a small host list inspired by `ip.skk.moe`
  - adds public IP ownership insight with ASN, ISP, organization, and a simple network-role hint
- Local port-scan baseline
  - ships with a built-in scan engine and does not rely on any external scanner
  - uses asynchronous TCP connect scanning with bounded concurrency
  - applies lightweight banner, TLS, and HTTP probes for common services
  - provides safe scan templates for relay and HTTPS-oriented hosts
  - shows structured findings plus raw scan logs in the GUI
## Next expansion ideas

- Batch stability series across multiple relays in the same time window
- ASN and CDN labeling for more route and host views
- Richer business-level unlock checks beyond HTTP reachability and status codes

## Third-party online data used by the current map view

- OpenStreetMap tile server for background map tiles
- ipwho.is for IP geolocation lookups of public route hops
- Cloudflare speed-test endpoints for download and upload measurements

The app caches tiles locally for repeat viewing and caches geolocation responses to reduce repeated external requests.
Speed-test packet loss currently uses an ICMP fallback estimator because Cloudflare's public TURN-based packet-loss path is deprecated in the open-source engine notes.

Local runtime state such as relay configuration, app-state snapshots, reports, exports, and tile caches is generated under `config/` and `data/` on demand and should not be committed to Git.

## Run

Preferred startup path:

```powershell
.\start.ps1
```

or

```powershell
.\start.cmd
```

`start.ps1` now uses a framework-dependent launcher in `dist\win-x64\NetTest.App.exe`.
Before launch, it checks whether `.NET Desktop Runtime 10` is installed.
If the runtime is missing, it opens the official .NET 10 download page and asks the user to install it manually.
If startup fails at the launcher level, details are written to `start.log`.
If the desktop app itself fails during window initialization, details are written to `dist\win-x64\app-startup.log`.

If the published files are missing but a local .NET SDK is available, `start.ps1` will publish the framework-dependent build once and then start it.

For end users who only receive the published folder, the publish output also includes:

```powershell
.\dist\win-x64\start.cmd
```

That launcher performs the same runtime check before starting the app.

To publish the framework-dependent build explicitly:

```powershell
.\publish.ps1
```

Manual development path:

```powershell
dotnet build .\NetTestSuite.slnx -p:UseSharedCompilation=false
dotnet run --no-build --project .\NetTest.App\NetTest.App.csproj -p:UseSharedCompilation=false
```

## Files

- `NetTest.App`
  - WPF UI
  - view models
  - command and property-notification helpers
  - local JSON state persistence for saved settings and recent history
- `NetTest.Core`
  - diagnostics models
  - network, ChatGPT trace, STUN, and proxy services
  - embedded OpenAI supported-region snapshot

## Next suggested steps

1. Persist endpoint profiles and past results.
2. Extend the proxy test with burst mode and cross-relay historical comparison.
3. Add chart-based history views for relay health, latency, and TTFT.
4. Decide whether to add a self-hosted TURN option for richer packet-loss measurement.


