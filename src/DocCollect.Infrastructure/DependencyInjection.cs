using DocCollect.Core.Ai;
using DocCollect.Core.Delivery;
using DocCollect.Core.Documents;
using DocCollect.Core.Notifications;
using DocCollect.Core.Security;
using DocCollect.Core.Storage;
using DocCollect.Infrastructure.Delivery;
using DocCollect.Infrastructure.Email;
using DocCollect.Infrastructure.Persistence;
using DocCollect.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocCollect.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Enregistre persistance, stockages, chiffrement, IA, e-mail et tâches de fond.
    /// Pour ajouter un type de stockage : <c>services.AddSingleton&lt;IStorageProvider, MonProvider&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddDocCollectInfrastructure(this IServiceCollection services, IConfiguration config,
        Action<DbContextOptionsBuilder>? configureDb = null)
    {
        services.Configure<AppOptions>(config.GetSection(AppOptions.Section));
        services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.Section));
        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<AppDbContext>(o =>
        {
            if (configureDb is not null) configureDb(o);
            else o.UseNpgsql(config.GetConnectionString("Default"),
                npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", "doccollect"));
        });

        // Stockages disponibles
        services.AddSingleton<IStorageProvider, LocalFileSystemStorageProvider>();
        services.AddSingleton<IStorageProvider, FtpStorageProvider>();
        services.AddSingleton<IStorageProvider, SftpStorageProvider>();
        services.AddSingleton<IStorageProvider, AzureBlobStorageProvider>();
        services.AddSingleton<IStorageProvider, GoogleCloudStorageProvider>();
        services.AddSingleton<StorageProviderRegistry>();
        services.AddScoped<StorageProfileService>();
        services.AddScoped<IStorageResolver>(sp => sp.GetRequiredService<StorageProfileService>());

        // Chiffrement
        var encryption = config.GetSection(EncryptionOptions.Section).Get<EncryptionOptions>() ?? new EncryptionOptions();
        services.AddSingleton(encryption);
        services.AddSingleton<IMasterKeyProvider>(sp => new ConfiguredMasterKeyProvider(sp.GetRequiredService<EncryptionOptions>()));
        services.AddSingleton<IDocumentEncryptor>(sp => new DocumentEncryptor(sp.GetRequiredService<IMasterKeyProvider>(), encryption.ChunkSize));
        services.AddSingleton<ISecretProtector, SecretProtector>();

        // IA
        var ai = config.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton(ai);
        services.AddHttpClient("ai");
        services.AddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new AiClassifierRegistry(ai, _ => factory.CreateClient("ai"), sp.GetServices<IDocumentClassifier>());
        });

        services.AddScoped<DocumentIngestionService>();

        // E-mail et livraison
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddHttpClient<WebhookDeliveryHandler>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IDeliveryHandler>(sp => sp.GetRequiredService<WebhookDeliveryHandler>());
        services.AddScoped<IDeliveryHandler, StorageCopyDeliveryHandler>();
        services.AddScoped<IDeliveryHandler, EmailNotificationDeliveryHandler>();
        services.AddSingleton<DeliverySignal>();
        services.AddScoped<DeliveryScheduler>();

        return services;
    }

    /// <summary>Tâches de fond (désactivables dans les tests d'intégration).</summary>
    public static IServiceCollection AddDocCollectWorkers(this IServiceCollection services)
    {
        services.AddHostedService<DeliveryWorker>();
        services.AddHostedService<ReminderWorker>();
        return services;
    }
}
