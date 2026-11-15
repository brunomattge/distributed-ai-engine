# Phase 1: minimal distributed cluster foundation

## Objective

Start and monitor one llama.cpp RPC worker on each of three Android phones, with USB ADB forwarding from the Mac. This phase adds cluster lifecycle and health checks; it does not load an LLM or implement chat/agent behavior.

## Scope and risks

- Detect exactly three authorized ADB devices and record each Android model/version.
- Start one RPC worker per device, forward distinct Mac ports to worker port 50052, and verify each endpoint.
- Show worker connectivity, process state, and RPC endpoint state in a live terminal monitor.
- Preserve raw worker logs as produced by the RPC server; collect them without translating their contents.
- Use USB only. Wi-Fi transport remains unverified.
- Current devices are two Galaxy A07s and one Galaxy A06. The A06 is documented as a hardware deviation from the final target.
- RPC is a proof-of-concept protocol and must stay on a trusted local connection.

## Acceptance criteria

1. start rejects anything other than exactly three authorized phones and gives a useful diagnostic.
2. start launches one worker on each phone and verifies all three forwarded RPC ports.
3. status reports ADB connection, worker process, and TCP reachability for each forwarded port.
4. watch records timestamped health samples and exits cleanly on Ctrl+C without stopping workers.
5. logs displays raw worker output; stop collects raw logs and removes processes/forwards.

## Use

Run these commands from this directory in one Mac Terminal window:

    ./clusterctl.sh start
    ./clusterctl.sh status
    ./clusterctl.sh watch 2
    ./clusterctl.sh logs
    ./clusterctl.sh stop

Press Ctrl+C to leave watch; workers remain active until stop is run. An optional sample count makes a bounded run, for example watch 2 3 checks the cluster three times at two-second intervals. Each session writes cluster.log, status.log, and (after stopping) worker-1.log through worker-3.log under a timestamped directory in results/.

The status log identifies devices with SHA-256-derived `adb-` node IDs and does not write raw ADB serials. The ignored `.runtime/active.tsv` file keeps serials locally because the controller needs them to address USB devices.

The Android worker binary is reused from ../phase-0/build-android/bin/ggml-rpc-server. ANDROID_ADB can override the default macOS SDK path.

`clusterctl.sh status` proves only that a TCP connection to each forwarded port can be established. It does not perform an RPC HELLO or prove that a model is loaded. The dashboard separately reports ADB heartbeat and process state. A recurring dashboard TCP probe is intentionally not used: the llama.cpp RPC server handles a long-lived coordinator connection in its accept loop, so a raw TCP connect can enter the listen backlog without validating protocol service.

## ASP.NET Core platform slice

The first product-control-plane slice lives in `src/dotnet/DistributedAI.Platform`. It has an in-memory node registry, health/heartbeat endpoints, and a six-second offline timeout. `tools/DistributedAI.NodeSimulator` registers three clearly labeled simulated A07 nodes and sends telemetry; it does not communicate with phones.

Build and unit-test it from the repository root:

    dotnet build DistributedAI.slnx
    dotnet test DistributedAI.slnx --no-build

Run the API and simulator lifecycle check in the same Terminal:

    ./tools/phase-1-smoke.sh

The API binds to 127.0.0.1:5100 by default. After starting the API and simulator, inspect `http://127.0.0.1:5100/api/nodes`. Stop the simulator with Ctrl+C and the registry reports its nodes Offline after six seconds without a heartbeat. This prototype is in-memory and has no authentication; do not expose it to a network.

## Current .NET slice result

Validated on 2026-11-14 from the Mac Terminal:

- `dotnet build DistributedAI.slnx` succeeded, including the API, shared contracts, tests, and simulator.
- `dotnet test DistributedAI.slnx --no-build` passed all 18 .NET tests.
- `./tools/phase-1-smoke.sh` passed: the simulator registered 3/3 nodes as Online, all three became Offline after the heartbeat timeout, an explicit HTTP disconnect marked a node Offline immediately, and its next heartbeat restored Online.

These nodes are synthetic. This result validates the API lifecycle and timeout behavior only; it does not verify phone connectivity, real device metrics, or the Angular dashboard. The phase remains in progress.

## Angular cluster dashboard

The `apps/web` Angular application polls `GET /api/nodes` every five seconds, shows node health and reported telemetry, and provides a manual refresh and API error state. The development server proxies `/api` to the loopback-only .NET API, so the API does not need broad CORS access.

From the repository root, install frontend dependencies and launch the connected demo:

    cd apps/web && npm ci
    cd ../..
    dotnet build DistributedAI.slnx
    ./tools/run-dashboard.sh

Open http://127.0.0.1:4200/cluster. The agent chat is available at http://127.0.0.1:4200/chat. The launch script starts the API, simulator, and Angular server in one Terminal and confirms that all three simulated nodes are Online through the frontend proxy. Press Ctrl+C to stop them. The cards prominently identify simulated data; the UI does not claim that synthetic readings come from physical devices.

The Angular production build and four component tests passed on 2026-11-14. The tests cover three simulated nodes rendering Online, all three rendering Offline after heartbeat expiry, the simulation notice, API failure, retry, ADB data provenance, and a missing temperature sensor.

