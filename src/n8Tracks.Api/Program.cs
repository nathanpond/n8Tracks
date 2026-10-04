using n8Tracks.Api.Endpoints;
using n8Tracks.Application;
using n8Tracks.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealth();

app.Run();

public partial class Program;
