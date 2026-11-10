using System.Globalization;
using DocCollect.Client;
using DocCollect.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("fr-FR");

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
// Singletons : l'usine HttpClient crée ses gestionnaires dans une portée DI distincte ;
// les jetons et l'état d'authentification doivent être partagés (une seule session par onglet en WebAssembly).
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddTransient<AuthHeaderHandler>();
builder.Services.AddHttpClient("api", c => c.BaseAddress = baseAddress).AddHttpMessageHandler<AuthHeaderHandler>();
builder.Services.AddHttpClient("anonymous", c => c.BaseAddress = baseAddress);
builder.Services.AddSingleton<AppAuthStateProvider>();
builder.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<AppAuthStateProvider>());
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<ApiClient>();
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

await builder.Build().RunAsync();
