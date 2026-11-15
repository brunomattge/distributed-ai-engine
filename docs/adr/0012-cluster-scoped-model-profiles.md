# ADR 0012: Select one model profile for the whole cluster

- Status: Accepted
- Date: 2026-11-14

## Context

The current experiment runs one llama.cpp coordinator and one GGUF partitioned over the selected RPC workers. It does not load multiple independent model instances, and changing the GGUF requires unloading/reloading the coordinator. The 7B model's first response took over two minutes on the current mixed phone cohort, while a local 0.5B candidate completed the same short prompt in about nine seconds.

## Decision

Expose model choice as a cluster-session profile selected at launcher startup. Provide a fast 0.5B profile as the default measured option and retain the 7B profile as an explicit larger-model option. Allow a local GGUF path for experiments. The chat and cluster dashboard report the same active model and readiness; model choice is global to the cluster session, not per conversation. Switching requires stopping and restarting the inference session.

The web app will not send an arbitrary model ID per chat request. A live web switch would require a supported model-server lifecycle controller, validation of files/profiles, and safe unload/reload/error handling; those are separate work and are not required to expose an honest model choice for the current experiment.

## Evidence and limits

One cold request per available profile used the same short prompt and two-token output on two Galaxy A07s plus one Galaxy A06 over USB. The 0.5B took 9.08 s in llama.cpp and 19 s to load; the 7B took 133.05 s and 163 s to load. All three worker logs recorded graph work in both runs, with zero host model-weight allocation. A separate single prompt made the 0.5B select the read-only cluster-status tool when its callback returned simulated state; no worker probes were used. These measurements identify the fastest of the two locally tested profiles for this prompt, not a universal optimum or a quality/reliability comparison.

## Consequences

- New launcher sessions default to the fastest measured local profile (0.5B); `large` selects the 7B profile.
- The UI presents the loaded profile in both the chat and cluster pages, preventing ambiguous separate selections.
- Selection is a restart-time choice; no runtime hot swapping is promised.
- More model candidates and repeated benchmark samples may change the default later.
