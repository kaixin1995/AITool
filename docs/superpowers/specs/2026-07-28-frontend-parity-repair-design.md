# Frontend Parity Repair Design

**Date:** 2026-07-28  
**Scope:** Vue administration frontend parity with the Razor administration UI at `79bc0f3b157cea207e920a43c861e075b49e9e84`.

## Goal

Restore required functional and visual parity in the Vue SPA without reverting the JWT/REST architecture or weakening current secret-handling protections. The work covers shared application layout plus access keys, routes, usage logs, RouteFallback, system settings, developer diagnostics, model health, models, Codex, sites, chat/conversations, and analytics.

## Non-goals

- Reintroducing Razor Pages, inline Razor JavaScript, or cookie authentication.
- Restoring plaintext access keys or site API keys in list APIs or list/table UI.
- Treating a global Naive UI theme override as a replacement for page-specific information architecture.
- Changing unrelated business behavior.

## Constraints

- List responses continue to use masked secrets only.
- Newly created access keys may be shown once and copied, but are never redisplayed after the create workflow closes.
- Any restored Markdown rendering sanitizes unsafe HTML before display.
- All persisted route edits preserve availability schedules and enabled state.
- Responsive behavior is validated at 320px, 375px, 768px, 1280px, 1440px, and 1920px available widths.

## Architecture

### 1. Shared shell and styling boundaries

`MainLayout.vue` becomes the only owner of shell-level navigation and page-level scrolling. The responsive sidebar uses desktop expanded/collapsed behavior and a narrow-screen off-canvas drawer with backdrop. Routed pages must not inherit competing viewport-height scroll containers.

`main.css` retains color tokens and baseline typography but stops imposing a single card/table geometry on all pages. Dense tables, operational cards, dashboard grids, and conversational panes receive scoped page/component styles. Shared table helpers define only common semantics: non-wrapping action cells, explicit overflow behavior, and consistent accessible focus states.

### 2. Contract-first configuration screens

Routes, access keys, models, sites, and settings use typed API contracts as their source of truth. Frontend select values exactly match server enums. Save operations send and preserve all editable state; they do not rebuild records with implicit defaults. Dialogs refresh current server state before editing mutable permissions or mappings.

### 3. Operations and diagnostics screens

Usage logs, RouteFallback, developer invocations, model health, and analytics use server-filtered aggregate endpoints for summaries and server detail endpoints for request/attempt chains. Page polling is opt-in where historically user-controlled, runs only while the routed page is active and the document is visible, and is cleaned up on navigation.

### 4. Resource and interaction screens

Codex, sites, models, chat, and conversations restore the workflows that remain supported by current backend APIs. Each page has its own responsive grid or panel rules rather than relying on a generic card/table layout.

## Work Packages

### Package A — Shared shell baseline

1. Refactor `MainLayout.vue` to avoid nested scroll ownership.
2. Add mobile navigation drawer/backdrop behavior while retaining persisted desktop collapse preference.
3. Narrow global table/card overrides in `main.css`.
4. Provide reusable scoped utilities for compact action groups, status rows, and responsive page toolbars.

**Acceptance criteria**

- No page has two unintended vertical scrollbars.
- Sidebar, dialogs, drawers, tooltips, and tables remain usable in light/dark themes.
- Action buttons remain single-line or intentionally scrollable; they never wrap invisibly below table content.

### Package B — P0 routing and operational correctness

#### Route rules

- Replace unsupported availability mode values with `AllDay`, `AvailableOnly`, and `Unavailable`.
- Extend backend save DTO/persistence to preserve `isEnabled`.
- Restore an equivalent named-entry/candidate-pool workflow, including refresh/search, selection, ordering, and compact rows.

**Acceptance criteria**

- A saved schedule reloads unchanged.
- Reordering or saving does not enable a disabled rule.
- Operators can create, select, delete, search, queue, and order route entries.

#### Usage logs

- Start filters collapsed.
- Restore model keyword, complete source filters, user-controlled auto refresh, server summary, request-chain details, and historical operational columns.
- Use one explicit formatting policy: full localized numbers in rows; compact values in KPIs; duration values switch correctly between milliseconds and seconds.

**Acceptance criteria**

