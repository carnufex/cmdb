using FastEndpoints;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Cmdb")
    ?? throw new InvalidOperationException("ConnectionStrings:Cmdb is not configured.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddFastEndpoints();

var app = builder.Build();
app.UseFastEndpoints();
app.Run();

public partial class Program;
