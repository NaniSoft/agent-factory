using AgentFactory.Infrastructure;
using AgentFactory;

var builder = WebApplication.CreateBuilder(args);
var options = FactorySettings.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath);
var app = FactoryApp.Create(builder, options);
app.Run();
