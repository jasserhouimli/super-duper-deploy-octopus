using Microsoft.EntityFrameworkCore;
using Octopus.Deployments;
using Octopus.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContext<OctopusDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Octopus") ?? "Data Source=octopus.db"));
builder.Services.AddHostedService<DeploymentWorker>();

var host = builder.Build();

// Ensure DB exists for `dotnet run` without the API.
using (var scope = host.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<OctopusDbContext>().Database.EnsureCreated();
}

host.Run();
