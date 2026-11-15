# ADR-0003: USB ADB first for worker transport

- Status: Accepted for the prototype; Wi-Fi transport evaluation deferred by owner
- Date: 2026-11-14

## Context

The three phones are on Wi-Fi, but Android wireless debugging pairing failed on the available router. We need repeatable RPC connectivity to continue proving inference.

## Decision

Use USB ADB forwarding from distinct Mac loopback ports to RPC port 50052 on each phone. Do not expose RPC to a public or untrusted network.

## Consequences

This makes current runs repeatable and easy to inspect in one Mac Terminal. It does not validate Wi-Fi latency, routing, or unattended operation. The comparative one/two/three-worker USB experiment is documented separately; Wi-Fi remains unvalidated and deferred.

## Update — USB-only experiments for the current MVP increment

The owner chose to keep inference experiments on USB and defer Wi-Fi transport because network behavior may affect performance. Do not treat the absence of Wi-Fi results as a failed USB experiment or replace the transport silently.
