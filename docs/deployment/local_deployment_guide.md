# Local Deployment Guide — Real TIA Portal V19 + Local OpenAI Agent

Complete runbook for running the full plan → approve → execute → verify loop on a real
Windows workstation with TIA Portal V19 installed, driven by the local OpenAI-powered agent
(`python/agent/openai_mcp_agent.py`) on the agent side and the existing dev console on the
human-operator side. Follow the steps in order.

## 0. Prerequisites check

```powershell
dotnet --version          # need .NET 8 SDK (for building the worker + optionally the C# host)
python --version          # need 3.11+
git --version             # Git for Windows bundles openssl.exe, used below for certs
```

Confirm the Windows account you'll run the host/worker as is a member of the local
**`Siemens TIA Openness`** group (`lusrmgr.msc` → Groups). Log off/on after adding it — this is
the #1 cause of `EngineeringSecurityException` later.

## 1. Build the Siemens worker

```powershell
cd C:\git\tia-portal-mcp-server
dotnet build EngineerPc.sln -c Release
dotnet test  EngineerPc.sln -c Release        # 16 test projects, no TIA required — sanity check first
```

If the TIA V19 PublicAPI isn't at the default
`C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19`, pass
`-p:TiaV19PublicApiDirectory=<path>` to the build. Confirm the worker exists:

```powershell
Get-Item src\EngineerPc.Tia.V19.Worker\bin\Release\net48\EngineerPc.Tia.V19.Worker.exe
```

## 2. Set up the Python host

```powershell
cd C:\git\tia-portal-mcp-server\python
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -e ".[dev,agent]"
.\.venv\Scripts\python.exe -m pytest        # 173 tests, no TIA required — confirms the mTLS fix is intact
```

## 3. Point the worker at a COPY of a real project — never the original

```powershell
Copy-Item "C:\Path\To\YourRealProject.ap19" -Destination "C:\Engineering\WriteTestCopy.ap19" -Recurse
```

Create `C:\Engineering\tia-v19-worker.json`:

```json
{
    "projects": [
        { "projectId": "test-project", "projectFilePath": "C:\\Engineering\\WriteTestCopy.ap19" }
    ]
}
```

## 4. Generate the test CA and two client certificates (run in Git Bash)

```bash
mkdir -p C:/Engineering/certs && cd C:/Engineering/certs
export MSYS_NO_PATHCONV=1

openssl req -x509 -newkey rsa:2048 -nodes -keyout ca.key -out ca.crt -days 730 -subj "/CN=Engineer-PC Test CA"

openssl req -newkey rsa:2048 -nodes -keyout server.key -out server.csr -subj "/CN=localhost"
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out server.crt -days 365
cat server.crt server.key > server.pem

openssl req -newkey rsa:2048 -nodes -keyout agent.key -out agent.csr -subj "/CN=local-openai-agent"
openssl x509 -req -in agent.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out agent.crt -days 365

openssl req -newkey rsa:2048 -nodes -keyout operator.key -out operator.csr -subj "/CN=human-operator"
openssl x509 -req -in operator.csr -CA ca.crt -CAkey ca.key -CAcreateserial -out operator.crt -days 365

echo "AGENT THUMBPRINT:"; openssl x509 -in agent.crt -noout -fingerprint -sha1
echo "OPERATOR THUMBPRINT:"; openssl x509 -in operator.crt -noout -fingerprint -sha1
```

Copy the two thumbprints (strip the colons, e.g. `B30417FA7ADB75994FD202C266920FFC8D78EBFE`) —
you'll need them next.

**Important:** `server.pem` must stay PEM (cert+key concatenated) — the Python host's `ssl`
module can't load a `.pfx` directly, unlike the C# host.

## 5. Start the Python host with real mTLS + real TIA worker

In a PowerShell terminal, set every variable in the **same session** you start the host from:

