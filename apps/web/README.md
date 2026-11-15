# Angular cluster dashboard

This Angular 22 application displays node health and the latest telemetry returned by the ASP.NET Core platform API. It polls every five seconds, clearly labels simulator-generated data, and provides a streaming chat UI at `/api/chat/stream` for the Python agent runtime through the ASP.NET Core SSE proxy. When the runtime calls the allowlisted cluster-status tool, the chat shows that tool event and whether the query reported a healthy cluster.

## Run the integrated demo

From the repository root, install dependencies once and start the API, three simulated nodes, and Angular development server:

```sh
cd apps/web && npm ci
cd ../..
dotnet build DistributedAI.slnx
./tools/start-postgres.sh
./tools/run-dashboard.sh
```

Open <http://127.0.0.1:4200/cluster> for the cluster overview and <http://127.0.0.1:4200/chat> for the agent chat. The root URL redirects to /cluster. Press Ctrl+C in the same Terminal to stop the processes.

To collect telemetry from authorized USB devices instead of using simulated nodes, run `./tools/run-dashboard.sh adb` from the repository root. The bridge only accepts the Galaxy A07 and Galaxy A06 model identifiers, uses hashed IDs, and reads metrics through ADB. Its collection duration is not network or inference latency.

To run only the Angular server, first start the platform API at <http://127.0.0.1:5100>, then run `npm start` from this directory. The development server proxies `/api` to the loopback API.

The chat also requires the Python agent API at `http://127.0.0.1:8000/` (or the `AGENT_RUNTIME_URL` configured for the .NET API). The existing Phase 3 launcher starts the Python API and distributed model; run it alongside the dashboard launcher while developing the integrated chat. The Angular chat stores completed message snapshots through the .NET API in PostgreSQL, resumes its last conversation, lists up to 50 recent conversations, and lets you start a separate chat without deleting old ones. It also lets you save up to 10 user-managed notes that are included in future model requests. Replies continue to stream while they are generated.

## Build and test

```sh
npm ci
npm run build
npm test -- --watch=false
```
