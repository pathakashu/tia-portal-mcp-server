using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EngineerPc.Audit;
using EngineerPc.Contracts;
using EngineerPc.Engineering.Approvals;
using EngineerPc.Engineering.Engine;
using EngineerPc.Engineering.Ir;
using EngineerPc.Engineering.Policy;
using EngineerPc.Engineering.Transactions;
using EngineerPc.Engineering.Validation;
using EngineerPc.Mcp;
using EngineerPc.Mcp.Host;
using EngineerPc.Security;
using EngineerPc.Tia.Abstractions;
using EngineerPc.Tia.V19.Client;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
var transportOptions = builder.Configuration.GetSection("McpTransport").Get<McpTransportOptions>() ?? new McpTransportOptions();
var auditOptions = builder.Configuration.GetSection("McpAudit").Get<McpAuditOptions>() ?? new McpAuditOptions();
var tiaV19WorkerOptions = builder.Configuration.GetSection("TiaV19Worker").Get<TiaV19WorkerHostOptions>() ?? new TiaV19WorkerHostOptions();
var optionValidation = McpTransportOptionsValidator.Validate(transportOptions);
if (!optionValidation.IsValid)
{
	throw new InvalidOperationException(string.Join(" ", optionValidation.Errors));
}

builder.WebHost.ConfigureKestrel(serverOptions =>
{
	serverOptions.Limits.MaxRequestBodySize = transportOptions.MaxRequestBodySizeBytes;
	serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(transportOptions.RequestTimeoutSeconds);

	if (transportOptions.AllowInsecureLocalhost)
	{
		serverOptions.ListenLocalhost(transportOptions.Port);
		return;
	}

	var serverCertificate = new X509Certificate2(
		transportOptions.ServerCertificatePath!,
		transportOptions.ServerCertificatePassword);
	var clientCertificateValidator = new ClientCertificateValidator(
		transportOptions.TrustedClientIssuers,
		TimeProvider.System);

	serverOptions.ListenAnyIP(transportOptions.Port, listenOptions => listenOptions.UseHttps(httpsOptions =>
	{
		httpsOptions.ServerCertificate = serverCertificate;
		httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
		httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
		httpsOptions.ClientCertificateValidation = clientCertificateValidator.Validate;
	}));
});

var app = builder.Build();

app.Use(async (context, next) =>
{
	try
	{
		await next(context).WaitAsync(TimeSpan.FromSeconds(transportOptions.RequestTimeoutSeconds), context.RequestAborted);
	}
	catch (TimeoutException) when (!context.Response.HasStarted)
	{
		context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
	}
});

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));

var timeProvider = TimeProvider.System;
var sessionManager = new McpSessionManager(timeProvider);
var engineeringAuditSink = new JsonLinesEngineeringAuditSink(auditOptions.EngineeringFilePath);
var tiaV19WorkerClient = tiaV19WorkerOptions.Enabled
	? new TiaV19WorkerClient(tiaV19WorkerOptions.ToClientOptions())
	: null;
ITiaAdapter projectContextAdapter = tiaV19WorkerClient is not null
	? tiaV19WorkerClient
	: new PlanningOnlyTiaAdapter();
if (tiaV19WorkerClient is IDisposable disposableProjectContextAdapter)
{
	app.Lifetime.ApplicationStopping.Register(disposableProjectContextAdapter.Dispose);
}

var blockWriteEnabled = tiaV19WorkerOptions.Enabled && tiaV19WorkerOptions.EnableBlockWrite;
ITiaAdapter executionAdapter = blockWriteEnabled && tiaV19WorkerClient is not null
	? tiaV19WorkerClient
	: new PlanningOnlyTiaAdapter();

var operationPlanner = new EngineeringOperationPlanner(new CreateBlockOperationValidator());
var engineeringPolicy = new DefaultEngineeringPolicy();
var workflow = new CreateBlockWorkflow(
	operationPlanner,
	engineeringPolicy,
	new ApprovalService(),
	new TransactionStateMachine(),
	executionAdapter,
	engineeringAuditSink);