```powershell
$env:McpTransport__AllowInsecureLocalhost = "false"
$env:McpTransport__ServerCertificatePath  = "C:\Engineering\certs\server.pem"
$env:McpTransport__TrustedClientIssuers__0 = "CN=Engineer-PC Test CA"

$env:McpTransport__ClientPrincipalMappings__0__CertificateThumbprint = "<AGENT THUMBPRINT>"
$env:McpTransport__ClientPrincipalMappings__0__Roles__0  = "Engineer"
$env:McpTransport__ClientPrincipalMappings__0__Scopes__0 = "engineering.plan"
$env:McpTransport__ClientPrincipalMappings__0__Scopes__1 = "engineering.read"

$env:McpTransport__ClientPrincipalMappings__1__CertificateThumbprint = "<OPERATOR THUMBPRINT>"
$env:McpTransport__ClientPrincipalMappings__1__Roles__0  = "Engineer"
$env:McpTransport__ClientPrincipalMappings__1__Scopes__0 = "engineering.plan"
$env:McpTransport__ClientPrincipalMappings__1__Scopes__1 = "engineering.read"
$env:McpTransport__ClientPrincipalMappings__1__Scopes__2 = "engineering.execute"

$env:McpTransport__RequestTimeoutSeconds  = "300"
$env:McpTransport__SessionDurationSeconds = "1800"

$env:TiaV19Worker__Enabled                = "true"
$env:TiaV19Worker__EnableBlockCatalogRead = "true"
$env:TiaV19Worker__EnableBlockWrite       = "false"   # keep off until read path is confirmed (step 6)
$env:TiaV19Worker__WorkerExecutablePath   = "C:\git\tia-portal-mcp-server\src\EngineerPc.Tia.V19.Worker\bin\Release\net48\EngineerPc.Tia.V19.Worker.exe"
$env:TiaV19Worker__ConfigurationPath      = "C:\Engineering\tia-v19-worker.json"
$env:TiaV19Worker__RequestTimeoutSeconds  = "300"

cd C:\git\tia-portal-mcp-server\python
.\.venv\Scripts\python.exe -m engineerpc.host
```

Leave this running. In a **second** terminal:

```powershell
$agentCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2("C:\Engineering\certs\agent.crt")
# just to eyeball the thumbprint matches what you configured above
$agentCert.Thumbprint
```

## 6. Confirm the read path against the real project

Use `tools/full_loop_acceptance.py` — with `EnableBlockWrite=false` it stops right after
`preview_scl_block`, which is exactly what you want for this first check:

```powershell
cd C:\git\tia-portal-mcp-server\python
.\.venv\Scripts\python.exe tools\full_loop_acceptance.py `
    --base-url https://localhost:7443/mcp `
    --ca-bundle C:\Engineering\certs\ca.crt `
    --agent-cert C:\Engineering\certs\agent.crt --agent-key C:\Engineering\certs\agent.key `
    --operator-cert C:\Engineering\certs\operator.crt --operator-key C:\Engineering\certs\operator.key `
    --project-id test-project
```

Expect real project data: an actual snapshot hash, an actual block catalog from
`WriteTestCopy.ap19`, and a "write path" line noting execute is disabled. A cold TIA session
can take 40–65s per call here — that's normal, not a hang.

## 7. Enable writes and run the real end-to-end loop

Stop the host (Ctrl+C), set `$env:TiaV19Worker__EnableBlockWrite = "true"`, restart it, then
rerun the **same** acceptance command from step 6. This time it runs the full chain against
real TIA Openness: `plan_create_block` → agent's own approve/execute attempts get denied →
operator approves → operator executes → `GenerateBlocksFromSource` writes a real block →
catalog re-read confirms it. A `RESULT: PASS` here is the actual milestone — a real
LLM-adjacent pipeline writing a verified block into a real TIA Portal project through the full
approval gate.

## 8. Run the OpenAI agent for real, driven by natural language

```powershell
cd C:\git\tia-portal-mcp-server\python
$env:OPENAI_API_KEY = "sk-..."
.\.venv\Scripts\python.exe agent\openai_mcp_agent.py `
    --base-url https://localhost:7443/mcp `
    --ca-bundle C:\Engineering\certs\ca.crt `
    --agent-cert C:\Engineering\certs\agent.crt --agent-key C:\Engineering\certs\agent.key `
    --project-id test-project
```

Try something like: *"Show me the current block catalog, then create a function block called
FB_ConveyorControl on PLC_1 with a Start bool input and a Running bool output that just passes
Start through to Running. Preview the SCL before you say it's ready."* Watch it call
`get_project_context`/`get_block_catalog`/`preview_scl_block` on its own.

## 9. Human approves and executes

Open `https://localhost:7443/` in a browser that has the **operator** certificate available
(import `operator.crt`+`operator.key`, e.g. combine into a `.pfx` via
`openssl pkcs12 -export -out operator.pfx -inkey operator.key -in operator.crt` and
double-click to install into the current user's certificate store, then select it when the
browser prompts for a client cert). Use **Insert last transaction**, then
**approve_create_block** → **execute_create_block**.

## 10. Close the loop

Back in the agent's REPL, ask it to re-read the block catalog and confirm the new block is
there — same real-hardware verification `full_loop_acceptance.py` already proved
mechanically, now driven by conversation instead of a script.

## Troubleshooting

Reuse the table in [`README.md`](../../README.md#troubleshooting) — the
`EngineeringSecurityException` (Openness group membership) and session-timeout entries are the
two most likely to bite first.
