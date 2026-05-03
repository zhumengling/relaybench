---
version: alpha
name: RelayBench
source: "Installed from VoltAgent/awesome-design-md, IBM-inspired baseline, adapted for RelayBench."
source_url: "https://github.com/VoltAgent/awesome-design-md"
source_license: "MIT License, Copyright (c) 2026 VoltAgent"
description: "RelayBench is a desktop diagnostics, model testing, and local proxy gateway tool. The UI should feel like a precise operations cockpit: calm, readable, data-dense, and refined. Use an IBM Carbon-inspired base for structure and accessibility, then soften it for a modern Windows desktop app with subtle depth, compact controls, clear status signals, and lightweight motion. Avoid marketing-page composition, oversized hero typography, decorative gradients, and card-heavy layouts."
---

# RelayBench Design System

## 1. Product Personality

RelayBench is not a landing page. It is a working cockpit for repeated technical decisions: testing upstream model APIs, comparing routes, running security suites, watching local proxy health, and reviewing logs.

The interface should feel:

- Precise, trustworthy, and operational.
- Dense enough for scanning, but never cramped.
- Quiet by default, expressive only when status changes.
- Desktop-native, not web-marketing inspired.
- Elegant in small details: spacing, alignment, numeric rhythm, hover states, and empty states.

Do not make RelayBench look like a generic UI kit demo. It should look like a dedicated instrument.

## 2. Color System

Base palette, adapted from IBM Carbon:

| Token | Hex | Role |
| --- | --- | --- |
| canvas | `#FFFFFF` | Main app background |
| canvas-soft | `#F6F8FA` | Page background and quiet bands |
| surface | `#FFFFFF` | Primary panels and controls |
| surface-muted | `#F4F4F4` | Secondary panels |
| surface-raised | `#FBFCFE` | Floating tools, popovers, detail drawers |
| ink | `#161616` | Main text |
| ink-muted | `#525252` | Secondary text |
| ink-subtle | `#6F6F6F` | Hints, metadata |
| hairline | `#E0E0E0` | Borders and grid lines |
| hairline-strong | `#C6C6C6` | Active borders |
| primary | `#0F62FE` | Primary action, selected nav, focus |
| primary-hover | `#0050E6` | Primary hover |
| primary-soft | `#E8F1FF` | Selected row / quiet emphasis |
| success | `#24A148` | Healthy, passed, running |
| warning | `#F1C21B` | Degraded, attention |
| danger | `#DA1E28` | Failed, stopped by error |
| info | `#4589FF` | Probing, neutral activity |
| token-live | `#0E9F6E` | Live token flow |
| token-idle | `#64748B` | Idle token total |

Usage rules:

- Keep most pages light. Dark surfaces are allowed only for console-like previews, token meter compact mode, and code/log wells.
- Use blue as the main interactive accent, green for live/healthy states, yellow for degraded states, red for blocking failures.
- Do not create one-note blue dashboards. Use status colors sparingly and semantically.
- Avoid large gradients, decorative blobs, bokeh, or atmospheric backgrounds.

## 3. Typography

Preferred desktop stack:

- UI text: `Segoe UI`, `Microsoft YaHei UI`, `IBM Plex Sans`, `Inter`, sans-serif.
- Numeric and code text: `Cascadia Mono`, `Consolas`, `JetBrains Mono`, monospace.

Type scale:

| Token | Size | Weight | Use |
| --- | --- | --- | --- |
| page-title | 18-20 | 600 | Page title |
| section-title | 14-16 | 600 | Panel titles |
| body | 12.5-13.5 | 400 | Default desktop copy |
| body-strong | 12.5-13.5 | 600 | Labels and important values |
| caption | 11-12 | 400 | Metadata and helper text |
| micro | 10.5-11 | 500 | Status chips and compact tables |
| metric | 20-28 | 600 | Key numeric values |
| token-meter | 22-30 | 600 | Floating token meter primary number |
| mono | 12-13 | 400 | URLs, model names, request IDs |

Rules:

- No viewport-based font scaling.
- Letter spacing should be `0` for app UI. Avoid negative tracking in compact controls.
- Long technical strings must use ellipsis plus tooltip.
- Reserve large type for metrics and focused readouts, not ordinary cards.

## 4. Layout Principles

RelayBench pages should use working layouts:

- Command bar at top for page state and high-frequency actions.
- Left configuration column only when it materially helps repeated work.
- Main data area with DataGrid, charts, logs, or structured results.
- Detail drawer or modal for editing and deeper inspection.

Spacing:

| Token | Value |
| --- | --- |
| gap-xs | 4 |
| gap-sm | 8 |
| gap-md | 12 |
| gap-lg | 16 |
| gap-xl | 24 |
| page-padding | 10-14 |
| panel-padding | 10-14 |
| dense-row-height | 34-40 |
| icon-button | 34-40 |

Rules:

- Do not nest cards inside cards.
- Page sections should be full-width layouts or plain panels, not decorative floating islands.
- Cards are for repeated items, small metrics, dialogs, and genuinely framed tools.
- Keep operational screens dense, aligned, and scannable.
- When width is tight, prefer horizontal scroll in tables over unreadable compressed columns.

## 5. Components

### Buttons

- Primary action: blue fill, white text, 6-8 px radius.
- Secondary action: white or muted surface, hairline border.
- Icon actions: square 34-40 px, MDL2/lucide-like glyphs, tooltip required.
- Destructive action: red only for irreversible or exit actions.