## ADB telemetry bridge

Run `./tools/run-dashboard.sh adb` from the repository root to use authorized Android devices over USB instead of the simulator. The bridge runs on the Mac and reads the phones; it is a development telemetry adapter, not an Android-resident agent and not part of llama.cpp inference.

- Supported models are Galaxy A07 (SM-A075M) and Galaxy A06 (SM-A065M). Other devices are ignored.
- Stable node IDs are derived from a SHA-256 hash of the ADB serial. Raw serials remain in process memory and are neither printed nor written to project files.
- CPU is calculated from two `/proc/stat` samples 350 ms apart. Available RAM comes from `/proc/meminfo` (`MemAvailable`). Surface temperature comes from the Android `SKIN` sensor in `dumpsys thermalservice`; if unavailable, the dashboard shows no temperature instead of inventing one.
- The `ADB collection` value is the combined duration of the RAM and thermal ADB queries. It is not Wi-Fi RTT or llama.cpp RPC latency.
- The bridge discovers devices and sends heartbeats every two seconds. At startup it reconciles existing `adb-` nodes from the API with the current ADB scan; during operation, a successful scan that omits a previously visible phone immediately tells the API to mark it Offline. If the bridge or ADB scan fails, the API's six-second heartbeat timeout remains the fallback. Reconnecting the same phone reuses its hashed ID and the next heartbeat marks it Online.

Validated on 2026-11-14 with four authorized Android devices visible to ADB: the bridge selected two Galaxy A07s and one Galaxy A06, and excluded the fourth unsupported model. All three sent real heartbeats and appeared Online in the Angular dashboard with CPU, available RAM, and surface temperature. Unplugging two USB cables produced two Offline nodes in the API; reconnecting them restored 3/3 Online. Exact transition latency was not separately timed. This remains a hardware deviation from the target of exactly three Galaxy A07s.

On this increment, `dotnet build DistributedAI.slnx`, all 18 .NET tests, all 13 Python tests, the Angular production build, and all four Angular component tests passed. The smoke test verified the explicit disconnect and heartbeat recovery endpoints; the Python suite covers startup reconciliation of previously registered ADB nodes.

## Faster status detection

The initial ADB/dashboard intervals were five seconds with a ten-second Offline timeout, which could take around 10–15 seconds to show a disconnect. The current settings are two-second ADB polling, five-second UI polling, and immediate Offline notification when a successful ADB scan omits a device. The six-second API timeout remains as a fallback. Device collections run concurrently and each ADB command has a two-second limit, so one unresponsive phone does not hold up heartbeats from the other devices. A temporary USB renegotiation can briefly mark a phone Offline; its next heartbeat restores Online.

On 2026-11-15, a Mac-side `adb -s <serial> reconnect device` was issued for one connected Galaxy A07 while the API and bridge were running. ADB returned success, but the transport recovered before the bridge's two-second discovery scan observed it missing; the API remained at 3/3 Online. This validates only a brief ADB transport restart and does not measure physical USB-disconnect latency. At the end of the observation, two of the three RPC processes were reported Running and one Stopped; node liveness and RPC process state still do not prove model readiness. The Mac ADB server reported the `NATIVE` USB backend; ADB's `detach` command is limited to the libusb backend.

## Distinguishing liveness from the RPC process

The dashboard's `Online` state means a recent heartbeat was received. The ADB bridge now separately probes `pidof ggml-rpc-server` and the API exposes `rpcWorkerProcessState` as `Unknown`, `Running`, or `Stopped`. When the heartbeat expires or the bridge marks the node disconnected, the API returns this process state as `Unknown` to avoid presenting a stale observation. The dashboard labels it as process state and explicitly says that a running process does not prove a model is loaded or ready for inference.

Validated on 2026-11-14: all three currently connected phones (two A07s and one A06, USB) appeared `Online` with the RPC process `Running`; the Angular chat proxy still streamed a response after the API update. The process stop/start transition and RPC/model-specific readiness probe remain open. The new status must not be used for scheduling.

The later model-readiness investigation evaluated a recurring TCP connect probe against the active RPC forwards and removed it after ADB telemetry calls began timing out. Removing the project forwards restored ADB shell access on all three phones; the launcher then restarted the cluster, and inference plus the Angular chat proxy passed again. The dashboard therefore keeps heartbeat and process observations only; no per-request RPC readiness claim is made.

Automated validation after this change: 47 .NET tests passed and two database integration tests were skipped; all 9 ADB bridge tests and all 12 Angular tests passed. The live Angular API proxy returned `Online`/`Running` for all three phones.

## Validation

The integration run on 2026-11-14 passed on two Galaxy A07s and one Galaxy A06, Android 16:

- Shell syntax and command help succeeded.
- start launched all three workers and verified all three forwarded RPC ports.
- status reported 3/3 workers connected, active, and reachable.
- watch recorded repeated health samples. Ctrl+C ended the monitor; a subsequent status still reported 3/3 healthy.
- logs displayed raw RPC output. stop collected all three raw worker logs, stopped the processes, and removed the USB forwards.

Each session writes `cluster.log`, `status.log`, and (after stopping) `worker-1.log` through `worker-3.log` to the local results directory. Device-specific artifacts are excluded from Git.
