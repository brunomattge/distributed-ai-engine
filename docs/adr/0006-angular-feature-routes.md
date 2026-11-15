# ADR 0006: Separate the cluster dashboard and agent chat into Angular routes

**Status:** Accepted

## Context

The cluster overview and agent chat are distinct user workflows. Keeping both screens in one component made the dashboard poll nodes while the user was chatting and coupled two independent UI states and test suites.

## Decision

Keep one Angular single-page application and the existing shared navigation shell. Render the cluster overview at `/cluster` and the agent chat at `/chat`, using standalone route components loaded lazily. Redirect the root URL to `/cluster`.

## Consequences

- Each screen owns its state, API requests, template, and tests.
- The cluster page polls node telemetry only while that route is active.
- The chat page loads conversation history only while that route is active.
- The router provides browser history and direct links without creating separately deployed frontends or introducing a new backend boundary.
- Shared visual styles remain global to keep both route components visually consistent.
