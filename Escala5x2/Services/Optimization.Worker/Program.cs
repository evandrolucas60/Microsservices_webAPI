using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Web;
using Google.OrTools.Sat;
using Optimization.Worker.Consumers;
using Optimization.Worker.Engine;

var builder = Host.CreateApplicationBuilder(args);
builder.AddWorkerDefaults();

builder.Services.Configure<SolverOptions>(builder.Configuration.GetSection(SolverOptions.Section));
builder.Services.AddSingleton<ScheduleSolver>();
builder.Services.AddSingleton<ComplianceEvaluator>();
// Gerações em andamento nesta réplica e cancelamentos recebidos (cancelar-geracao)
builder.Services.AddSingleton<GenerationRegistry>();
builder.Services.AddEventBus(builder.Configuration, x =>
{
    // Cada geração já usa vários núcleos: no máximo 2 em paralelo por réplica.
    x.AddConsumer<GenerateScheduleConsumer>().Endpoint(e => e.ConcurrentMessageLimit = 2);
    x.AddConsumer<SuggestCorrectionsConsumer>().Endpoint(e => e.ConcurrentMessageLimit = 2);
    // Cancelamento: fila temporária por réplica (todas recebem; a que está calculando interrompe o CP-SAT)
    x.AddConsumer<CancelScheduleGenerationConsumer>().Endpoint(e =>
    {
        e.InstanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();
        e.Temporary = true;
    });
});

var host = builder.Build();
VerificarOrTools(host.Services.GetRequiredService<ILogger<Program>>());
host.Run();

// No Windows a biblioteca nativa do OR-Tools depende do Microsoft Visual C++ Redistributable (x64).
// Sem ele toda geração falharia com DllNotFoundException; avisa já na subida.
static void VerificarOrTools(ILogger logger)
{
    try
    {
        var model = new CpModel();
        model.NewBoolVar("teste");
        var status = new CpSolver().Solve(model);
        logger.LogInformation("OR-Tools CP-SAT carregado ({Status})", status);
    }
    catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
    {
        logger.LogCritical(ex,
            "Não foi possível carregar o OR-Tools nativo. No Windows, instale o Microsoft Visual C++ Redistributable 2015-2022 (x64).");
    }
}
