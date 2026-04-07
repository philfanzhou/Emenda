using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Mistake.Database;
using Ruoyu.Study.Mistake.Domain.Services;
using Ruoyu.Study.Mistake.Service;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddDbContext<MistakeDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddScoped<MistakeDomainService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<MistakeServiceImpl>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();