Do not use text buttons for common tool actions when an established icon exists.

### Inputs

- Text fields use white or very light background, 1 px border, 6-8 px radius.
- Invalid fields show inline validation below the field, not only a modal.
- Password/API key fields must default to masked and show only preview fragments.

### Tables

- DataGrids are central to RelayBench.
- Header height 34-36 px, row height 34-40 px.
- Use virtualization for route lists, logs, and history.
- Core columns get minimum widths.
- Long cells use ellipsis and tooltip.
- Status cells use compact badges, not verbose sentences.

### Status Badges

Use short, stable labels:

- `Running`
- `Stopped`
- `Probing`
- `Healthy`
- `Degraded`
- `Down`
- `Closed`
- `Open`
- `Half-open`
- `Cached`
- `Fallback`

Badges should be compact and aligned. Avoid noisy pill overload.

### Dialogs and Drawers

- Use modals for creation and editing.
- Use right-side detail drawers for logs, request chains, route attempts, and advanced read-only inspection.
- Keep modal primary actions bottom-right.
- Escape closes non-destructive dialogs.

## 6. Transparent Proxy Page

The transparent proxy page should become a local gateway console.

Recommended structure:

- Top command bar: title, run state, local endpoint, start/stop, copy, probe, import.
- Left settings rail: listen address/port, rate limit, concurrency, cache, route policy.
- Main route queue: priority, enabled, name, base URL, model, protocol badges, health, circuit state, latency, success rate, actions.
- Bottom observability: request logs, filters, selected request detail.

Rules:

- No raw route text box as the primary UI.
- Route editing belongs in a dialog.
- Logs should be a compact table, not a stack of large cards.
- Protocol support should be shown as readable badges: `Responses`, `Anthropic`, `Chat`.

## 7. Floating Token Meter

The floating token meter is a desktop instrument, not a mini control panel.

Visual rules:

- Default size: about `220 x 58`.
- Radius: 8 px.
- Use subtle glass-like surface, hairline border, and soft shadow.
- No table, no form controls, no long explanatory text.
- Primary number uses monospace or tabular numerals.
- Live state uses `token-live`; idle state uses `token-idle`.

Display rules:

- During streaming: show tokens per second, for example `42.8 tok/s`.
- After a non-streaming response: briefly show delta, for example `+861 tokens`.
- When idle for 5 seconds: show current phase total, for example `12.4k tokens`.
- Rotate idle secondary data every 4 seconds: phase total, input/output split, last request duration.
- Hover pauses rotation and reveals precise values.

Interaction rules:

- Drag to move.
- Snap gently to screen edges.
- Double-click opens RelayBench.
- Right-click menu: open main window, lock position, click-through, reset phase, hide.
- It may stay topmost, but must not steal focus.

## 8. Tray and Background Mode

When local proxy or floating meter is active, closing the main window should hide RelayBench to the tray instead of exiting.

Rules:

- The tray menu is the real exit path.
- Tray menu includes open, start/stop proxy, show/hide token meter, background mode, exit.
- Use notifications sparingly: first hide-to-tray, proxy error, port conflict, exit failure.
- Exiting should stop the proxy, flush logs, close floating windows, release the tray icon, and release the port.

## 9. Motion

Motion is functional:

- Hover: 100-160 ms.
- State color transition: 150-200 ms.
- Dialog open: opacity plus 4-6 px vertical movement.
- Floating meter number change: subtle fade or numeric roll, no flashing.
- Running-only sections: 200-240 ms height/opacity transition.

Respect reduced motion settings where possible.

## 10. Empty, Loading, and Error States

Every data surface needs all three:

- Empty: concise message plus one obvious action.
- Loading: skeleton or small spinner with stable layout.
- Error: what failed, safe reason, next action.

Do not leave blank panels. Do not show raw exceptions with secrets.

## 11. Accessibility

- Text contrast should meet WCAG AA.
- Keyboard focus must be visible.
- Icon-only buttons need tooltips.
- Hit targets should be at least 32 px in dense desktop areas.
- Status must not rely on color alone.
- Important live metrics should update without causing layout shift.

## 12. Design Anti-patterns

Avoid:

- Marketing hero sections inside the app.
- Nested cards and decorative panels.
- Huge gradients or orbiting decorative shapes.
- Table columns squeezed until unreadable.
- Long Chinese or English labels inside narrow buttons.
- Full secret values in UI, logs, tooltips, exports, or screenshots.
- Dark-only pages unless the surface is a console, preview, or floating meter.
- Purely aesthetic motion that distracts from diagnostics.

## 13. Implementation Notes for WPF

- Prefer shared styles in `RelayBench.App/Resources/WorkbenchTheme.xaml`.
- Page-specific styles may live in page resources when scoped.
- Use `DataGrid` virtualization for logs and route tables.
- Use stable `MinWidth`, `MaxWidth`, row heights, and icon button sizes.
- Use `TextTrimming=CharacterEllipsis` and tooltips for URLs, model names, and request IDs.
- Avoid layout changes caused by hover, loading text, or status badges.
- Test at 1366x768, 1600x900, 1920x1080, and high DPI.

## 14. Source Notice

This design file was installed using the methodology from `VoltAgent/awesome-design-md` and adapted from its IBM-inspired `DESIGN.md` baseline for RelayBench's WPF desktop product needs.

Original collection:

- `https://github.com/VoltAgent/awesome-design-md`
- MIT License, Copyright (c) 2026 VoltAgent
