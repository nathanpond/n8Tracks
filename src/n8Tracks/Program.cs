var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
    .WithName("GetHealth");

app.Run();

internal sealed record HealthResponse(string Status);

public partial class Program;