- Summary scope always matches the active filters and is not derived from the current table page.
- Chain details identify target site, attempts, latency, fallback, status, protocol, and token fields where available.
- Polling stops when disabled, hidden, or unmounted.

#### RouteFallback

- Add a routed and navigable SPA monitoring view using the existing backend contract.
- Restore filtering, summary metrics, pagination, and controlled refresh.

### Package C — Secure administration workflows

#### Access keys

- Keep masked list values.
- Display a newly generated secret once with copy affordance.
- Restore efficient route-permission editing: summary, select all, clear to unrestricted, multi-column list, and refresh-before-edit.

#### Sites

- Remove the meaningless list-level secret display column.
- Use content-based columns: short name, readable URL, protocol/capabilities, status, creation time, and non-wrapping actions.
- Restore the backend-supported site catalog workflow: single/bulk discovery, progress, filtering, remote model selection, and mapping import.

#### Models and model health

- Restore model search, metadata, vendor header rendering, editable model names, vendor catalog management, mapping concurrency and enabled-state controls.
- Return and display model/site health totals, mixed success/failure timeline composition, controlled details, search/filter, and distinct empty states.

### Package D — Settings, Codex, and developer diagnostics

#### System settings

- Recreate independently contained operational groups and multi-column layouts.
- Use an outlined, correctly anchored help affordance.
- Restore filtered usage-log clearing.
- Allow Codex inspection configuration to be preconfigured when master features are disabled.
- Correct trace-retention text to maximum 40 traces for 20 minutes.

#### Codex

- Restore adaptive account cards and selected-account credential export.
- Verify quota semantics before implementation; labels, progress direction, colors, and thresholds must all represent the same `used` or `remaining` meaning.
- Poll only while the route is active and the document is visible.

#### Developer invocations

- Restore structured diagnostic views instead of raw JSON-only detail.
- Restore user-controlled active-tab refresh, visible errors, routing/retry state, concurrency fields, and capability-aware simulator initialization.

### Package E — Chat, conversations, and analytics

#### Chat and conversations

- Restore model search and a non-intrusive reasoning/routing detail layout.
- Fix desktop/mobile bubble and panel geometry.
- Restore time/source/model/keyword filters, title rename, relevant metadata, independent pane scrolling, and sanitized Markdown/code rendering.

#### Analytics

- Add `bucketType` control and request plumbing.
- Display effective server-selected bucket type.
- Restore latency KPIs and align compact number/duration/percentage formatting across KPIs, axes, and tooltips.
- Choose one request model: explicit query or debounced auto-query, never duplicate uncoordinated requests.
- Make chart grid responsive below desktop widths.

## Data Flow and Error Handling

- API wrappers expose discriminated unions for alternate responses such as analytics queue/pending states.
- Page summaries are loaded from server summary endpoints with the same filter object as the list query.
- Mutation requests update local state only from confirmed server responses; on failure they show a visible error and preserve unsaved user input where safe.
- Feature-gated 404 responses show a specific disabled-feature state; all other errors remain visible and retryable.
- Polling timers are owned by composables/components and cleared in lifecycle cleanup.

## Test Strategy

### Automated

- Add focused frontend unit/component tests for enum payloads, formatting, filter serialization, polling lifecycle, secret display rules, Markdown sanitization, and route enabled-state persistence.
- Add backend tests for route save DTO enabled-state preservation and schedule persistence.
- Run frontend type checking and production build.
- Run relevant backend test projects or targeted controller/service tests.

### Browser regression

Use an authenticated local test workflow without exposing credentials in reports. For each repaired page verify:

1. desktop expanded sidebar;
2. desktop collapsed sidebar;
3. narrow viewport;
4. light and dark themes;
5. empty, loading, error, and populated states;
6. dialogs/drawers/tooltips;
7. long data and action columns;
8. mutation persistence after reload.

Screenshots and DOM checks must confirm no accidental wrapping, clipping, horizontal overflow, duplicate scroll regions, or inaccessible actions.

## Implementation Order

1. Package A shared shell baseline.
2. Package B P0 route rules, usage logs, and RouteFallback.
3. Package C secure administration workflows.
4. Package D settings, Codex, and diagnostics.
5. Package E chat/conversations and analytics.
6. Full browser, type-check, build, and persistence regression pass.

This order addresses source-proven destructive/persistence defects before visual completion while avoiding regressions from the shared layout layer.