var sclBlockPreviewService = new SclBlockPreviewService(
	operationPlanner,
	engineeringPolicy,
	engineeringAuditSink,
	timeProvider);
var projectContextReadService = new ProjectContextReadService(
	projectContextAdapter,
	engineeringAuditSink,
	timeProvider);
var projectBlockCatalogReadService = new ProjectBlockCatalogReadService(
	tiaV19WorkerClient,
	engineeringAuditSink,
	timeProvider);
var authorizationService = new ScopeAuthorizationService(
	[
		new AuthorizationRule("PlanCreateBlock", "Engineer", "engineering.plan"),
		new AuthorizationRule("PreviewSclBlock", "Engineer", "engineering.plan"),
		new AuthorizationRule("GetProjectContext", "Engineer", "engineering.read"),
		new AuthorizationRule("GetBlockCatalog", "Engineer", "engineering.read"),
		new AuthorizationRule("ApproveCreateBlock", "Engineer", "engineering.execute"),
		new AuthorizationRule("ExecuteCreateBlock", "Engineer", "engineering.execute")
	],
	new JsonLinesSecurityEventSink(auditOptions.FilePath),
	timeProvider);
var requestProcessor = new McpJsonRpcRequestProcessor(
	sessionManager,
	new McpToolRouter(
		sessionManager,
		workflow,
		sclBlockPreviewService,
		projectContextReadService,
		projectBlockCatalogReadService,
		authorizationService,
		timeProvider),
	tiaV19WorkerOptions.Enabled,
	tiaV19WorkerOptions.Enabled && tiaV19WorkerOptions.EnableBlockCatalogRead,
	blockWriteEnabled);

if (transportOptions.AllowInsecureLocalhost)
{
	var developmentPrincipal = new AuthenticatedPrincipal(
		new AuthenticatedIdentity("localhost-development", "localhost-development"),
		new HashSet<string>(["Engineer"], StringComparer.Ordinal),
		new HashSet<string>(["engineering.plan", "engineering.read", "engineering.execute"], StringComparer.Ordinal));
	app.MapMethods("/mcp", ["POST", "GET"], async context =>
	{
		await HandleMcpRequestAsync(context, developmentPrincipal);
	});
}
else
{
	var principalMapper = new ClientCertificatePrincipalMapper(transportOptions.ClientPrincipalMappings);
	app.MapMethods("/mcp", ["POST", "GET"], async context =>
	{
		var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
		if (!principalMapper.TryMap(certificate, out var principal))
		{
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return;
		}

		await HandleMcpRequestAsync(context, principal);
	});
}

async Task HandleMcpRequestAsync(HttpContext context, AuthenticatedPrincipal principal)
{
	if (!IsAllowedOrigin(context))
	{
		context.Response.StatusCode = StatusCodes.Status403Forbidden;
		return;
	}

	if (HttpMethods.IsGet(context.Request.Method))
	{
		context.Response.Headers.Allow = "POST";
		context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
		return;
	}

	if (!context.Request.HasJsonContentType())
	{
		context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
		return;
	}

	using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
	var message = await reader.ReadToEndAsync(context.RequestAborted);
	var processingResult = await requestProcessor.ProcessAsync(
		message,
		principal,
		context.Request.Headers["Mcp-Session-Id"].ToString(),
		context.Request.Headers["MCP-Protocol-Version"].ToString(),
		context.RequestAborted);
	context.Response.StatusCode = processingResult.StatusCode;
	if (processingResult.SessionId is not null)
	{
		context.Response.Headers["Mcp-Session-Id"] = processingResult.SessionId;
	}

	if (processingResult.Response is not null)
	{
		context.Response.ContentType = "application/json";
		await context.Response.WriteAsync(McpJsonRpcRequestProcessor.SerializeResponse(processingResult.Response), context.RequestAborted);
	}
}

bool IsAllowedOrigin(HttpContext context)
{
	if (!context.Request.Headers.TryGetValue("Origin", out var originValues))
	{
		return true;
	}

	return Uri.TryCreate(originValues.ToString(), UriKind.Absolute, out var origin) &&
		string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
		string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
}

app.Run();
