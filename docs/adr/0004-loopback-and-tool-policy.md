# ADR-0004: Local services and an allowlisted first tool

- Status: Accepted
- Date: 2026-11-14

## Context

The early agent needs a visible tool-calling proof without creating arbitrary command execution or exposing experimental RPC.

## Decision

Bind prototype HTTP services to `127.0.0.1`. The first tool is a fixed, read-only cluster-status command invoked as an argument vector without a shell. The model cannot choose an executable or pass arbitrary command arguments.

## Consequences

This is suitable for the local feasibility prototype but does not provide multi-user authentication. Remote clients, side-effecting tools, and execution require explicit policy, approval, audit, and isolation work in later phases.
